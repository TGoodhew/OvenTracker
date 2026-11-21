"""The EEVblog 121GW (Eevblog121GwTests.cs): its packets, the meter against a fake Bluetooth LE link, the driver,
and the bleak link against a fake bleak. Nothing here needs bleak or a Bluetooth adapter, and nothing scans or
connects: bleak is hidden from every test, and the ones that need it get a fake."""

from __future__ import annotations

import os
import subprocess
import sys
import threading
import time
import types
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest

from conftest import SRC
from oventracker.meter import ble
from oventracker.meter import driver as drivers
from oventracker.meter import eevblog121gw as gw
from oventracker.meter import eevblog121gw_protocol as protocol
from oventracker.meter.auto_power_off import AutoPowerOffState
from oventracker.meter.base import MeterException, ReadingStatus
from oventracker.meter.driver import MeterRequest
from oventracker.meter.eevblog121gw import Eevblog121GwDriver, Eevblog121GwMeter
from oventracker.meter.eevblog121gw_protocol import Gw121Assembler, Gw121Packet
from oventracker.meter.hp34401a import check as hp34401a_check
from oventracker.meter.resource_spec import ResourceSpec, Transport
from oventracker.meter.u1253b import check as u1253b_check
from oventracker.units import TempUnit

EPOCH = datetime(1970, 1, 1, tzinfo=timezone.utc)


@pytest.fixture(autouse=True)
def no_bleak(monkeypatch):
    """bleak hidden (import bleak raises ImportError), so no test can reach a real adapter."""
    monkeypatch.setitem(sys.modules, "bleak", None)
    ble._reset()
    yield
    ble._reset()


def hex_bytes(s: str) -> bytes:
    return bytes.fromhex(s.replace(" ", ""))


def packet(mode: int, b6: int, count: int, b15: int = 0x04, b16: int = 0x40, b17: int = 0x00) -> bytes:
    """A packet with these fields and a correct checksum."""
    p = bytearray(19)
    p[0] = 0xF2
    p[1:5] = b"\x17\x84\x21\x21"
    p[5] = (mode | ((count >> 16) << 6)) & 0xFF
    p[6] = b6
    p[7] = (count >> 8) & 0xFF
    p[8] = count & 0xFF
    p[9], p[10], p[11], p[12] = 100, 0x01, 0x01, 0x17
    p[15], p[16], p[17] = b15, b16, b17
    x = 0
    for v in p[:18]:
        x ^= v
    p[18] = x
    return bytes(p)


# Two captured from real meters (sigrok's wiki, 121gwcli), and four from the dmm-tools write-up.
@pytest.mark.parametrize(
    "text,mode,value,overload",
    [
        ("F2 17 84 21 21 08 00 00 00 64 01 01 17 12 37 02 40 00 7D", 8, 0.0, False),
        ("F2 17 85 43 21 09 86 00 00 64 01 01 02 01 21 06 40 00 8D", 9, None, True),
        ("F2 12 61 23 45 01 00 30 39 64 01 00 F3 00 00 0C 40 00 35", 1, 1.2345, False),
        ("F2 12 61 23 45 46 00 56 66 00 00 00 00 00 00 04 40 00 D5", 6, 87.654, False),
        ("F2 12 61 23 45 03 40 30 39 00 00 00 00 00 00 0C 40 00 E1", 3, -12.345, False),
        ("F2 12 61 23 45 09 86 00 00 00 00 00 00 00 00 04 40 00 2C", 9, None, True),
    ],
)
def test_decodes_the_known_packets(text, mode, value, overload):
    p = Gw121Packet(hex_bytes(text))
    assert p.mode == mode
    assert p.overload == overload
    if value is None:
        assert p.value is None
    else:
        assert p.value == pytest.approx(value, abs=5e-10)
    assert str(p) == text.replace(" ", "")


def test_rejects_a_bad_checksum_or_an_impossible_mode():
    good = hex_bytes("F2 12 61 23 45 01 00 30 39 64 01 00 F3 00 00 0C 40 00 35")
    assert protocol.is_valid(good)
    bad = bytearray(good)
    bad[8] ^= 1
    assert not protocol.is_valid(bad)
    assert not protocol.is_valid(packet(25, 0, 0))  # no such mode
    assert not protocol.is_valid(packet(5, 0x23, 0))  # temperature has one range
    with pytest.raises(ValueError):
        Gw121Packet(bad)


