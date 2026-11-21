"""Works out the thermostat adjustment that fits the oven best across its setpoints (Analysis/AdjustmentAdvisor.cs).

It models the error at each setpoint, how far the oven's adjustment really moves it, and what the adjustment
leaves behind. See docs/oven-control-model.md.
"""

from __future__ import annotations

import enum
import math
from dataclasses import dataclass, replace
from typing import Any, Callable

from ..jsonio import opt_float, parse_enum
from ..netfmt import clamp, net_ceiling, net_floor, round_away, round_even
from ..units import OvenMode, TempUnit
from . import stats
from .session_summary import distinct, group_by


class AdjustObjective(enum.IntEnum):
    Balanced = 0
    """The overall error: least squares across the measured setpoints."""
    Minimax = 1
    """The worst setpoint's error."""


class AdjustModel(enum.IntEnum):
    Shift = 0
    """An adjustment of k moves the oven by k at every setting."""
    Gain = 1
    """An adjustment of k moves the oven by b·k, where b is the slope of measured against set."""


@dataclass(frozen=True)
class AdjustmentOptions:
    step_c: float
    range_c: float
    objective: AdjustObjective = AdjustObjective.Balanced
    favour_c: float | None = None
    model: AdjustModel = AdjustModel.Shift
    shared_across_modes: bool = True
    known_gains: dict[OvenMode, float] | None = None

    @staticmethod
    def for_unit(unit: TempUnit) -> "AdjustmentOptions":
        """The defaults for an oven that works in this unit: 1° steps, up to ±35 °F (±19 °C)."""
        return AdjustmentOptions(1 / 1.8, 35 / 1.8) if unit == TempUnit.Fahrenheit else AdjustmentOptions(1.0, 19.0)

    @staticmethod
    def for_oven(oven: Any, unit: TempUnit) -> "AdjustmentOptions":
        """The defaults for a known oven; the generic profile gives the unit's defaults."""
        if oven.is_generic:
            return replace(AdjustmentOptions.for_unit(unit), known_gains=_profile_gains(oven))
        return AdjustmentOptions(
            oven.adjust_step_c, oven.adjust_range_c, shared_across_modes=oven.shared_adjustment, known_gains=_profile_gains(oven)
        )

    def to_json(self) -> dict[str, Any]:
        return {
            "stepC": self.step_c,
            "rangeC": self.range_c,
            "objective": self.objective,
            "favourC": self.favour_c,
            "model": self.model,
            "sharedAcrossModes": self.shared_across_modes,
            "knownGains": self.known_gains,
        }

    @staticmethod
    def from_json(d: dict[str, Any] | None) -> "AdjustmentOptions | None":
        if d is None:
            return None
        kg = d.get("knownGains")
        return AdjustmentOptions(
            float(d.get("stepC", 0)),
            float(d.get("rangeC", 0)),
            parse_enum(AdjustObjective, d.get("objective", 0)),
            opt_float(d.get("favourC")),
            parse_enum(AdjustModel, d.get("model", 0)),
            bool(d.get("sharedAcrossModes", True)),
            None if kg is None else {parse_enum(OvenMode, k): float(v) for k, v in kg.items()},
        )


def _profile_gains(oven: Any) -> dict[OvenMode, float] | None:
    g = oven.gains
    return {k: v.gain for k, v in g.items()} if g else None


@dataclass(frozen=True)
class AdjustPoint:
    """One measured setpoint, and the adjustment the oven had while it was measured (0 when not recorded)."""

    mode: OvenMode
    setpoint_c: float
    offset_c: float
    half_width_c: float | None
    adjustment_c: float


@dataclass(frozen=True)
class Residual:
    mode: OvenMode
    setpoint_c: float
    now_c: float
    after_c: float
    half_width_c: float | None

    def to_json(self) -> dict[str, Any]:
        return {
            "mode": self.mode,
            "setpointC": self.setpoint_c,
            "nowC": self.now_c,
            "afterC": self.after_c,
            "halfWidthC": self.half_width_c,
        }


