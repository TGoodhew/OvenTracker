"""The text protocol of the Keysight (Agilent) U1250-series handheld meters (Meter/U1253BProtocol.cs).

As spoken through the U1173 IR-USB cable or the U1177A IR-to-Bluetooth adapter: 9600 baud 8N1, a command ends in
LF, a reply ends in CR LF. A reply of ``*E`` means the command was not accepted; other lines that start with
``*`` are events the meter sends on its own (dial turned, button pressed)."""

from __future__ import annotations

import math
import re
from datetime import datetime

from ..probes import Sensor
from ..units import TempUnit, f_to_c
from .base import MeterConfig, MeterIdentity, MeterReading, ReadingStatus

BAUD_RATE = 9600
IDENTIFY = "*IDN?"
FETCH = "FETC?"
CONFIGURATION = "CONF?"
BATTERY = "SYST:BATT?"
BEEP_COMMAND = "SYST:BEEP"

_FLOAT = re.compile(r"^\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?\s*$")
_SPECIAL = {"nan": math.nan, "infinity": math.inf, "+infinity": math.inf, "-infinity": -math.inf, "∞": math.inf, "-∞": -math.inf}


def try_parse_number(reply: str) -> float | None:
    """double.TryParse(reply.Trim(), NumberStyles.Float, InvariantCulture)."""
    s = reply.strip()
    if _FLOAT.match(s):
        try:
            return float(s)
        except (ValueError, OverflowError):
            return None
    return _SPECIAL.get(s.lower())


def is_overload(value: float) -> bool:
    """What the meter sends for OL / −OL: ±9.9E+37."""
    return math.isnan(value) or abs(value) >= 9.0e37


def is_command_error(line: str) -> bool:
    return line.strip() == "*E"


def is_event(line: str) -> bool:
    """An unsolicited event line such as ``*3`` (dial moved) or ``*L`` (button)."""
    s = line.strip()
    return 2 <= len(s) <= 3 and s[0] == "*" and not is_command_error(s)


def _split(s: str) -> list[str]:
    return [p for p in re.split(r"[ ,]", s) if p]


def parse_config(reply: str) -> MeterConfig:
    """Parses a CONF? reply. Temperature on a U1253B reads ``"TEMP:K CEL"`` or ``"TEMP:K FAR"`` (``T1:K`` on some
    U1250 models); the letter after the colon is the thermocouple type."""
    s = reply.strip().strip('"').strip()
    parts = _split(s)
    function = parts[0] if parts else ""
    is_temp = function.upper().startswith("TEMP") or (
        len(function) >= 3 and function[0] in "Tt" and ":" in function and function[1].isdigit()
    )
    unit = None
    for p in parts[1:]:
        if p.upper() == "CEL":
            unit = TempUnit.Celsius
        elif p.upper().startswith("FA"):
            unit = TempUnit.Fahrenheit
    colon = function.find(":")
    sensor = Sensor.thermocouple(function[colon + 1 :]) if is_temp and colon >= 0 else None
    return MeterConfig(s, is_temp, unit, sensor)


def parse_identity(reply: str) -> MeterIdentity:
    f = reply.strip().split(",")

    def at(i: int) -> str:
        return f[i].strip() if i < len(f) else "?"

    return MeterIdentity(at(0), at(1), at(2), at(3))


def to_reading(time: datetime, reply: str, config: MeterConfig | None) -> MeterReading:
    """Turns a FETC? reply into a reading, given the meter's current configuration."""
    v = try_parse_number(reply)
    if v is None:
        return MeterReading(time, ReadingStatus.Error, None, reply)
    if config is None or not config.is_temperature:
        return MeterReading(time, ReadingStatus.NotTemperature, None, reply)
    if is_overload(v):
        return MeterReading(time, ReadingStatus.Overload, None, reply)
    c = f_to_c(v) if config.unit == TempUnit.Fahrenheit else v
    return MeterReading(time, ReadingStatus.Ok, c, reply)
