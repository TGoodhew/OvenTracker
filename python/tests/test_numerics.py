"""The statistics, the series, the turning points and the adjustment advisor, against values worked by hand."""

from __future__ import annotations

import math

import pytest

from oventracker.analysis import stats
from oventracker.analysis.adjustment_advisor import (
    AdjustmentOptions,
    AdjustPoint,
    AdjustObjective,
    GainSource,
    advise,
    mode_gains,
)
from oventracker.analysis.sample_series import Sample, SampleSeries
from oventracker.analysis.setpoint_analyzer import AnalysisOptions, CompletionReason, SetpointAnalyzer, UnitKind
from oventracker.analysis.turning_points import TurningKind, TurningPointDetector
from oventracker.units import OvenMode, TempUnit


def test_student_t_table_and_its_tail():
    assert stats.t_crit_95(0) == math.inf
    assert stats.t_crit_95(1) == 12.706
    assert stats.t_crit_95(3) == 3.182
    assert stats.t_crit_95(30) == 2.042
    assert stats.t_crit_95(40) == 1.96 + 2.4 / 40


def test_mean_std_dev_and_half_width():
    x = [1.0, 2.0, 3.0, 4.0]
    assert stats.mean(x) == 2.5
    assert stats.std_dev(x) == math.sqrt(5 / 3)
    assert stats.half_width_95(x) == 3.182 * math.sqrt(5 / 3) / 2
    assert stats.half_width_95([1.0]) is None
    assert stats.std_dev([1.0]) == 0
    with pytest.raises(ValueError):
        stats.mean([])


def test_the_mean_sums_in_order():
    # Summed left to right, as the C# loop does: 1e16 + 1 + 1 stays 1e16 at each step.
    assert stats.mean([1e16, 1.0, 1.0, -1e16]) == 0.0
    assert stats.average([1e16, 1.0, 1.0, -1e16]) == 0.0


def test_required_count():
    assert stats.required_count(0, 1, 4, 99) == 4
    # sd 2, target 1: the first n with t(n-1) * 2 / sqrt(n) <= 1
    n = stats.required_count(2.0, 1.0, 4, 99)
    assert stats.t_crit_95(n - 1) * 2 / math.sqrt(n) <= 1 < stats.t_crit_95(n - 2) * 2 / math.sqrt(n - 1)
    assert stats.required_count(100.0, 0.01, 4, 99) == 99


def test_fit():
    f = stats.fit([0.0, 1.0, 2.0], [1.0, 3.0, 5.0])
    assert f is not None
    assert (f.slope, f.intercept, f.slope_std_err, f.n) == (2.0, 1.0, 0.0, 3)
    two = stats.fit([0.0, 2.0], [0.0, 1.0])
    assert two is not None and two.slope_std_err is None
    assert stats.fit([1.0, 1.0], [0.0, 1.0]) is None
    assert stats.fit([1.0], [1.0]) is None


def test_median_and_noise_sigma():
    assert stats.median([3.0, 1.0, 2.0]) == 2.0
    assert stats.median([4.0, 1.0, 3.0, 2.0]) == 2.5
    assert stats.noise_sigma([1.0, 2.0, 3.0]) == 0
    # A steady ramp has no second difference: no noise.
    assert stats.noise_sigma([float(i) for i in range(20)]) == 0
    assert stats.noise_sigma([0.0, 1.0, 0.0, 1.0, 0.0, 1.0]) == 1.4826 * 2 / math.sqrt(6)


def test_sample_series():
    s = SampleSeries()
    assert s.add(Sample(0, 10))
    assert s.add(Sample(10, 20))
    assert not s.add(Sample(10, 30))  # not later than the last
    assert s.add(Sample(20, 10))
    assert s.value_at(5) == 15
    assert s.value_at(-1) == 10
    assert s.value_at(99) == 10
    assert s.mean(0, 20) == 15
    assert s.mean(5, 5) == 15
    assert s.range(5, 15) == (15, 20)
    assert s.slope(0, 10) is None  # fewer than three samples


def test_turning_points():
    d = TurningPointDetector()
    seen = [d.add(t, c, 1.0) for t, c in enumerate([0, 0.5, 1.2, 3, 5, 4.5, 3.9, 3, 4, 5])]
    found = [tp for tp in seen if tp is not None]
    assert [(tp.kind, tp.t, tp.c, tp.confirmed_at) for tp in found] == [
        (TurningKind.Peak, 4, 5, 6),
        (TurningKind.Trough, 7, 3, 8),
    ]


def _cycling_oven(setpoint: float, minutes: float) -> list[tuple[float, float]]:
    """A thermostat cycling ±9 °C about setpoint + 2, every 3 minutes, after a minute's heating."""
    out = []
    t = 0.0
    while t < minutes * 60:
        if t < 60:
            c = 20 + (setpoint + 11 - 20) * t / 60
        else:
            phase = ((t - 60) % 180) / 180
            c = setpoint + 2 + 9 * (1 - 4 * abs(phase - 0.5))
        out.append((t, c))
        t += 1
    return out


def test_a_cycling_oven_converges():
    a = SetpointAnalyzer(180, AnalysisOptions())
    for t, c in _cycling_oven(180, 60):
        a.add(t, c)
    m = a.metrics()
    assert m.reason == CompletionReason.Converged
    assert m.kind == UnitKind.Cycle
    assert m.units >= 4
    assert m.offset_c is not None and abs(m.offset_c - 2) < 0.1
    assert m.cycle_minutes is not None and abs(m.cycle_minutes - 3) < 0.05


def _points(*rows):
    return [AdjustPoint(OvenMode.Bake, sp, off, 0.2, adj) for sp, off, adj in rows]


def test_the_adjustment_is_the_least_squares_step():
    options = AdjustmentOptions.for_unit(TempUnit.Celsius)
    [a] = advise(_points((180, 4.4, 0), (220, 4.3, 0)), options, 0.0)
    assert a.change_c == -4
    assert a.new_setting_c == -4
    assert not a.clamped
    assert a.gains is not None and a.gains[OvenMode.Bake].source == GainSource.Assumed


def test_the_range_limits_the_adjustment():
    options = AdjustmentOptions.for_unit(TempUnit.Celsius)
    [a] = advise(_points((180, 30, 0), (220, 30, 0)), options, 0.0)
    assert a.change_c == -19
    assert a.clamped
    assert a.dial_corrections  # 11 °C out after adjusting


def test_minimax_and_favour():
    options = AdjustmentOptions(1.0, 19.0, objective=AdjustObjective.Minimax)
    [a] = advise(_points((150, 2, 0), (250, 8, 0)), options, 0.0)
    assert a.change_c == -5  # leaves −3 and +3
    favour = AdjustmentOptions(1.0, 19.0, favour_c=150.0)
    [b] = advise(_points((150, 2, 0), (250, 8, 0)), favour, 0.0)
    assert b.change_c == -2


def test_a_gain_is_measured_from_the_same_setpoint_at_two_adjustments():
    points = _points((180, 4.0, 0), (180, -0.5, -5))
    gains = mode_gains(points, AdjustmentOptions.for_unit(TempUnit.Celsius))
    g = gains[OvenMode.Bake]
    assert g.source == GainSource.Measured
    assert g.gain == pytest.approx(0.9)
    assert g.half_width == math.sqrt((0.04 + 0.04) * 25) / 25
