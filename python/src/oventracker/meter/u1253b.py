"""A Keysight (Agilent) U1253B, or another U1250-series meter, on a serial port (Meter/U1253BDriver.cs and
U1253BMeter.cs): the U1177A's Bluetooth serial link (an outgoing Bluetooth COM port on Windows, an RFCOMM device on
Linux), or a U1173 IR-USB cable. pyserial is imported only when a port is opened."""

from __future__ import annotations

import sys
import time
from datetime import datetime, timedelta, timezone
from typing import Any

from . import port_finder, u1253b_protocol as protocol
from .auto_power_off import AutoPowerOff
from .base import MeterConfig, MeterException, MeterIdentity, MeterReading, TemperatureMeter
from .driver import MeterCandidate, MeterDriver, MeterRequest, ProbeResult
from .resource_spec import U1250, ResourceSpec, SerialSettings, Transport, describe, open_serial

DRIVER_NAME = "u1253b"


def _windows() -> bool:
    return sys.platform == "win32"


class U1253BMeter(TemperatureMeter):
    """A U1253B reached through a serial port. ``settings``: 9600 8N1 unless given in --resource."""

    def __init__(self, port_name: str, settings: SerialSettings | None = None):
        self.port_name = port_name
        self._serial = settings if settings is not None else U1250
        self._port: Any = None
        self._config: MeterConfig | None = None
        self._reads_since_config = 0
        #: The configuration is re-read every this many readings, so turning the dial is noticed quickly.
        self.config_every = 5
        self.open_attempts = 4
        self._timeout = 3.0

    @property
    def last_config(self) -> MeterConfig | None:
        return self._config

    @property
    def location(self) -> str:
        """The port, with its settings when they aren't the usual 9600 8N1."""
        return self.port_name if self._serial == U1250 else f"{self.port_name}:{self._serial}"

    def connect(self) -> MeterIdentity:
        self.disconnect()
        p = open_serial(self.port_name, self._serial, dtr_dsr=False)
        p.timeout = self._timeout
        p.write_timeout = self._timeout
        # Opening the outgoing Bluetooth port is what makes Windows connect to the adapter: a second or two.
        # Straight after the port was closed, the old link may still be coming down, so a failed open is tried
        # again after a pause.
        attempt = 1
        while True:
            try:
                p.open()
                break
            except OSError as ex:
                if attempt < self.open_attempts:
                    time.sleep(1.5 * attempt)
                    attempt += 1
                    continue
                raise MeterException(f"Could not open {self.port_name}: {ex}") from ex
        self._port = p
        idn = self._query_with_retry(protocol.IDENTIFY, 3)
        self.read_config()
        return protocol.parse_identity(idn)

    def disconnect(self) -> None:
        if self._port is None:
            return
        try:
            self._port.close()
        except Exception:
            pass  # the link may already be gone
        self._port = None
        self._config = None

    def read_config(self) -> MeterConfig:
        self._config = protocol.parse_config(self._query(protocol.CONFIGURATION))
        self._reads_since_config = 0
        return self._config

    def read(self) -> MeterReading:
        if self._config is None:
            self.read_config()
        else:
            self._reads_since_config += 1
            if self._reads_since_config >= self.config_every:
                self.read_config()
        reply = self._query(protocol.FETCH)
        return protocol.to_reading(datetime.now(timezone.utc), reply, self._config)

    def read_battery_percent(self) -> float | None:
        return protocol.try_parse_number(self._query(protocol.BATTERY))

    def beep(self) -> None:
        self._send(protocol.BEEP_COMMAND)

    def _query_with_retry(self, command: str, attempts: int) -> str:
        i = 1
        while True:
            try:
                return self._query(command)
            except TimeoutError:
                if i >= attempts:
                    raise
                i += 1

    def _send(self, command: str) -> None:
        if self._port is None:
            raise MeterException("Not connected.")
        self._port.write((command + "\n").encode("ascii"))

    def _read_line(self) -> str:
        """SerialPort.ReadLine with NewLine "\\n": a TimeoutError if no whole line comes in time."""
        data = self._port.read_until(b"\n")
        if not data.endswith(b"\n"):
            raise TimeoutError("The operation has timed out.")
        return data.decode("ascii", errors="replace")[:-1]

    def _query(self, command: str) -> str:
        p = self._port
        if p is None:
            raise MeterException("Not connected.")
        if p.in_waiting > 0:
            p.reset_input_buffer()
        p.write((command + "\n").encode("ascii"))
        deadline = time.monotonic() + self._timeout
        while True:
            line = self._read_line().strip("\r\n ")
            if not line:
                continue
            if protocol.is_command_error(line):
                raise MeterException(f"The meter did not accept {command}.")
            if protocol.is_event(line):
                self._config = None  # the dial or a button changed something
                if time.monotonic() > deadline:
                    raise TimeoutError("The operation has timed out.")
                continue
            return line

    def dispose(self) -> None:
        self.disconnect()


def _is_u1250(identity: MeterIdentity) -> bool:
    return identity.model.upper().startswith("U12")


