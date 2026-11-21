"""Checks, at the start of a setpoint, that the oven is heading for its new setting (Analysis/HeadingCheck.cs).

If the setting is well above where the oven was, it should be getting hotter; well below, cooler. An oven left at
its old setting (the user forgot to change it) just cycles around where it was. Within six minutes the oven should
have made ``progress_c`` of progress: half the step, but no more than 25 °F, and always beyond half the measured
swing. A change of mode at the same temperature can't be seen this way."""

from __future__ import annotations

import enum


class HeadingState(enum.IntEnum):
    Unknown = 0  # too early to say, or the step is too small to tell
    OnItsWay = 1  # the oven is going the right way
    NotHeading = 2  # the oven isn't going towards the new setting: it probably wasn't changed


#: A smaller step (20 °F) can't be told from the thermostat's own swing.
MIN_STEP_C = 20 / 1.8
#: How long the oven has to make its progress, in seconds.
WITHIN_SECONDS = 6 * 60.0
#: The most progress asked for (25 °F): more than a thermostat's swing either side of its mean.
MAX_PROGRESS_C = 25 / 1.8
#: Beyond half the swing, by this much (5 °F), counts as more than the thermostat's own cycle.
SWING_MARGIN_C = 5 / 1.8


class HeadingCheck:
    """``from_c``: where the oven was (the previous setpoint's mean, or the mean of its last two minutes).
    ``swing_c``: the thermostat's peak-to-peak swing, if measured: progress must go beyond half of it."""

    def __init__(self, from_c: float, setpoint_c: float, swing_c: float | None = None):
        self.from_c = from_c
        self.setpoint_c = setpoint_c
        self.swing_c = swing_c
        self._best = from_c  # the furthest it has got towards the setting
        self.state = HeadingState.Unknown

    @property
    def applies(self) -> bool:
        return abs(self.setpoint_c - self.from_c) >= MIN_STEP_C

    @property
    def heating(self) -> bool:
        return self.setpoint_c > self.from_c

    @property
    def progress_c(self) -> float:
        step = abs(self.setpoint_c - self.from_c)
        progress = min(step / 2, MAX_PROGRESS_C)
        if self.swing_c is not None:
            progress = max(progress, self.swing_c / 2 + SWING_MARGIN_C)
        return min(progress, step * 0.8)

    @property
    def target_c(self) -> float:
        """The temperature that shows the oven is on its way."""
        return self.from_c + self.progress_c if self.heating else self.from_c - self.progress_c

    def update(self, t: float, c: float) -> HeadingState:
        """``t``: seconds since the setpoint started."""
        if not self.applies:
            return self.state
        self._best = max(self._best, c) if self.heating else min(self._best, c)
        on_its_way = self._best >= self.target_c if self.heating else self._best <= self.target_c
        if on_its_way:
            self.state = HeadingState.OnItsWay
        elif t >= WITHIN_SECONDS:
            self.state = HeadingState.NotHeading
        return self.state
