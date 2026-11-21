"""The meters: the U1253B's protocol, --resource, the serial ports, the drivers and the remembered settings (the
C# app's ProtocolTests, ResourceSpecTests, PortFinderTests and MeterDriverTests)."""

from __future__ import annotations

import time
from datetime import datetime, timedelta, timezone

import pytest

import conftest  # noqa: F401  (puts python/src on the path)
from oventracker.analysis.setpoint_analyzer import CompletionReason, PointPhase, SetpointAnalyzer
from oventracker.meter import driver as drivers
from oventracker.meter import port_finder
from oventracker.meter import u1253b_protocol as protocol
from oventracker.meter.base import MeterIdentity, MeterReading, OvenSimulation, ReadingStatus, TemperatureMeter
from oventracker.meter.resource_spec import U1250, Handshake, Parity, ResourceSpec, SerialSettings, StopBits, Transport
from oventracker.meter.simulated import SimulatedDriver, SimulatedOven
from oventracker.meter.u1253b import U1253BDriver, U1253BMeter, check
from oventracker.probes import Sensor
from oventracker.session.app_settings import AppSettings
from oventracker.session.meter_poller import MeterPoller
from oventracker.units import OvenMode, TempUnit

NOW = datetime(1970, 1, 1, tzinfo=timezone.utc)


# The U1253B's protocol ----------------------------------------------------------------------------


def test_reads_the_configuration_a_u1253b_sends_in_temperature_mode():
    # Captured from a U1253B (fw V2.19) through a U1177A.
    c = protocol.parse_config('"TEMP:K CEL"')
    assert c.is_temperature
    assert c.unit == TempUnit.Celsius
    assert c.description == "TEMP:K CEL"
    assert c.sensor.name == "type K thermocouple"
    assert protocol.parse_config('"TEMP:K FAR"').unit == TempUnit.Fahrenheit
    assert protocol.parse_config('"T1:K CEL"').is_temperature
    assert protocol.parse_config('"TEMP:J CEL"').sensor.name == "type J thermocouple"
    assert protocol.parse_config('"VOLT:DC +5.000000E+00,+1.000000E-04"').sensor is None


@pytest.mark.parametrize("reply", ['"VOLT:DC +5.000000E+00,+1.000000E-04"', '"RES +5.000000E+02,+1.000000E-02"', '"FREQ"'])
def test_other_functions_are_not_temperature(reply):
    assert not protocol.parse_config(reply).is_temperature


def test_converts_readings_to_celsius():
    cel = protocol.parse_config('"TEMP:K CEL"')
    far = protocol.parse_config('"TEMP:K FAR"')
    r = protocol.to_reading(NOW, "+1.65446000E+02", cel)
    assert r.status == ReadingStatus.Ok
    assert r.celsius == pytest.approx(165.446, abs=1e-6)
    assert r.raw == "+1.65446000E+02"
    assert protocol.to_reading(NOW, "+3.50000000E+02", far).celsius == pytest.approx(176.6667, abs=1e-3)


def test_an_open_thermocouple_is_overload_not_a_reading():
    cel = protocol.parse_config('"TEMP:K CEL"')
    assert protocol.to_reading(NOW, "+9.90000000E+37", cel).status == ReadingStatus.Overload
    assert protocol.to_reading(NOW, "-9.90000000E+37", cel).status == ReadingStatus.Overload
    assert protocol.to_reading(NOW, "+9.90000000E+37", cel).celsius is None


def test_a_reading_in_the_wrong_mode_is_not_used():
    volts = protocol.parse_config('"VOLT:DC +5.000000E+00,+1.000000E-04"')
    assert protocol.to_reading(NOW, "+1.20000000E+00", volts).status == ReadingStatus.NotTemperature
    assert protocol.to_reading(NOW, "garbage", volts).status == ReadingStatus.Error


def test_parses_the_identity():
    i = protocol.parse_identity("Agilent Technologies,U1253B,MY50490064,V2.19")
    assert (i.manufacturer, i.model, i.serial, i.firmware) == ("Agilent Technologies", "U1253B", "MY50490064", "V2.19")
    assert protocol.parse_identity("Agilent").model == "?"


@pytest.mark.parametrize(
    "line,is_event", [("*0", True), ("*7", True), ("*L", True), ("*E", False), ("+1.0E+00", False), ('"TEMP:K CEL"', False)]
)
def test_tells_events_from_replies(line, is_event):
    assert protocol.is_event(line) == is_event


