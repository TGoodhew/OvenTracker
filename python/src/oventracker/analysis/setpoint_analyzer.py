"""Works out, from the readings alone, when the oven has settled at a setpoint, measures its steady-state mean
over whole thermostat cycles, and decides how long it has to keep measuring (Analysis/SetpointAnalyzer.cs).

The phases are: heating (or cooling) until the first turning point, whose peak is the overshoot; settling, until
the first trough that is not the first turning point; and measuring, where each trough closes a cycle whose
time-weighted mean is one independent value. While the cycle means still trend the oldest cycle is dropped. It
finishes once there are at least ``min_cycles`` cycles and the 95 % interval of their mean is within
``precision_c``. An oven that holds temperature without a visible cycle is measured in fixed blocks instead.
"""

from __future__ import annotations

import enum
import math
from collections import deque
from dataclasses import dataclass, fields
from typing import Any

from . import stats
from .sample_series import Sample, SampleSeries
from .turning_points import TurningKind, TurningPoint, TurningPointDetector


class PointPhase(enum.IntEnum):
    Heating = 0
    Settling = 1
    Measuring = 2
    Complete = 3


class UnitKind(enum.IntEnum):
    """How the steady state is cut into independent pieces: whole cycles, or fixed-length blocks."""

    Cycle = 0
    Block = 1


# "None" is a Python keyword, so this enum is made with the functional API; CompletionReason["None"] is it.
CompletionReason = enum.IntEnum(  # type: ignore[misc]
    "CompletionReason", [("None", 0), ("Converged", 1), ("TimedOut", 2), ("Manual", 3), ("Aborted", 4), ("Skipped", 5)]
)
REASON_NONE = CompletionReason["None"]


@dataclass(frozen=True)
class AnalysisOptions:
    precision_c: float = 1.0
    """Target half-width of the 95 % confidence interval of the mean (°C)."""
    min_cycles: int = 4
    min_blocks: int = 5
    block_minutes: float = 3.0
    min_hysteresis_c: float = 1.0
    smoothing_seconds: float = 5.0
    max_point_minutes: float = 120.0
    no_cycle_minutes: float = 15.0
    flat_window_minutes: float = 10.0
    flat_slope_c_per_minute: float = 0.15
    drift_horizon_minutes: float = 30.0
    default_cycle_minutes: float = 6.0

    _JSON = {
        "precision_c": "precisionC",
        "min_cycles": "minCycles",
        "min_blocks": "minBlocks",
        "block_minutes": "blockMinutes",
        "min_hysteresis_c": "minHysteresisC",
        "smoothing_seconds": "smoothingSeconds",
        "max_point_minutes": "maxPointMinutes",
        "no_cycle_minutes": "noCycleMinutes",
        "flat_window_minutes": "flatWindowMinutes",
        "flat_slope_c_per_minute": "flatSlopeCPerMinute",
        "drift_horizon_minutes": "driftHorizonMinutes",
        "default_cycle_minutes": "defaultCycleMinutes",
    }

    def to_json(self) -> dict[str, Any]:
        return {self._JSON[f.name]: getattr(self, f.name) for f in fields(self)}

    @staticmethod
    def from_json(d: dict[str, Any] | None) -> "AnalysisOptions":
        if d is None:
            return AnalysisOptions()
        kwargs: dict[str, Any] = {}
        for f in fields(AnalysisOptions):
            key = AnalysisOptions._JSON[f.name]
            if key in d and d[key] is not None:
                kwargs[f.name] = int(d[key]) if f.name in ("min_cycles", "min_blocks") else float(d[key])
        return AnalysisOptions(**kwargs)


@dataclass(frozen=True)
class MeasurementUnit:
    """One cycle or block of the steady state."""

    kind: UnitKind
    start: float
    end: float
    mean_c: float
    min_c: float
    max_c: float

    @property
    def duration(self) -> float:
        return self.end - self.start

    @property
    def mid(self) -> float:
        return (self.start + self.end) / 2

    @property
    def swing_c(self) -> float:
        return self.max_c - self.min_c


@dataclass(frozen=True)
class Estimate:
    n: int
    mean_c: float
    std_dev_c: float
    half_width_c: float | None
    drift_c_per_hour: float | None


@dataclass(frozen=True)
class Eta:
    seconds: float
    rough: bool


def _sub(a: float | None, b: float | None) -> float | None:
    return None if a is None or b is None else a - b


def _div(a: float | None, b: float) -> float | None:
    return None if a is None else a / b


