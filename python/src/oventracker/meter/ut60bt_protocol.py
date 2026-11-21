"""The UNI-T UT60BT's Bluetooth LE frames (Meter/Ut60btProtocol.cs): the 19-byte data frame, the commands, the
reply with the meter's name, what the meter is set to, the reading, and picking frames out of the meter's
notifications.

Sources: olegv142/dmmpy (adapters/ut61xp.py, its UT60BT class; The Unlicense), ljakob/unit_ut61eplus (the UT61E+
over USB; same frame) and libreble/multimeter's UNI-T notes (docs/protocols/uni-t.md, from captures of a real
UT60BT). They agree on the frame, the checksum, the mode numbers and the flag bits used here."""

from __future__ import annotations

from datetime import datetime

from ..netfmt import round_digits
from ..probes import Sensor
from ..units import TempUnit
from .base import MeterConfig, MeterReading, ReadingStatus

HEADER1 = 0xAB
HEADER2 = 0xCD
DATA_LENGTH = 19
CELSIUS_MODE = 10
FAHRENHEIT_MODE = 11

#: Microchip's (ISSC) "transparent UART" service, which the meter's Bluetooth module offers: the meter notifies on
#: one characteristic, and takes commands written to the other.
SERVICE = "49535343-fe7d-4ae5-8fa9-9fafd205e455"
NOTIFY = "49535343-1e4d-4bd9-ba61-23c647249616"
WRITE = "49535343-8841-43f4-a8d4-ecbe34729bb3"

#: The start of the name it advertises: "UT60BT" (some meters add a letter, e.g. "UT60BTk").
ADVERTISED_NAME = "UT60BT"


def command(code: int) -> bytes:
    """A command: AB CD 03, the command byte, and the 16-bit big-endian sum of those four bytes (the length, 3,
    counts the command and the checksum). So 5E gives AB CD 03 5E 01 D9."""
    total = HEADER1 + HEADER2 + 0x03 + code
    return bytes([HEADER1, HEADER2, 0x03, code, (total >> 8) & 0xFF, total & 0xFF])


#: Asks for the meter's name. libreble found a UT60BT ignores requests for data until it has answered this.
GET_NAME = command(0x5F)
#: Asks for data: libreble's command, after which its UT60BT sent frames a few times a second.
GET_DATA = command(0x5D)
#: Asks for one frame: dmmpy's and unit_ut61eplus's command (the UT61E+ answers each with a frame).
TRIGGER = command(0x5E)

MODE_NAMES = [
    "AC V", "AC mV", "DC V", "DC mV", "Hz", "duty cycle", "resistance", "continuity", "diode", "capacitance",
    "temperature (°C)", "temperature (°F)", "DC µA", "AC µA", "DC mA", "AC mA", "DC A", "AC A", "hFE", "live wire",
    "NCV", "LoZ V", "AC A", "DC A", "LPF", "AC/DC", "LPF", "AC+DC", "LPF", "AC+DC", "inrush",
]  # fmt: skip


def mode_name(mode: int) -> str:
    return MODE_NAMES[mode] if 0 <= mode < len(MODE_NAMES) else f"mode {mode}"


def checksum_ok(frame: bytes | bytearray | memoryview) -> bool:
    """Whether bytes [0..-2] sum to the big-endian number in the last two."""
    if len(frame) < 5:
        return False
    return (sum(frame[:-2]) & 0xFFFF) == ((frame[-2] << 8) | frame[-1])


def is_data(f: bytes | bytearray | memoryview) -> bool:
    """Whether these bytes are a data frame: the header, length 16, the checksum, a known mode and an ASCII range digit."""
    return (
        len(f) == DATA_LENGTH
        and f[0] == HEADER1
        and f[1] == HEADER2
        and f[2] == DATA_LENGTH - 3
        and checksum_ok(f)
        and f[3] < len(MODE_NAMES)
        and 0x30 <= f[4] <= 0x39
    )


def name_in(f: bytes | bytearray | memoryview) -> str | None:
    """The name in a reply to GET_NAME (AB CD, length, ASCII name, checksum), or None if it isn't one."""
    if len(f) < 6 or len(f) == DATA_LENGTH or f[0] != HEADER1 or f[1] != HEADER2 or f[2] != len(f) - 3 or not checksum_ok(f):
        return None
    name = bytes(f[3:-2])
    if any(c < 0x20 or c > 0x7E for c in name):
        return None
    return name.decode("ascii").strip()