@dataclass(frozen=True)
class DialCorrection:
    mode: OvenMode
    want_c: float
    set_c: float

    def to_json(self) -> dict[str, Any]:
        return {"mode": self.mode, "wantC": self.want_c, "setC": self.set_c}


class GainSource(enum.IntEnum):
    Measured = 0
    Profile = 1
    Assumed = 2


@dataclass(frozen=True)
class ModeGain:
    gain: float
    source: GainSource
    half_width: float | None

    def to_json(self) -> dict[str, Any]:
        return {"gain": self.gain, "source": self.source, "halfWidth": self.half_width}


@dataclass(frozen=True)
class AdjustmentAdvice:
    change_c: float
    current_c: float | None
    new_setting_c: float | None
    clamped: bool
    gain: float
    gain_measured: bool
    gain_half_width_c: float | None
    options: AdjustmentOptions
    mode: OvenMode | None
    residuals: list[Residual]
    dial_corrections: list[DialCorrection]
    add_setpoints_c: list[float]
    too_few_setpoints: str | None
    curved: bool
    gain_from_profile: bool = False
    gains: dict[OvenMode, ModeGain] | None = None

    @property
    def worst_after_c(self) -> float:
        return 0.0 if not self.residuals else max(abs(r.after_c) for r in self.residuals)

    def to_json(self) -> dict[str, Any]:
        return {
            "changeC": self.change_c,
            "currentC": self.current_c,
            "newSettingC": self.new_setting_c,
            "clamped": self.clamped,
            "gain": self.gain,
            "gainMeasured": self.gain_measured,
            "gainHalfWidthC": self.gain_half_width_c,
            "options": self.options,
            "mode": self.mode,
            "residuals": self.residuals,
            "dialCorrections": self.dial_corrections,
            "addSetpointsC": self.add_setpoints_c,
            "tooFewSetpoints": self.too_few_setpoints,
            "curved": self.curved,
            "gainFromProfile": self.gain_from_profile,
            "gains": self.gains,
            "worstAfterC": self.worst_after_c,
        }


WORTH_C = 5 / 1.8
"""An error below this isn't worth correcting: about the probe's own accuracy (5 °F)."""

SHAPE_SPAN_C = 50 / 1.8
"""Setpoints closer together than this (50 °F) can't show how the error changes with temperature."""

_SAME_SETPOINT_C = 0.05


def advise(points: list[AdjustPoint], options: AdjustmentOptions, current_c: float | None) -> list[AdjustmentAdvice]:
    if not points:
        return []
    modes = distinct(p.mode for p in points)
    if options.shared_across_modes:
        # With only one mode measured, "shared" advice is that mode's advice.
        groups: list[tuple[OvenMode | None, list[AdjustPoint]]] = [(modes[0] if len(modes) == 1 else None, points)]
    else:
        groups = sorted(group_by(points, lambda p: p.mode), key=lambda g: g[0])
    return [_advise_group(g, options, current_c, mode) for mode, g in groups]


