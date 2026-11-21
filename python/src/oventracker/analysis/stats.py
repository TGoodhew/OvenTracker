"""Statistics (Analysis/Stats.cs), with the same order of summation as the C#."""

from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Iterable, Sequence


@dataclass(frozen=True)
class LinearFit:
    slope: float
    intercept: float
    slope_std_err: float | None
    n: int


# Two-sided 95 % critical values of Student's t for 1..30 degrees of freedom.
T975 = [
    12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
    2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
    2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042,
]  # fmt: skip


def t_crit_95(df: int) -> float:
    if df < 1:
        return math.inf
    if df <= 30:
        return T975[df - 1]
    return 1.96 + 2.4 / df


def mean(x: Sequence[float]) -> float:
    if len(x) == 0:
        raise ValueError("No values.")
    s = 0.0
    for v in x:
        s += v
    return s / len(x)


def std_dev(x: Sequence[float]) -> float:
    """Sample standard deviation (n − 1)."""
    if len(x) < 2:
        return 0.0
    m = mean(x)
    ss = 0.0
    for v in x:
        ss += (v - m) * (v - m)
    return math.sqrt(ss / (len(x) - 1))


def half_width_95(x: Sequence[float]) -> float | None:
    """Half-width of the 95 % confidence interval of the mean, or None with fewer than two values."""
    if len(x) < 2:
        return None
    return t_crit_95(len(x) - 1) * std_dev(x) / math.sqrt(len(x))


def required_count(sd: float, target: float, minimum: int, maximum: int) -> int:
    """The fewest independent values whose 95 % half-width would be at most ``target``, given a spread ``sd``."""
    if sd <= 0:
        return minimum
    for n in range(max(minimum, 2), maximum + 1):
        if t_crit_95(n - 1) * sd / math.sqrt(n) <= target:
            return n
    return maximum


def fit(x: Sequence[float], y: Sequence[float]) -> LinearFit | None:
    n = len(x)
    if n < 2 or len(y) != n:
        return None
    mx = mean(x)
    my = mean(y)
    sxx = 0.0
    sxy = 0.0
    for i in range(n):
        sxx += (x[i] - mx) * (x[i] - mx)
        sxy += (x[i] - mx) * (y[i] - my)
    if sxx <= 0:
        return None
    slope = sxy / sxx
    intercept = my - slope * mx
    se = None
    if n >= 3:
        sse = 0.0
        for i in range(n):
            r = y[i] - (intercept + slope * x[i])
            sse += r * r
        se = math.sqrt(sse / (n - 2) / sxx)
    return LinearFit(slope, intercept, se, n)


def median(values: Iterable[float]) -> float:
    a = sorted(values)
    if len(a) == 0:
        raise ValueError("No values.")
    h = len(a) // 2
    return a[h] if len(a) % 2 == 1 else (a[h - 1] + a[h]) / 2


def noise_sigma(x: Sequence[float]) -> float:
    """White-noise sigma of a series, from the median absolute second difference."""
    if len(x) < 5:
        return 0.0
    d2 = [abs(x[i] - 2 * x[i - 1] + x[i - 2]) for i in range(2, len(x))]
    # For white noise, var(second difference) = 6 sigma^2; MAD -> sigma via 1.4826.
    return 1.4826 * median(d2) / math.sqrt(6)


def average(values: Iterable[float]) -> float:
    """LINQ Average: summed in order, divided by the count."""
    s = 0.0
    n = 0
    for v in values:
        s += v
        n += 1
    if n == 0:
        raise ValueError("Sequence contains no elements")
    return s / n


def total(values: Iterable[float]) -> float:
    """LINQ Sum over doubles, in order."""
    s = 0.0
    for v in values:
        s += v
    return s