def test_reads_temperature_in_celsius():
    p = Gw121Packet(packet(5, 0x20, 2345))  # 234.5 °C
    r = protocol.to_reading(EPOCH, p)
    assert r.status == ReadingStatus.Ok
    assert r.celsius == pytest.approx(234.5, abs=5e-7)
    assert r.raw == str(p)
    config = protocol.config(p)
    assert config.is_temperature
    assert config.unit == TempUnit.Celsius
    assert config.sensor is not None and config.sensor.name == "type K thermocouple"
    assert config.description == "temperature, °C"


def test_converts_fahrenheit():
    p = Gw121Packet(packet(5, 0x10, 4500))  # 450.0 °F
    assert protocol.config(p).unit == TempUnit.Fahrenheit
    assert protocol.to_reading(EPOCH, p).celsius == pytest.approx((450 - 32) / 1.8, abs=5e-4)
    assert protocol.to_reading(EPOCH, p).celsius == round((450 - 32) / 1.8, 4)  # Math.Round(c, 4)


def test_an_older_meter_without_unit_flags_is_taken_as_celsius():
    p = Gw121Packet(packet(5, 0x00, 1800))
    assert protocol.to_reading(EPOCH, p).celsius == pytest.approx(180.0, abs=5e-7)


def test_an_open_thermocouple_is_an_overload():
    assert protocol.to_reading(EPOCH, Gw121Packet(packet(5, 0xA0, 0))).status == ReadingStatus.Overload


@pytest.mark.parametrize(
    "mode,b6,b16,b17,description",
    [
        (1, 0x00, 0x40, 0x00, "DC V"),  # DC volts
        (5, 0x20, 0x40, 0x08, "temperature, °C, with HOLD on"),
        (5, 0x20, 0x50, 0x00, "temperature, °C, with REL on"),
        (5, 0x20, 0x41, 0x00, "temperature, °C, with MIN/MAX on"),  # MAX
    ],
)
def test_only_live_temperature_counts(mode, b6, b16, b17, description):
    p = Gw121Packet(packet(mode, b6, 1800, b16=b16, b17=b17))
    assert not protocol.config(p).is_temperature
    assert protocol.config(p).description == description
    assert protocol.to_reading(EPOCH, p).status == ReadingStatus.NotTemperature


def test_reassembles_packets_split_across_notifications():
    a = packet(5, 0x20, 2000)
    b = packet(5, 0x20, 2010)
    stream = bytes([0x00, 0xF2, 0x13]) + a + b  # junk, including a stray F2, first
    asm = Gw121Assembler()
    got: list[Gw121Packet] = []
    for i in range(0, len(stream), 7):
        got.extend(asm.add(stream[i : i + 7]))
    assert [p.value for p in got] == [200.0, 201.0]


def test_takes_two_packets_in_one_notification():
    both = packet(5, 0x20, 2000) + packet(5, 0x20, 2010)
    assert len(Gw121Assembler().add(both)) == 2


def test_takes_a_packet_sent_without_its_start_byte():
    got = Gw121Assembler().add(packet(5, 0x20, 2000)[1:])
    assert len(got) == 1 and got[0].value == 200.0


class FakeLink(ble.BleLink):
    def __init__(self, address: str = "C4:BE:84:12:34:56", on_open: list[bytes] | None = None):
        self._address = address
        self._received = None
        self.opened = False
        self.disposed = 0
        self.on_open = on_open or []

    @property
    def address(self) -> str:
        return self._address

    def open(self, received) -> None:
        self._received = received
        self.opened = True
        for data in self.on_open:
            received(data)

    def send(self, data: bytes) -> None:
        self._received(data)

    def dispose(self) -> None:
        self.opened = False
        self.disposed += 1


