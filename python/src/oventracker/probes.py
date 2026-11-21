"""Temperature sensors and the probes the report knows by name (Meter/Probes.cs, Sensor in Meter/Meter.cs)."""

from __future__ import annotations

import re
from dataclasses import dataclass, replace
from typing import Any

from .units import TempUnit, f_to_c


@dataclass(frozen=True)
class Sensor:
    """A temperature sensor and its standard tolerance: the larger of ``limit_c`` and ``limit_fraction`` of the
    temperature, or the two added together when ``limits_add`` is set (as RTD classes are written)."""

    name: str
    limit_c: float
    limit_fraction: float
    limits_add: bool = False
    max_c: float | None = None

    @property
    def is_generic_type(self) -> bool:
        """A generic sensor type ("type K thermocouple"), as opposed to a particular probe."""
        return self.name.startswith("type ")

    def tolerance_c(self, c: float) -> float:
        return (
            self.limit_c + self.limit_fraction * abs(c)
            if self.limits_add
            else max(self.limit_c, self.limit_fraction * abs(c))
        )

    @staticmethod
    def thermocouple(kind: str) -> "Sensor | None":
        return {
            "K": Sensor("type K thermocouple", 2.2, 0.0075),
            "J": Sensor("type J thermocouple", 2.2, 0.0075),
            "N": Sensor("type N thermocouple", 2.2, 0.0075),
            "T": Sensor("type T thermocouple", 1.0, 0.0075),
            "E": Sensor("type E thermocouple", 1.7, 0.005),
        }.get(kind.strip().upper())

    def to_json(self) -> dict[str, Any]:
        return {
            "name": self.name,
            "limitC": self.limit_c,
            "limitFraction": self.limit_fraction,
            "limitsAdd": self.limits_add,
            "maxC": self.max_c,
            "isGenericType": self.is_generic_type,
        }

    @staticmethod
    def from_json(d: dict[str, Any] | None) -> "Sensor | None":
        if d is None:
            return None
        return Sensor(
            name=d.get("name"),  # type: ignore[arg-type]
            limit_c=float(d.get("limitC", 0)),
            limit_fraction=float(d.get("limitFraction", 0)),
            limits_add=bool(d.get("limitsAdd", False)),
            max_c=None if d.get("maxC") is None else float(d["maxC"]),
        )


@dataclass(frozen=True)
class KnownProbe:
    key: str
    sensor: Sensor
    source: str


ALL: list[KnownProbe] = [
    KnownProbe("type-k", Sensor("type K thermocouple", 2.2, 0.0075), "ASTM E230 standard limits of error, above 0 °C"),
    KnownProbe(
        "type-k-special",
        Sensor("type K thermocouple (special limits)", 1.1, 0.004),
        "ASTM E230 special limits of error, above 0 °C",
    ),
    KnownProbe(
        "80bk-a",
        Sensor("Fluke 80BK-A type K probe", 2.2, 0.02, max_c=260.0),
        "Fluke 80BK-A: −40 to 260 °C, ±2.2 °C or 2 % whichever is greater (0 to 260 °C), "
        "https://www.fluke.com/en-us/product/accessories/probes/fluke-80bk-a",
    ),
]


def find(key: str | None) -> KnownProbe | None:
    wanted = None if key is None else key.strip()
    for p in ALL:
        if wanted is not None and p.key.lower() == wanted.lower():
            return p
    return None


def keys() -> str:
    return ", ".join(p.key for p in ALL)


_TOLERANCE = re.compile(
    r"^\s*(?P<fixed>\d+(\.\d+)?)\s*(?P<unit>[CF])?\s*(?P<join>[,+])\s*(?P<pct>\d+(\.\d+)?)\s*%\s*$", re.IGNORECASE
)
_TEMPERATURE = re.compile(r"^\s*(?P<value>-?\d+(\.\d+)?)\s*(?P<unit>[CF])?\s*$", re.IGNORECASE)


def _in_f(m: re.Match[str], unit: TempUnit) -> bool:
    u = m.group("unit")
    return u.upper() == "F" if u is not None else unit == TempUnit.Fahrenheit


def parse_tolerance(text: str, unit: TempUnit) -> tuple[float, float, bool] | None:
    """Parses "2.2C,2%" (the greater of ±2.2 °C and ±2 %), "4F,2%", or "0.3C+0.5%" (the two added).
    Without C or F, the degrees are in ``unit``. Returns (limit °C, fraction, adds)."""
    m = _TOLERANCE.match(text)
    if not m:
        return None
    degrees = float(m.group("fixed"))
    limit_c = degrees / 1.8 if _in_f(m, unit) else degrees
    fraction = float(m.group("pct")) / 100
    return limit_c, fraction, m.group("join") == "+"


def parse_temperature(text: str, unit: TempUnit) -> float | None:
    """Parses "260C" or "500F"; without C or F, in ``unit``. In °C."""
    m = _TEMPERATURE.match(text)
    if not m:
        return None
    value = float(m.group("value"))
    return f_to_c(value) if _in_f(m, unit) else value


def resolve(
    reported: Sensor | None, probe: str | None, tolerance: str | None, max_text: str | None, unit: TempUnit
) -> Sensor | None:
    """The sensor to report: the named probe, or what the meter reported, with a custom tolerance and maximum on top."""
    known = find(probe)
    sensor = known.sensor if known is not None else reported
    if tolerance is not None:
        t = parse_tolerance(tolerance, unit)
        if t is not None:
            base = sensor if sensor is not None else Sensor("probe", 0.0, 0.0)
            name = f"{sensor.name if sensor is not None else 'probe'} (custom tolerance)" if probe is None else sensor.name  # type: ignore[union-attr]
            sensor = replace(base, name=name, limit_c=t[0], limit_fraction=t[1], limits_add=t[2])
    if max_text is not None:
        mx = parse_temperature(max_text, unit)
        if mx is not None:
            base = sensor if sensor is not None else Sensor("probe", 0.0, 0.0)
            sensor = replace(base, max_c=mx)
    return sensor
