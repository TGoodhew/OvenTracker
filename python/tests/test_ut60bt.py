"""The UNI-T UT60BT (Ut60btTests.cs): its frames and commands, the meter against fake Bluetooth LE links, the driver,
and the bleak link's write against a fake bleak. Nothing here needs bleak or a Bluetooth adapter."""

from __future__ import annotations

import sys
import threading
import time
import types
from datetime import datetime, timedelta, timezone

import pytest

from oventracker.meter import ble
from oventracker.meter import driver as drivers
from oventracker.meter import ut60bt_protocol as protocol
from oventracker.meter.auto_power_off import AutoPowerOffState
from oventracker.meter.base import MeterException, ReadingStatus
from oventracker.meter.ut60bt import Ut60btDriver, Ut60btMeter, check, is_ut60bt
from oventracker.meter.ut60bt_protocol import Ut60btAssembler, Ut60btFrame
from oventracker.units import TempUnit

EPOCH = datetime(1970, 1, 1, tzinfo=timezone.utc)
NAME_REPLY = bytes.fromhex("AB CD 08 55 54 36 30 42 54 03 25")  # libreble's notes: a UT60BT's answer to GET_NAME


@pytest.fixture(autouse=True)
def no_bleak(monkeypatch):
    """bleak hidden (import bleak raises ImportError), so no test can reach a real adapter."""
    monkeypatch.setitem(sys.modules, "bleak", None)
    ble._reset()


def frame(mode: int, display: str, f14: int = 0x30, f15: int = 0x30, f16: int = 0x30, range_: str = "0") -> bytes:
    """A data frame with these fields and a correct checksum. The flag bytes carry 0x30, as real ones do."""
    f = bytearray(19)
    f[0:3] = b"\xab\xcd\x10"
    f[3] = mode
    f[4] = ord(range_)
    f[5:12] = display.rjust(7).encode("ascii")
    f[14], f[15], f[16] = f14, f15, f16
    total = sum(f[:17])
    f[17], f[18] = (total >> 8) & 0xFF, total & 0xFF
    return bytes(f)


def test_the_commands_are_the_ones_the_sources_send():
    assert protocol.GET_NAME == bytes.fromhex("ABCD035F01DA")
    assert protocol.GET_DATA == bytes.fromhex("ABCD035D01D8")
    assert protocol.TRIGGER == bytes.fromhex("ABCD035E01D9")


def test_decodes_unit_ut61eplus_example_frame():
    # From unit_ut61eplus (ut61eplus.py): 53.54 mV AC, manual range.
    f = Ut60btFrame(bytes.fromhex("ab cd 10 01 30 20 20 35 33 2e 35 34 01 00 30 34 30 03 8d"))
    assert f.mode == 1 and f.range == 0
    assert f.value == pytest.approx(53.54)
    assert f.manual_range and not f.low_battery
    assert not protocol.config(f).is_temperature
    assert protocol.describe(f) == "AC mV 53.54"


def test_reads_the_name_reply():
    assert protocol.name_in(NAME_REPLY) == "UT60BT"
    assert protocol.name_in(NAME_REPLY[:-1] + b"\x26") is None
    assert protocol.name_in(frame(10, "234")) is None


def test_rejects_a_bad_checksum_or_an_impossible_mode():
    good = frame(10, "234")
    assert protocol.is_data(good)
    bad = bytearray(good)
    bad[9] ^= 1
    assert not protocol.is_data(bytes(bad))
    assert not protocol.is_data(frame(31, "234"))
    assert not protocol.is_data(frame(10, "234", range_="x"))


def test_reads_temperature_in_celsius():
    f = Ut60btFrame(frame(10, "234"))
    r = protocol.to_reading(EPOCH, f)
    assert r.status == ReadingStatus.Ok and r.celsius == pytest.approx(234.0)
    c = protocol.config(f)
    assert c.is_temperature and c.unit == TempUnit.Celsius
    assert c.sensor is not None and c.sensor.name == "type K thermocouple"


def test_converts_fahrenheit():
    f = Ut60btFrame(frame(11, "450"))
    assert protocol.config(f).unit == TempUnit.Fahrenheit
    assert protocol.to_reading(EPOCH, f).celsius == pytest.approx((450 - 32) / 1.8, abs=1e-3)


def test_reads_a_negative_temperature():
    assert protocol.to_reading(EPOCH, Ut60btFrame(frame(10, "-12"))).celsius == pytest.approx(-12.0)


@pytest.mark.parametrize("display", [" OL.  ", "O.L", "-OL"])
def test_an_open_thermocouple_is_an_overload(display):
    assert protocol.to_reading(EPOCH, Ut60btFrame(frame(10, display))).status == ReadingStatus.Overload


@pytest.mark.parametrize(
    "display, expected",
    [("-  12", -12.0), ("23\x00\x00\x00", 23.0), ("- 1.5", -1.5)],
    ids=["sign in its own column", "NUL padding", "sign and point"],
)
def test_reads_the_display_without_its_spaces(display, expected):
    assert protocol.to_reading(EPOCH, Ut60btFrame(frame(10, display))).celsius == pytest.approx(expected)