def test_the_meter_connects_and_reads_each_packet_once():
    link = FakeLink()
    meter = Eevblog121GwMeter(link, timeout=2)

    def feeder():
        while not link.opened:
            time.sleep(0.005)
        link.send(packet(5, 0x20, 1800, b15=0x06))

    t = threading.Thread(target=feeder)
    t.start()
    try:
        identity = meter.connect()
        t.join()
        assert identity.model == "121GW"
        assert identity.serial == "17842121"
        assert meter.location == "ble:C4:BE:84:12:34:56"
        assert meter.read_auto_power_off().state == AutoPowerOffState.On
        assert meter.read_auto_power_off().timeout == timedelta(minutes=30)
        assert meter.read_config().is_temperature
        assert meter.low_battery is False

        # The first reading is the packet that arrived while connecting; after that, read waits for a newer one.
        assert meter.read().celsius == pytest.approx(180.0, abs=5e-7)
        result: list = []
        reading = threading.Thread(target=lambda: result.append(meter.read()))
        reading.start()
        time.sleep(0.1)
        assert reading.is_alive()
        link.send(packet(5, 0x20, 1812, b15=0x05))
        reading.join(2)
        assert result[0].celsius == pytest.approx(181.2, abs=5e-7)
        assert meter.read_auto_power_off().state == AutoPowerOffState.Off
        assert meter.low_battery is True
        assert meter.read_battery_percent() is None
        assert meter.packets_received == 2
    finally:
        meter.dispose()
    assert not link.opened and link.disposed == 1


def test_the_meter_times_out_when_the_stream_stops():
    link = FakeLink()
    meter = Eevblog121GwMeter(link, timeout=0.2)
    with pytest.raises(TimeoutError):
        meter.connect()
    meter.dispose()
    assert link.disposed == 1
    # Before anything arrives, the meter can't say what it's set to.
    assert meter.read_config() is None
    assert meter.read_auto_power_off().state == AutoPowerOffState.Unknown


@pytest.mark.parametrize("text,address", [("ble:C4:BE:84:12:34:56", "C4:BE:84:12:34:56"), ("BLE:121GW", "121GW")])
def test_parses_bluetooth_le_resources(text, address):
    spec = ResourceSpec.parse(text)
    assert spec is not None
    assert spec.transport == Transport.Ble
    assert spec.address == address
    assert ResourceSpec.parse("ble:") is None


def test_each_driver_refuses_the_others_connections():
    assert gw.check("ble:121GW") is None
    assert "Bluetooth LE" in gw.check("COM5")
    assert "not Bluetooth LE" in u1253b_check("ble:121GW")
    assert "Bluetooth" in hp34401a_check("ble:121GW")


@pytest.mark.parametrize(
    "text,expected",
    [("c4be84123456", "C4:BE:84:12:34:56"), ("C4-BE-84-12-34-56", "C4:BE:84:12:34:56"), ("121GW", None), ("C4:BE:84:12:34", None)],
)
def test_normalizes_addresses(text, expected):
    assert ble.normalize_address(text) == expected


def test_its_auto_power_off_is_known_and_bluetooth_le_needs_bleak():
    driver = drivers.find("121gw")
    assert isinstance(driver, Eevblog121GwDriver) and not driver.not_ported
    assert driver.auto_power_off.default_timeout == timedelta(minutes=30)
    assert not driver.auto_power_off.remote_reads_keep_it_on
    assert any("APO.oF" in step for step in driver.auto_power_off.how_to_turn_off)
    # Without bleak: not supported, and every way in says what to install rather than failing.
    assert not ble.is_supported()
    assert "pip install bleak" in ble.not_supported_help() and "./python[ble]" in ble.not_supported_help()
    assert driver.not_found_help == ble.not_supported_help()
    assert driver.discover() == []
    assert driver.open(MeterRequest(None, None, 1)).error == ble.not_supported_help()
    assert driver.open(MeterRequest("ble:121GW", None, 1)).error == ble.not_supported_help()
    assert driver.probe("ble:121GW").error == ble.not_supported_help()
    assert ble.scan() == []
    with pytest.raises(MeterException, match="bleak"):
        ble.link_for("C4:BE:84:12:34:56", protocol.SERVICE, protocol.CHARACTERISTIC)


# A fake bleak: what BleakLink and scan use of it, recording each call.


