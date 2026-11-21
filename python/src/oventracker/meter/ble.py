"""Bluetooth LE (Meter/Ble/BleLink.cs), through bleak, which is an optional extra: ``pip install "./python[ble]"``.
bleak is imported only when Bluetooth LE is used, so everything else runs without it.

bleak works the same way on each system: Windows through its Bluetooth LE (WinRT) API, Linux through BlueZ, macOS
through CoreBluetooth. It finds devices by scanning for their advertisements, so unlike the C# app (which lists
the devices Windows has paired), the meter needn't be paired: it must be on and advertising. bleak is asyncio, so
its calls run on an event loop of their own, on a background thread, and the rest of the app stays synchronous."""

from __future__ import annotations

import asyncio
import sys
import threading
from dataclasses import dataclass
from typing import Any, Callable, Coroutine, TypeVar

from .base import MeterException

T = TypeVar("T")

#: How long a scan for devices takes.
SCAN_SECONDS = 5.0


@dataclass(frozen=True)
class BleDevice:
    """A Bluetooth LE device seen in a scan. ``address``: its Bluetooth address, e.g. "C4:BE:84:12:34:56" (on
    macOS, the identifier CoreBluetooth gives it instead)."""

    name: str
    address: str


class BleLink:
    """A subscription to one GATT characteristic of a Bluetooth LE device: the bytes of each notification or
    indication arrive through the callback given to :meth:`open`, on another thread. Tests use a fake."""

    @property
    def address(self) -> str:
        raise NotImplementedError

    def open(self, received: Callable[[bytes], None]) -> None:
        """Connects, and subscribes to the characteristic."""
        raise NotImplementedError

    def write(self, data: bytes) -> None:
        """Sends bytes to the device's command characteristic (the ``write`` one given to :func:`link_for`), after
        :meth:`open`: for a meter that answers requests, such as the UT60BT. A meter that only streams (the 121GW)
        never calls it."""
        raise NotImplementedError("This Bluetooth LE link has no characteristic to write to.")

    def dispose(self) -> None:
        raise NotImplementedError


def normalize_address(text: str) -> str | None:
    """"c4be84123456", "C4:BE:84:12:34:56" or "C4-BE-84-12-34-56" as "C4:BE:84:12:34:56". None if it isn't one."""
    hex_digits = "".join(ch for ch in text if ch in "0123456789abcdefABCDEF").upper()
    clean = text.strip().replace(":", "").replace("-", "")
    if len(hex_digits) != 12 or len(clean) != 12:
        return None
    return ":".join(hex_digits[i * 2 : i * 2 + 2] for i in range(6))


# bleak, imported when first needed (False: not tried yet).
_bleak_module: Any = False


def _bleak() -> Any:
    """The bleak module, or None if it isn't installed (or won't import)."""
    global _bleak_module
    if _bleak_module is False:
        try:
            import bleak  # type: ignore[import-not-found]

            _bleak_module = bleak
        except Exception:  # ImportError, or a backend that won't load
            _bleak_module = None
    return _bleak_module


def _reset() -> None:
    """Forgets whether bleak was found, and the last scan's error: for tests."""
    global _bleak_module, _scan_error
    _bleak_module = False
    _scan_error = None


def is_supported() -> bool:
    """Whether Bluetooth LE can be used: bleak is installed."""
    return _bleak() is not None


def not_supported_help() -> str:
    return "Bluetooth LE needs bleak: pip install bleak (or pip install ./python[ble])."


class _Loop:
    """An asyncio event loop on a daemon thread, for bleak, shared by every link and scan."""

    _lock = threading.Lock()
    _loop: asyncio.AbstractEventLoop | None = None

    @classmethod
    def get(cls) -> asyncio.AbstractEventLoop:
        with cls._lock:
            if cls._loop is None or cls._loop.is_closed():
                loop = asyncio.new_event_loop()
                threading.Thread(target=loop.run_forever, name="oventracker-ble", daemon=True).start()
                cls._loop = loop
            return cls._loop


