"""The 34401A with a type K thermocouple: the type K conversion, the meter against a fake SCPI port, the driver, its
options, and the meter options a run records (the C# app's TypeKTests and Hp34401ATests)."""

from __future__ import annotations

import io
import json
import sys
import threading
import types
from collections import deque
from datetime import datetime, timezone
from typing import Callable

import pytest

import conftest  # noqa: F401  (puts python/src on the path)
from oventracker import jsonio
from oventracker.analysis.setpoint_analyzer import AnalysisOptions
from oventracker.jsonio import DateTimeOffset
from oventracker.meter import driver as drivers
from oventracker.meter import hp34401a, visa
from oventracker.meter.base import MeterException, ReadingStatus
from oventracker.meter.driver import MeterRequest
from oventracker.meter.hp34401a import (
    ASSUMED_COLD_JUNCTION_NOTE,
    FACTORY_SERIAL,
    Hp34401ADriver,
    Hp34401AMeter,
    check,
    is_34401a,
    parse_temperature_c,
    port_for,
    to_reading,
)
from oventracker.meter.resource_spec import ResourceSpec
from oventracker.meter.scpi_port import ScpiPort, SerialScpiPort, VisaScpiPort
from oventracker.meter.thermocouple import TypeK
from oventracker.probes import Sensor
from oventracker.session.app_settings import AppSettings
from oventracker.session.recording import SessionInfo
from oventracker.units import TempUnit

EPOCH = datetime(1970, 1, 1, tzinfo=timezone.utc)


# Type K -------------------------------------------------------------------------------------------


# NIST ITS-90 type K reference table, reference junction at 0 °C (mV at °C).
@pytest.mark.parametrize(
    "c,mv",
    [(-200, -5.891), (-50, -1.889), (0, 0.000), (25, 1.000), (100, 4.096), (200, 8.138), (260, 10.561), (300, 12.209),
     (500, 20.644), (1000, 41.276), (1372, 54.886)],
)
def test_matches_the_nist_table(c, mv):
    assert round(TypeK.millivolts_at(c), 3) == pytest.approx(mv, abs=1e-9)
    assert round(TypeK.temperature_for(mv, 0), 1) == pytest.approx(c, abs=1e-9)  # the table is rounded to 1 µV, about 0.03 °C


@pytest.mark.parametrize("hot,cold_junction", [(176.67, 23.0), (232.22, 18.5), (-10.0, 22.0), (500.0, 0.0)])
def test_the_cold_junction_is_added_back(hot, cold_junction):
    # The meter sees only the difference between the two junctions.
    measured = TypeK.millivolts_at(hot) - TypeK.millivolts_at(cold_junction)
    assert round(TypeK.temperature_for(measured, cold_junction), 6) == pytest.approx(hot, abs=1e-9)


def test_a_cold_junction_error_shows_in_the_reading():
    # Assuming 23 °C when the room is 28 °C reads about 5 °C low: the reason cj matters.
    measured = TypeK.millivolts_at(200) - TypeK.millivolts_at(28)
    assert round(TypeK.temperature_for(measured, 23)) == 195


@pytest.mark.parametrize("mv", [60.0, -8.0])  # -8 mV, with the cold junction at 23 °C: below −270 °C
def test_beyond_type_k_is_no_temperature(mv):
    assert TypeK.temperature_for(mv, 23) is None


# The meter, against a fake SCPI port --------------------------------------------------------------


def volts_at(hot_c: float, cold_junction_c: float) -> str:
    """The 34401A's reply at a temperature, with the cold junction at another: volts, as READ? gives them."""
    return f"{(TypeK.millivolts_at(hot_c) - TypeK.millivolts_at(cold_junction_c)) / 1000:+.8E}"