class FakeBleakClient:
    instances: list["FakeBleakClient"] = []
    fail_notify: Exception | None = None

    def __init__(self, address, timeout=30, **kwargs):
        self.address = address
        self.timeout = timeout
        self.calls: list[str] = []
        self.callback = None
        self.notify_uuid = None
        self.loop_thread: str | None = None
        FakeBleakClient.instances.append(self)

    async def connect(self, **kwargs):
        self.calls.append("connect")
        self.loop_thread = threading.current_thread().name

    async def start_notify(self, char_specifier, callback, **kwargs):
        self.calls.append("start_notify")
        if FakeBleakClient.fail_notify is not None:
            raise FakeBleakClient.fail_notify
        self.notify_uuid = char_specifier
        self.callback = callback

    async def stop_notify(self, char_specifier):
        self.calls.append(f"stop_notify {char_specifier}")

    async def disconnect(self):
        self.calls.append("disconnect")


class BleakCharacteristicNotFoundError(Exception):
    pass


class BleakDBusError(Exception):
    def __init__(self, name: str, body: list):
        super().__init__(name, body)

    @property
    def dbus_error(self) -> str:
        return self.args[0]


@pytest.fixture
def fake_bleak(monkeypatch):
    module = types.ModuleType("bleak")
    module.BleakClient = FakeBleakClient  # type: ignore[attr-defined]
    scanned: dict = {"devices": [], "error": None, "timeouts": []}

    class BleakScanner:
        @staticmethod
        async def discover(timeout=5.0, **kwargs):
            scanned["timeouts"].append(timeout)
            if scanned["error"] is not None:
                raise scanned["error"]
            return scanned["devices"]

    module.BleakScanner = BleakScanner  # type: ignore[attr-defined]
    FakeBleakClient.instances = []
    FakeBleakClient.fail_notify = None
    monkeypatch.setitem(sys.modules, "bleak", module)
    ble._reset()
    yield scanned
    FakeBleakClient.fail_notify = None


def test_the_bleak_link_connects_subscribes_and_lets_go(fake_bleak):
    assert ble.is_supported()
    link = ble.link_for("C4:BE:84:12:34:56", protocol.SERVICE, protocol.CHARACTERISTIC)
    assert isinstance(link, ble.BleakLink) and link.address == "C4:BE:84:12:34:56"
    got: list[bytes] = []
    link.open(got.append)
    client = FakeBleakClient.instances[-1]
    assert client.address == "C4:BE:84:12:34:56"
    assert client.calls == ["connect", "start_notify"]
    assert client.notify_uuid == "e7add780-b042-4876-aae1-112855353cc1"
    assert client.loop_thread == "oventracker-ble"  # bleak runs on its own loop, not the caller's thread
    # bleak hands each value over as a bytearray, with the characteristic; the link passes on bytes.
    client.callback(object(), bytearray(b"\xf2\x17"))
    client.callback(object(), bytearray(b"\x84"))
    assert got == [b"\xf2\x17", b"\x84"] and all(type(g) is bytes for g in got)
    link.dispose()
    assert client.calls[2:] == ["stop_notify e7add780-b042-4876-aae1-112855353cc1", "disconnect"]
    link.dispose()  # twice is harmless
    assert len(client.calls) == 4


def test_the_meter_through_the_bleak_link(fake_bleak):
    meter = Eevblog121GwMeter(ble.link_for("C4:BE:84:12:34:56", protocol.SERVICE, protocol.CHARACTERISTIC), timeout=2)

    def feeder():
        while not FakeBleakClient.instances or FakeBleakClient.instances[-1].callback is None:
            time.sleep(0.005)
        whole = packet(5, 0x20, 2345)
        FakeBleakClient.instances[-1].callback(None, bytearray(whole[:10]))
        FakeBleakClient.instances[-1].callback(None, bytearray(whole[10:]))

    t = threading.Thread(target=feeder)
    t.start()
    assert meter.connect().serial == "17842121"
    t.join()
    assert meter.read().celsius == pytest.approx(234.5)
    meter.dispose()
    assert FakeBleakClient.instances[-1].calls[-1] == "disconnect"


def test_a_link_that_fails_to_subscribe_disconnects_and_says_why(fake_bleak):
    FakeBleakClient.fail_notify = BleakCharacteristicNotFoundError("e7add780")
    link = ble.link_for("C4:BE:84:12:34:56", protocol.SERVICE, protocol.CHARACTERISTIC)
    with pytest.raises(MeterException, match="doesn't offer the meter's GATT characteristic"):
        link.open(lambda data: None)
    assert FakeBleakClient.instances[-1].calls == ["connect", "start_notify", "disconnect"]
    FakeBleakClient.fail_notify = BleakDBusError("org.freedesktop.DBus.Error.AccessDenied", [])
    with pytest.raises(MeterException, match="bluetooth group"):
        link.open(lambda data: None)
    link.dispose()  # nothing open: nothing to undo
    assert FakeBleakClient.instances[-1].calls == ["connect", "start_notify", "disconnect"]