class U1253BDriver(MeterDriver):
    name = DRIVER_NAME
    description = "Keysight U1253B through a U1177A Bluetooth adapter (or a U1173 cable)"

    @property
    def resource_help(self) -> str:  # type: ignore[override]
        if _windows():
            return "a serial port, e.g. COM5, optionally with its settings (9600 8N1 by default), e.g. COM5:9600,N,8,1"
        return "a serial device, e.g. /dev/rfcomm0, optionally with its settings (9600 8N1 by default), e.g. /dev/ttyUSB0:9600,N,8,1"

    @property
    def not_found_help(self) -> str:
        if _windows():
            return (
                "Check the meter is on, the U1177A is clipped on and switched on (its light blinks), and that it is paired in "
                'Windows Bluetooth settings: pairing adds a "Standard Serial over Bluetooth link" port.'
            )
        return (
            "Check the meter is on and the U1177A is clipped on and switched on (its light blinks). On Linux, pair it once "
            '(bluetoothctl: scan on, pair, trust), then bind it to a serial device with "sudo rfcomm bind 0 <address>", '
            "which makes /dev/rfcomm0. To open it you need to be in the dialout group."
        )

    @property
    def not_temperature_help(self) -> str:
        return "Turn the dial to temperature (°C/°F) with the thermocouple plugged in."

    _APO = AutoPowerOff(
        timedelta(minutes=10),
        [
            "Hold the button that steps through the functions at the dial's current position (button 4 in the guide's keypad table) for more than a second, to enter Setup.",
            "Use the arrow buttons to go to APO, then press Hz to edit it.",
            "Set every digit to zero, which means OFF.",
            "Press Hz to save, then hold the same button for more than a second to leave Setup.",
        ],
        "Keysight U1253B User's and Service Guide, pp. 34, 110 and 126–127",
        how_to_wake="press any button on the meter, or turn the dial",
    )

    @property
    def auto_power_off(self) -> AutoPowerOff:
        """The U1253B's auto power-off is 10 minutes from the factory, and reading it over the IR port doesn't
        restart the countdown (User's and Service Guide, p. 126). It can't report the setting remotely, so the app
        shows the steps as a reminder."""
        return self._APO

    def open(self, request: MeterRequest) -> ProbeResult | None:
        if request.resource is not None:
            given = request.resource
            problem = check(given)
            if problem is not None:
                return ProbeResult(given, None, problem)
            spec = ResourceSpec.parse(given)
            assert spec is not None
            port = port_finder.normalize(spec.address)
            if request.progress is not None:
                request.progress(port)
            return _probe(port, keep_open=True, settings=spec.settings)

        # The port that worked last time, then each outgoing Bluetooth port. Other serial ports are only tried
        # when named, since writing to an unknown serial device is not always harmless.
        order: list[str] = []
        if request.preferred is not None and request.preferred.strip():
            order.append(port_finder.normalize(request.preferred))
        order.extend(p.port for p in port_finder.list_ports() if p.is_bluetooth_outgoing)
        seen: list[str] = []
        for p in order:
            if any(port_finder.same_name(p, s) for s in seen):
                continue
            seen.append(p)
            if request.progress is not None:
                request.progress(f"{p} (a Bluetooth link can take a few seconds)")
            r = _probe(p, keep_open=True)
            if r.meter is not None:
                return r
        return None

    def discover(self) -> list[MeterCandidate]:
        out = []
        for p in port_finder.list_ports():
            if p.is_bluetooth_outgoing:
                note: str | None = f"{'outgoing ' if _windows() else ''}Bluetooth {p.bluetooth_address or ''}".rstrip()
            elif p.is_bluetooth:
                note = "incoming Bluetooth (not usable)"
            else:
                note = None
            out.append(MeterCandidate(p.port, p.description, note, p.is_bluetooth_outgoing, "Bluetooth" if p.is_bluetooth else "Serial"))
        return out

    def probe(self, resource: str) -> ProbeResult:
        problem = check(resource)
        if problem is not None:
            return ProbeResult(resource, None, problem)
        spec = ResourceSpec.parse(resource)
        assert spec is not None
        return _probe(port_finder.normalize(spec.address), keep_open=False, settings=spec.settings)


def check(resource: str) -> str | None:
    """Why a resource can't be a U1253B's, or None if it can: the U1250 series connects only through a serial port."""
    spec = ResourceSpec.parse(resource)
    if spec is None:
        return f'"{resource}" isn\'t a serial port the app understands (e.g. COM5, or COM5:9600,N,8,1).'
    if spec.visa:
        return f"the U1253B connects only through a serial port (COM5, /dev/rfcomm0), not {describe(spec.transport)} through VISA."
    if spec.transport != Transport.Serial:
        return f"the U1253B connects only through a serial port (COM5, /dev/rfcomm0), not {describe(spec.transport)}."
    return None


def _probe(port: str, keep_open: bool, settings: SerialSettings | None = None) -> ProbeResult:
    """``keep_open``: hand back the connected meter instead of closing it (reopening a Bluetooth port straight
    away can fail)."""
    meter = U1253BMeter(port, settings)
    resource = meter.location  # with the settings when they aren't the usual ones, so they're remembered
    try:
        identity = meter.connect()
        if keep_open and _is_u1250(identity):
            return ProbeResult(resource, identity, None, meter)
        meter.dispose()
        return ProbeResult(resource, identity, None if _is_u1250(identity) else "not a U1250-series meter")
    except Exception as ex:
        meter.dispose()
        return ProbeResult(resource, None, explain(ex))


def explain(ex: BaseException) -> str:
    """Why a port couldn't be used, in terms of what to do about it."""
    if isinstance(ex, TimeoutError):
        return "no reply"
    if isinstance(ex, ImportError):
        return str(ex)
    inner = ex.__cause__ if ex.__cause__ is not None else ex
    text = f"{ex} {inner}"
    denied = isinstance(inner, PermissionError) or getattr(inner, "errno", None) == 13 or (
        "Access is denied" in text or "Permission denied" in text or "PermissionError" in text
    )
    if not denied:
        return str(ex)
    if _windows():
        return "in use by another program"
    return "permission denied: add yourself to the dialout group (sudo usermod -aG dialout $USER), then log in again"