class FakeScpi(ScpiPort):
    """A 34401A that answers from a table, and remembers what it was sent."""

    def __init__(self, serial: bool = False):
        self.serial = serial
        self.sent: list[str] = []
        self.replies: dict[str, Callable[[], str]] = {
            "*IDN?": lambda: "HEWLETT-PACKARD,34401A,0,11-5-2",
            "*OPC?": lambda: "1",
            "SYST:ERR?": lambda: '+0,"No error"',
            "CONF?": lambda: '"VOLT +1.000000E-01,+3.000000E-07"',
            "READ?": lambda: volts_at(200, 23),
        }
        self._pending: deque[str] = deque()
        self.released = False

    @property
    def location(self) -> str:
        return "COM3:9600,E,7,2" if self.serial else "GPIB0::22::INSTR"

    @property
    def is_serial(self) -> bool:
        return self.serial

    def open(self) -> None:
        pass

    def write_line(self, command: str) -> None:
        self.sent.append(command)
        reply = self.replies.get(command)
        if reply is not None:
            self._pending.append(reply())

    def read_line(self) -> str:
        if not self._pending:
            raise TimeoutError()
        return self._pending.popleft()

    def clear(self) -> None:
        self._pending.clear()

    def release(self) -> None:
        self.released = True

    def dispose(self) -> None:
        pass


def test_sets_up_dc_millivolts_and_reads_a_type_k_temperature():
    port = FakeScpi()
    with Hp34401AMeter(port, cold_junction_c=23) as meter:
        identity = meter.connect()
        assert identity.model == "34401A"
        assert identity.manufacturer == "HEWLETT-PACKARD"
        assert "*RST" in port.sent
        assert "CONF:VOLT:DC 0.1" in port.sent
        assert "VOLT:DC:NPLC 10" in port.sent
        assert "INP:IMP:AUTO ON" in port.sent  # > 10 GΩ, so the thermocouple isn't loaded
        assert "SYST:REM" not in port.sent  # GPIB doesn't need it

        r = meter.read()
        assert r.status == ReadingStatus.Ok
        assert round(r.celsius, 2) == 200
        assert r.raw == volts_at(200, 23)
        assert meter.last_config.is_temperature
        # A generic type K: --probe says which probe, as for any meter.
        assert meter.last_config.sensor == Sensor.thermocouple("K")
        assert meter.last_config.sensor.is_generic_type
        assert "cold junction 23.0 °C" in meter.last_config.description

        meter.disconnect()
        assert port.released


def test_the_setup_commands_are_the_c_sharp_apps_in_order():
    port = FakeScpi()
    Hp34401AMeter(port, 22.2, nplc=0.02).connect()
    assert port.sent == [
        "*IDN?", "*RST", "*CLS", "CONF:VOLT:DC 0.1", "VOLT:DC:NPLC 0.02", "INP:IMP:AUTO ON", "ZERO:AUTO ON", "*OPC?", "SYST:ERR?", "CONF?",
    ]


def test_puts_it_in_remote_first_over_rs232():
    port = FakeScpi(serial=True)
    with Hp34401AMeter(port, 23) as meter:
        meter.connect()
        assert port.sent[0] == "SYST:REM"


def test_leaves_another_instrument_alone():
    port = FakeScpi()
    port.replies["*IDN?"] = lambda: "HEWLETT-PACKARD,3478A,0,1"
    with Hp34401AMeter(port, 23) as meter:
        assert not is_34401a(meter.connect())
        assert port.sent == ["*IDN?"]
        assert port.released  # given its front panel back


def test_a_rejected_setup_is_an_error():
    port = FakeScpi()
    port.replies["SYST:ERR?"] = lambda: '-113,"Undefined header"'
    with Hp34401AMeter(port, 23) as meter:
        with pytest.raises(MeterException, match="Undefined header"):
            meter.connect()


def test_an_overload_is_open_and_beyond_type_k_is_an_error():
    assert to_reading(EPOCH, "+9.90000000E+37", 23).status == ReadingStatus.Overload
    assert to_reading(EPOCH, "+6.00000000E-02", 23).status == ReadingStatus.Error  # 60 mV
    assert to_reading(EPOCH, "garbage", 23).status == ReadingStatus.Error
    assert to_reading(EPOCH, "NaN", 23).status == ReadingStatus.Error  # not a temperature, even though it parses


