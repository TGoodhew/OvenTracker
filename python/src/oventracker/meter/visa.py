"""VISA (Meter/Visa.cs), for GPIB instruments: through PyVISA, which drives the VISA library installed on the
system (NI-VISA, Keysight IO Libraries, R&S VISA), or pyvisa-py.

PyVISA is optional (``pip install ./python[visa]``) and imported only when VISA is asked for, so the app installs
and runs without it: :func:`is_available` says whether VISA can be used, and :func:`missing_help` what to install."""

from __future__ import annotations

import ctypes
import struct
import sys
from typing import Any

from .base import MeterException

#: Status codes, from visa.h, as PyVISA gives them (signed).
ERROR_TIMEOUT = 0xBFFF0015 - (1 << 32)
ERROR_RESOURCE_NOT_FOUND = 0xBFFF0011 - (1 << 32)
_GPIB_REN_ADDRESS_GTL = 6


class VisaMissing(MeterException):
    """VISA can't be used: PyVISA or a VISA library isn't installed (DllNotFoundException in the C# app)."""


def candidates() -> list[str]:
    """The VISA library names the C# app tries, in order, for this system."""
    if sys.platform == "win32":
        return ["visa64.dll"] if struct.calcsize("P") == 8 else ["visa32.dll"]
    if sys.platform == "darwin":
        return ["/Library/Frameworks/VISA.framework/VISA"]
    return ["libvisa.so", "libvisa.so.0", "libiovisa.so", "librsvisa.so"]


_state: dict[str, Any] = {"checked": False, "rm": None, "pyvisa": None, "library": None, "error": None}


def _reset() -> None:
    """Forgets what was found, so the next call looks again (for the tests)."""
    _state.update(checked=False, rm=None, pyvisa=None, library=None, error=None)


def _library_loads() -> bool:
    """Whether one of the VISA libraries loads, as the C# app checks it."""
    if _state["library"] is None:
        found = False
        for name in candidates():
            try:
                (ctypes.WinDLL if sys.platform == "win32" else ctypes.CDLL)(name)  # type: ignore[attr-defined]
                found = True
                break
            except OSError:
                continue
        _state["library"] = found
    return bool(_state["library"])


def _pyvisa() -> Any:
    """The pyvisa module, or None if it isn't installed."""
    try:
        import pyvisa  # type: ignore[import-not-found]
    except ImportError:
        return None
    return pyvisa


def _check() -> None:
    """Imports PyVISA and opens the default resource manager, once."""
    if _state["checked"]:
        return
    _state["checked"] = True
    pyvisa = _pyvisa()
    _state["pyvisa"] = pyvisa
    if pyvisa is None:
        return
    try:
        _state["rm"] = pyvisa.ResourceManager()
    except Exception as ex:  # no VISA library (ValueError, OSError), or it wouldn't open
        _state["error"] = ex


def is_available() -> bool:
    """Whether VISA can be used: PyVISA is installed and found a VISA library."""
    _check()
    return _state["rm"] is not None


def missing_help() -> str:
    """What to install for VISA, as the C# app says it, with PyVISA when that's missing too."""
    _check()
    if sys.platform == "win32":
        library = (
            f"VISA isn't installed (no {candidates()[0]}). For GPIB, install NI-VISA (with NI-488.2 for an NI adapter) "
            f"or Keysight IO Libraries Suite, {'64-bit' if sys.maxsize > 2**32 else '32-bit'}."
        )
    else:
        library = (
            f"VISA isn't installed (none of {', '.join(candidates())}). For GPIB on Linux, install NI-VISA with NI-488.2 "
            "(or linux-gpib with a VISA that uses it)."
        )
    pip = "pip install pyvisa (or pip install ./python[visa])"
    if _state["pyvisa"] is None:
        if _library_loads():
            return f"VISA isn't installed for Python: the app uses PyVISA for it, so {pip}."
        return f"{library} The Python app also needs PyVISA: {pip}."
    if _state["rm"] is None and _library_loads() and _state["error"] is not None:
        return f"VISA isn't working: PyVISA couldn't open it ({_state['error']})."
    return library


