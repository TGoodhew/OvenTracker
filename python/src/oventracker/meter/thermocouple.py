"""A type K thermocouple's voltage and temperature (Meter/Thermocouple.cs): the NIST ITS-90 reference function (NIST
Monograph 175), with the reference junction at 0 °C.

A meter that reads the voltage (a DMM on its millivolt range) needs the cold junction's own temperature too, where
the thermocouple meets the copper of the meter's terminals: the thermocouple measures only the difference, so
:meth:`TypeK.temperature_for` adds the cold junction's voltage back. The sums are done in the C# app's order, so
the two give the same doubles."""

from __future__ import annotations

import math

from ..netfmt import clamp

#: −270 to 0 °C.
_BELOW_0 = (
    0.0, 0.394501280250E-01, 0.236223735980E-04, -0.328589067840E-06, -0.499048287770E-08, -0.675090591730E-10,
    -0.574103274280E-12, -0.310888728940E-14, -0.104516093650E-16, -0.198892668780E-19, -0.163226974860E-22,
)

#: 0 to 1372 °C, with an exponential term.
_ABOVE_0 = (
    -0.176004136860E-01, 0.389212049750E-01, 0.185587700320E-04, -0.994575928740E-07, 0.318409457190E-09,
    -0.560728448890E-12, 0.560750590590E-15, -0.320207200030E-18, 0.971511471520E-22, -0.121047212750E-25,
)

_A0, _A1, _A2 = 0.118597600000E+00, -0.118343200000E-03, 0.126968600000E+03


def _divide(a: float, b: float) -> float:
    """``a / b`` as a double: infinity or NaN for a zero divisor, rather than an exception."""
    if b != 0:
        return a / b
    if a == 0 or math.isnan(a):
        return math.nan
    return math.copysign(math.inf, a) * math.copysign(1.0, b)


class TypeK:
    MIN_C = -270.0
    MAX_C = 1372.0

    @staticmethod
    def millivolts_at(c: float) -> float:
        """The voltage, in mV, of a type K thermocouple at a temperature (°C) with its reference junction at 0 °C."""
        coeffs = _BELOW_0 if c < 0 else _ABOVE_0
        total = 0.0
        power = 1.0
        for k in coeffs:
            total += k * power
            power *= c
        if c >= 0:
            total += _A0 * math.exp(_A1 * (c - _A2) * (c - _A2))
        return total

    @staticmethod
    def _slope(c: float) -> float:
        """d(mV)/d(°C), for Newton's method."""
        coeffs = _BELOW_0 if c < 0 else _ABOVE_0
        total = 0.0
        power = 1.0
        for i in range(1, len(coeffs)):
            total += i * coeffs[i] * power
            power *= c
        if c >= 0:
            total += _A0 * math.exp(_A1 * (c - _A2) * (c - _A2)) * 2 * _A1 * (c - _A2)
        return total

    @staticmethod
    def temperature_for(millivolts: float, cold_junction_c: float) -> float | None:
        """The temperature (°C) at the measuring junction, from the voltage across the thermocouple (mV) and the cold
        junction's temperature (°C). None if the voltage is beyond type K's range."""
        e = millivolts + TypeK.millivolts_at(cold_junction_c)
        if e < TypeK.millivolts_at(TypeK.MIN_C) or e > TypeK.millivolts_at(TypeK.MAX_C):
            return None
        # Newton's method on the reference function itself, so the two directions agree exactly; 41 µV/°C to start.
        t = e / 0.041
        for _ in range(50):
            step = _divide(TypeK.millivolts_at(t) - e, TypeK._slope(t))
            t = clamp(t - step, TypeK.MIN_C, TypeK.MAX_C)
            if abs(step) < 1e-9:
                break
        return t
