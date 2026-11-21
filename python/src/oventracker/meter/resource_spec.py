"""Where a meter is and how it's connected, from --resource (Meter/ResourceSpec.cs), and a serial port's settings.

The Python version opens serial ports only (pyserial, imported when a port is opened). The other forms are
still read, so a driver can say clearly that it doesn't take them."""

from __future__ import annotations

import enum
import re
from dataclasses import dataclass
from typing import Any


class Transport(enum.IntEnum):
    Serial = 0  # RS-232, a USB serial adapter, or a Bluetooth serial link
    Gpib = 1  # GPIB (IEEE-488), through VISA
    Usb = 2  # USB-TMC, through VISA
    Lan = 3  # LAN (VXI-11 or HiSLIP), through VISA
    Ble = 4  # Bluetooth LE (GATT)


class Parity(enum.IntEnum):
    # System.IO.Ports.Parity
    None_ = 0
    Odd = 1
    Even = 2
    Mark = 3
    Space = 4


class StopBits(enum.IntEnum):
    # System.IO.Ports.StopBits
    One = 1
    Two = 2
    OnePointFive = 3


class Handshake(enum.IntEnum):
    # System.IO.Ports.Handshake
    None_ = 0
    XOnXOff = 1
    RequestToSend = 2
    RequestToSendXOnXOff = 3


@dataclass(frozen=True)
class SerialSettings:
    """A serial port's settings: they must match the meter's (often set on its front panel)."""

    baud: int = 9600
    parity: Parity = Parity.None_
    data_bits: int = 8
    stop_bits: StopBits = StopBits.One
    handshake: Handshake = Handshake.None_

    def __str__(self) -> str:
        """"9600,N,8,1" (with ",rts", ",xon" or ",rts+xon" for a handshake)."""
        parity = {Parity.Even: "E", Parity.Odd: "O", Parity.Mark: "M", Parity.Space: "S"}.get(self.parity, "N")
        stop = {StopBits.Two: "2", StopBits.OnePointFive: "1.5"}.get(self.stop_bits, "1")
        hs = {Handshake.RequestToSend: ",rts", Handshake.XOnXOff: ",xon", Handshake.RequestToSendXOnXOff: ",rts+xon"}.get(
            self.handshake, ""
        )
        return f"{self.baud},{parity},{self.data_bits},{stop}{hs}"


#: The U1250 series through a U1177A or U1173: 9600 baud, 8N1.
U1250 = SerialSettings()

_SERIAL_WITH_SETTINGS = re.compile(
    r"^(?P<port>.+?):(?P<baud>\d+)\s*,\s*(?P<parity>[NEOMS])\s*,\s*(?P<data>[5-8])\s*,\s*(?P<stop>1|1\.5|2)\s*"
    r"(,\s*(?P<hs>rts\+xon|rts|xon|dtr|none))?$",
    re.IGNORECASE,
)
_GPIB = re.compile(r"^gpib(?P<board>\d*)(::|:)(?P<addr>\d{1,2})(::(?P<sec>\d{1,3}))?(::INSTR)?$", re.IGNORECASE)