def test_scanning_lists_names_and_addresses(fake_bleak):
    fake_bleak["devices"] = [
        types.SimpleNamespace(name="121GW", address="c4:be:84:12:34:56"),
        types.SimpleNamespace(name=None, address="11:22:33:44:55:66"),
        types.SimpleNamespace(name="121GW", address="C4:BE:84:12:34:56"),  # the same one again
    ]
    assert ble.scan(1.5) == [ble.BleDevice("121GW", "C4:BE:84:12:34:56"), ble.BleDevice("", "11:22:33:44:55:66")]
    assert fake_bleak["timeouts"] == [1.5]
    assert ble.scan_error() is None
    # Discovery lists the 121GWs first, then the other named devices.
    fake_bleak["devices"] = [
        types.SimpleNamespace(name="MX Master", address="AA:BB:CC:DD:EE:FF"),
        types.SimpleNamespace(name=None, address="11:22:33:44:55:66"),
        types.SimpleNamespace(name="121GW", address="C4:BE:84:12:34:56"),
    ]
    found = Eevblog121GwDriver().discover()
    assert [(c.resource, c.usable, c.note, c.transport) for c in found] == [
        ("ble:C4:BE:84:12:34:56", True, "121GW nearby", "Bluetooth LE"),
        ("ble:AA:BB:CC:DD:EE:FF", False, "Bluetooth LE device nearby", "Bluetooth LE"),
    ]


def test_without_bluetooth_discovery_finds_nothing_and_says_why(fake_bleak):
    fake_bleak["error"] = BleakDBusError("org.freedesktop.DBus.Error.ServiceUnknown", [])
    driver = Eevblog121GwDriver()
    assert driver.discover() == []
    assert ble.scan_error() is not None and "BlueZ isn't running" in ble.scan_error()
    assert "BlueZ isn't running" in driver.not_found_help
    if sys.platform.startswith("linux"):
        assert "bluetoothctl" in driver.not_found_help and "bluetooth group" in driver.not_found_help


@pytest.fixture
def fake_links(monkeypatch):
    """The driver with bleak faked out: a scan that returns what's set, and links that stream a packet (or
    nothing, for an address in ``silent``)."""
    state: dict = {"scan": [], "scans": 0, "opened": [], "silent": set()}

    def scan(timeout: float = ble.SCAN_SECONDS):
        state["scans"] += 1
        return list(state["scan"])

    def link_for(address, service, characteristic):
        assert (service, characteristic) == (protocol.SERVICE, protocol.CHARACTERISTIC)
        state["opened"].append(address)
        return FakeLink(address, [] if address in state["silent"] else [packet(5, 0x20, 2000)])

    monkeypatch.setattr(ble, "is_supported", lambda: True)
    monkeypatch.setattr(ble, "scan", scan)
    monkeypatch.setattr(ble, "link_for", link_for)
    monkeypatch.setattr(Eevblog121GwMeter, "__init__", _short_timeout(Eevblog121GwMeter.__init__))
    return state


def _short_timeout(init):
    def wrapped(self, link, timeout: float = 0.2):
        init(self, link, timeout)

    return wrapped


