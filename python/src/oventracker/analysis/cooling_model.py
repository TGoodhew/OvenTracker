"""Watches an oven cool and predicts when it reaches a temperature (Analysis/CoolingModel.cs).

Newton's law of cooling, T(t) = T_ambient + (T0 − T_ambient)·e^(−t/τ), fitted to the recent readings: ln(T −
T_ambient) is a straight line in t, with slope −1/τ. An oven isn't a single thermal mass, so τ grows as it cools;
fitting only the last stretch keeps the prediction close to the oven's current rate."""

from __future__ import annotations

import enum
import math

from . import stats


class CoolingState(enum.IntEnum):
    Learning = 0  # not enough readings yet to say
    Cooling = 1  # cooling, and the model can predict when it reaches a temperature
    Heating = 2  # getting hotter: the oven is on
    DoorOpen = 3  # dropping much faster than an oven cools with the door shut


#: Getting hotter by more than this (°C a minute) means the oven is on.
HEATING_RATE = 0.5
#: Dropping faster than this (°C a minute) means the door is open.
DOOR_RATE = -6.0
#: How much cooling the fit looks at, in seconds.
WINDOW_SECONDS = 15 * 60.0
#: The least cooling the fit needs before it predicts anything, in seconds.
MINIMUM_FIT_SECONDS = 3 * 60.0


class CoolingModel:
    def __init__(self, ambient_c: float):
        self.ambient_c = ambient_c
        self.samples: list[tuple[float, float]] = []
        self.state = CoolingState.Learning
        self._segment_start = 0.0  # where the current stretch of shut-door cooling began
        self._tau: float | None = None  # seconds, from the latest good fit

    @property
    def tau_seconds(self) -> float | None:
        return self._tau

    @property
    def now(self) -> float:
        return 0.0 if not self.samples else self.samples[-1][0]

    @property
    def current_c(self) -> float | None:
        """The current temperature, smoothed over the last half-minute."""
        if not self.samples:
            return None
        frm = self.now - 30
        total = 0.0
        n = 0
        i = len(self.samples) - 1
        while i >= 0 and self.samples[i][0] >= frm:
            total += self.samples[i][1]
            n += 1
            i -= 1
        return total / n

    def add(self, t: float, c: float) -> None:
        """``t``: seconds since watching began."""
        self.samples.append((t, c))
        short_rate = self._rate(30)  # over the last half-minute: catches the door quickly
        long_rate = self._rate(150)  # over the last 2½ minutes: steady enough to call heating
        if short_rate is not None and short_rate < DOOR_RATE:
            self.state = CoolingState.DoorOpen
            self._segment_start = t  # what follows is a new stretch of cooling
            return
        if long_rate is not None and long_rate > HEATING_RATE:
            self.state = CoolingState.Heating
            self._segment_start = t
            self._tau = None  # a hotter, re-soaked oven cools differently: learn again
            return
        self._fit()
        self.state = CoolingState.Learning if self._tau is None else CoolingState.Cooling

    def time_until(self, target_c: float) -> float | None:
        """Seconds until the oven is at or below ``target_c``, or None if the model can't say (or it never will)."""
        now = self.current_c
        if now is None:
            return None
        if now <= target_c:
            return 0.0
        t = self._tau
        if t is None or self.state == CoolingState.Heating:
            return None
        if target_c - self.ambient_c < 0.5:
            return None  # it only gets there in the limit
        seconds = t * math.log((now - self.ambient_c) / (target_c - self.ambient_c))
        return max(0.0, seconds)

    def predict(self, ahead_seconds: float) -> float | None:
        """The predicted temperature this far ahead, for drawing the curve."""
        now = self.current_c
        if now is None or self._tau is None:
            return None
        return self.ambient_c + (now - self.ambient_c) * math.exp(-ahead_seconds / self._tau)

    def _rate(self, seconds: float) -> float | None:
        """The rate of change over the last ``seconds``, in °C a minute."""
        frm = self.now - seconds
        s = self.samples
        i = len(s) - 1
        while i > 0 and s[i - 1][0] >= frm:
            i -= 1
        if len(s) - i < 5 or self.now - s[i][0] < seconds * 0.6:
            return None
        fit = stats.fit([p[0] for p in s[i:]], [p[1] for p in s[i:]])
        return None if fit is None else fit.slope * 60

    def _fit(self) -> None:
        frm = max(self._segment_start, self.now - WINDOW_SECONDS)
        xs: list[float] = []
        ys: list[float] = []
        for t, c in self.samples:
            if t < frm or c - self.ambient_c < 2:
                continue
            xs.append(t)
            ys.append(math.log(c - self.ambient_c))
        if len(xs) < 10 or xs[-1] - xs[0] < MINIMUM_FIT_SECONDS:
            return  # keep the last good τ meanwhile
        fit = stats.fit(xs, ys)
        if fit is not None and fit.slope < 0:
            self._tau = -1 / fit.slope