class _FakePort:
    """A serial port that answers the U1253B's commands from a script."""

    def __init__(self, replies: dict[str, list[str]]):
        self.replies = {k: list(v) for k, v in replies.items()}
        self.buffer = b""
        self.written: list[str] = []

    @property
    def in_waiting(self) -> int:
        return len(self.buffer)

    def reset_input_buffer(self) -> None:
        self.buffer = b""

    def write(self, data: bytes) -> None:
        command = data.decode("ascii").strip()
        self.written.append(command)
        queue = self.replies.get(command)
        if queue:
            self.buffer += queue.pop(0).encode("ascii")

    def read_until(self, end: bytes) -> bytes:
        i = self.buffer.find(end)
        if i < 0:
            data, self.buffer = self.buffer, b""
            return data  # a timeout: no whole line
        data, self.buffer = self.buffer[: i + 1], self.buffer[i + 1 :]
        return data

    def close(self) -> None:
        pass


def test_the_meter_skips_events_and_rereads_its_configuration():
    meter = U1253BMeter("COM5")
    port = _FakePort(
        {
            "CONF?": ['"TEMP:K CEL"\r\n', '"TEMP:K FAR"\r\n'],
            "FETC?": ["*3\r\n+1.80873000E+02\r\n", "+3.50000000E+02\r\n"],
        }
    )
    meter._port = port
    meter.read_config()
    r = meter.read()
    # The dial moved (*3) during the query: as in the C# app (#41), the configuration is read again straight away
    # and the reply judged by it (now °F), rather than lost as "not temperature".
    assert port.written == ["CONF?", "FETC?", "CONF?"]
    assert r.status == ReadingStatus.Ok and r.raw == "+1.80873000E+02"
    assert r.celsius == pytest.approx((180.873 - 32) / 1.8, abs=1e-3)
    assert meter.last_config is not None
    r2 = meter.read()
    assert port.written.count("CONF?") == 2  # not again: the configuration is known
    assert r2.celsius == pytest.approx(176.6667, abs=1e-3)


def test_a_reading_after_an_event_is_judged_by_the_new_configuration():
    # The dial turned from temperature to volts during the query: the reply is then not a temperature.
    meter = U1253BMeter("COM5")
    port = _FakePort({"CONF?": ['"TEMP:K CEL"\r\n', '"VOLT:DC +5.000000E+00,+1.000000E-04"\r\n'], "FETC?": ["*1\r\n+1.23400000E+00\r\n"]})
    meter._port = port
    meter.read_config()
    r = meter.read()
    assert port.written == ["CONF?", "FETC?", "CONF?"]
    assert r.status == ReadingStatus.NotTemperature


def test_a_missing_reply_is_a_timeout():
    meter = U1253BMeter("COM5")
    meter._port = _FakePort({})
    with pytest.raises(TimeoutError):
        meter.read_config()


# --resource ---------------------------------------------------------------------------------------


@pytest.mark.parametrize("text", ["COM5", "/dev/rfcomm0", "/dev/ttyUSB0", "\\\\.\\COM10"])
def test_a_serial_port_on_its_own_uses_the_drivers_settings(text):
    r = ResourceSpec.parse(text)
    assert r.transport == Transport.Serial and r.address == text and r.settings is None and not r.visa
    assert r.settings_or(U1250) == U1250


def test_serial_settings_can_be_given_with_the_port():
    r = ResourceSpec.parse("COM3:9600,E,7,2,dtr")
    assert r.address == "COM3"
    assert r.settings == SerialSettings(9600, Parity.Even, 7, StopBits.Two)
    assert r.dtr_dsr
    linux = ResourceSpec.parse("/dev/ttyUSB0:19200,N,8,1,rts")
    assert linux.address == "/dev/ttyUSB0"
    assert linux.settings == SerialSettings(19200, Parity.None_, 8, StopBits.One, Handshake.RequestToSend)
    assert not linux.dtr_dsr
    assert ResourceSpec.parse("COM1:4800,O,7,1.5").settings.stop_bits == StopBits.OnePointFive


@pytest.mark.parametrize(
    "text,visa", [("GPIB0::22::INSTR", "GPIB0::22::INSTR"), ("GPIB::22", "GPIB0::22::INSTR"), ("gpib:22", "GPIB0::22::INSTR"), ("GPIB0::22::96::INSTR", "GPIB0::22::96::INSTR")]
)
def test_gpib_addresses_become_visa_resources(text, visa):
    r = ResourceSpec.parse(text)
    assert r.transport == Transport.Gpib and r.address == visa and r.visa


@pytest.mark.parametrize("text", ["", "COM3:fast", "COM3:9600,X,8,1", "COM3:9600,N,9,1", "GPIB0::31::INSTR"])
def test_mistakes_are_rejected(text):
    assert ResourceSpec.parse(text) is None


