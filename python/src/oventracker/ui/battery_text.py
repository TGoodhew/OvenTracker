"""The meter's battery, the same way on every screen (Ui/BatteryText.cs): red below 20 %, and nothing at all for a
meter that doesn't report one."""

from __future__ import annotations

from ..netfmt import fixed

#: Below this, the battery is shown as low.
LOW_PERCENT = 20.0


def is_low(percent: float) -> bool:
    return percent < LOW_PERCENT


def markup(percent: float | None) -> str | None:
    """"64 %", or "[red]15 % (low)[/]" as markup; None when the meter has no battery."""
    if percent is None:
        return None
    return f"[red]{fixed(percent, 0)} % (low)[/]" if is_low(percent) else f"{fixed(percent, 0)} %"


def plain(percent: float | None) -> str | None:
    """"battery 64 %" or "battery 15 % (low)" for plain output; None when the meter has no battery."""
    if percent is None:
        return None
    return f"battery {fixed(percent, 0)} %{' (low)' if is_low(percent) else ''}"
