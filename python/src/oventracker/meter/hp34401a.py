"""An HP / Agilent / Keysight 34401A bench DMM with a type K thermocouple on its DC volts inputs (HI and LO), through
GPIB (by VISA) or its RS-232 port (Meter/Hp34401AMeter.cs and Hp34401ADriver.cs).

The 34401A has no temperature function, so it reads the thermocouple's voltage on its 100 mV DC range, with the
input at its high impedance (over 10 GΩ, so the thermocouple isn't loaded). The app converts it with the type K
reference function and the cold junction's temperature: where the thermocouple meets the meter's copper terminals,
which the meter can't measure, so it's given (``--meter-option cj=``). A bench meter: mains powered, no battery, no
auto power-off. It doesn't search the bus by itself, since writing to unknown instruments isn't always harmless:
give it --resource once, and it's remembered."""

from __future__ import annotations
import math

import sys
import time
from datetime import datetime, timezone

from ..commands.names import APP_NAME
from ..netfmt import custom, round_trip
from ..probes import Sensor
from ..session.recording import _try_float  # double.TryParse with the invariant culture
from . import port_finder, visa
from .base import MeterConfig, MeterException, MeterIdentity, MeterReading, ReadingStatus, TemperatureMeter
from .driver import MeterCandidate, MeterDriver, MeterOption, MeterRequest, ProbeResult
from .resource_spec import Parity, ResourceSpec, SerialSettings, StopBits, Transport
from .scpi_port import ScpiPort, SerialScpiPort, VisaScpiPort
from .thermocouple import TypeK
from .u1253b import explain as _explain_serial

DRIVER_NAME = "34401a"

#: The 34401A's RS-232 port from the factory: 9600 baud, even parity, 7 data bits, and always 2 stop bits (User's
#: Guide, pp. 156 and 160).
FACTORY_SERIAL = SerialSettings(9600, Parity.Even, 7, StopBits.Two)

#: The cold junction's temperature when none is given: a typical room.
ASSUMED_COLD_JUNCTION_C = 23.0

#: What to say on connecting when the cold junction was assumed.
ASSUMED_COLD_JUNCTION_NOTE = (
    f"The cold junction (where the thermocouple meets the meter's terminals) is taken as {custom(ASSUMED_COLD_JUNCTION_C, 0)} °C, "
    "a typical room: every reading is off by as much as it differs. Give the room's temperature with --meter-option cj=22C "
    "(or cj=72F); it's remembered."
)

_NPLCS = (0.02, 0.2, 1.0, 10.0, 100.0)


def _windows() -> bool:
    return sys.platform == "win32"


