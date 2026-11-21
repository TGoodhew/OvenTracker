"""A meter that switches itself off after a while (Meter/AutoPowerOff.cs), and whether to warn about it."""

from __future__ import annotations

import enum
from dataclasses import dataclass, field
from datetime import timedelta
from typing import ClassVar

from ..netfmt import custom


@dataclass(frozen=True)
class AutoPowerOff:
    """A driver's metadata about its kind of meter's auto power-off.

    ``default_timeout``: how long the meter waits before switching off, from the factory. ``how_to_turn_off``:
    the steps, in plain text. ``source``: where they come from. ``remote_reads_keep_it_on``: reading it remotely
    restarts its countdown, so it needs no warning. ``how_to_wake``: what to do when it has switched off."""

    default_timeout: timedelta
    how_to_turn_off: list[str] = field(default_factory=list)
    source: str | None = None
    remote_reads_keep_it_on: bool = False
    how_to_wake: str = "press any button on the meter"


class AutoPowerOffState(enum.IntEnum):
    Unknown = 0
    Off = 1
    On = 2


@dataclass(frozen=True)
class AutoPowerOffSetting:
    """What a meter says its auto power-off is set to, when it can say."""

    state: AutoPowerOffState
    timeout: timedelta | None = None

    UNKNOWN: ClassVar["AutoPowerOffSetting"]
    OFF: ClassVar["AutoPowerOffSetting"]

    @staticmethod
    def on(timeout: timedelta) -> "AutoPowerOffSetting":
        return AutoPowerOffSetting(AutoPowerOffState.On, timeout)


AutoPowerOffSetting.UNKNOWN = AutoPowerOffSetting(AutoPowerOffState.Unknown)
AutoPowerOffSetting.OFF = AutoPowerOffSetting(AutoPowerOffState.Off)


class AutoPowerOffWarningKind(enum.IntEnum):
    On = 0  # the meter says auto power-off is on: it will switch off mid-run
    Reminder = 1  # the meter can't say, and it's on by default


@dataclass(frozen=True)
class AutoPowerOffWarning:
    kind: AutoPowerOffWarningKind
    headline: str
    steps: list[str]
    source: str | None


def _minutes(t: timedelta) -> str:
    total = t.total_seconds() / 60
    return "1 minute" if total == 1 else f"{custom(total, 0)} minutes"


def advice_for(
    metadata: AutoPowerOff | None, setting: AutoPowerOffSetting, confirmed_off: bool = False
) -> AutoPowerOffWarning | None:
    """Whether to warn before a run, and what to say (AutoPowerOffAdvice.For). No warning when the meter has no
    auto power-off, when reading it keeps it on, when it says auto power-off is off, or when the user has said so
    for this meter before (``confirmed_off``) and the meter can't say otherwise."""
    steps = list(metadata.how_to_turn_off) if metadata is not None else []
    source = metadata.source if metadata is not None else None
    if setting.state == AutoPowerOffState.On:
        after = setting.timeout if setting.timeout is not None else (metadata.default_timeout if metadata else None)
        when = f", set to {_minutes(after)}" if after is not None else ""
        return AutoPowerOffWarning(
            AutoPowerOffWarningKind.On,
            f"Auto power-off is on{when}: the meter will switch itself off in the middle of a setpoint.",
            steps,
            source,
        )
    if metadata is None or metadata.remote_reads_keep_it_on or setting.state == AutoPowerOffState.Off or confirmed_off:
        return None
    return AutoPowerOffWarning(
        AutoPowerOffWarningKind.Reminder,
        f"This meter switches itself off after {_minutes(metadata.default_timeout)} by default, and reading it remotely "
        "doesn't keep it on. A setpoint takes far longer, so turn auto power-off off before starting.",
        steps,
        source,
    )
