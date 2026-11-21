"""The EEVblog 121GW's Bluetooth LE packets (Meter/Eevblog121GwProtocol.cs): the 19-byte packet, its validation,
the mode and range tables, what the meter is set to, the reading, and putting packets back together from the
meter's notifications.

Source: the 121GW Bluetooth packet format V2 (EEVblog, 2018-07-03), as relayed by the dmm-tools project's write-up
(docs/research/121gw/reverse-engineered-protocol.md), and QtDMM's 121GW decoder. Both agree, and agree with
captures from real meters. The official PDF wasn't checked directly."""

from __future__ import annotations

from datetime import datetime

from ..netfmt import round_digits, round_trip
from ..probes import Sensor
from ..units import TempUnit
from .base import MeterConfig, MeterReading, ReadingStatus

START = 0xF2
LENGTH = 19
TEMPERATURE_MODE = 5

#: The service and characteristic the meter streams on (from EEVblog's and UEi's apps).
SERVICE = "0bd51666-e7cb-469b-8e4d-2742f1ba77cc"
CHARACTERISTIC = "e7add780-b042-4876-aae1-112855353cc1"

#: The name it advertises.
ADVERTISED_NAME = "121GW"

#: Decimal places for each mode's ranges.
RANGE_DECIMALS: list[list[int]] = [
    [1],  # 0 Low Z
    [4, 3, 2, 1],  # 1 DC V
    [4, 3, 2, 1],  # 2 AC V
    [3, 2],  # 3 DC mV
    [3, 2],  # 4 AC mV
    [1],  # 5 temperature
    [3, 2, 4, 3, 2],  # 6 Hz (Hz, then kHz)
    [4, 3, 2],  # 7 pulse width, ms
    [1],  # 8 duty, %
    [3, 2, 4, 3, 2, 4, 3],  # 9 resistance (Ω, kΩ, MΩ)
    [2],  # 10 continuity
    [4, 3],  # 11 diode
    [2, 1, 3, 2, 1, 0],  # 12 capacitance
    [2, 1, 1, 0], [3, 2, 2, 1], [1, 0, 3, 2],  # 13–15 AC VA
    [3, 2], [3, 2],  # 16–17 µA
    [4, 3], [4, 3],  # 18–19 mA
    [2, 4, 3], [2, 4, 3],  # 20–21 A
    [2, 1, 1, 0], [3, 2, 2, 1], [1, 0, 3, 2],  # 22–24 DC VA
]  # fmt: skip

MODE_NAMES = [
    "Low Z", "DC V", "AC V", "DC mV", "AC mV", "temperature", "Hz", "pulse width", "duty cycle", "resistance",
    "continuity", "diode", "capacitance", "AC µVA", "AC mVA", "AC VA", "AC µA", "DC µA", "AC mA", "DC mA",
    "AC A", "DC A", "DC µVA", "DC mVA", "DC VA",
]  # fmt: skip


def mode_name(mode: int) -> str:
    return MODE_NAMES[mode] if 0 <= mode < len(MODE_NAMES) else f"mode {mode}"


def decimals(mode: int, range_: int) -> int:
    return RANGE_DECIMALS[mode][range_] if 0 <= mode < len(RANGE_DECIMALS) and 0 <= range_ < len(RANGE_DECIMALS[mode]) else 0


def is_valid(p: bytes | bytearray | memoryview) -> bool:
    """Whether these 19 bytes are a packet: the start byte, the checksum, and a mode and range that exist."""
    if len(p) != LENGTH or p[0] != START:
        return False
    x = 0
    for v in p:
        x ^= v
    if x != 0:
        return False
    mode = p[5] & 0x1F
    if mode >= len(RANGE_DECIMALS) or (p[6] & 0x0F) >= len(RANGE_DECIMALS[mode]):
        return False
    return (p[13] & 0xE0) == 0


