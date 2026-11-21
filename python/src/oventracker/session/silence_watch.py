"""Notices when the meter stops sending readings mid-run, and when it starts again (Session/SilenceWatch.cs)."""

from __future__ import annotations

import enum
import math

from ..meter.auto_power_off import AutoPowerOff
from ..netfmt import fixed


class SilenceChange(enum.IntEnum):
    None_ = 0
    WentQuiet = 1
    CameBack = 2


#: Half a minute without a reading: far longer than the interval between readings, short enough to act on.
DEFAULT_THRESHOLD = 30.0


class SilenceWatch:
    def __init__(self, threshold_seconds: float = DEFAULT_THRESHOLD):
        self._threshold = threshold_seconds
        self.silent = False

    def update(self, silent_for: float) -> SilenceChange:
        if not self.silent and silent_for >= self._threshold:
            self.silent = True
            return SilenceChange.WentQuiet
        if self.silent and silent_for < self._threshold:
            self.silent = False
            return SilenceChange.CameBack
        return SilenceChange.None_


def message(silent_for: float, auto_power_off: AutoPowerOff | None) -> str:
    """What to tell the user while the meter is quiet: about auto power-off when the meter has one."""
    minutes = silent_for / 60
    how = f"{fixed(math.floor(minutes), 0)} min" if minutes >= 2 else f"{fixed(math.floor(silent_for), 0)} s"
    if auto_power_off is not None:
        return (
            f"No reading for {how}. The meter may have switched itself off (auto power-off): {auto_power_off.how_to_wake} "
            "to wake it. Tracking carries on when readings come back."
        )
    return f"No reading for {how}. The meter has stopped answering: check it's on and still connected. Tracking carries on when readings come back."