def test_settings_read_back_as_they_are_written():
    assert str(SerialSettings(9600, Parity.Even, 7, StopBits.Two)) == "9600,E,7,2"
    assert str(U1250) == "9600,N,8,1"
    s = SerialSettings(2400, Parity.Odd, 7, StopBits.One, Handshake.XOnXOff)
    assert ResourceSpec.parse(f"COM1:{s}").settings == s


def test_the_u1253b_only_connects_through_a_serial_port():
    assert check("COM5") is None
    assert check("/dev/rfcomm0:9600,N,8,1") is None
    assert "not GPIB" in check("GPIB0::22::INSTR")
    assert "isn't a serial port" in check("COM5:oops")
    r = U1253BDriver().probe("GPIB0::22::INSTR")
    assert r.identity is None and "serial port" in r.error


def test_a_meter_on_unusual_settings_remembers_them():
    assert U1253BMeter("COM5").location == "COM5"
    assert U1253BMeter("COM5", SerialSettings(19200)).location == "COM5:19200,N,8,1"


def test_run_rejects_a_resource_it_cant_read():
    from oventracker.commands.run import RunSettings

    error = RunSettings(resource="GPIB0::22::INSTR:x").validate()
    assert error is not None and "GPIB0::22::INSTR" in error
    assert RunSettings(resource="COM5:9600,N,8,1").validate() is None


# Serial ports -------------------------------------------------------------------------------------


def _port(name: str, bluetooth: bool = False) -> port_finder.PortInfo:
    return port_finder.PortInfo(name, "x", bluetooth, bluetooth, None)


def test_ports_sort_bluetooth_first_then_by_number():
    s = port_finder.sort([_port("COM10"), _port("COM3"), _port("COM5", True), _port("COM2")])
    assert [p.port for p in s] == ["COM5", "COM2", "COM3", "COM10"]
    s = port_finder.sort(
        [_port("/dev/ttyUSB10"), _port("/dev/ttyS0"), _port("/dev/ttyUSB2"), _port("/dev/rfcomm1", True), _port("/dev/rfcomm0", True), _port("/dev/ttyACM0")]
    )
    assert [p.port for p in s] == ["/dev/rfcomm0", "/dev/rfcomm1", "/dev/ttyACM0", "/dev/ttyS0", "/dev/ttyUSB2", "/dev/ttyUSB10"]


@pytest.mark.parametrize(
    "path,product,bluetooth,description",
    [
        ("/dev/rfcomm0", None, True, "Bluetooth serial link (RFCOMM)"),
        ("/dev/ttyUSB0", "U1173B", False, "U1173B"),
        ("/dev/ttyUSB1", None, False, "USB serial adapter"),
        ("/dev/ttyACM0", None, False, "USB serial device"),
        ("/dev/ttyS0", None, False, "Serial port"),
    ],
)
def test_linux_devices_are_described_by_kind(path, product, bluetooth, description):
    p = port_finder.describe_linux(path, product)
    assert p.is_bluetooth_outgoing == bluetooth and p.description == description


def test_listing_the_ports_never_fails():
    assert isinstance(port_finder.list_ports(), list)


# Drivers ------------------------------------------------------------------------------------------


class BareMeter(TemperatureMeter):
    """The least a meter can be: no battery, no beep, no way to say what it is set to."""

    def __init__(self, oven: SimulatedOven):
        self.oven = oven
        self.clock = NOW
        self.connects = 0

    @property
    def location(self) -> str:
        return "nowhere"

    def connect(self) -> MeterIdentity:
        self.connects += 1
        return MeterIdentity("Test", "Bare", "1", "1")

    def disconnect(self) -> None:
        pass

    def read(self) -> MeterReading:
        self.oven.step(1)
        self.clock += timedelta(seconds=1)
        return MeterReading(self.clock, ReadingStatus.Ok, self.oven.probe_c(), "")


def test_a_meter_with_only_the_required_members_measures_a_setpoint():
    oven = SimulatedOven(5)
    oven.set(OvenMode.Bake, 180)
    meter = BareMeter(oven)
    assert meter.read_config() is None and meter.last_config is None and meter.read_battery_percent() is None
    meter.beep()  # does nothing, and doesn't throw
    poller = MeterPoller(meter, 1, speed=20_000, connected=False)
    poller.battery_every = 1
    poller.min_simulated_period = 0  # as fast as it goes: no tracker here to keep pace with
    poller.enqueue(lambda m: m.beep())
    poller.start()
    a = SetpointAnalyzer(180)
    start = None
    deadline = time.monotonic() + 60
    try:
        while a.phase != PointPhase.Complete and time.monotonic() < deadline:
            r = poller.readings.try_read()
            if r is None:
                time.sleep(0.001)
                continue
            start = start or r.time
            a.add((r.time - start).total_seconds(), r.celsius)
    finally:
        poller.stop()
    assert meter.connects == 1
    assert poller.battery_percent is None and poller.config is None
    assert a.reason == CompletionReason.Converged
    bias = SimulatedOven.bias_c(OvenMode.Bake, 180)
    assert bias - 3 <= a.metrics().offset_c <= bias + 3


