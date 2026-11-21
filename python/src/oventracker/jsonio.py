"""Reading and writing JSON the way the C# app's System.Text.Json options do (``Json.Options``).

Writing: indented by two spaces, ``Environment.NewLine`` between lines, camelCase property names in the
records' declaration order, nulls left out, enums by name, numbers and strings as .NET writes them
(:mod:`oventracker.netfmt`). Reading: enums by name (any case) or number, a number where a double is expected
is a float.
"""

from __future__ import annotations

import enum
import os
import re
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Any

from .netfmt import json_number, json_string

NEWLINE = os.linesep

_EPOCH = datetime(1, 1, 1)
_TICKS_PER_SECOND = 10_000_000


@dataclass(frozen=True)
class DateTimeOffset:
    """A .NET DateTimeOffset: the local wall-clock time in 100 ns ticks, and the offset from UTC in minutes."""

    ticks: int
    offset_minutes: int

    @property
    def utc_ticks(self) -> int:
        return self.ticks - self.offset_minutes * 60 * _TICKS_PER_SECOND

    def __lt__(self, other: "DateTimeOffset") -> bool:
        return self.utc_ticks < other.utc_ticks

    def __le__(self, other: "DateTimeOffset") -> bool:
        return self.utc_ticks <= other.utc_ticks

    def __gt__(self, other: "DateTimeOffset") -> bool:
        return self.utc_ticks > other.utc_ticks

    def __ge__(self, other: "DateTimeOffset") -> bool:
        return self.utc_ticks >= other.utc_ticks

    _PATTERN = re.compile(
        r"^\s*(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2})(?:\.(\d+))?)?)?\s*(Z|[+-]\d{2}:?\d{2})?\s*$",
        re.IGNORECASE,
    )

    @classmethod
    def parse(cls, text: str) -> "DateTimeOffset":
        m = cls._PATTERN.match(text)
        if not m:
            raise ValueError(f"not a date and time: {text!r}")
        y, mo, d, h, mi, s, frac, off = m.groups()
        base = datetime(int(y), int(mo), int(d), int(h or 0), int(mi or 0), int(s or 0))
        delta = base - _EPOCH
        ticks = (delta.days * 86400 + delta.seconds) * _TICKS_PER_SECOND
        if frac:
            ticks += int((frac + "0000000")[:7])
        if off is None:
            # No offset: local time, as .NET reads it.
            local = base.astimezone()
            offset = int(local.utcoffset().total_seconds() // 60)  # type: ignore[union-attr]
        elif off.upper() == "Z":
            offset = 0
        else:
            sign = -1 if off[0] == "-" else 1
            digits = off[1:].replace(":", "")
            offset = sign * (int(digits[:2]) * 60 + int(digits[2:]))
        return cls(ticks, offset)

    @classmethod
    def from_timestamp_local(cls, timestamp: float) -> "DateTimeOffset":
        """A POSIX timestamp as local time (File.GetCreationTime, converted to a DateTimeOffset)."""
        local = datetime.fromtimestamp(timestamp).astimezone()
        offset = int(local.utcoffset().total_seconds() // 60)  # type: ignore[union-attr]
        naive = local.replace(tzinfo=None)
        delta = naive - _EPOCH
        ticks = (delta.days * 86400 + delta.seconds) * _TICKS_PER_SECOND + delta.microseconds * 10
        return cls(ticks, offset)

    def to_local(self) -> datetime:
        """``ToLocalTime()``: the same instant in this computer's time zone (to the microsecond)."""
        utc = _EPOCH + timedelta(microseconds=self.utc_ticks // 10)
        return utc.replace(tzinfo=timezone.utc).astimezone()

    def wall(self) -> datetime:
        """The time as it was saved, in its own offset (to the microsecond)."""
        utc_offset = timezone(timedelta(minutes=self.offset_minutes))
        return (_EPOCH + timedelta(microseconds=self.ticks // 10)).replace(tzinfo=utc_offset)

    def format(self, fmt: str) -> str:
        """``ToString(format, InvariantCulture)`` for the formats used here: strftime codes on the saved wall time."""
        return self.wall().strftime(fmt)

    def iso(self) -> str:
        """As System.Text.Json writes it: e.g. 2026-11-20T16:00:12.5937226-08:00."""
        seconds, frac = divmod(self.ticks, _TICKS_PER_SECOND)
        wall = _EPOCH + timedelta(seconds=seconds)
        text = wall.strftime("%Y-%m-%dT%H:%M:%S")
        if frac:
            text += "." + f"{frac:07d}".rstrip("0")
        sign = "-" if self.offset_minutes < 0 else "+"
        h, m = divmod(abs(self.offset_minutes), 60)
        return f"{text}{sign}{h:02d}:{m:02d}"

    def __str__(self) -> str:
        return self.iso()


def camel(name: str) -> str:
    """JsonNamingPolicy.CamelCase for the names used here (PascalCase → camelCase)."""
    return name[:1].lower() + name[1:] if name else name


def dumps(value: Any) -> str:
    """Serialises with the C# app's options. Objects give their properties with ``to_json()``."""
    out: list[str] = []
    _write(value, 0, out)
    return "".join(out)


def _write(value: Any, depth: int, out: list[str]) -> None:
    if hasattr(value, "to_json") and not isinstance(value, type):
        value = value.to_json()
    if value is None:
        out.append("null")
    elif isinstance(value, bool):
        out.append("true" if value else "false")
    elif isinstance(value, enum.Enum):
        out.append(json_string(value.name))
    elif isinstance(value, (int, float)):
        out.append(json_number(value))
    elif isinstance(value, str):
        out.append(json_string(value))
    elif isinstance(value, DateTimeOffset):
        out.append(json_string(value.iso()))
    elif isinstance(value, dict):
        items = [(k, v) for k, v in value.items() if v is not None]
        if not items:
            out.append("{}")
            return
        pad = "  " * (depth + 1)
        out.append("{")
        for i, (k, v) in enumerate(items):
            out.append(NEWLINE + pad)
            key = k.name if isinstance(k, enum.Enum) else str(k)
            out.append(json_string(key) + ": ")
            _write(v, depth + 1, out)
            if i < len(items) - 1:
                out.append(",")
        out.append(NEWLINE + "  " * depth + "}")
    elif isinstance(value, (list, tuple)):
        if not value:
            out.append("[]")
            return
        pad = "  " * (depth + 1)
        out.append("[")
        for i, v in enumerate(value):
            out.append(NEWLINE + pad)
            _write(v, depth + 1, out)
            if i < len(value) - 1:
                out.append(",")
        out.append(NEWLINE + "  " * depth + "]")
    else:
        raise TypeError(f"can't write {type(value).__name__} as JSON")


# Reading helpers -----------------------------------------------------------------------------------


def parse_enum(cls: type[enum.Enum], value: Any) -> Any:
    """JsonStringEnumConverter: a name in any case, or the number."""
    if isinstance(value, cls):
        return value
    if isinstance(value, bool):
        raise ValueError(f"not a {cls.__name__}: {value!r}")
    if isinstance(value, int):
        return cls(value)
    if isinstance(value, str):
        text = value.strip()
        for member in cls:
            if member.name.lower() == text.lower():
                return member
        if re.fullmatch(r"[+-]?\d+", text):
            return cls(int(text))
    raise ValueError(f"not a {cls.__name__}: {value!r}")


def opt_float(value: Any) -> float | None:
    return None if value is None else float(value)


def opt_int(value: Any) -> int | None:
    return None if value is None else int(value)


def opt_str_list(value: Any) -> list[str] | None:
    return None if value is None else [str(v) for v in value]