@pytest.mark.parametrize("conf", ['"VOLT:AC +1.000000E+01,+3.000000E-06"', '"FRES +1.000000E+03,+1.000000E-04"'])
def test_notices_when_someone_changes_the_function(conf):
    port = FakeScpi()
    with Hp34401AMeter(port, 23) as meter:
        meter.config_every = 2
        meter.connect()
        port.replies["CONF?"] = lambda: conf
        meter.read()
        r = meter.read()
        assert r.status == ReadingStatus.NotTemperature
        assert not meter.last_config.is_temperature


def test_is_a_bench_meter():
    driver = drivers.find("34401a")
    assert isinstance(driver, Hp34401ADriver)
    assert not driver.not_ported
    assert driver.auto_power_off is None
    with Hp34401AMeter(FakeScpi(), 23) as meter:
        assert meter.read_battery_percent() is None


# The driver ---------------------------------------------------------------------------------------


def test_only_gpib_and_serial_are_its_connections():
    assert "USB, LAN or Bluetooth" in check("USB0::0x0957::0x0607::MY1::INSTR")
    assert check("COM3") is None
    assert check("COM3:9600,N,8,2") is None
    if not visa.is_available():
        assert "VISA isn't installed" in check("GPIB0::22::INSTR")
    else:
        assert check("GPIB0::22::INSTR") is None


def test_without_an_address_it_doesnt_go_looking():
    assert Hp34401ADriver().open(MeterRequest(None, None, 1)) is None


@pytest.mark.parametrize("text,c", [("22C", 22.0), ("22.5 °C", 22.5), ("72F", 22.222222222222221), ("0c", 0.0)])
def test_reads_the_cold_junction_with_its_unit(text, c):
    assert parse_temperature_c(text) == pytest.approx(c, abs=1e-9)


@pytest.mark.parametrize("text", ["22", "350F", "warm"])  # no unit (22 °F or 22 °C?); an oven, not a room
def test_refuses_a_cold_junction_it_cant_be_sure_of(text):
    assert parse_temperature_c(text) is None


def test_checks_its_options():
    d = Hp34401ADriver()
    assert d.check_options({"cj": "22C", "nplc": "100"}) is None
    assert "cj=22" in d.check_options({"cj": "22"})
    assert "nplc=3" in d.check_options({"nplc": "3"})
    # The PT100 setting is gone.
    assert "no --meter-option rtd" in d.check_options({"rtd": "pt100"})
    assert "takes no --meter-option" in drivers.find("u1253b").check_options({"cj": "22C"})
    assert [o.key for o in d.options] == ["cj", "nplc"]


def test_its_messages_are_the_c_sharp_apps():
    d = Hp34401ADriver()
    assert d.check_options({"cj": "warm"}) == "--meter-option cj=warm: give the cold junction's temperature with its unit, e.g. cj=22C or cj=72F."
    assert d.check_options({"nplc": "3"}) == "--meter-option nplc=3: the 34401A takes 0.02, 0.2, 1, 10, 100."
    assert d.check_options({"range": "1"}) == "The 34401A has no --meter-option range; it takes cj and nplc."
    assert d.description == "HP/Agilent 34401A with a type K thermocouple (DC mV), through GPIB or RS-232"
    assert d.not_temperature_help == "The 34401A isn't on DC volts: press Local (Shift) and let the app set it up again, or restart the run."


def test_says_when_the_cold_junction_is_assumed():
    assert "23 °C" in ASSUMED_COLD_JUNCTION_NOTE
    assert "cj=" in ASSUMED_COLD_JUNCTION_NOTE
    assert ASSUMED_COLD_JUNCTION_NOTE == (
        "The cold junction (where the thermocouple meets the meter's terminals) is taken as 23 °C, a typical room: every reading "
        "is off by as much as it differs. Give the room's temperature with --meter-option cj=22C (or cj=72F); it's remembered."
    )