def _resource_manager() -> Any:
    if not is_available():
        raise VisaMissing(missing_help())
    return _state["rm"]


def find(expression: str) -> list[str]:
    """The instruments VISA knows matching an expression, e.g. "GPIB?*INSTR". Empty if VISA isn't installed or
    none match."""
    if not is_available():
        return []
    try:
        return [str(r) for r in _state["rm"].list_resources(expression)]
    except Exception:
        return []


def _error_code(ex: BaseException) -> int | None:
    code = getattr(ex, "error_code", None)
    return int(code) if code is not None else None


def _describe(inst: Any, code: int) -> str:
    try:
        text, _ = inst.visalib.status_description(inst.session, code)
        if text:
            return str(text).strip()
    except Exception:
        pass
    return f"VISA error 0x{code & 0xFFFFFFFF:08X}"


def _visa_errors() -> tuple[type[BaseException], ...]:
    pyvisa = _state["pyvisa"]
    error = getattr(getattr(pyvisa, "errors", None), "VisaIOError", None)
    return (error,) if isinstance(error, type) else ()


def _fail(inst: Any, ex: BaseException, what: str) -> BaseException:
    """The exception for a failed VISA call, as the C# app's Check gives it."""
    code = _error_code(ex)
    if code == ERROR_TIMEOUT:
        return TimeoutError(f"VISA timed out trying to {what}.")
    if code is None:
        return MeterException(f"Couldn't {what}: {ex}.")
    return MeterException(f"Couldn't {what}: {_describe(inst, code)}.")


def open_session(resource: str, timeout_s: float) -> "VisaSession":
    """Opens an instrument by its VISA resource string."""
    rm = _resource_manager()
    try:
        inst = rm.open_resource(resource, open_timeout=2000)
    except _visa_errors() as ex:
        if _error_code(ex) == ERROR_RESOURCE_NOT_FOUND:
            raise MeterException(f"VISA found nothing at {resource}.") from ex
        raise _fail(None, ex, f"open {resource}") from ex
    session = VisaSession(inst, resource)
    session.set_timeout(timeout_s)
    return session


def _ascii(data: bytes) -> str:
    """Encoding.ASCII.GetString: a byte above 127 is a question mark."""
    return bytes(b if b < 128 else 63 for b in data).decode("ascii")


class VisaSession:
    """One open instrument."""

    def __init__(self, inst: Any, resource: str):
        self._inst = inst
        self.resource = resource
        self._closed = False

    def _call(self, what: str, work: Any) -> Any:
        try:
            return work()
        except _visa_errors() as ex:
            raise _fail(self._inst, ex, what) from ex

    def set_timeout(self, seconds: float) -> None:
        def work() -> None:
            self._inst.timeout = max(1.0, seconds * 1000)

        self._call("set the timeout", work)

    def write(self, text: str) -> None:
        data = text.encode("ascii", errors="replace")
        self._call(f"send {text.strip()} to {self.resource}", lambda: self._inst.write_raw(data))

    def read(self) -> str:
        """Reads one reply, up to the END (EOI) or the termination character."""
        return _ascii(self._call(f"read from {self.resource}", self._inst.read_raw))

    def clear(self) -> None:
        """The device clear (SDC): stops whatever it was doing and empties its buffers."""
        self._call(f"clear {self.resource}", self._inst.clear)

    def go_to_local(self) -> None:
        """Sends a GPIB instrument back to local, so its front panel works again."""
        try:
            self._inst.visalib.gpib_control_ren(self._inst.session, _GPIB_REN_ADDRESS_GTL)
        except Exception:
            pass  # not GPIB, or not supported

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        try:
            self._inst.close()
        except Exception:
            pass
