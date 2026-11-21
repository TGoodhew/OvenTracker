"""A line-at-a-time link to a SCPI instrument (Meter/ScpiPort.cs): GPIB through VISA (PyVISA), or a serial port
(pyserial). Tests use a fake. Each is imported only when a port is opened."""

from __future__ import annotations

from typing import Any

from . import visa
from .base import MeterException
from .resource_spec import SerialSettings, open_serial


class ScpiPort:
    """IScpiPort: what the 34401A's meter needs of its link."""

    @property
    def location(self) -> str:
        """Where it is, e.g. "GPIB0::22::INSTR" or "COM3:9600,E,7,2"."""
        raise NotImplementedError

    @property
    def is_serial(self) -> bool:
        """Serial (RS-232) rather than GPIB: the 34401A then needs SYST:REM, and has no device clear."""
        raise NotImplementedError

    def open(self) -> None:
        raise NotImplementedError

    def write_line(self, command: str) -> None:
        raise NotImplementedError

    def read_line(self) -> str:
        """One reply, without its line ending. Raises TimeoutError if none comes."""
        raise NotImplementedError

    def clear(self) -> None:
        """Empties anything waiting, and on GPIB sends the device clear."""
        raise NotImplementedError

    def release(self) -> None:
        """Gives the front panel back (GPIB go-to-local, or SYST:LOC on serial), as far as it can."""
        raise NotImplementedError

    def dispose(self) -> None:
        raise NotImplementedError


class VisaScpiPort(ScpiPort):
    """An instrument through VISA (GPIB, USB-TMC, LAN, ASRL). ``timeout_s``: how long to wait for a reply."""

    def __init__(self, resource: str, timeout_s: float):
        self._resource = resource
        self._timeout = timeout_s
        self._session: visa.VisaSession | None = None

    @property
    def location(self) -> str:
        return self._resource

    @property
    def is_serial(self) -> bool:
        return self._resource.upper().startswith("ASRL")

    def open(self) -> None:
        self.dispose()
        self._session = visa.open_session(self._resource, self._timeout)

    @property
    def _s(self) -> visa.VisaSession:
        if self._session is None:
            raise MeterException("Not connected.")
        return self._session

    def write_line(self, command: str) -> None:
        self._s.write(command + "\n")

    def read_line(self) -> str:
        return self._s.read().rstrip("\r\n")

    def clear(self) -> None:
        if not self.is_serial:
            self._s.clear()

    def release(self) -> None:
        if self._session is None:
            return
        if self.is_serial:
            try:
                self.write_line("SYST:LOC")
            except Exception:
                pass
        else:
            self._session.go_to_local()

    def dispose(self) -> None:
        if self._session is not None:
            self._session.close()
        self._session = None


def _open_failure(port: str, ex: BaseException) -> str:
    """Why a serial port didn't open, in .NET's words where pyserial's differ (a port that isn't there)."""
    text = str(ex)
    if "FileNotFoundError" in text or getattr(ex, "errno", None) == 2 or "No such file or directory" in text:
        return f"Could not find file '{port}'."
    return text


class SerialScpiPort(ScpiPort):
    """An instrument on a serial port, with DTR held on (the 34401A won't send without it)."""

    def __init__(self, port: str, settings: SerialSettings, timeout_s: float):
        self._port = port
        self._settings = settings
        self._timeout = timeout_s
        self._sp: Any = None
        self._partial = b""

    @property
    def location(self) -> str:
        return f"{self._port}:{self._settings}"

    @property
    def is_serial(self) -> bool:
        return True

    def open(self) -> None:
        self.dispose()
        p = open_serial(self._port, self._settings, dtr_dsr=True)
        p.timeout = self._timeout
        p.write_timeout = self._timeout
        try:
            p.open()
        except Exception as ex:
            raise MeterException(f"Could not open {self._port}: {_open_failure(self._port, ex)}") from ex
        self._sp = p
        self._partial = b""

    @property
    def _p(self) -> Any:
        if self._sp is None:
            raise MeterException("Not connected.")
        return self._sp

    def write_line(self, command: str) -> None:
        p = self._p
        try:
            p.write((command + "\n").encode("ascii", errors="replace"))
        except Exception as ex:
            if type(ex).__name__ == "SerialTimeoutException":
                raise TimeoutError("The write timed out.") from ex
            raise

    def read_line(self) -> str:
        """SerialPort.ReadLine with NewLine "\\n": a TimeoutError if no whole line comes in time. What did come is
        kept for the next read, as .NET keeps it."""
        data = self._partial + self._p.read_until(b"\n")
        if not data.endswith(b"\n"):
            self._partial = data
            raise TimeoutError("The operation has timed out.")
        self._partial = b""
        return bytes(b if b < 128 else 63 for b in data[:-1]).decode("ascii").rstrip("\r\n")

    def clear(self) -> None:
        p = self._p
        p.reset_input_buffer()
        p.reset_output_buffer()
        self._partial = b""

    def release(self) -> None:
        if self._sp is None:
            return
        try:
            self.write_line("SYST:LOC")
        except Exception:
            pass

    def dispose(self) -> None:
        if self._sp is None:
            return
        try:
            self._sp.close()
        except Exception:
            pass
        self._sp = None