@pytest.fixture
def fake_link(monkeypatch):
    """The driver's ports are fakes: each one made is kept, with the timeout it was given."""
    made: list[tuple[FakeScpi, float]] = []

    def fake_port_for(spec: ResourceSpec, timeout_s: float) -> ScpiPort:
        port = FakeScpi(serial=not spec.visa)
        made.append((port, timeout_s))
        return port

    monkeypatch.setattr(hp34401a, "port_for", fake_port_for)
    return made


def test_opening_notes_an_assumed_cold_junction_and_uses_a_given_one(fake_link):
    d = Hp34401ADriver()
    tried: list[str] = []
    r = d.open(MeterRequest("COM3", None, 1, tried.append))
    assert r.meter is not None and r.error is None
    assert r.notes == [ASSUMED_COLD_JUNCTION_NOTE]
    assert r.meter.cold_junction_c == 23
    assert tried == ["COM3"]
    assert fake_link[-1][1] == 5  # long enough for a reading at 10 NPLC
    r.meter.dispose()

    r = d.open(MeterRequest("COM3", None, 1, options={"cj": "72F", "nplc": "100"}))
    assert r.notes is None
    assert r.meter.cold_junction_c == pytest.approx(22.222222222222221, abs=0)
    assert "VOLT:DC:NPLC 100" in fake_link[-1][0].sent
    assert fake_link[-1][1] == 11
    r.meter.dispose()

    # Where it answered last time, when none is given.
    assert d.open(MeterRequest(None, "COM4", 1)).meter is not None


def test_probing_only_asks_what_it_is(fake_link):
    r = Hp34401ADriver().probe("COM3")
    assert r.identity.model == "34401A" and r.error is None and r.meter is None
    port, timeout = fake_link[-1]
    assert port.sent == ["SYST:REM", "*IDN?"] and port.released and timeout == 3
    assert "no reply" in hp34401a.explain(TimeoutError())


def test_another_instrument_is_not_a_34401a(fake_link):
    def other(spec, timeout_s):
        port = FakeScpi(serial=True)
        port.replies["*IDN?"] = lambda: "HEWLETT-PACKARD,3478A,0,1"
        return port

    hp34401a.port_for = other  # restored by fake_link's monkeypatch
    r = Hp34401ADriver().open(MeterRequest("COM3", None, 1))
    assert r.meter is None and r.error == "not a 34401A"


def test_parses_meter_options():
    o = drivers.parse_options(["CJ=22C", "nplc=100,x=1"])
    assert o["cj"] == "22C"
    assert o["nplc"] == "100"
    assert o["x"] == "1"
    assert drivers.parse_options(None) == {}
    assert drivers.parse_options(["cj"]) is None
    assert drivers.parse_options(["=1"]) is None


def test_remembers_options_per_meter():
    s = AppSettings()
    s.remember_meter_options("34401a", {"cj": "22C"})
    assert s.meter_options_for("34401a")["cj"] == "22C"
    assert s.meter_options_for("u1253b") == {}
    s.remember_meter_options("34401a", {})
    assert s.meter_options_for("34401a") == {}


def test_its_serial_defaults_are_the_factorys():
    assert str(FACTORY_SERIAL) == "9600,E,7,2"
    port = port_for(ResourceSpec.parse("COM3"), 1)
    assert isinstance(port, SerialScpiPort)
    assert port.location == "COM3:9600,E,7,2"
    assert isinstance(port_for(ResourceSpec.parse("gpib:22"), 1), VisaScpiPort)
    assert port_for(ResourceSpec.parse("gpib:22"), 1).location == "GPIB0::22::INSTR"