class Hp34401AMeter(TemperatureMeter):
    """A 34401A reading a type K thermocouple. ``cold_junction_c``: the cold junction's temperature, °C (every
    reading is off by as much as this is). ``nplc``: integration time in power-line cycles; 10 (the default) gives
    6½ digits and rejects mains hum."""

    #: What the 34401A sends for an overload: more than the range, as an open thermocouple's floating input can give.
    OVERLOAD_VALUE = 9.9e37

    #: The 100 mV range: type K reaches 54.9 mV at its top, 1372 °C.
    RANGE_V = 0.1

    def __init__(self, port: ScpiPort, cold_junction_c: float, nplc: float = 10.0):
        self._port = port
        self._cold_junction_c = cold_junction_c
        self._nplc = nplc
        self._config: MeterConfig | None = None
        self._reads_since_config = 0
        self._connected = False
        #: The setup is checked again every this many readings, in case someone pressed Local and changed it.
        self.config_every = 30

    @property
    def location(self) -> str:
        return self._port.location

    @property
    def cold_junction_c(self) -> float:
        return self._cold_junction_c

    @property
    def last_config(self) -> MeterConfig | None:
        return self._config

    def connect(self) -> MeterIdentity:
        self.disconnect()
        self._port.open()
        try:
            self._port.clear()
            # Over RS-232 the 34401A must be put in remote first (User's Guide, p. 160).
            if self._port.is_serial:
                self._send("SYST:REM")
            identity = parse_identity(self._query("*IDN?"))
            if not is_34401a(identity):
                # The driver says it isn't one; nothing is changed on another instrument, and on RS-232 it's given its
                # front panel back.
                try:
                    self._port.release()
                except Exception:
                    pass
                return identity
            self._send("*RST")
            self._send("*CLS")
            self._send(f"CONF:VOLT:DC {custom(self.RANGE_V, 1, 1)}")
            self._send(f"VOLT:DC:NPLC {round_trip(self._nplc)}")
            self._send("INP:IMP:AUTO ON")  # over 10 GΩ on the 100 mV range, rather than 10 MΩ
            self._send("ZERO:AUTO ON")
            self._query("*OPC?")  # waits for it to take all that in, which matters at 9600 baud
            err = self._query("SYST:ERR?")
            if not err.startswith("+0"):
                raise MeterException(f"The 34401A didn't accept its setup: {err}")
            self._connected = True
            self.read_config()
            return identity
        except BaseException:
            self._port.dispose()
            raise

    def disconnect(self) -> None:
        if self._connected:
            try:
                self._port.release()
            except Exception:
                pass
        self._connected = False
        self._config = None
        self._port.dispose()

    def read_config(self) -> MeterConfig:
        self._config = parse_config(self._query("CONF?"), self._cold_junction_c)
        self._reads_since_config = 0
        return self._config

    def read(self) -> MeterReading:
        if self._config is None:
            self.read_config()
        else:
            self._reads_since_config += 1
            if self._reads_since_config >= self.config_every:
                self.read_config()
        now = datetime.now(timezone.utc)
        config = self._config
        if config is not None and not config.is_temperature:
            return MeterReading(now, ReadingStatus.NotTemperature, None, config.description)
        return to_reading(now, self._query("READ?"), self._cold_junction_c)

    def beep(self) -> None:
        self._send("SYST:BEEP")

    def _send(self, command: str) -> None:
        self._port.write_line(command)
        if self._port.is_serial:
            time.sleep(0.05)  # its input buffer is small, and nothing paces on DSR

    def _query(self, command: str) -> str:
        self._port.write_line(command)
        return self._port.read_line().strip()

    def dispose(self) -> None:
        self.disconnect()


def is_34401a(identity: MeterIdentity) -> bool:
    return "34401A" in identity.model.upper()


def parse_identity(idn: str) -> MeterIdentity:
    """"HEWLETT-PACKARD,34401A,0,11-5-2": the 34401A gives 0 for its serial number."""
    p = [x.strip() for x in idn.split(",")]

    def at(i: int) -> str:
        return p[i] if i < len(p) else ""

    return MeterIdentity(at(0), at(1), at(2), at(3))


def parse_config(reply: str, cold_junction_c: float) -> MeterConfig:
    """The reply to CONF?, e.g. ``"VOLT +1.000000E-01,+3.000000E-07"``: only DC volts gives a temperature (AC volts
    is ``VOLT:AC``). The sensor is a generic type K thermocouple; ``--probe`` says which one."""
    text = reply.strip().strip('"')
    function = text.split(" ", 1)[0].upper()
    if function == "VOLT":
        return MeterConfig(f"DC mV, type K, cold junction {custom(cold_junction_c, 1, 1)} °C", True, None, Sensor.thermocouple("K"))
    return MeterConfig(text, False, None)


def to_reading(time_: datetime, reply: str, cold_junction_c: float) -> MeterReading:
    """A reply to READ? (volts) as a temperature."""
    volts = _try_float(reply.strip())
    if volts is None or not math.isfinite(volts):  # NaN parses, but isn't a temperature
        return MeterReading(time_, ReadingStatus.Error, None, reply)
    if abs(volts) >= Hp34401AMeter.OVERLOAD_VALUE * 0.99:
        return MeterReading(time_, ReadingStatus.Overload, None, reply)
    # Beyond type K's range isn't a thermocouple reading: a wiring fault, or no thermocouple at all.
    c = TypeK.temperature_for(volts * 1000, cold_junction_c)
    return MeterReading(time_, ReadingStatus.Ok, c, reply) if c is not None else MeterReading(time_, ReadingStatus.Error, None, reply)