def _advise_group(
    points: list[AdjustPoint], options: AdjustmentOptions, current_c: float | None, mode: OvenMode | None
) -> AdjustmentAdvice:
    reference = current_c if current_c is not None else points[-1].adjustment_c
    gains = mode_gains(points, options)

    def G(m: OvenMode) -> float:
        return gains[m].gain

    # Every setpoint's error as it would be at the reference adjustment, repeats averaged.
    moved = [
        replace(p, offset_c=p.offset_c + G(p.mode) * (reference - p.adjustment_c), adjustment_c=reference) for p in points
    ]
    now: list[AdjustPoint] = []
    for (m, _), g in group_by(moved, lambda p: (p.mode, round_even(p.setpoint_c / _SAME_SETPOINT_C))):
        hw = (
            math.sqrt(stats.total(p.half_width_c * p.half_width_c for p in g)) / len(g)  # type: ignore[operator]
            if all(p.half_width_c is not None for p in g)
            else None
        )
        now.append(
            AdjustPoint(m, stats.average(p.setpoint_c for p in g), stats.average(p.offset_c for p in g), hw, reference)
        )
    now.sort(key=lambda p: (p.mode, p.setpoint_c))

    base = current_c if current_c is not None else 0.0
    lo = -options.range_c - base
    hi = options.range_c - base
    change, clamped = _best(now, options, G, lo, hi)

    residuals = [Residual(p.mode, p.setpoint_c, p.offset_c, p.offset_c + G(p.mode) * change, p.half_width_c) for p in now]
    used = list(gains.values())
    measured = all(g.source == GainSource.Measured for g in used)
    if len(used) == 1:
        hw = used[0].half_width
    elif all(g.half_width is not None for g in used):
        hw = math.sqrt(stats.total(g.half_width * g.half_width for g in used)) / len(used)  # type: ignore[operator]
    else:
        hw = None
    add, why = _add_setpoints(now)
    return AdjustmentAdvice(
        change,
        current_c,
        current_c + change if current_c is not None else None,
        clamped,
        stats.average(g.gain for g in used),
        measured,
        hw if measured else None,
        options,
        mode,
        residuals,
        _dial_corrections(residuals),
        add,
        why,
        _is_curved(now),
        (not measured) and any(g.source == GainSource.Profile for g in used),
        gains,
    )


def mode_gains(points: list[AdjustPoint], options: AdjustmentOptions) -> dict[OvenMode, ModeGain]:
    """How far the oven moves per degree of adjustment, for each mode: measured from the same setpoint run at
    different adjustments when there are such pairs; otherwise the oven profile's; otherwise assumed."""
    result: dict[OvenMode, ModeGain] = {}
    for mode, group in group_by(points, lambda p: p.mode):
        num = 0.0
        den = 0.0
        variance = 0.0
        for _, lst in group_by(group, lambda p: round_even(p.setpoint_c / _SAME_SETPOINT_C)):
            for i in range(len(lst)):
                for j in range(i + 1, len(lst)):
                    dk = lst[j].adjustment_c - lst[i].adjustment_c
                    if abs(dk) < 2 * options.step_c - 1e-9:
                        continue  # too small a change to measure anything
                    num += (lst[j].offset_c - lst[i].offset_c) * dk
                    den += dk * dk
                    hi_ = lst[i].half_width_c if lst[i].half_width_c is not None else 1.0
                    hj = lst[j].half_width_c if lst[j].half_width_c is not None else 1.0
                    pair_hw = math.pow(hi_, 2) + math.pow(hj, 2)
                    variance += pair_hw * dk * dk
        if den > 0:
            result[mode] = ModeGain(num / den, GainSource.Measured, math.sqrt(variance) / den)
            continue
        known = options.known_gains
        if known is not None and mode in known:
            result[mode] = ModeGain(known[mode], GainSource.Profile, None)
            continue
        assumed = 1.0
        if options.model == AdjustModel.Gain:
            f = stats.fit([p.setpoint_c for p in group], [p.setpoint_c + p.offset_c for p in group])
            if f is not None and 0.5 < f.slope < 1.5:
                assumed = f.slope
        result[mode] = ModeGain(assumed, GainSource.Assumed, None)
    return result