def test_the_serial_port_holds_dtr_at_the_factory_settings():
    serial = pytest.importorskip("serial")
    from oventracker.meter.resource_spec import open_serial

    p = open_serial("COM3", FACTORY_SERIAL, dtr_dsr=True)
    assert (p.baudrate, p.parity, p.bytesize, p.stopbits) == (9600, serial.PARITY_EVEN, 7, serial.STOPBITS_TWO)
    assert p.dtr and not p.rtscts and not p.xonxoff


def test_a_serial_port_that_isnt_there_says_so_as_the_c_sharp_app_does():
    pytest.importorskip("serial")
    port = SerialScpiPort("COM99" if sys.platform == "win32" else "/dev/ttyNOPE99", FACTORY_SERIAL, 1)
    with pytest.raises(MeterException) as e:
        port.open()
    if sys.platform == "win32":
        assert str(e.value) == "Could not open COM99: Could not find file 'COM99'."


# VISA, without PyVISA and through a fake one ------------------------------------------------------


@pytest.fixture
def no_pyvisa(monkeypatch):
    monkeypatch.setitem(sys.modules, "pyvisa", None)  # import pyvisa raises ImportError
    visa._reset()
    yield
    visa._reset()


def test_without_pyvisa_gpib_says_what_to_install(no_pyvisa):
    assert not visa.is_available()
    problem = check("GPIB0::22::INSTR")
    assert problem.startswith("VISA isn't installed")
    assert "pip install pyvisa" in problem
    assert visa.find("GPIB?*INSTR") == []
    assert visa.missing_help() in Hp34401ADriver().not_found_help
    r = Hp34401ADriver().open(MeterRequest("GPIB0::22::INSTR", None, 1))
    assert r.meter is None and r.error == problem
    # The serial ports are still listed, but not asked unless --all.
    assert all(not c.usable and c.transport == "Serial" for c in Hp34401ADriver().discover())


class _VisaIOError(Exception):
    def __init__(self, code: int):
        super().__init__(f"VI_ERROR ({code})")
        self.error_code = code


class _FakeInstrument:
    def __init__(self, replies: list[bytes | Exception]):
        self.replies = replies
        self.written: list[bytes] = []
        self.timeout: float | None = None
        self.cleared = False
        self.closed = False
        self.session = 7
        self.ren: list[int] = []
        lib = types.SimpleNamespace()
        lib.status_description = lambda session, code: ("VI_ERROR_NLISTENERS: No listeners condition is detected.", code)
        lib.gpib_control_ren = lambda session, mode: self.ren.append(mode)
        self.visalib = lib

    def write_raw(self, data: bytes) -> int:
        self.written.append(data)
        return len(data)

    def read_raw(self) -> bytes:
        r = self.replies.pop(0)
        if isinstance(r, Exception):
            raise r
        return r

    def clear(self) -> None:
        self.cleared = True

    def close(self) -> None:
        self.closed = True


@pytest.fixture
def fake_pyvisa(monkeypatch):
    instruments: dict[str, _FakeInstrument] = {}

    class ResourceManager:
        def list_resources(self, expression: str):
            return tuple(instruments)

        def open_resource(self, resource: str, open_timeout: int = 0):
            if resource not in instruments:
                raise _VisaIOError(visa.ERROR_RESOURCE_NOT_FOUND)
            return instruments[resource]

    module = types.ModuleType("pyvisa")
    module.ResourceManager = ResourceManager  # type: ignore[attr-defined]
    module.errors = types.SimpleNamespace(VisaIOError=_VisaIOError)  # type: ignore[attr-defined]
    monkeypatch.setitem(sys.modules, "pyvisa", module)
    visa._reset()
    yield instruments
    visa._reset()


