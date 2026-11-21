"""A time-ordered series of readings with time-weighted statistics (Analysis/SampleSeries.cs)."""

from __future__ import annotations

from typing import NamedTuple

from . import stats


class Sample(NamedTuple):
    """One reading: seconds since the test point started, and °C."""

    t: float
    c: float


class SampleSeries:
    def __init__(self) -> None:
        self._items: list[Sample] = []

    @property
    def count(self) -> int:
        return len(self._items)

    @property
    def items(self) -> list[Sample]:
        return self._items

    def __getitem__(self, i: int) -> Sample:
        return self._items[i]

    def __len__(self) -> int:
        return len(self._items)

    @property
    def last(self) -> Sample:
        return self._items[-1]

    def add(self, s: Sample) -> bool:
        if self._items and s.t <= self._items[-1].t:
            return False
        self._items.append(s)
        return True

    def lower_bound(self, t: float) -> int:
        """Index of the first sample at or after ``t``."""
        items = self._items
        lo, hi = 0, len(items)
        while lo < hi:
            mid = (lo + hi) >> 1
            if items[mid].t < t:
                lo = mid + 1
            else:
                hi = mid
        return lo

    def value_at(self, t: float) -> float:
        """Linearly interpolated value at ``t``, clamped to the ends."""
        items = self._items
        if not items:
            raise RuntimeError("No samples.")
        i = self.lower_bound(t)
        if i == 0:
            return items[0].c
        if i >= len(items):
            return items[-1].c
        a = items[i - 1]
        b = items[i]
        return a.c + (b.c - a.c) * (t - a.t) / (b.t - a.t)

    def mean(self, t0: float, t1: float) -> float:
        """Time-weighted (trapezoidal) mean over [t0, t1]."""
        if t1 <= t0:
            return self.value_at(t0)
        items = self._items
        area = 0.0
        prev_t = t0
        prev_v = self.value_at(t0)
        i = self.lower_bound(t0)
        while i < len(items) and items[i].t < t1:
            if items[i].t > t0:
                area += (items[i].t - prev_t) * (items[i].c + prev_v) / 2
                prev_t = items[i].t
                prev_v = items[i].c
            i += 1
        end_v = self.value_at(t1)
        area += (t1 - prev_t) * (end_v + prev_v) / 2
        return area / (t1 - t0)

    def range(self, t0: float, t1: float) -> tuple[float, float]:
        mn = self.value_at(t0)
        mx = mn
        end = self.value_at(t1)
        mn = min(mn, end)
        mx = max(mx, end)
        items = self._items
        i = self.lower_bound(t0)
        while i < len(items) and items[i].t <= t1:
            mn = min(mn, items[i].c)
            mx = max(mx, items[i].c)
            i += 1
        return mn, mx

    def slope(self, t0: float, t1: float) -> float | None:
        """Least-squares slope in °C per second over [t0, t1], or None with too few samples."""
        xs: list[float] = []
        ys: list[float] = []
        items = self._items
        i = self.lower_bound(t0)
        while i < len(items) and items[i].t <= t1:
            xs.append(items[i].t)
            ys.append(items[i].c)
            i += 1
        if len(xs) < 3:
            return None
        f = stats.fit(xs, ys)
        return None if f is None else f.slope