@dataclass(frozen=True)
class PointMetrics:
    setpoint_c: float
    reason: Any  # CompletionReason
    kind: UnitKind | None
    units: int
    discarded_units: int
    mean_c: float | None
    half_width_c: float | None
    std_dev_c: float | None
    drift_c_per_hour: float | None
    swing_c: float | None
    min_c: float | None
    max_c: float | None
    cycle_minutes: float | None
    start_c: float | None
    time_to_setpoint_minutes: float | None
    overshoot_c: float | None
    settle_minutes: float | None
    measure_minutes: float | None
    total_minutes: float
    preheat_signal_minutes: float | None
    preheat_signal_c: float | None
    precision_target_c: float
    longest_gap_seconds: float | None = None

    @property
    def offset_c(self) -> float | None:
        return _sub(self.mean_c, self.setpoint_c)

    @property
    def met_precision(self) -> bool:
        hw = self.half_width_c
        return hw is not None and hw <= self.precision_target_c + 1e-9

    def to_json(self) -> dict[str, Any]:
        return {
            "setpointC": self.setpoint_c,
            "reason": self.reason,
            "kind": self.kind,
            "units": self.units,
            "discardedUnits": self.discarded_units,
            "meanC": self.mean_c,
            "halfWidthC": self.half_width_c,
            "stdDevC": self.std_dev_c,
            "driftCPerHour": self.drift_c_per_hour,
            "swingC": self.swing_c,
            "minC": self.min_c,
            "maxC": self.max_c,
            "cycleMinutes": self.cycle_minutes,
            "startC": self.start_c,
            "timeToSetpointMinutes": self.time_to_setpoint_minutes,
            "overshootC": self.overshoot_c,
            "settleMinutes": self.settle_minutes,
            "measureMinutes": self.measure_minutes,
            "totalMinutes": self.total_minutes,
            "preheatSignalMinutes": self.preheat_signal_minutes,
            "preheatSignalC": self.preheat_signal_c,
            "precisionTargetC": self.precision_target_c,
            "longestGapSeconds": self.longest_gap_seconds,
            "offsetC": self.offset_c,
            "metPrecision": self.met_precision,
        }


def _sign(x: float) -> int:
    return (x > 0) - (x < 0)