def test_a_gpib_meter_through_pyvisa(fake_pyvisa):
    inst = _FakeInstrument([b"HEWLETT-PACKARD,34401A,0,11-5-2\n", b"\xb0C\r\n", _VisaIOError(visa.ERROR_TIMEOUT), _VisaIOError(-1073807265)])
    fake_pyvisa["GPIB0::22::INSTR"] = inst
    assert visa.is_available()
    assert check("GPIB0::22::INSTR") is None
    assert [c.resource for c in Hp34401ADriver().discover() if c.usable] == ["GPIB0::22::INSTR"]

    port = VisaScpiPort("GPIB0::22::INSTR", 5)
    port.open()
    assert inst.timeout == 5000
    port.clear()
    assert inst.cleared
    port.write_line("*IDN?")
    assert inst.written == [b"*IDN?\n"]
    assert port.read_line() == "HEWLETT-PACKARD,34401A,0,11-5-2"
    assert port.read_line() == "?C"  # Encoding.ASCII: a byte above 127 is a question mark
    with pytest.raises(TimeoutError, match="VISA timed out trying to read from GPIB0::22::INSTR"):
        port.read_line()
    with pytest.raises(MeterException, match="Couldn't read from GPIB0::22::INSTR: VI_ERROR_NLISTENERS"):
        port.read_line()
    port.release()
    assert inst.ren == [6]  # go to local
    port.dispose()
    assert inst.closed

    with pytest.raises(MeterException, match="VISA found nothing at GPIB0::9::INSTR"):
        VisaScpiPort("GPIB0::9::INSTR", 5).open()


# The run's record of its options ------------------------------------------------------------------


def _info(**changes) -> SessionInfo:
    return SessionInfo(
        "test", DateTimeOffset(621355968000000000, 0), "HEWLETT-PACKARD 34401A", "GPIB0::22::INSTR", TempUnit.Fahrenheit, 8,
        AnalysisOptions(), [], **changes,
    )


def test_the_run_records_the_meter_options_it_used():
    # The cold junction moves every reading, so session.json says what it was; nothing is written without options.
    text = jsonio.dumps(_info(meter_options={"cj": "22C"}))
    assert '"meterOptions": {' in text
    assert '"cj": "22C"' in text
    assert "meterOptions" not in jsonio.dumps(_info())
    # The last property, as the C# app writes it, and read back as it was.
    assert list(json.loads(text))[-1] == "meterOptions"
    assert text.endswith('"meterOptions": {' + jsonio.NEWLINE + '    "cj": "22C"' + jsonio.NEWLINE + "  }" + jsonio.NEWLINE + "}")
    assert SessionInfo.from_json(json.loads(text)).meter_options == {"cj": "22C"}
    assert SessionInfo.from_json(json.loads(jsonio.dumps(_info()))).meter_options is None
    assert jsonio.dumps(_info(meter_options={"cj": "22.5 °C"})).count('"22.5 \\u00B0C"') == 1  # as System.Text.Json escapes it


def test_meter_options_come_after_characterise():
    from oventracker.analysis.characterisation import CharacteriseInfo

    keys = list(json.loads(jsonio.dumps(_info(characterise=CharacteriseInfo(5.0, 0.0), meter_options={"nplc": "100"}))))
    assert keys[-2:] == ["characterise", "meterOptions"]


# The run, with the 34401A on a fake link ----------------------------------------------------------


def _run(tmp_path, monkeypatch, *, settings: AppSettings | None = None, options: list[str] | None = None, resume: bool = False) -> tuple[int, str]:
    """A run with the 34401A on a fake link, stopped once it has started measuring: the exit code and what it said."""
    pytest.importorskip("rich")
    from oventracker.commands import run as run_command
    from oventracker.session.cancel import CancellationToken
    from oventracker.ui import console as ui

    appdata = tmp_path / "appdata"
    monkeypatch.setenv("APPDATA", str(appdata))
    monkeypatch.setenv("XDG_CONFIG_HOME", str(appdata))
    if settings is not None:
        settings.save()
    monkeypatch.setattr(hp34401a, "port_for", lambda spec, timeout_s: FakeScpi(serial=not spec.visa))
    buf = io.StringIO()
    before = ui.use(ui.make(file=buf, width=400, color_system=None))
    monkeypatch.setattr(ui, "FORCE_LIVE", False)
    monkeypatch.setattr(ui, "FORCE_KEYS", False)
    token = CancellationToken()
    stopper = threading.Timer(2.0, token.cancel)
    stopper.start()
    try:
        s = run_command.RunSettings(
            meter="34401a", resource="COM3", meter_options=options or [], yes=True, unit="F", modes="bake", setpoints="400",
            interval=0.5, out=None if resume else str(tmp_path / "run"), resume=str(tmp_path / "run") if resume else None,
        )
        code = run_command.execute(s, token)
    finally:
        stopper.cancel()
        ui.use(before)
    return code, buf.getvalue()