def test_the_driver_finds_a_121gw(fake_links):
    driver = Eevblog121GwDriver()
    meter_a = ble.BleDevice("121GW", "C4:BE:84:12:34:56")
    meter_b = ble.BleDevice("121GW 2", "C4:BE:84:65:43:21")
    mouse = ble.BleDevice("MX Master", "AA:BB:CC:DD:EE:FF")
    fake_links["scan"] = [mouse, meter_a, meter_b]

    # By address: straight there, without a scan.
    r = driver.open(MeterRequest("ble:c4be84654321", None, 1))
    assert r.meter is not None and r.resource == "ble:C4:BE:84:65:43:21" and r.identity.model == "121GW"
    assert fake_links["scans"] == 0
    r.meter.dispose()

    # By name: from a scan.
    progress: list[str] = []
    r = driver.open(MeterRequest("ble:121gw 2", None, 1, progress.append))
    assert r.meter is not None and r.resource == "ble:C4:BE:84:65:43:21"
    assert fake_links["scans"] == 1 and progress[-1] == "121GW 2 C4:BE:84:65:43:21"
    r.meter.dispose()
    r = driver.open(MeterRequest("ble:Nokia", None, 1))
    assert r.meter is None and 'no Bluetooth LE device called or at "Nokia"' in r.error
    assert driver.open(MeterRequest("COM5", None, 1)).error.startswith("the 121GW connects by Bluetooth LE")

    # Left out: the one that answered last time, with no scan when it answers.
    fake_links["opened"].clear()
    fake_links["scans"] = 0
    r = driver.open(MeterRequest(None, "ble:C4:BE:84:65:43:21", 1))
    assert r.meter is not None and r.resource == "ble:C4:BE:84:65:43:21"
    assert fake_links["opened"] == ["C4:BE:84:65:43:21"] and fake_links["scans"] == 0
    r.meter.dispose()

    # When it doesn't, the 121GWs a scan finds, each once, never the mouse.
    fake_links["opened"].clear()
    fake_links["silent"] = {"C4:BE:84:65:43:21"}
    r = driver.open(MeterRequest(None, "ble:C4:BE:84:65:43:21", 1))
    assert r.meter is not None and r.resource == "ble:C4:BE:84:12:34:56"
    assert fake_links["opened"] == ["C4:BE:84:65:43:21", "C4:BE:84:12:34:56"]
    r.meter.dispose()
    fake_links["silent"] = {meter_a.address, meter_b.address}
    assert driver.open(MeterRequest(None, None, 1)) is None

    # Probing says what didn't answer, and leaves nothing open.
    p = driver.probe("ble:C4:BE:84:12:34:56")
    assert p.meter is None and p.identity is None and p.error.startswith("no data")
    fake_links["silent"] = set()
    p = driver.probe("ble:121GW")
    assert p.meter is None and p.identity.serial == "17842121" and p.error is None


def _run_app(*args: str, tmp_path: Path) -> subprocess.CompletedProcess[str]:
    """The app, with bleak hidden (a bleak.py that won't import comes first on the path) and a settings folder of
    its own, so no Bluetooth device is ever looked for."""
    hide = tmp_path / "no-bleak"
    hide.mkdir(exist_ok=True)
    (hide / "bleak.py").write_text('raise ImportError("hidden by the test")\n', encoding="utf-8")
    env = dict(os.environ)
    env["PYTHONPATH"] = os.pathsep.join([str(hide), str(SRC)])
    env["PYTHONIOENCODING"] = "utf-8"
    env["COLUMNS"] = "160"
    env["APPDATA"] = str(tmp_path)
    env["XDG_CONFIG_HOME"] = str(tmp_path)
    env.pop("FORCE_COLOR", None)
    return subprocess.run(
        [sys.executable, "-m", "oventracker", *args],
        stdin=subprocess.DEVNULL,
        capture_output=True,
        text=True,
        encoding="utf-8",
        env=env,
        timeout=120,
    )


def test_without_bleak_the_app_says_what_to_install(tmp_path: Path):
    pytest.importorskip("rich")
    p = _run_app("run", "--meter", "121gw", "--resource", "ble:121GW", "-y", "--unit", "F", tmp_path=tmp_path)
    out = p.stdout + p.stderr
    assert p.returncode == 1, out
    assert "No meter answered on ble:121GW: Bluetooth LE needs bleak: pip install bleak" in p.stdout
    assert p.stdout.count("pip install bleak") == 1  # said once, not again as the help
    assert "Traceback" not in out
    p = _run_app("run", "--meter", "121gw", "-y", "--unit", "F", tmp_path=tmp_path)
    assert p.returncode == 1 and "Traceback" not in p.stdout + p.stderr
    assert "No meter answered (looking for a EEVblog 121GW over Bluetooth LE)" in p.stdout
    assert "pip install bleak" in p.stdout
    p = _run_app("meters", "--meter", "121gw", tmp_path=tmp_path)
    assert p.returncode == 1 and "Traceback" not in p.stdout + p.stderr
    assert "121gw: nothing found (EEVblog 121GW over Bluetooth LE). Bluetooth LE needs bleak" in p.stdout