def _run(coro: Coroutine[Any, Any, T], timeout: float) -> T:
    """Runs a coroutine on the Bluetooth LE loop and waits for it. A TimeoutError if it takes longer."""
    future = asyncio.run_coroutine_threadsafe(coro, _Loop.get())
    try:
        return future.result(timeout)
    except TimeoutError:
        future.cancel()
        raise


def _linux() -> bool:
    return sys.platform.startswith("linux")


def explain(ex: BaseException, address: str | None = None) -> str:
    """What went wrong in bleak, in terms of what to do about it (BlueZLink.Explain, for bleak's errors)."""
    kind = type(ex).__name__
    dbus = getattr(ex, "dbus_error", None)
    if isinstance(dbus, str):
        if dbus == "org.freedesktop.DBus.Error.ServiceUnknown":
            return "BlueZ isn't running. Install bluez and start it (sudo systemctl enable --now bluetooth)."
        if dbus == "org.freedesktop.DBus.Error.AccessDenied":
            return "BlueZ refused access. Add yourself to the bluetooth group (sudo usermod -aG bluetooth $USER), then log in again."
        if dbus == "org.bluez.Error.NotReady":
            return "The Bluetooth adapter is off. Turn it on (bluetoothctl power on)."
        if dbus in ("org.bluez.Error.Failed", "org.bluez.Error.NotAvailable"):
            return f"BlueZ couldn't connect to the meter: {ex}. Check it's on, with Bluetooth on, and in range."
        return f"BlueZ: {ex}"
    if kind == "BleakDeviceNotFoundError":
        where = address or "the meter"
        return f"Can't see {where}. Check the meter is on with Bluetooth on (BT on its display), and in range."
    if kind == "BleakCharacteristicNotFoundError":
        return f"{address or 'The device'} connected, but doesn't offer the meter's GATT characteristic."
    if kind == "BleakBluetoothNotAvailableError":
        reason = getattr(getattr(ex, "reason", None), "name", "")
        if reason == "POWERED_OFF":
            return "Bluetooth is off on this computer. Turn it on." + (" (bluetoothctl power on)" if _linux() else "")
        if reason in ("NO_BLUETOOTH", "NO_BLE_CENTRAL_ROLE"):
            return "This computer has no Bluetooth LE adapter (or it isn't plugged in)."
        if reason.startswith("DENIED"):
            if _linux():
                return "Bluetooth access was refused. Add yourself to the bluetooth group (sudo usermod -aG bluetooth $USER), then log in again."
            return "Bluetooth access was refused. Allow this terminal to use Bluetooth in the system's privacy settings."
        return f"Bluetooth isn't available: {ex}"
    if isinstance(ex, TimeoutError):
        return f"Timed out connecting to {address or 'the meter'}. Check it's on, with Bluetooth on, and in range."
    if isinstance(ex, (FileNotFoundError, ConnectionRefusedError)) and _linux():
        # dbus-fast can't reach the system bus.
        return f"Can't reach the system D-Bus, which BlueZ uses. Is this a full Linux system with Bluetooth (not WSL)? ({ex})"
    text = str(ex)
    return f"Bluetooth LE: {text}" if text else f"Bluetooth LE: {kind}"


# Why the last scan found nothing, when it failed: for the driver's help.
_scan_error: str | None = None


def scan_error() -> str | None:
    """Why the last scan failed, in words for people; None if it worked (or there hasn't been one)."""
    return _scan_error