def _best(
    now: list[AdjustPoint], options: AdjustmentOptions, gain: Callable[[OvenMode], float], lo: float, hi: float
) -> tuple[float, bool]:
    """The change that best meets the objective, on the oven's steps and within its range, and whether the
    range cut it short. The steps are searched outright."""
    if all(gain(p.mode) <= 0.05 for p in now):
        return 0.0, False  # the adjustment doesn't move the oven: nothing to recommend
    step = options.step_c
    if options.favour_c is not None:
        g = stats.average(gain(p.mode) for p in now)
        wanted = _snap(-_error_at(now, options.favour_c) / g, step)
        allowed = clamp(wanted, _snap_up(lo, step), _snap_down(hi, step))
        return allowed, abs(allowed - wanted) > 1e-9

    if options.objective == AdjustObjective.Minimax:

        def cost(change: float) -> float:
            return max(abs(p.offset_c + gain(p.mode) * change) for p in now)

    else:

        def cost(change: float) -> float:
            return stats.total(math.pow(p.offset_c + gain(p.mode) * change, 2) for p in now)

    # The best step anywhere (well beyond the range, to see whether the range cuts it short), then within it.
    def best_in(frm: float, to: float) -> float:
        best = 0.0
        best_cost = math.inf
        k = int(net_ceiling(frm / step - 1e-9))
        last = int(net_floor(to / step + 1e-9))
        while k <= last:
            change = float(k) * step
            c = cost(change)
            # On a tie, the smaller change.
            if c < best_cost - 1e-9 or (abs(c - best_cost) <= 1e-9 and abs(change) < abs(best)):
                best, best_cost = change, c
            k += 1
        return best

    reach = 4 * options.range_c + max(abs(p.offset_c) for p in now) / max(0.05, min(gain(p.mode) for p in now))
    anywhere = best_in(-reach, reach)
    in_range = best_in(lo, hi)
    return in_range, anywhere < lo - 1e-9 or anywhere > hi + 1e-9


def _error_at(now: list[AdjustPoint], c: float) -> float:
    """The error at a temperature: from a straight line through the setpoints, or their mean with only one."""
    f = stats.fit([p.setpoint_c for p in now], [p.offset_c for p in now])
    return stats.average(p.offset_c for p in now) if f is None else f.intercept + f.slope * c


def _dial_corrections(residuals: list[Residual]) -> list[DialCorrection]:
    if all(abs(r.after_c) < WORTH_C for r in residuals):
        return []
    # Where the oven still runs hot by r at a setting, set that much lower to get the temperature wanted.
    return [DialCorrection(r.mode, r.setpoint_c, r.setpoint_c - r.after_c) for r in residuals if abs(r.after_c) >= WORTH_C]


def _add_setpoints(now: list[AdjustPoint]) -> tuple[list[float], str | None]:
    """Setpoints to add when the ones measured can't show whether the error changes with temperature."""
    why: str | None = None
    add: list[float] = []
    for _, g in group_by(now, lambda p: p.mode):
        sps = sorted(distinct(p.setpoint_c for p in g))
        span = sps[-1] - sps[0]
        if len(sps) == 1:
            why = "With one setpoint per mode, the app can't tell an error that's the same at every setting from one that grows with temperature."
        elif span < SHAPE_SPAN_C:
            why = "The setpoints are too close together to show how the error changes with temperature."
        elif len(sps) == 2 and abs(g[-1].offset_c - g[0].offset_c) >= WORTH_C:
            why = "The error changes between the two setpoints. A third, in between, would show whether it changes in a straight line."
        else:
            continue

        if len(sps) == 2 and span >= SHAPE_SPAN_C:
            add.append((sps[0] + sps[1]) / 2)
        else:
            add.append(sps[0] - SHAPE_SPAN_C)
            add.append(sps[-1] + SHAPE_SPAN_C)
    # Keep to ordinary baking temperatures (250–500 °F).
    return sorted(distinct(clamp(c, 121.0, 260.0) for c in add)), why


def _is_curved(now: list[AdjustPoint]) -> bool:
    """With three or more setpoints in a mode, whether the error bends more than a line and the uncertainty explain."""
    for _, lst in group_by(now, lambda p: p.mode):
        if len(lst) < 3:
            continue
        f = stats.fit([p.setpoint_c for p in lst], [p.offset_c for p in lst])
        if f is None:
            continue
        worst = max(
            abs(p.offset_c - (f.intercept + f.slope * p.setpoint_c)) - (p.half_width_c if p.half_width_c is not None else 0.0)
            for p in lst
        )
        if worst > max(1.0, WORTH_C / 2):
            return True
    return False


def _snap(c: float, step: float) -> float:
    return round_away(c / step) * step


def _snap_down(c: float, step: float) -> float:
    return net_floor(c / step + 1e-9) * step


def _snap_up(c: float, step: float) -> float:
    return net_ceiling(c / step - 1e-9) * step