def test_a_display_that_isnt_a_number_is_an_error():
    assert protocol.to_reading(EPOCH, Ut60btFrame(frame(10, "----"))).status == ReadingStatus.Error


@pytest.mark.parametrize(
    "mode, f14, f16",
    [(2, 0x30, 0x30), (10, 0x32, 0x30), (10, 0x31, 0x30), (10, 0x38, 0x30), (10, 0x30, 0x34)],
    ids=["DC volts", "HOLD", "REL", "MAX", "peak max"],
)
def test_only_live_temperature_counts(mode, f14, f16):
    f = Ut60btFrame(frame(mode, "234", f14=f14, f16=f16))
    assert not protocol.config(f).is_temperature
    assert protocol.to_reading(EPOCH, f).status == ReadingStatus.NotTemperature


def test_sees_the_low_battery_sign():
    assert Ut60btFrame(frame(10, "234", f15=0x32)).low_battery
    assert not Ut60btFrame(frame(10, "234", f15=0x34)).low_battery


def test_reassembles_frames_split_across_notifications():
    stream = bytes([0x00, 0xAB, 0xCD, 0x7F]) + NAME_REPLY + frame(10, "200") + frame(10, "201")
    asm = Ut60btAssembler()
    got: list[bytes] = []
    for i in range(0, len(stream), 5):
        got.extend(asm.add(stream[i : i + 5]))
    assert len(got) == 3
    assert protocol.name_in(got[0]) == "UT60BT"
    assert [Ut60btFrame(f).value for f in got[1:]] == [200.0, 201.0]


def test_takes_two_frames_in_one_notification():
    assert len(Ut60btAssembler().add(frame(10, "200") + frame(10, "201"))) == 2


class FakeMeterLink(ble.BleLink):
    """A meter on a fake link. It answers GET_NAME with its name (unless ``answers_name`` is off), and after one of
    ``data_command`` either streams (the test sends frames) or, with ``one_frame_per_request``, answers each request
    with ``next()``."""

    def __init__(self, answers_name=True, data_command=protocol.GET_DATA, one_frame_per_request=False):
        self.answers_name = answers_name
        self.data_command = data_command
        self.one_frame_per_request = one_frame_per_request
        self.streaming = False
        self.next = lambda: frame(10, "180")
        self.writes: list[bytes] = []
        self._lock = threading.Lock()
        self._received = None
        self.opened = False

    @property
    def address(self) -> str:
        return "C4:BE:84:12:34:56"

    def open(self, received):
        self._received = received
        self.opened = True
        self.streaming = False  # a fresh connection: not streaming yet

    def write(self, data: bytes) -> None:
        with self._lock:
            self.writes.append(data)
        if data == protocol.GET_NAME and self.answers_name:
            self._received(NAME_REPLY)
        elif self.data_command is not None and data == self.data_command:
            if self.one_frame_per_request:
                self._received(self.next())
            elif not self.streaming:
                self.streaming = True
                self._received(self.next())

    def send(self, data: bytes) -> None:
        self._received(data)

    def dispose(self) -> None:
        self.opened = False

    def written(self) -> list[bytes]:
        with self._lock:
            return list(self.writes)


def test_a_streaming_meter_is_asked_for_its_name_then_for_data_once():
    link = FakeMeterLink()
    meter = Ut60btMeter(link, timeout=2, poll_every=0.3)
    identity = meter.connect()
    assert identity.model == "UT60BT"
    assert meter.device_name == "UT60BT"
    assert meter.location == "ble:C4:BE:84:12:34:56"
    assert link.written()[:2] == [protocol.GET_NAME, protocol.GET_DATA]
    assert meter.read_config().is_temperature

    # The first reading is the frame that came while connecting; after that, read waits for a newer one.
    assert meter.read().celsius == pytest.approx(180.0)
    result: list = []
    t = threading.Thread(target=lambda: result.append(meter.read()))
    t.start()
    time.sleep(0.1)
    assert not result
    link.send(frame(10, "181", f15=0x32))
    t.join(2)
    assert result[0].celsius == pytest.approx(181.0)
    assert meter.read_low_battery() is True
    assert meter.read_battery_percent() is None
    assert meter.read_auto_power_off().state == AutoPowerOffState.Unknown
    meter.dispose()


def test_a_meter_that_answers_only_the_other_request_is_polled_with_it():
    link = FakeMeterLink(answers_name=False, data_command=protocol.TRIGGER, one_frame_per_request=True)
    count = iter(range(200, 300))
    link.next = lambda: frame(10, str(next(count)))
    meter = Ut60btMeter(link, timeout=3, poll_every=0.05, name_wait=0.02)
    meter.connect()
    assert meter.device_name is None
    readings = [meter.read().celsius for _ in range(3)]
    assert readings[0] < readings[1] < readings[2], readings
    # It asked for the name three times, tried GET_DATA once, then kept to the request that worked.
    writes = link.written()
    assert writes.count(protocol.GET_NAME) == 3
    assert writes.count(protocol.GET_DATA) == 1
    assert writes.count(protocol.TRIGGER) >= 3
    meter.dispose()