def scan(timeout: float = SCAN_SECONDS) -> list[BleDevice]:
    """The Bluetooth LE devices advertising nearby: a scan of ``timeout`` seconds (BleakScanner.discover). Empty
    without bleak, or where Bluetooth LE can't be reached; :func:`scan_error` then says why."""
    global _scan_error
    bleak = _bleak()
    if bleak is None:
        _scan_error = None
        return []
    try:
        found = _run(bleak.BleakScanner.discover(timeout=timeout), timeout + 10)
    except Exception as ex:
        _scan_error = explain(ex)
        return []
    _scan_error = None
    out: list[BleDevice] = []
    seen: set[str] = set()
    for d in found:
        raw = str(getattr(d, "address", "") or "")
        address = normalize_address(raw) or raw
        if not address or address in seen:
            continue
        seen.add(address)
        out.append(BleDevice(str(getattr(d, "name", None) or ""), address))
    return out


def list_devices() -> list[BleDevice]:
    """The Bluetooth LE devices the system can see. bleak has no list of known devices that works the same on
    each system, so this is a scan too (BleLinks.List in the C# app lists paired ones on Windows)."""
    return scan()


class BleakLink(BleLink):
    """One GATT characteristic through bleak: BleakClient(address), connect, then start_notify. bleak uses
    notifications when the characteristic has them and indications otherwise; each value arrives on the
    Bluetooth LE loop's thread. ``write_characteristic``: the one :meth:`write` uses, if any. ``connect_timeout``:
    for finding the device (bleak scans for it) and connecting."""

    def __init__(
        self, address: str, service: str, characteristic: str, write_characteristic: str | None = None, connect_timeout: float = 20.0
    ):
        self._address = address
        self.service = service
        self.characteristic = characteristic
        self.write_characteristic = write_characteristic
        self.connect_timeout = connect_timeout
        self._client: Any = None
        self._notifying = False

    @property
    def address(self) -> str:
        return self._address

    def open(self, received: Callable[[bytes], None]) -> None:
        bleak = _bleak()
        if bleak is None:
            raise MeterException(not_supported_help())
        self.dispose()

        def on_value(_characteristic: Any, data: bytearray) -> None:
            try:
                received(bytes(data))
            except Exception:
                pass  # never into bleak's loop

        async def start() -> Any:
            client = bleak.BleakClient(self._address, timeout=self.connect_timeout)
            await client.connect()
            try:
                # As the C# app does: a missing command characteristic fails here, not as unanswered requests later.
                services = getattr(client, "services", None)
                if self.write_characteristic is not None and services is not None and services.get_characteristic(self.write_characteristic) is None:
                    raise MeterException(f"{self._address} connected, but doesn't offer the meter's GATT command characteristic.")
                await client.start_notify(self.characteristic, on_value)
            except BaseException:
                try:
                    await client.disconnect()
                except Exception:
                    pass
                raise
            return client

        try:
            self._client = _run(start(), self.connect_timeout + 10)
        except MeterException:
            raise
        except Exception as ex:
            raise MeterException(explain(ex, self._address)) from ex
        self._notifying = True

    def write(self, data: bytes) -> None:
        """Writes to the command characteristic without a response, as dmmpy does for the UT60BT."""
        client = self._client
        if client is None or self.write_characteristic is None:
            raise MeterException("The Bluetooth LE link isn't open for writing.")
        try:
            _run(client.write_gatt_char(self.write_characteristic, bytes(data), response=False), 10)
        except Exception as ex:
            raise MeterException(explain(ex, self._address)) from ex

    def dispose(self) -> None:
        client, self._client = self._client, None
        notifying, self._notifying = self._notifying, False
        if client is None:
            return

        async def stop() -> None:
            if notifying:
                try:
                    await client.stop_notify(self.characteristic)
                except Exception:
                    pass  # the link may already be gone
            try:
                await client.disconnect()
            except Exception:
                pass

        try:
            _run(stop(), 10)
        except Exception:
            pass


def link_for(address: str, service: str, characteristic: str, write: str | None = None) -> BleLink:
    """BleLinks.For: the link to a device's ``characteristic`` (notified), and, if given, the ``write``
    characteristic that :meth:`BleLink.write` uses."""
    if not is_supported():
        raise MeterException(not_supported_help())
    return BleakLink(address, service, characteristic, write)