class SetpointAnalyzer:
    def __init__(self, setpoint_c: float, options: AnalysisOptions | None = None):
        self.setpoint_c = setpoint_c
        self.options = options if options is not None else AnalysisOptions()
        self.series = SampleSeries()
        self._detector = TurningPointDetector()
        self.turning_points: list[TurningPoint] = []
        self.units: list[MeasurementUnit] = []
        self._cycle_durations: list[float] = []
        self._smooth_window: deque[Sample] = deque()
        self._noise_window: list[float] = []
        self._smooth_sum = 0.0
        self._noise_sigma = 0.0
        self._unit_start = 0.0
        self._last_flat_check = -math.inf
        self.measuring_start: float | None = None
        self._time_to_setpoint: float | None = None
        self._overshoot_c: float | None = None
        self._preheat_t: float | None = None
        self._preheat_c: float | None = None
        self.phase = PointPhase.Heating
        self.kind: UnitKind | None = None
        self.reason = REASON_NONE
        self.completed_at: float | None = None
        self.discarded_units = 0
        self.start_c: float | None = None

    @property
    def now(self) -> float:
        return 0.0 if self.series.count == 0 else self.series.last.t

    @property
    def latest_c(self) -> float | None:
        return None if self.series.count == 0 else self.series.last.c

    @property
    def smoothed_c(self) -> float | None:
        return None if not self._smooth_window else self._smooth_sum / len(self._smooth_window)

    @property
    def hysteresis(self) -> float:
        return max(self.options.min_hysteresis_c, 5 * self._noise_sigma)

    @property
    def cooling(self) -> bool:
        return self.start_c is not None and self.start_c > self.setpoint_c

    @property
    def min_units(self) -> int:
        return self.options.min_blocks if self.kind == UnitKind.Block else self.options.min_cycles

    def add(self, t: float, c: float) -> None:
        o = self.options
        if self.phase == PointPhase.Complete:
            return
        if not self.series.add(Sample(t, c)):
            return
        if self.start_c is None:
            self.start_c = c

        self._smooth_window.append(Sample(t, c))
        self._smooth_sum += c
        while self._smooth_window[0].t < t - o.smoothing_seconds:
            self._smooth_sum -= self._smooth_window.popleft().c
        smooth = self._smooth_sum / len(self._smooth_window)

        self._noise_window.append(c)
        if len(self._noise_window) > 120:
            del self._noise_window[0]
        if self.series.count % 30 == 0:
            self._noise_sigma = stats.noise_sigma(self._noise_window)

        if self._time_to_setpoint is None and (smooth <= self.setpoint_c if self.cooling else smooth >= self.setpoint_c):
            self._time_to_setpoint = t

        tp = self._detector.add(t, smooth, self.hysteresis)
        if tp is not None:
            self._on_turning_point(tp)

        if self.phase == PointPhase.Measuring and self.kind == UnitKind.Block:
            length = o.block_minutes * 60
            while self.phase == PointPhase.Measuring and t >= self._unit_start + length:
                self._close_unit(UnitKind.Block, self._unit_start, self._unit_start + length)
                self._unit_start += length

        if self.phase != PointPhase.Complete and t - self._last_flat_check >= 10:
            self._last_flat_check = t
            self._check_for_no_cycling(t)

        if self.phase != PointPhase.Complete and t >= o.max_point_minutes * 60:
            self._complete(CompletionReason.TimedOut)

    def _on_turning_point(self, tp: TurningPoint) -> None:
        self.turning_points.append(tp)

        if self.phase == PointPhase.Heating:
            self.phase = PointPhase.Settling
            if self._time_to_setpoint is None:
                self._time_to_setpoint = tp.t  # an oven that runs cold turns round before it gets there
            if tp.kind == TurningKind.Peak and not self.cooling:
                self._overshoot_c = self.series.range(tp.t - 30, tp.t + 30)[1] - self.setpoint_c

        if tp.kind != TurningKind.Trough or self.kind == UnitKind.Block:
            return

        if self.phase == PointPhase.Measuring:
            self._close_unit(UnitKind.Cycle, self._unit_start, tp.t)
            self._unit_start = tp.t
        elif self.phase == PointPhase.Settling and len(self.turning_points) >= 2:
            self.phase = PointPhase.Measuring
            self.kind = UnitKind.Cycle
            self.measuring_start = tp.t
            self._unit_start = tp.t

    def _check_for_no_cycling(self, t: float) -> None:
        o = self.options
        last_turn = self.turning_points[-1].confirmed_at if self.turning_points else 0.0
        quiet_for = t - last_turn
        no_cycle = o.no_cycle_minutes * 60

        # Measuring cycles, but the cycling has stopped: fall back to blocks from here.
        if self.phase == PointPhase.Measuring and self.kind == UnitKind.Cycle:
            expected = self.expected_unit_seconds()
            if expected is None:
                expected = o.default_cycle_minutes * 60
            if quiet_for < max(3 * expected, no_cycle) or not self._is_flat(t):
                return
            self.discarded_units += len(self.units)
            self.units.clear()
            self._start_blocks(t)
            return

        if (
            self.phase in (PointPhase.Heating, PointPhase.Settling)
            and quiet_for >= no_cycle
            and t >= o.flat_window_minutes * 60
            and self._is_flat(t)
        ):
            self._start_blocks(t)

    def _is_flat(self, t: float) -> bool:
        slope = self.series.slope(t - self.options.flat_window_minutes * 60, t)
        return slope is not None and abs(slope * 60) < self.options.flat_slope_c_per_minute

    def _start_blocks(self, t: float) -> None:
        if self.phase == PointPhase.Heating:
            self.phase = PointPhase.Settling
        self.phase = PointPhase.Measuring
        self.kind = UnitKind.Block
        if self.measuring_start is None:
            self.measuring_start = t
        self._unit_start = t

    def _close_unit(self, kind: UnitKind, t0: float, t1: float) -> None:
        if t1 - t0 < 20:
            return  # a reversal this quick is a disturbance, not a cycle
        mn, mx = self.series.range(t0, t1)
        self.units.append(MeasurementUnit(kind, t0, t1, self.series.mean(t0, t1), mn, mx))
        if kind == UnitKind.Cycle:
            self._cycle_durations.append(t1 - t0)
        self._evaluate()

    def _evaluate(self) -> None:
        self._drop_warm_up_units()
        e = self.current_estimate
        if e is not None and e.half_width_c is not None and e.n >= self.min_units and e.half_width_c <= self.options.precision_c:
            self._complete(CompletionReason.Converged)

    def _drop_warm_up_units(self) -> None:
        """While the unit means still trend by more than the precision target, the oven is still settling."""
        o = self.options
        units = self.units
        while len(units) >= 3:
            f = stats.fit([u.mid for u in units], [u.mean_c for u in units])
            if f is None or f.slope_std_err is None:
                return
            se = f.slope_std_err
            change = abs(f.slope * max(units[-1].mid - units[0].mid, o.drift_horizon_minutes * 60))
            if se > 0:
                t_stat = abs(f.slope) / se
            else:
                t_stat = math.inf if f.slope != 0 else 0.0
            if change <= o.precision_c or t_stat <= 2:
                return
            del units[0]
            self.discarded_units += 1

    def _complete(self, reason: Any) -> None:
        self.phase = PointPhase.Complete
        self.reason = reason
        self.completed_at = self.now

    def mark_preheat_signal(self) -> None:
        if self.series.count == 0:
            return
        self._preheat_t = self.now
        self._preheat_c = self.smoothed_c

    def finish(self, reason: Any) -> None:
        if self.phase != PointPhase.Complete:
            self._complete(reason)

    @property
    def current_estimate(self) -> Estimate | None:
        units = self.units
        if not units:
            return None
        means = [u.mean_c for u in units]
        drift = None
        if len(units) >= 3:
            f = stats.fit([u.mid for u in units], means)
            if f is not None:
                drift = f.slope * 3600
        return Estimate(len(units), stats.mean(means), stats.std_dev(means), stats.half_width_95(means), drift)

    @property
    def required_units(self) -> int:
        """How many units the current spread says are needed to reach the precision target."""
        if len(self.units) < 2:
            return self.min_units
        sd = stats.std_dev([u.mean_c for u in self.units])
        return max(len(self.units), stats.required_count(sd, self.options.precision_c, self.min_units, 99))

    def expected_unit_seconds(self) -> float | None:
        """Expected length of one unit: the oven's own cycle once it has been seen."""
        if self.kind == UnitKind.Block:
            return self.options.block_minutes * 60
        if self._cycle_durations:
            return stats.median(self._cycle_durations)
        tps = self.turning_points
        if len(tps) >= 2:
            gaps = [tps[i].t - tps[i - 1].t for i in range(1, len(tps))]
            return 2 * stats.median(gaps)
        return None

    def estimate_remaining(self) -> Eta | None:
        """Estimated time still needed at this setpoint."""
        o = self.options
        if self.phase == PointPhase.Complete:
            return Eta(0.0, False)
        now = self.now
        cap = max(0.0, o.max_point_minutes * 60 - now)
        known = self.expected_unit_seconds()
        unit = known if known is not None else o.default_cycle_minutes * 60
        required = self.required_units
        if self.phase == PointPhase.Measuring:
            into = now - self._unit_start
            n = len(self.units)
            seconds = (required - n) * unit - into if required > n else unit - into
            seconds = max(seconds, 0.0)
            rough = n < 2
        elif self.phase == PointPhase.Settling:
            seconds = 0.5 * unit + required * unit
            rough = True
        else:
            slope = self.series.slope(now - 120, now) if self.series.count > 10 else None
            smoothed = self.smoothed_c
            to_go = self.setpoint_c - (smoothed if smoothed is not None else self.setpoint_c)
            reach = 0.0
            if slope is not None and abs(slope) > 0.002 and _sign(slope) == _sign(to_go):
                reach = to_go / slope
            elif abs(to_go) > 5:
                return None
            seconds = reach + 1.5 * unit + required * unit
            rough = True
        return Eta(min(seconds, cap), rough)

    def _longest_gap(self) -> float | None:
        """The longest time between two readings, e.g. while the meter was switched off."""
        items = self.series.items
        if len(items) < 2:
            return None
        longest = 0.0
        for i in range(1, len(items)):
            longest = max(longest, items[i].t - items[i - 1].t)
        return longest

    def metrics(self) -> PointMetrics:
        e = self.current_estimate
        units = self.units
        swing = mn = mx = cycle_min = measure_min = None
        if units:
            swing = stats.average(u.swing_c for u in units)
            mn = min(u.min_c for u in units)
            mx = max(u.max_c for u in units)
            measure_min = (units[-1].end - units[0].start) / 60
            if self.kind == UnitKind.Cycle:
                cycle_min = stats.average(u.duration for u in units) / 60
        return PointMetrics(
            self.setpoint_c,
            self.reason,
            self.kind,
            len(units),
            self.discarded_units,
            None if e is None else e.mean_c,
            None if e is None else e.half_width_c,
            None if e is None else e.std_dev_c,
            None if e is None else e.drift_c_per_hour,
            swing,
            mn,
            mx,
            cycle_min,
            self.start_c,
            _div(self._time_to_setpoint, 60),
            self._overshoot_c,
            units[0].start / 60 if units else _div(self.measuring_start, 60),
            measure_min,
            (self.completed_at if self.completed_at is not None else self.now) / 60,
            _div(self._preheat_t, 60),
            self._preheat_c,
            self.options.precision_c,
            self._longest_gap(),
        )