def test_reconnecting_starts_clean_and_asks_for_the_name_again():
    # As after a battery swap: the meter is switched off and on, and the poller calls connect again.
    link = FakeMeterLink()
    link.next = lambda: frame(10, "180", f15=0x32)  # low battery
    meter = Ut60btMeter(link, timeout=2, poll_every=0.3)
    meter.connect()
    assert meter.read_low_battery() is True
    assert meter.read().celsius == pytest.approx(180.0)

    link.next = lambda: frame(10, "181")  # a fresh battery
    before = len(link.written())
    meter.connect()
    after = link.written()[before:]
    assert after[:2] == [protocol.GET_NAME, protocol.GET_DATA]
    assert meter.read_low_battery() is False  # not the frame from before
    assert meter.read().celsius == pytest.approx(181.0)
    meter.dispose()


def test_the_meter_times_out_when_nothing_answers():
    meter = Ut60btMeter(FakeMeterLink(answers_name=False, data_command=None), timeout=0.3, poll_every=0.05, name_wait=0.01)
    with pytest.raises(TimeoutError):
        meter.connect()


def test_its_driver_is_known_with_its_auto_power_off_and_battery():
    driver = drivers.find("ut60bt")
    assert isinstance(driver, Ut60btDriver)
    assert driver.auto_power_off.default_timeout == timedelta(minutes=15)
    assert driver.has_battery
    assert check("ble:UT60BT") is None
    assert "Bluetooth LE" in check("COM5")
    assert is_ut60bt(ble.BleDevice("UT60BTk", "C4:BE:84:12:34:56"))
    assert not is_ut60bt(ble.BleDevice("121GW", "C4:BE:84:12:34:56"))
    # Without bleak, discovery finds nothing rather than failing.
    assert driver.discover() == []


class FakeServices:
    def __init__(self, uuids: set[str]):
        self.uuids = uuids

    def get_characteristic(self, specifier):
        return object() if specifier in self.uuids else None


class FakeBleakClient:
    instances: list["FakeBleakClient"] = []
    characteristics: set[str] = {protocol.NOTIFY, protocol.WRITE}

    def __init__(self, address, timeout=30, **kwargs):
        self.address = address
        self.calls: list[str] = []
        self.written: list[tuple[str, bytes, bool]] = []
        self.services = FakeServices(FakeBleakClient.characteristics)
        FakeBleakClient.instances.append(self)

    async def connect(self, **kwargs):
        self.calls.append("connect")

    async def start_notify(self, char_specifier, callback, **kwargs):
        self.calls.append(f"start_notify {char_specifier}")

    async def write_gatt_char(self, char_specifier, data, response=None):
        self.written.append((char_specifier, bytes(data), response))

    async def stop_notify(self, char_specifier):
        self.calls.append(f"stop_notify {char_specifier}")

    async def disconnect(self):
        self.calls.append("disconnect")


def test_the_bleak_link_writes_commands_to_the_write_characteristic(monkeypatch):
    module = types.ModuleType("bleak")
    module.BleakClient = FakeBleakClient  # type: ignore[attr-defined]
    FakeBleakClient.instances = []
    monkeypatch.setitem(sys.modules, "bleak", module)
    ble._reset()

    link = ble.link_for("C4:BE:84:12:34:56", protocol.SERVICE, protocol.NOTIFY, protocol.WRITE)
    with pytest.raises(MeterException):
        link.write(protocol.GET_NAME)  # not open yet
    link.open(lambda _: None)
    client = FakeBleakClient.instances[-1]
    assert client.calls == ["connect", f"start_notify {protocol.NOTIFY}"]
    link.write(protocol.GET_NAME)
    assert client.written == [(protocol.WRITE, protocol.GET_NAME, False)]
    link.dispose()

    # A link without a write characteristic (the 121GW's) can't write.
    other = ble.link_for("C4:BE:84:12:34:56", protocol.SERVICE, protocol.NOTIFY)
    other.open(lambda _: None)
    with pytest.raises(MeterException):
        other.write(protocol.GET_NAME)
    other.dispose()

    # A device without the command characteristic fails when it's opened, and is let go.
    FakeBleakClient.characteristics = {protocol.NOTIFY}
    try:
        missing = ble.link_for("C4:BE:84:12:34:56", protocol.SERVICE, protocol.NOTIFY, protocol.WRITE)
        with pytest.raises(MeterException, match="command characteristic"):
            missing.open(lambda _: None)
        assert FakeBleakClient.instances[-1].calls == ["connect", "disconnect"]
    finally:
        FakeBleakClient.characteristics = {protocol.NOTIFY, protocol.WRITE}