class Ut60btFrame:
    """One 19-byte data frame from a UNI-T UT60BT (UNI-T's "iDMM" framing, shared with the UT61E+ family): AB CD 10,
    the mode, the range (an ASCII digit), seven ASCII characters of display, two bargraph bytes, three flag bytes,
    and a 16-bit big-endian sum of the first 17 bytes."""

    __slots__ = ("_b",)

    def __init__(self, data: bytes | bytearray | memoryview):
        if not is_data(data):
            raise ValueError("Not a valid UT60BT data frame.")
        self._b = bytes(data)

    @property
    def bytes(self) -> bytes:
        return self._b

    @property
    def mode(self) -> int:
        """The function the dial is on: 10 is temperature in °C, 11 in °F."""
        return self._b[3]

    @property
    def range(self) -> int:
        return self._b[4] - 0x30

    @property
    def display(self) -> str:
        """The display as the meter sends it: seven characters, with its sign and decimal point, e.g. "  234"."""
        return self._b[5:12].decode("ascii", errors="replace")

    @property
    def max(self) -> bool:
        return (self._b[14] & 0x08) != 0

    @property
    def min(self) -> bool:
        return (self._b[14] & 0x04) != 0

    @property
    def hold(self) -> bool:
        return (self._b[14] & 0x02) != 0

    @property
    def relative(self) -> bool:
        return (self._b[14] & 0x01) != 0

    @property
    def manual_range(self) -> bool:
        return (self._b[15] & 0x04) != 0

    @property
    def low_battery(self) -> bool:
        """The low-battery sign: the manual shows it from 4.2 V (three AAA cells), while the meter still measures."""
        return (self._b[15] & 0x02) != 0

    @property
    def peak_max(self) -> bool:
        return (self._b[16] & 0x04) != 0

    @property
    def peak_min(self) -> bool:
        return (self._b[16] & 0x02) != 0

    @property
    def overload(self) -> bool:
        """"OL" on the display (with or without a dot or sign): for temperature, no thermocouple, or out of range."""
        return self.display.replace(".", "").strip() in ("OL", "-OL")

    @property
    def value(self) -> float | None:
        """The number on the display, or None when it isn't one (an overload, or something the app can't read)."""
        if self.overload:
            return None
        text = self.display.strip()
        # As .NET's AllowLeadingSign | AllowDecimalPoint: digits with at most one point, and a sign in front.
        body = text[1:] if text[:1] in ("-", "+") else text
        if not body or body.count(".") > 1 or not body.replace(".", "").isdigit() or not body.replace(".", "").isascii():
            return None
        return float(text)

    def __str__(self) -> str:
        return self._b.hex().upper()

    def __repr__(self) -> str:
        return f"Ut60btFrame({self})"


def config(f: Ut60btFrame) -> MeterConfig:
    """What the meter is set to: only plain temperature (no HOLD, REL, MAX/MIN or peak) gives readings the app can use."""
    if f.mode not in (CELSIUS_MODE, FAHRENHEIT_MODE):
        return MeterConfig(mode_name(f.mode), False, None)
    unit = TempUnit.Fahrenheit if f.mode == FAHRENHEIT_MODE else TempUnit.Celsius
    shown = "°F" if unit == TempUnit.Fahrenheit else "°C"
    held = (
        "HOLD" if f.hold
        else "REL" if f.relative
        else "MAX/MIN" if f.max or f.min
        else "peak" if f.peak_max or f.peak_min
        else None
    )  # fmt: skip
    if held is None:
        return MeterConfig(f"temperature, {shown}", True, unit, Sensor.thermocouple("K"))
    return MeterConfig(f"temperature, {shown}, with {held} on", False, unit)


def to_reading(time: datetime, f: Ut60btFrame) -> MeterReading:
    raw = str(f)
    if not config(f).is_temperature:
        return MeterReading(time, ReadingStatus.NotTemperature, None, raw)
    if f.overload:
        return MeterReading(time, ReadingStatus.Overload, None, raw)
    shown = f.value
    if shown is None:
        return MeterReading(time, ReadingStatus.Error, None, raw)
    c = (shown - 32) / 1.8 if f.mode == FAHRENHEIT_MODE else shown
    return MeterReading(time, ReadingStatus.Ok, round_digits(c, 4), raw)


def describe(f: Ut60btFrame) -> str:
    return f"{mode_name(f.mode)} {f.display.strip()}"


class Ut60btAssembler:
    """Picks frames out of the meter's notifications. libreble saw each notification carry one whole frame, but the
    bytes are treated as one stream anyway, so a frame split across notifications, or two in one, still comes
    through: find AB CD, read the length, and keep the frame only if its checksum is right."""

    #: Longer than any frame the meter sends; a bigger length byte means AB CD was data, not a header.
    MAX_LENGTH = 64

    def __init__(self) -> None:
        self._buffer = bytearray()

    def add(self, notification: bytes | bytearray | memoryview) -> list[bytes]:
        """The whole frames (data frames and replies) completed by these bytes."""
        found: list[bytes] = []
        buf = self._buffer
        buf.extend(notification)
        i = 0
        while len(buf) - i >= 3:
            if buf[i] != HEADER1 or buf[i + 1] != HEADER2 or not 3 <= buf[i + 2] <= self.MAX_LENGTH - 3:
                i += 1
                continue
            total = 3 + buf[i + 2]
            if len(buf) - i < total:
                break  # the rest is still to come
            candidate = bytes(buf[i : i + total])
            if checksum_ok(candidate):
                found.append(candidate)
                i += total
            else:
                i += 1
        del buf[:i]
        if len(buf) > 2 * self.MAX_LENGTH:
            del buf[: len(buf) - self.MAX_LENGTH]
        return found
