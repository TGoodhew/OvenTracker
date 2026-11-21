"""Test points, their results, the verdict and the per-mode summaries (Analysis/SessionSummary.cs)."""

from __future__ import annotations

import enum
import math
from dataclasses import dataclass
from typing import Any, Iterable

from ..jsonio import opt_float, parse_enum
from ..units import OvenMode
from . import stats
from .setpoint_analyzer import PointMetrics


@dataclass(frozen=True)
class TestPoint:
    """``adjustment_c``: the oven's thermostat adjustment while this point was measured, when it differs from
    the session's (points from several runs analysed together). None means the session's."""

    __test__ = False  # not a pytest test class

    index: int
    mode: OvenMode
    setpoint_c: float
    adjustment_c: float | None = None

    def to_json(self) -> dict[str, Any]:
        return {"index": self.index, "mode": self.mode, "setpointC": self.setpoint_c, "adjustmentC": self.adjustment_c}

    @staticmethod
    def from_json(d: dict[str, Any]) -> "TestPoint":
        return TestPoint(
            int(d.get("index", 0)),
            parse_enum(OvenMode, d.get("mode", 0)),
            float(d.get("setpointC", 0)),
            opt_float(d.get("adjustmentC")),
        )


@dataclass(frozen=True)
class PointResult:
    """``after_reset``: measured after a break; ``started_warm``: the first setpoint, started with the oven warm."""

    point: TestPoint
    metrics: PointMetrics
    after_reset: bool = False
    started_warm: bool = False

    def to_json(self) -> dict[str, Any]:
        return {
            "point": self.point,
            "metrics": self.metrics,
            "afterReset": self.after_reset,
            "startedWarm": self.started_warm,
        }


class Verdict(enum.IntEnum):
    Accurate = 0
    Borderline = 1
    OutOfTolerance = 2
    Inconclusive = 3


@dataclass(frozen=True)
class ModeSummary:
    mode: OvenMode
    points: int
    mean_offset_c: float
    max_abs_offset_c: float
    gain_per_degree: float | None
    mean_swing_c: float | None
    mean_cycle_minutes: float | None
    suggested_calibration_c: float


@dataclass(frozen=True)
class ModeComparison:
    setpoint_c: float
    bake: PointMetrics
    convection: PointMetrics

    @property
    def delta_mean_c(self) -> float:
        """Convection mean minus bake mean."""
        return self.convection.mean_c - self.bake.mean_c  # type: ignore[operator]

    @property
    def delta_swing_c(self) -> float | None:
        a, b = self.convection.swing_c, self.bake.swing_c
        return None if a is None or b is None else a - b

    @property
    def delta_time_to_setpoint_minutes(self) -> float | None:
        a, b = self.convection.time_to_setpoint_minutes, self.bake.time_to_setpoint_minutes
        return None if a is None or b is None else a - b

    @property
    def significant(self) -> bool:
        """Whether the two means differ by more than their combined 95 % uncertainty."""
        a = self.bake.half_width_c if self.bake.half_width_c is not None else math.inf
        b = self.convection.half_width_c if self.convection.half_width_c is not None else math.inf
        return abs(self.delta_mean_c) > math.sqrt(a * a + b * b)


def group_by(items: Iterable[Any], key: Any) -> list[tuple[Any, list[Any]]]:
    """LINQ GroupBy: groups in order of their first element, elements in order."""
    groups: dict[Any, list[Any]] = {}
    for item in items:
        groups.setdefault(key(item), []).append(item)
    return list(groups.items())


def judge(m: PointMetrics, tolerance_c: float) -> Verdict:
    off = m.offset_c
    return Verdict.Inconclusive if off is None else judge_offset(off, m.half_width_c, tolerance_c)


def judge_offset(offset_c: float, half_width_c: float | None, tolerance_c: float) -> Verdict:
    """An error against the tolerance, given its ±95 % half-width (SessionSummary.Judge(offset, halfWidth,
    tolerance)). Without a half-width the uncertainty is unknown, so an error within tolerance is only Borderline.
    The verdicts and the report's "Within tolerance?" column both use this."""
    hw = half_width_c
    if hw is None:
        return Verdict.Borderline if abs(offset_c) <= tolerance_c else Verdict.OutOfTolerance
    if abs(offset_c) + hw <= tolerance_c:
        return Verdict.Accurate
    if abs(offset_c) - hw > tolerance_c:
        return Verdict.OutOfTolerance
    return Verdict.Borderline


def usable(results: Iterable[PointResult]) -> list[PointResult]:
    return [r for r in results if r.metrics.mean_c is not None]


def distinct(values: Iterable[Any]) -> list[Any]:
    """LINQ Distinct: first occurrences, in order."""
    seen: list[Any] = []
    for v in values:
        if v not in seen:
            seen.append(v)
    return seen


def by_mode(results: Iterable[PointResult]) -> list[ModeSummary]:
    out: list[ModeSummary] = []
    for mode, group in sorted(group_by(usable(results), lambda r: r.point.mode), key=lambda g: g[0]):
        pts = sorted(group, key=lambda r: r.point.setpoint_c)
        offsets = [r.metrics.offset_c for r in pts]
        gain = None
        if len(distinct(p.point.setpoint_c for p in pts)) >= 2:
            f = stats.fit([p.point.setpoint_c for p in pts], [p.metrics.mean_c for p in pts])
            gain = None if f is None else f.slope
        swings = [p.metrics.swing_c for p in pts if p.metrics.swing_c is not None]
        cycles = [p.metrics.cycle_minutes for p in pts if p.metrics.cycle_minutes is not None]
        mean_offset = stats.average(offsets)
        out.append(
            ModeSummary(
                mode,
                len(pts),
                mean_offset,
                max(abs(o) for o in offsets),
                gain,
                stats.average(swings) if swings else None,
                stats.average(cycles) if cycles else None,
                -mean_offset,
            )
        )
    return out


def compare(results: Iterable[PointResult]) -> list[ModeComparison]:
    from .adjustment_advisor import setpoint_key  # imports this module

    out: list[ModeComparison] = []
    groups = group_by(usable(results), lambda r: setpoint_key(r.point.setpoint_c))
    for _, g in sorted(groups, key=lambda kv: kv[0]):
        bake = next((r for r in reversed(g) if r.point.mode == OvenMode.Bake), None)
        conv = next((r for r in reversed(g) if r.point.mode == OvenMode.Convection), None)
        if bake is not None and conv is not None:
            out.append(ModeComparison(g[0].point.setpoint_c, bake.metrics, conv.metrics))
    return out