@pytest.mark.parametrize("name,kind", [("u1253b", U1253BDriver), ("U1253B", U1253BDriver), ("simulated", SimulatedDriver)])
def test_drivers_are_found_by_name(name, kind):
    assert isinstance(drivers.find(name), kind)


def test_every_meter_is_in_the_python_version_now():
    for name in ("121gw", "121GW", "ut60bt", "UT60BT"):
        d = drivers.find(name)
        assert d is not None and not d.not_ported
    assert drivers.names() == "u1253b, 34401a, 121gw, ut60bt, simulated"
    assert not any(d.not_ported for d in drivers.all_drivers())
    # A meter the C# app gets first still has a way to say so.
    later = drivers.NotPortedDriver("fluke87", "Fluke 87 over IR", "COM5")
    assert later.not_ported and "isn't in the Python version yet" in later.not_ported_message
    assert later.open(drivers.MeterRequest(None, None, 1)).error == later.not_ported_message


def test_an_unknown_driver_is_not_found():
    assert drivers.find("fluke87") is None
    assert drivers.find(None) is None
    assert any(d.name == drivers.DEFAULT_NAME for d in drivers.all_drivers())


def test_the_driver_is_the_one_asked_for_then_the_last_one_then_the_default():
    from oventracker.commands.run import RunSettings, choose_driver

    none = AppSettings()
    assert choose_driver(RunSettings(), none).name == drivers.DEFAULT_NAME
    assert choose_driver(RunSettings(simulate=True), none).name == "simulated"
    assert choose_driver(RunSettings(meter="simulated"), none).name == "simulated"
    simulated_last = AppSettings()
    simulated_last.remember("simulated", "simulator")
    assert choose_driver(RunSettings(), simulated_last).name == drivers.DEFAULT_NAME  # not a simulation again
    ble_last = AppSettings()
    ble_last.remember("121gw", "ble:121GW")  # in both apps now
    assert choose_driver(RunSettings(), ble_last).name == "121gw"
    bench_last = AppSettings()
    bench_last.remember("34401a", "GPIB0::22::INSTR")  # in both apps now
    assert choose_driver(RunSettings(), bench_last).name == "34401a"


def test_settings_from_before_drivers_still_find_the_serial_port(tmp_path):
    old = AppSettings(last_port="COM7")
    assert old.last_resource_for("u1253b") == "COM7"
    assert old.last_resource_for("simulated") is None
    old.remember("u1253b", "COM9")
    assert old.last_resource_for("u1253b") == "COM9"
    assert old.last_port is None
    # The same file as the C# app's: PascalCase, nulls left out.
    old.remember_auto_power_off_turned_off("u1253b:MY1")
    old.remember_meter_options("34401a", {"cj": "22C"})
    path = tmp_path / "settings.json"
    old.save(str(path))
    assert path.read_text(encoding="utf-8") == (
        '{"LastMeter":"u1253b","LastResource":"COM9","AutoPowerOffTurnedOff":["u1253b:MY1"],"MeterOptions":{"34401a":{"cj":"22C"}}}'
    )
    back = AppSettings.load(str(path))
    assert back.last_resource_for("u1253b") == "COM9"
    assert back.is_auto_power_off_turned_off("U1253B:my1")
    assert back.meter_options_for("34401a") == {"cj": "22C"}


def test_the_simulator_is_a_driver_of_its_own():
    from oventracker.meter.driver import MeterRequest

    d = drivers.find("simulated")
    assert d.is_simulation
    r = d.open(MeterRequest(None, None, 1))
    assert isinstance(r.meter, OvenSimulation)
    assert r.meter.read_config().sensor.name == "type K thermocouple"
    assert r.meter.read().status == ReadingStatus.Ok


@pytest.mark.parametrize("kind,c,expected", [("K", 232.2, 2.2), ("K", 400, 3.0), ("T", 100, 1.0), ("E", 400, 2.0)])
def test_sensor_tolerances_follow_the_standard_limits(kind, c, expected):
    assert Sensor.thermocouple(kind).tolerance_c(c) == pytest.approx(expected, abs=1e-3)


def test_meter_options_are_key_value():
    assert drivers.parse_options(["cj=22C", "nplc=100, X=1"]) == {"cj": "22C", "nplc": "100", "x": "1"}
    assert drivers.parse_options(["cj"]) is None
    assert drivers.parse_options(None) == {}
    assert U1253BDriver().check_options({"cj": "x"}) == "The u1253b meter takes no --meter-option."
