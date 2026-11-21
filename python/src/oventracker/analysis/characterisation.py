"""What characterise (or analyze over runs at different adjustments) found about an oven
(Analysis/Characterisation.cs): each mode's gain and error shape, how repeatable the results are, and the best
adjustment with the measured gains."""

from __future__ import annotations

from dataclasses import dataclass, replace
from typing import TYPE_CHECKING, Any

from ..jsonio import NEWLINE, DateTimeOffset, opt_float
from ..netfmt import fixed, round_even
from ..units import OvenMode, UnitFormatter, mode_name
from . import stats
from .adjustment_advisor import AdjustmentAdvice, GainSource, ModeGain, advise
from .session_summary import group_by

if TYPE_CHECKING:
    from ..ovens import MeasuredGain
    from ..session.recording import SessionInfo
    from .session_summary import PointResult


@dataclass(frozen=True)
class CharacteriseInfo:
    """A characterisation run's plan: the adjustment step, and once phase 2 has begun, the adjustment it moved to."""

    step_c: float
    from_c: float | None = None
    changed_to_c: float | None = None
    changed_at: DateTimeOffset | None = None

    def to_json(self) -> dict[str, Any]:
        return {"stepC": self.step_c, "fromC": self.from_c, "changedToC": self.changed_to_c, "changedAt": self.changed_at}

    @staticmethod
    def from_json(d: dict[str, Any] | None) -> "CharacteriseInfo | None":
        if d is None:
            return None
        at = d.get("changedAt")
        return CharacteriseInfo(
            float(d.get("stepC", 0)),
            opt_float(d.get("fromC")),
            opt_float(d.get("changedToC")),
            DateTimeOffset.parse(at) if isinstance(at, str) else None,
        )


@dataclass(frozen=True)
class ModeCharacter:
    """One mode's characterisation: its measured gain, how its error changes per degree of setting, and whether
    the error bends with temperature."""

    mode: OvenMode
    gain: ModeGain | None
    slope_per_degree: float | None
    curved: bool
    setpoints: int

    def to_json(self) -> dict[str, Any]:
        return {
            "mode": self.mode,
            "gain": self.gain,
            "slopePerDegree": self.slope_per_degree,
            "curved": self.curved,
            "setpoints": self.setpoints,
        }


@dataclass(frozen=True)
class Repeat:
    """The same setpoint, at the same adjustment, measured more than once."""

    mode: OvenMode
    setpoint_c: float
    adjustment_c: float
    means_c: list[float]

    @property
    def spread_c(self) -> float:
        return max(self.means_c) - min(self.means_c)

    @property
    def std_dev_c(self) -> float:
        return stats.std_dev(self.means_c)

    def to_json(self) -> dict[str, Any]:
        return {
            "mode": self.mode,
            "setpointC": self.setpoint_c,
            "adjustmentC": self.adjustment_c,
            "meansC": self.means_c,
            "spreadC": self.spread_c,
            "stdDevC": self.std_dev_c,
        }


_SAME_C = 0.05


