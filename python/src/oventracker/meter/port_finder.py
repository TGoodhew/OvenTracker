"""The serial ports the system knows, and which are Bluetooth links (Meter/PortFinder.cs).

Linux: the devices under /dev (rfcomm, ttyUSB, ttyACM) with their USB product names from sysfs, and the other
ports pyserial lists. Windows: pyserial's serial.tools.list_ports (imported only here), with the Bluetooth
device's address from its instance ID; without pyserial, the names in the registry."""

from __future__ import annotations

import glob
import os
import re
import sys
from dataclasses import dataclass
from typing import Iterable

_COM_NAME = re.compile(r"\((COM\d+)\)", re.IGNORECASE)
_BT_ADDRESS = re.compile(r"[&\\]([0-9A-F]{12})_", re.IGNORECASE)


@dataclass(frozen=True)
class PortInfo:
    """``port``: the name to open (COM5, /dev/rfcomm0). ``is_bluetooth_outgoing``: a link to a paired Bluetooth
    device (on Windows a "Standard Serial over Bluetooth link" port with the device's address; on Linux an RFCOMM
    device bound with rfcomm bind)."""

    port: str
    description: str
    is_bluetooth: bool
    is_bluetooth_outgoing: bool
    bluetooth_address: str | None


def _windows() -> bool:
    return sys.platform == "win32"


def same_name(a: str, b: str) -> bool:
    """Port names are case-insensitive on Windows (COM5 is com5) and case-sensitive elsewhere."""
    return a.upper() == b.upper() if _windows() else a == b


def normalize(port: str) -> str:
    """A port name as the system writes it: upper case on Windows, untouched elsewhere."""
    return port.strip().upper() if _windows() else port.strip()


def _key(name: str) -> str:
    return name.upper() if _windows() else name


def list_ports() -> list[PortInfo]:
    found: dict[str, PortInfo] = {}
    try:
        if _windows():
            for p in _from_windows():
                found[_key(p.port)] = p
        elif sys.platform.startswith("linux"):
            for p in _from_linux():
                found[_key(p.port)] = p
    except Exception:
        pass  # the details are a nicety: fall back to names only
    for name in _port_names():
        found.setdefault(_key(name), PortInfo(name, "Serial port", False, False, None))
    return sort(found.values())


def _split_number(port: str) -> tuple[str, int]:
    i = len(port)
    while i > 0 and port[i - 1] in "0123456789":
        i -= 1
    return port[:i], (int(port[i:]) if i < len(port) else -1)


def sort(ports: Iterable[PortInfo]) -> list[PortInfo]:
    """Bluetooth links first, then by name with numbers in numeric order (COM2 before COM10)."""

    def key(p: PortInfo) -> tuple[int, list[int], int, list[int]]:
        stem, number = _split_number(p.port)
        return (0 if p.is_bluetooth_outgoing else 1, [ord(c) for c in stem], number, [ord(c) for c in p.port])

    return sorted(ports, key=key)


def describe_linux(path: str, usb_product: str | None) -> PortInfo:
    """What a Linux serial device is, from its name and, for a USB one, the product name the device reports."""
    name = os.path.basename(path)
    if name.startswith("rfcomm"):
        return PortInfo(path, "Bluetooth serial link (RFCOMM)", True, True, None)
    if name.startswith("ttyUSB"):
        return PortInfo(path, usb_product or "USB serial adapter", False, False, None)
    if name.startswith("ttyACM"):
        return PortInfo(path, usb_product or "USB serial device", False, False, None)
    return PortInfo(path, "Serial port", False, False, None)


def _from_linux() -> list[PortInfo]:
    out = []
    for pattern in ("rfcomm*", "ttyUSB*", "ttyACM*"):
        for path in sorted(glob.glob(os.path.join("/dev", pattern))):
            out.append(describe_linux(path, _usb_product(os.path.basename(path))))
    return out


def _usb_product(tty: str) -> str | None:
    """The USB product string behind a tty, found by walking up its sysfs device path."""
    try:
        d = os.path.realpath(os.path.join("/sys/class/tty", tty, "device"))
        while d.startswith("/sys/devices"):
            product = os.path.join(d, "product")
            if os.path.isfile(product):
                with open(product, encoding="utf-8", errors="replace") as fh:
                    text = fh.read().strip()
                return text or None
            parent = os.path.dirname(d)
            if parent == d:
                break
            d = parent
    except OSError:
        pass
    return None


def _from_windows() -> list[PortInfo]:
    from serial.tools import list_ports  # type: ignore[import-not-found]

    out = []
    for p in list_ports.comports():
        name = p.description or ""
        hwid = p.hwid or ""
        m = _COM_NAME.search(name)
        port = m.group(1).upper() if m else p.device.upper()
        is_bt = hwid.upper().startswith("BTHENUM")
        addr = None
        if is_bt:
            a = _BT_ADDRESS.search(hwid)
            if a:
                addr = a.group(1).upper()
        outgoing = is_bt and addr is not None and addr != "000000000000"
        description = (name.replace(m.group(0), "") if m else name).strip() or "Serial port"
        out.append(
            PortInfo(port, description, is_bt, outgoing, None if addr is None else ":".join(addr[i : i + 2] for i in range(0, 12, 2)))
        )
    return out


def _port_names() -> list[str]:
    """SerialPort.GetPortNames: the names the system has, without details."""
    names: list[str] = []
    if _windows():
        try:
            import winreg

            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"HARDWARE\DEVICEMAP\SERIALCOMM") as key:
                i = 0
                while True:
                    try:
                        names.append(str(winreg.EnumValue(key, i)[1]))
                    except OSError:
                        break
                    i += 1
        except OSError:
            pass
        return names
    try:
        from serial.tools import list_ports  # type: ignore[import-not-found]

        names.extend(p.device for p in list_ports.comports())
    except Exception:
        pass  # without pyserial, only the devices found above
    return names