def parse_temperature_c(text: str) -> float | None:
    """"22C", "72F", "22.5 °C" as °C. None without a unit, or if it isn't a temperature."""
    s = text.strip().replace("°", "").replace(" ", "")
    if len(s) < 2:
        return None
    unit = s[-1].upper()
    if unit not in ("C", "F"):
        return None
    v = _try_float(s[:-1])
    if v is None:
        return None
    c = (v - 32) / 1.8 if unit == "F" else v
    return c if -40 <= c <= 80 else None  # a room, a workshop or an ice bath, not an oven


def _cold_junction_from(options: dict[str, str] | None) -> tuple[float, bool]:
    """The cold junction's temperature, and whether it was assumed rather than given."""
    if options and "cj" in options:
        c = parse_temperature_c(options["cj"])
        if c is not None:
            return c, False
    return ASSUMED_COLD_JUNCTION_C, True


def _nplc_from(options: dict[str, str] | None) -> float:
    if options and "nplc" in options:
        v = _try_float(options["nplc"])
        if v is not None:
            return v
    return 10.0


def timeout_for(nplc: float) -> float:
    """Seconds: long enough for one reading at this integration time, DC volts with auto-zero (two integrations),
    with room to spare."""
    return max(5.0, 4 * nplc / 50 + 3)


def check(resource: str) -> str | None:
    """Why a resource can't be a 34401A's, or None if it can: GPIB or a serial port (the 34401A has no USB or LAN)."""
    spec = ResourceSpec.parse(resource)
    if spec is None:
        return f'"{resource}" isn\'t an address the app understands (e.g. GPIB0::22::INSTR, COM3, or COM3:9600,E,7,2).'
    if spec.transport in (Transport.Usb, Transport.Lan, Transport.Ble):
        return "the 34401A connects by GPIB or RS-232, not USB, LAN or Bluetooth."
    if spec.visa and not visa.is_available():
        return visa.missing_help()
    return None


def port_for(spec: ResourceSpec, timeout_s: float) -> ScpiPort:
    """The link for a resource: VISA for GPIB (and ASRL), otherwise the serial port itself."""
    if spec.visa:
        return VisaScpiPort(spec.address, timeout_s)
    return SerialScpiPort(port_finder.normalize(spec.address), spec.settings_or(FACTORY_SERIAL), timeout_s)


def explain(ex: BaseException) -> str:
    if isinstance(ex, TimeoutError):
        return "no reply (check the address, the interface chosen under I/O MENU, and for RS-232 the settings and a cable that passes DTR)"
    if isinstance(ex, visa.VisaMissing):
        return visa.missing_help()
    return _explain_serial(ex)


