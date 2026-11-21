"""Temperatures and units (Temperature.cs): °C inside, the oven's own unit on show."""

from __future__ import annotations

import enum

from .netfmt import custom, fixed, round_digits


class TempUnit(enum.IntEnum):
    Fahrenheit = 0
    Celsius = 1


class OvenMode(enum.IntEnum):
    Bake = 0
    Convection = 1


def c_to_f(c: float) -> float:
    return c * 9.0 / 5.0 + 32.0


def f_to_c(f: float) -> float:
    return (f - 32.0) * 5.0 / 9.0


def parse_unit(text: str | None) -> TempUnit | None:
    if text is None:
        return None
    t = text.strip().upper()
    if t in ("F", "°F", "FAHRENHEIT"):
        return TempUnit.Fahrenheit
    if t in ("C", "°C", "CELSIUS"):
        return TempUnit.Celsius
    return None


def mode_name(mode: OvenMode) -> str:
    return "Bake" if mode == OvenMode.Bake else "Convection"


class UnitFormatter:
    """Converts and formats temperatures held in °C for display in the oven's own unit."""

    def __init__(self, unit: TempUnit):
        self.unit = unit

    @property
    def symbol(self) -> str:
        return "°F" if self.unit == TempUnit.Fahrenheit else "°C"

    def to_display(self, c: float) -> float:
        return c_to_f(c) if self.unit == TempUnit.Fahrenheit else c

    def from_display(self, d: float) -> float:
        return f_to_c(d) if self.unit == TempUnit.Fahrenheit else d

    def delta_to_display(self, dc: float) -> float:
        return dc * 1.8 if self.unit == TempUnit.Fahrenheit else dc

    def delta_from_display(self, dd: float) -> float:
        return dd / 1.8 if self.unit == TempUnit.Fahrenheit else dd

    def T(self, c: float, decimals: int = 1) -> str:
        """An absolute temperature, e.g. "352.4 °F"."""
        return f"{fixed(self.to_display(c), decimals)} {self.symbol}"

    def D(self, dc: float, decimals: int = 1) -> str:
        """A signed difference, e.g. "+2.4 °F"."""
        v = self.delta_to_display(dc)
        s = fixed(abs(v), decimals)
        r = round_digits(v, decimals)
        sign = "+" if r > 0 else "−" if r < 0 else "±"
        return f"{sign}{s} {self.symbol}"

    def span(self, dc: float, decimals: int = 1) -> str:
        """An unsigned span (swing, uncertainty), e.g. "4.2 °F"."""
        return f"{fixed(abs(self.delta_to_display(dc)), decimals)} {self.symbol}"

    def setpoint(self, c: float) -> str:
        """A setpoint, shown as the user entered it, e.g. "350 °F"."""
        return f"{custom(round_digits(self.to_display(c), 1), 1)} {self.symbol}"
