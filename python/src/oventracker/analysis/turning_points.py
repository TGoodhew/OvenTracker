"""Finds the peaks and troughs of a thermostat's on/off cycle (Analysis/TurningPointDetector.cs)."""

from __future__ import annotations

import enum
import math
from dataclasses import dataclass


class TurningKind(enum.IntEnum):
    Peak = 0
    Trough = 1


@dataclass(frozen=True)
class TurningPoint:
    """``t``: when the extreme happened (seconds since the point started); ``c``: the smoothed temperature
    there; ``confirmed_at``: when the reversal was large enough to be sure of it."""

    kind: TurningKind
    t: float
    c: float
    confirmed_at: float


_UNKNOWN, _RISING, _FALLING = 0, 1, 2


class TurningPointDetector:
    """A peak is confirmed once the temperature has fallen a hysteresis below the highest value since the
    last trough (and the reverse for a trough), so noise smaller than the hysteresis never makes one."""

    def __init__(self) -> None:
        self._direction = _UNKNOWN
        self._extreme_c = 0.0
        self._extreme_t = 0.0
        self._unknown_min = math.inf
        self._unknown_max = -math.inf
        self._any = False

    def add(self, t: float, c: float, hysteresis: float) -> TurningPoint | None:
        if not self._any:
            self._any = True
            self._unknown_min = self._unknown_max = c

        if self._direction == _UNKNOWN:
            self._unknown_min = min(self._unknown_min, c)
            self._unknown_max = max(self._unknown_max, c)
            if c >= self._unknown_min + hysteresis:
                self._direction = _RISING
                self._extreme_c = c
                self._extreme_t = t
            elif c <= self._unknown_max - hysteresis:
                self._direction = _FALLING
                self._extreme_c = c
                self._extreme_t = t
            return None

        if self._direction == _RISING:
            if c > self._extreme_c:
                self._extreme_c = c
                self._extreme_t = t
                return None
            if c <= self._extreme_c - hysteresis:
                peak = TurningPoint(TurningKind.Peak, self._extreme_t, self._extreme_c, t)
                self._direction = _FALLING
                self._extreme_c = c
                self._extreme_t = t
                return peak
            return None

        if c < self._extreme_c:
            self._extreme_c = c
            self._extreme_t = t
            return None
        if c >= self._extreme_c + hysteresis:
            trough = TurningPoint(TurningKind.Trough, self._extreme_t, self._extreme_c, t)
            self._direction = _RISING
            self._extreme_c = c
            self._extreme_t = t
            return trough
        return None