class Hp34401ADriver(MeterDriver):
    name = DRIVER_NAME
    description = "HP/Agilent 34401A with a type K thermocouple (DC mV), through GPIB or RS-232"

    #: The cold junction's temperature when none is given: a typical room.
    ASSUMED_COLD_JUNCTION_C = ASSUMED_COLD_JUNCTION_C
    ASSUMED_COLD_JUNCTION_NOTE = ASSUMED_COLD_JUNCTION_NOTE
    FACTORY_SERIAL = FACTORY_SERIAL

    @property
    def resource_help(self) -> str:  # type: ignore[override]
        example = "COM3" if _windows() else "/dev/ttyUSB0"
        return (
            f"a GPIB address through VISA, e.g. GPIB0::22::INSTR (or gpib:22), or a serial port, e.g. {example}, "
            f"with its settings if they aren't the factory {FACTORY_SERIAL} (e.g. {example}:9600,N,8,2)"
        )

    @property
    def not_found_help(self) -> str:
        return (
            "Give the meter's address with --resource, e.g. --resource GPIB0::22::INSTR (the address is under I/O MENU, HP-IB ADDR; "
            "22 from the factory), or --resource COM3 for RS-232 (choose RS-232 under I/O MENU, INTERFACE; the app expects 9600 baud, "
            "even parity, 7 data bits, and a cable that passes DTR). "
            f"{APP_NAME} meters --probe --meter 34401a asks each GPIB instrument VISA can see."
            + ("" if visa.is_available() else " " + visa.missing_help())
        )

    @property
    def not_temperature_help(self) -> str:
        return "The 34401A isn't on DC volts: press Local (Shift) and let the app set it up again, or restart the run."

    _OPTIONS = [
        MeterOption(
            "cj",
            "a temperature with its unit, e.g. 22C or 72F",
            "The cold junction's temperature: where the thermocouple meets the meter's terminals, normally the room's. "
            "Every reading is off by as much as this is. 23 °C when left off.",
        ),
        MeterOption(
            "nplc",
            "0.02, 0.2, 1, 10, 100",
            "Integration time in power-line cycles: 10 (the default) is quiet and fast enough; 100 is quieter, at about 3 s a reading.",
        ),
    ]

    @property
    def options(self) -> list[MeterOption]:
        return list(self._OPTIONS)

    def check_options(self, options: dict[str, str]) -> str | None:
        for key, value in options.items():
            if key == "cj":
                if parse_temperature_c(value) is None:
                    return f"--meter-option cj={value}: give the cold junction's temperature with its unit, e.g. cj=22C or cj=72F."
            elif key == "nplc":
                n = _try_float(value)
                if n is None or n not in _NPLCS:
                    return f"--meter-option nplc={value}: the 34401A takes {', '.join(round_trip(x) for x in _NPLCS)}."
            else:
                return f"The 34401A has no --meter-option {key}; it takes cj and nplc."
        return None

    def open(self, request: MeterRequest) -> ProbeResult | None:
        where = request.resource if request.resource is not None else request.preferred
        if where is None:
            return None
        if request.progress is not None:
            request.progress(where)
        return self._probe(where, True, request.options)

    def discover(self) -> list[MeterCandidate]:
        found = [MeterCandidate(r, "GPIB instrument (VISA)", None, True, "GPIB") for r in visa.find("GPIB?*INSTR")]
        found.extend(
            MeterCandidate(p.port, p.description, f"RS-232 at {FACTORY_SERIAL} if it's a 34401A", False, "Serial")
            for p in port_finder.list_ports()
            if not p.is_bluetooth
        )
        return found

    def probe(self, resource: str) -> ProbeResult:
        return self._probe(resource, False, None)

    def _probe(self, resource: str, keep_open: bool, options: dict[str, str] | None) -> ProbeResult:
        problem = check(resource)
        if problem is not None:
            return ProbeResult(resource, None, problem)
        nplc = _nplc_from(options)
        spec = ResourceSpec.parse(resource)
        assert spec is not None
        port = port_for(spec, timeout_for(nplc) if keep_open else 3.0)
        where = spec.address if spec.visa else port.location
        if not keep_open:
            # Just ask what it is: nothing is changed.
            try:
                port.open()
                port.clear()  # a reply left over from before mustn't be taken for the identity
                if port.is_serial:
                    port.write_line("SYST:REM")
                port.write_line("*IDN?")
                identity = parse_identity(port.read_line().strip())
                port.release()
                return ProbeResult(where, identity, None if is_34401a(identity) else "not a 34401A")
            except Exception as ex:
                return ProbeResult(where, None, explain(ex))
            finally:
                port.dispose()

        cj, assumed = _cold_junction_from(options)
        meter = Hp34401AMeter(port, cj, nplc)
        try:
            identity = meter.connect()
            if is_34401a(identity):
                return ProbeResult(where, identity, None, meter, [ASSUMED_COLD_JUNCTION_NOTE] if assumed else None)
            meter.dispose()
            return ProbeResult(where, identity, "not a 34401A")
        except Exception as ex:
            meter.dispose()
            return ProbeResult(where, None, explain(ex))