@dataclass(frozen=True)
class ResourceSpec:
    """``address``: for a serial port, its name (COM3, /dev/ttyUSB0); for VISA, the full resource string.
    ``settings``: the serial settings given (None: the driver's default). ``dtr_dsr``: the DTR/DSR handshake was
    asked for (",dtr"). ``visa``: opened through VISA (GPIB, USB-TMC, LAN, or ASRL serial)."""

    transport: Transport
    address: str
    settings: SerialSettings | None = None
    dtr_dsr: bool = False
    visa: bool = False

    @staticmethod
    def parse(text: str | None) -> "ResourceSpec | None":
        """Reads a resource. None if it's empty or a serial settings part is malformed."""
        s = text.strip() if text is not None else ""
        if not s:
            return None
        g = _GPIB.match(s)
        if g:
            board = g.group("board") or "0"
            addr = int(g.group("addr"))
            if addr > 30:
                return None
            sec = f"::{g.group('sec')}" if g.group("sec") is not None else ""
            return ResourceSpec(Transport.Gpib, f"GPIB{board}::{addr}{sec}::INSTR", visa=True)
        if s.lower().startswith("ble:"):
            what = s[4:].strip()
            return None if not what else ResourceSpec(Transport.Ble, what)
        upper = s.upper()
        if upper.startswith("ASRL"):
            return ResourceSpec(Transport.Serial, s, visa=True)
        if upper.startswith("USB") and "::" in s:
            return ResourceSpec(Transport.Usb, s, visa=True)
        if upper.startswith("TCPIP"):
            return ResourceSpec(Transport.Lan, s, visa=True)

        m = _SERIAL_WITH_SETTINGS.match(s)
        if m:
            parity = {"E": Parity.Even, "O": Parity.Odd, "M": Parity.Mark, "S": Parity.Space}.get(
                m.group("parity")[0].upper(), Parity.None_
            )
            stop = {"2": StopBits.Two, "1.5": StopBits.OnePointFive}.get(m.group("stop"), StopBits.One)
            hs = m.group("hs").lower() if m.group("hs") is not None else "none"
            handshake = {
                "rts": Handshake.RequestToSend,
                "xon": Handshake.XOnXOff,
                "rts+xon": Handshake.RequestToSendXOnXOff,
            }.get(hs, Handshake.None_)
            baud = int(m.group("baud"))
            if baud < 50 or baud > 4_000_000:
                return None
            return ResourceSpec(
                Transport.Serial,
                m.group("port").strip(),
                SerialSettings(baud, parity, int(m.group("data")), stop, handshake),
                dtr_dsr=hs == "dtr",
            )
        # A colon followed by something that isn't valid settings is a mistake, not a port name (except \\.\COM10).
        if ":" in s and not s.startswith("\\\\.\\"):
            return None
        return ResourceSpec(Transport.Serial, s)

    def settings_or(self, driver_default: SerialSettings) -> SerialSettings:
        return self.settings if self.settings is not None else driver_default


def describe(t: Transport) -> str:
    return {Transport.Gpib: "GPIB", Transport.Usb: "USB", Transport.Lan: "LAN", Transport.Ble: "Bluetooth LE"}.get(t, "Serial")


def open_serial(port: str, settings: SerialSettings, dtr_dsr: bool) -> Any:
    """A pyserial port configured from these settings, not opened yet (ResourceSpec.OpenSerial). pyserial is
    imported here, so the simulator works without it."""
    try:
        import serial  # type: ignore[import-not-found]
    except ImportError as ex:  # pragma: no cover - depends on the environment
        raise ImportError(
            "A serial meter needs pyserial, which isn't installed: pip install pyserial (or reinstall oventracker)."
        ) from ex
    p = serial.Serial()
    p.port = port
    p.baudrate = settings.baud
    p.parity = {
        Parity.None_: serial.PARITY_NONE,
        Parity.Odd: serial.PARITY_ODD,
        Parity.Even: serial.PARITY_EVEN,
        Parity.Mark: serial.PARITY_MARK,
        Parity.Space: serial.PARITY_SPACE,
    }[settings.parity]
    p.bytesize = settings.data_bits
    p.stopbits = {
        StopBits.One: serial.STOPBITS_ONE,
        StopBits.Two: serial.STOPBITS_TWO,
        StopBits.OnePointFive: serial.STOPBITS_ONE_POINT_FIVE,
    }[settings.stop_bits]
    p.rtscts = settings.handshake in (Handshake.RequestToSend, Handshake.RequestToSendXOnXOff)
    p.xonxoff = settings.handshake in (Handshake.XOnXOff, Handshake.RequestToSendXOnXOff)
    # .NET's SerialPort leaves DTR and RTS off unless asked; pyserial raises them on open. DTR/DSR: hold DTR
    # so the meter may send (the 34401A waits for it).
    p.dtr = dtr_dsr
    if not p.rtscts:
        p.rts = False
    return p
