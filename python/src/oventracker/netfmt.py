"""Number formatting and rounding exactly as .NET (the C# app) does them, with the invariant culture.

The report's text has to match the C# app's to the character, so these follow .NET 11's rules, checked
against the runtime:

- Standard formats such as ``F1`` give the exact decimal value of the double, rounded half to even
  (``2.5.ToString("F0")`` is ``2``, ``0.375.ToString("F2")`` is ``0.38``). That is what Python's ``%f`` does.
- Custom formats such as ``0.#`` first round the double to 15 significant digits, then round that half
  away from zero (``0.25.ToString("0.#")`` is ``0.3``, ``1.45.ToString("0.#")`` is ``1.5``).
- A negative value keeps its sign even when it rounds to zero (``-0.04.ToString("F0")`` is ``-0``).
- ``Math.Round(x)`` and ``Math.Round(x, digits)`` round the exact value half to even, as Python's ``round`` does.
- ``Math.Round(x, MidpointRounding.AwayFromZero)`` rounds half away from zero.
- JSON numbers are the shortest round-trip digits, with an exponent (``1E-05``, ``1E+17``) outside 1e-4 to 1e17.
"""

from __future__ import annotations

import math
from decimal import ROUND_HALF_UP, Context, Decimal

_WIDE = Context(prec=1000)

__all__ = [
    "fixed",
    "custom",
    "round_even",
    "round_digits",
    "round_away",
    "net_floor",
    "net_ceiling",
    "clamp",
    "json_number",
    "round_trip",
    "json_string",
]


def fixed(x: float, decimals: int) -> str:
    """``x.ToString("F<decimals>", InvariantCulture)``."""
    if math.isnan(x):
        return "NaN"
    if math.isinf(x):
        return "Infinity" if x > 0 else "-Infinity"
    return f"{x:.{decimals}f}"


def custom(x: float, max_decimals: int, min_decimals: int = 0) -> str:
    """A custom format of zeros and hashes: ``"0"`` is (0, 0), ``"0.#"`` is (1, 0), ``"0.###"`` is (3, 0)."""
    if math.isnan(x):
        return "NaN"
    if math.isinf(x):
        return "Infinity" if x > 0 else "-Infinity"
    negative = math.copysign(1.0, x) < 0
    a = abs(x)
    if a == 0:
        value = Decimal(0)
    else:
        # Round to 15 significant digits first (DoublePrecisionCustomFormat), as .NET does.
        value = Decimal(f"{a:.14e}")
    rounded = value.quantize(Decimal(1).scaleb(-max_decimals), rounding=ROUND_HALF_UP, context=_WIDE)
    text = f"{rounded:f}"
    if "." in text:
        whole, frac = text.split(".")
        frac = frac.rstrip("0")
        if len(frac) < min_decimals:
            frac = frac + "0" * (min_decimals - len(frac))
        text = whole + ("." + frac if frac else "")
    elif min_decimals > 0:
        text = text + "." + "0" * min_decimals
    return ("-" if negative else "") + text


def round_even(x: float) -> float:
    """``Math.Round(x)``: to the nearest integer, half to even, keeping the sign of zero."""
    if math.isnan(x) or math.isinf(x):
        return x
    r = float(round(x))
    return math.copysign(r, x) if r == 0 else r


def round_digits(x: float, digits: int) -> float:
    """``Math.Round(x, digits)``: the exact value rounded to ``digits`` places, half to even.

    .NET 11 rounds correctly (``Math.Round(2.675, 2)`` is 2.67, as the double is 2.67499999...), not by scaling
    by 10^digits (which would give 2.68). That is exactly Python's ``round(x, digits)``; checked against .NET
    on 20,000 values.
    """
    if math.isnan(x) or math.isinf(x):
        return x
    return round(x, digits)


def round_away(x: float) -> float:
    """``Math.Round(x, MidpointRounding.AwayFromZero)``."""
    if math.isnan(x) or math.isinf(x):
        return x
    frac, whole = math.modf(x)
    if abs(frac) >= 0.5:
        whole += math.copysign(1.0, frac)
    return whole


def net_floor(x: float) -> float:
    """``Math.Floor``: a double, keeping the sign of zero."""
    if math.isnan(x) or math.isinf(x):
        return x
    r = float(math.floor(x))
    return math.copysign(r, x) if r == 0 else r


def net_ceiling(x: float) -> float:
    """``Math.Ceiling``: a double, keeping the sign of zero (``Math.Ceiling(-0.3)`` is -0)."""
    if math.isnan(x) or math.isinf(x):
        return x
    r = float(math.ceil(x))
    return math.copysign(r, x) if r == 0 else r


def clamp(value: float, lo: float, hi: float) -> float:
    """``Math.Clamp``, which refuses a minimum above the maximum."""
    if lo > hi:
        raise ValueError(f"'{lo}' cannot be greater than {hi}.")
    if value < lo:
        return lo
    if value > hi:
        return hi
    return value


def json_number(x: float | int) -> str:
    """A number as System.Text.Json writes it."""
    if isinstance(x, bool):
        raise TypeError("a bool is not a number")
    if isinstance(x, int):
        return str(x)
    if math.isnan(x) or math.isinf(x):
        raise ValueError(".NET doesn't write NaN or infinity to JSON")
    if x == 0:
        return "-0" if math.copysign(1.0, x) < 0 else "0"
    sign = "-" if x < 0 else ""
    d = Decimal(repr(abs(x)))
    t = d.as_tuple()
    digits = "".join(map(str, t.digits)).rstrip("0") or "0"
    # value = 0.d1d2... * 10^(point); the scientific exponent is point - 1
    point = len(t.digits) + t.exponent
    e = point - 1
    if e < -4 or e >= 17:
        mant = digits[0] + ("." + digits[1:] if len(digits) > 1 else "")
        return f"{sign}{mant}E{'+' if e >= 0 else '-'}{abs(e):02d}"
    if point <= 0:
        return f"{sign}0.{'0' * -point}{digits}"
    if point >= len(digits):
        return sign + digits + "0" * (point - len(digits))
    return f"{sign}{digits[:point]}.{digits[point:]}"


def round_trip(x: float) -> str:
    """``x.ToString("R", InvariantCulture)``: the shortest digits that read back as the same double. .NET 11 writes
    these exactly as System.Text.Json does (``2``, ``1E-05``, ``1E+17``), and NaN and the infinities by name."""
    if math.isnan(x):
        return "NaN"
    if math.isinf(x):
        return "Infinity" if x > 0 else "-Infinity"
    return json_number(float(x))


_SHORT = {"\b": "\\b", "\f": "\\f", "\n": "\\n", "\r": "\\r", "\t": "\\t", "\\": "\\\\"}
_ESCAPED = set("\"&'+<>`")


def json_string(s: str) -> str:
    """A string as System.Text.Json's default encoder writes it: ASCII, with HTML-sensitive characters escaped."""
    out = ['"']
    for ch in s:
        if ch in _SHORT:
            out.append(_SHORT[ch])
            continue
        o = ord(ch)
        if 0x20 <= o < 0x7F and ch not in _ESCAPED:
            out.append(ch)
        elif o > 0xFFFF:
            v = o - 0x10000
            out.append(f"\\u{0xD800 + (v >> 10):04X}\\u{0xDC00 + (v & 0x3FF):04X}")
        else:
            out.append(f"\\u{o:04X}")
    out.append('"')
    return "".join(out)