class Gw121Packet:
    """One 19-byte data packet from an EEVblog 121GW: F2, 17 bytes of display data, and an XOR checksum that makes
    all 19 bytes XOR to zero. Multi-byte fields are big-endian."""

    __slots__ = ("_b",)

    def __init__(self, data: bytes | bytearray | memoryview):
        if not is_valid(data):
            raise ValueError("Not a valid 121GW packet.")
        self._b = bytes(data)

    @property
    def bytes(self) -> bytes:
        return self._b

    @property
    def mode(self) -> int:
        """The main display's mode, 0–24 (5 is temperature)."""
        return self._b[5] & 0x1F

    @property
    def range(self) -> int:
        """The range, an index into the mode's ranges: it sets the decimal point."""
        return self._b[6] & 0x0F

    @property
    def overload(self) -> bool:
        return (self._b[6] & 0x80) != 0

    @property
    def negative(self) -> bool:
        return (self._b[6] & 0x40) != 0

    @property
    def celsius(self) -> bool:
        """The °C flag (firmware 1.21 and later). An older meter sends neither flag."""
        return (self._b[6] & 0x20) != 0

    @property
    def fahrenheit(self) -> bool:
        return (self._b[6] & 0x10) != 0

    @property
    def count(self) -> int:
        """The main display's count: 18 bits, unsigned (the two high bits from firmware 1.22)."""
        b = self._b
        return ((b[5] >> 6) << 16) | (b[7] << 8) | b[8]

    @property
    def low_battery(self) -> bool:
        return (self._b[15] & 0x01) != 0

    @property
    def auto_power_off_armed(self) -> bool:
        return (self._b[15] & 0x02) != 0

    @property
    def relative(self) -> bool:
        return (self._b[16] & 0x10) != 0

    @property
    def min_max(self) -> int:
        return self._b[16] & 0x07

    @property
    def hold(self) -> int:
        return (self._b[17] >> 2) & 0x03

    @property
    def serial(self) -> str:
        """The meter's identity bytes (year, month and five serial digits), as hex: enough to tell meters apart."""
        return self._b[1:5].hex().upper()

    @property
    def value(self) -> float | None:
        """The main display's value, with its decimal point and sign. None on an overload."""
        if self.overload:
            return None
        return (-1 if self.negative else 1) * self.count / 10.0 ** decimals(self.mode, self.range)

    def __str__(self) -> str:
        return self._b.hex().upper()

    def __repr__(self) -> str:
        return f"Gw121Packet({self})"


def temperature_unit(p: Gw121Packet) -> TempUnit:
    """The unit shown. Firmware 1.21 and later set a °C or °F flag. An older meter sets neither, and is taken as
    °C, as QtDMM does."""
    return TempUnit.Fahrenheit if p.fahrenheit and not p.celsius else TempUnit.Celsius


def config(p: Gw121Packet) -> MeterConfig:
    """What the meter is set to: only plain temperature (no HOLD, REL or MIN/MAX) gives readings the app can use."""
    if p.mode != TEMPERATURE_MODE:
        return MeterConfig(mode_name(p.mode), False, None)
    unit = temperature_unit(p)
    shown = "°F" if unit == TempUnit.Fahrenheit else "°C"
    held = "HOLD" if p.hold != 0 else "REL" if p.relative else "MIN/MAX" if p.min_max != 0 else None
    if held is None:
        return MeterConfig(f"temperature, {shown}", True, unit, Sensor.thermocouple("K"))
    return MeterConfig(f"temperature, {shown}, with {held} on", False, unit)


def to_reading(time: datetime, p: Gw121Packet) -> MeterReading:
    raw = str(p)
    if not config(p).is_temperature:
        return MeterReading(time, ReadingStatus.NotTemperature, None, raw)
    if p.overload:
        return MeterReading(time, ReadingStatus.Overload, None, raw)
    shown = p.value
    assert shown is not None
    c = (shown - 32) / 1.8 if temperature_unit(p) == TempUnit.Fahrenheit else shown
    return MeterReading(time, ReadingStatus.Ok, round_digits(c, 4), raw)


def describe(p: Gw121Packet) -> str:
    v = p.value
    return f"{mode_name(p.mode)} {round_trip(v)}" if v is not None else f"{mode_name(p.mode)} OL"


class Gw121Assembler:
    """Puts packets back together from the meter's notifications. A packet can straddle notifications, and a
    notification can hold parts of two, so the bytes are treated as one stream: find F2, take 19 bytes, and keep
    them only if they check out (a data byte can also be F2). Some meters send each packet as 18 bytes without its
    F2; those are recognised too."""

    def __init__(self) -> None:
        self._buffer = bytearray()

    def add(self, notification: bytes | bytearray | memoryview) -> list[Gw121Packet]:
        found: list[Gw121Packet] = []
        # One packet without its start byte: the 18 bytes after F2.
        if len(notification) == LENGTH - 1:
            whole = bytes([START]) + bytes(notification)
            if is_valid(whole):
                self._buffer.clear()
                found.append(Gw121Packet(whole))
                return found

        buf = self._buffer
        buf.extend(notification)
        i = 0
        while len(buf) - i >= LENGTH:
            if buf[i] != START:
                i += 1
                continue
            candidate = bytes(buf[i : i + LENGTH])
            if is_valid(candidate):
                found.append(Gw121Packet(candidate))
                i += LENGTH
            else:
                i += 1
        del buf[:i]
        if len(buf) > 4 * LENGTH:
            del buf[: len(buf) - LENGTH]
        return found