def test_a_run_says_the_cold_junction_was_assumed_and_records_none(tmp_path, monkeypatch):
    code, out = _run(tmp_path, monkeypatch)
    assert "Connected to HEWLETT-PACKARD 34401A" in out
    lines = out.splitlines()
    connected = next(i for i, line in enumerate(lines) if line.startswith("Connected"))
    assert lines[connected + 1] == ASSUMED_COLD_JUNCTION_NOTE
    assert "Meter set to DC mV, type K, cold junction 23.0 °C." in out
    session = json.loads((tmp_path / "run" / "session.json").read_text(encoding="utf-8"))
    assert "meterOptions" not in session
    assert session["driver"] == "34401a" and session["location"] == "COM3:9600,E,7,2"


def test_a_run_records_the_options_it_was_given_and_remembers_them(tmp_path, monkeypatch):
    code, out = _run(tmp_path, monkeypatch, options=["cj=72F", "nplc=1"])
    assert ASSUMED_COLD_JUNCTION_NOTE not in out
    assert "cold junction 22.2 °C" in out
    session = json.loads((tmp_path / "run" / "session.json").read_text(encoding="utf-8"))
    assert session["meterOptions"] == {"cj": "72F", "nplc": "1"}
    assert list(session)[-1] == "meterOptions"
    assert AppSettings.load().meter_options_for("34401a") == {"cj": "72F", "nplc": "1"}


def test_stale_remembered_options_are_forgotten_with_a_note(tmp_path, monkeypatch):
    old = AppSettings()
    old.remember_meter_options("34401a", {"rtd": "pt100-b"})  # from the PT100 version of the driver
    code, out = _run(tmp_path, monkeypatch, settings=old)
    assert (
        "The settings remembered for this meter no longer apply, so they're forgotten: "
        "The 34401A has no --meter-option rtd; it takes cj and nplc."
    ) in out
    assert out.index("forgotten") < out.index("Connected")
    assert ASSUMED_COLD_JUNCTION_NOTE in out
    assert AppSettings.load().meter_options_for("34401a") == {}
    assert "meterOptions" not in json.loads((tmp_path / "run" / "session.json").read_text(encoding="utf-8"))


def test_given_options_that_are_wrong_stop_the_run(tmp_path, monkeypatch):
    code, out = _run(tmp_path, monkeypatch, options=["cj=22"])
    assert code == 1
    assert out.strip() == "--meter-option cj=22: give the cold junction's temperature with its unit, e.g. cj=22C or cj=72F."


def test_a_resumed_run_keeps_its_options_unless_others_are_given(tmp_path, monkeypatch):
    _run(tmp_path, monkeypatch, options=["cj=72F"])
    session = tmp_path / "run" / "session.json"
    assert json.loads(session.read_text(encoding="utf-8"))["meterOptions"] == {"cj": "72F"}
    # None given and none remembered: the run's own.
    _run(tmp_path, monkeypatch, settings=AppSettings(), resume=True)
    resumed = json.loads(session.read_text(encoding="utf-8"))
    assert len(resumed["sittings"]) == 2
    assert resumed["meterOptions"] == {"cj": "72F"}
    # Given: those.
    _run(tmp_path, monkeypatch, options=["cj=20C"], resume=True)
    assert json.loads(session.read_text(encoding="utf-8"))["meterOptions"] == {"cj": "20C"}