@dataclass(frozen=True)
class Characterisation:
    modes: list[ModeCharacter]
    repeats: list[Repeat]
    recommendation: AdjustmentAdvice | None

    def measured_gains(self, when: DateTimeOffset) -> "dict[OvenMode, MeasuredGain]":
        """The gains worth keeping in the oven's profile: those measured."""
        from ..ovens import MeasuredGain

        return {
            m.mode: MeasuredGain(m.gain.gain, m.gain.half_width, when)
            for m in self.modes
            if m.gain is not None and m.gain.source == GainSource.Measured
        }

    @property
    def measured_any(self) -> bool:
        return any(m.gain is not None and m.gain.source == GainSource.Measured for m in self.modes)

    def to_json(self) -> dict[str, Any]:
        return {
            "modes": self.modes,
            "repeats": self.repeats,
            "recommendation": self.recommendation,
            "measuredAny": self.measured_any,
        }

    @staticmethod
    def from_results(info: "SessionInfo", results: "list[PointResult]") -> "Characterisation":
        from ..session.report import Report

        report = Report(info, results)
        points = report.adjust_points()
        options = report.adjust_options

        # Each mode on its own: its gain, and its error's shape at the current adjustment.
        per_mode = advise(points, replace(options, shared_across_modes=False), info.adjustment_c)
        modes: list[ModeCharacter] = []
        for a in per_mode:
            mode = a.mode if a.mode is not None else a.residuals[0].mode
            mg = a.gains.get(mode) if a.gains is not None else None
            gain = mg if mg is not None and mg.source == GainSource.Measured else None
            now = [r for r in a.residuals if r.mode == mode]
            f = stats.fit([r.setpoint_c for r in now], [r.now_c for r in now]) if len(now) >= 2 else None
            modes.append(ModeCharacter(mode, gain, None if f is None else f.slope, a.curved, len(now)))

        # Repeats: the same mode, setpoint and adjustment, more than once.
        def adj(r: "PointResult") -> float:
            if r.point.adjustment_c is not None:
                return r.point.adjustment_c
            return info.adjustment_c if info.adjustment_c is not None else 0.0

        groups = group_by(
            [r for r in results if r.metrics.mean_c is not None],
            lambda r: (r.point.mode, round_even(r.point.setpoint_c / _SAME_C), round_even(adj(r) / _SAME_C)),
        )
        repeats = [
            Repeat(key[0], g[0].point.setpoint_c, adj(g[0]), [r.metrics.mean_c for r in g])
            for key, g in groups
            if len(g) >= 2
        ]

        advice = report.advice()
        return Characterisation(modes, repeats, advice[0] if advice else None)

    def markdown(self, info: "SessionInfo") -> str:
        from ..session.report import Report

        f = UnitFormatter(info.unit)
        lines: list[str] = []
        add = lines.append
        add(f"# Characterisation: {info.oven.name if info.oven is not None else 'the oven'}")
        add("")
        # The date as recorded, in the offset it was saved with, as the report's times are (not this computer's zone).
        add(
            f"Measured {info.started.wall():%Y-%m-%d} with {info.meter}"
            + (f" and a {info.sensor.name}" if info.sensor is not None else "")
            + "."
        )
        add("")
        add("## How far the adjustment moves the oven")
        add("")
        add("| Mode | Gain | ±95 % | Meaning |")
        add("| --- | --- | --- | --- |")
        for m in self.modes:
            g = m.gain
            if g is not None:
                hw = fixed(g.half_width, 2) if g.half_width is not None else "—"
                add(
                    f"| {mode_name(m.mode)} | {fixed(g.gain, 2)} | {hw} | each degree of adjustment moves it {fixed(g.gain, 2)} degrees |"
                )
            else:
                add(f"| {mode_name(m.mode)} | — | — | not measured at two adjustments |")
        add("")
        add("A gain of 1.00 means the adjustment does exactly what it says. Under 1, the oven moves less than the setting; over 1, more.")
        add("")
        add("## How the error changes with temperature")
        add("")
        for m in self.modes:
            sl = m.slope_per_degree
            if m.setpoints < 2:
                shape = "one setpoint, so its shape isn't known"
            elif m.curved:
                shape = "it bends with temperature, more than a straight line explains"
            elif sl is not None and abs(sl) >= 0.02:
                # The slope is degrees per degree, the same in °F and °C: 0.1 is 10° more error per 100° of setting.
                shape = (
                    f"its error changes by about {f.D(f.delta_from_display(sl * 100), 0)} per 100 {f.symbol} of setting "
                    f"({'hotter' if sl > 0 else 'cooler'} at higher settings), so one adjustment can only centre it"
                )
            else:
                shape = "its error is about the same at every setting, so one adjustment corrects it everywhere"
            add(f"- {mode_name(m.mode)}: {shape}.")
        add("")
        if self.repeats:
            add("## Repeatability")
            add("")
            add("| Mode | Set | Adjustment | Means | Spread |")
            add("| --- | --- | --- | --- | --- |")
            for r in self.repeats:
                means = ", ".join(f.T(c) for c in r.means_c)
                add(f"| {mode_name(r.mode)} | {f.setpoint(r.setpoint_c)} | {f.D(r.adjustment_c, 0)} | {means} | {f.span(r.spread_c)} |")
            add("")
            add("The spread between repeats is the real limit on how finely this oven can be adjusted.")
            add("")
        if self.recommendation is not None:
            report = Report(info, [])
            add("## Best adjustment, with the measured gains")
            add("")
            add(f"**{report.advice_headline(self.recommendation)}**")
            add("")
        return "".join(line + NEWLINE for line in lines)
