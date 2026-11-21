"""The analysis and recording are consistent with themselves (issue #36; the C# app's ConsistencyTests)."""

from __future__ import annotations

import os
from dataclasses import replace
from datetime import datetime, timedelta, timezone

import pytest

import conftest  # noqa: F401  (puts python/src on the path)
from oventracker.analysis.adjustment_advisor import (
    AdjustmentOptions,
    AdjustPoint,
    GainSource,
    advise,
    mode_gains,
    setpoint_key,
)
from oventracker.analysis.session_summary import TestPoint, Verdict, judge_offset
from oventracker.analysis.setpoint_analyzer import AnalysisOptions, within_precision
from oventracker.commands.analyze import AnalyzeSettings, combine, load
from oventracker.jsonio import DateTimeOffset
from oventracker.meter.base import MeterReading, ReadingStatus
from oventracker.session.recording import HEADER, RunRecorder, SessionInfo, number
from oventracker.units import OvenMode, TempUnit

OPTIONS = AdjustmentOptions(step_c=1 / 1.8, range_c=35 / 1.8)

UNIX_EPOCH = datetime(1970, 1, 1, tzinfo=timezone.utc)
_UNIX_EPOCH_TICKS = 621355968000000000
_TICKS_PER_DAY = 864000000000


def session(*plan: TestPoint) -> SessionInfo:
    return SessionInfo(
        "test", DateTimeOffset(_UNIX_EPOCH_TICKS, 0), "test meter", "nowhere", TempUnit.Celsius, 8, AnalysisOptions(), list(plan)
    )


def test_events_with_commas_keep_the_row_to_nine_fields(tmp_path):
    with RunRecorder(str(tmp_path), session()) as rec:
        rec.note(UNIX_EPOCH, 'prompt:PRESS W TO LET IT COOL, OR SET IT TO "350"\r\nNOW')
        rec.reading(MeterReading(UNIX_EPOCH, ReadingStatus.Ok, 20.0, "+2,0E+01\r\n"), None, None)
    with open(os.path.join(tmp_path, "readings.csv"), encoding="utf-8") as fh:
        rows = fh.read().splitlines()[1:]
    assert len(rows) == 2
    assert all(len(r.split(",")) == 9 for r in rows)
    assert rows[0].endswith("prompt:PRESS W TO LET IT COOL; OR SET IT TO 350 NOW")


@pytest.mark.parametrize("v", [176.66666666666666, 0.1 + 0.2, 2.0, 123.456789012345])
def test_numbers_read_back_exactly(v):
    assert float(number(v)) == v


@pytest.mark.parametrize(
    "v, text",
    [(2.0, "2"), (1e-05, "1E-05"), (0.0001, "0.0001"), (1e16, "10000000000000000"), (1e17, "1E+17"), (-0.0, "-0"), (14.25, "14.25")],
)
def test_numbers_are_written_as_dotnet_writes_them(v, text):
    # Checked against .NET 11's ToString("R", InvariantCulture).
    assert number(v) == text


def test_a_recorded_simulation_keeps_its_events_on_the_simulated_clock(tmp_path):
    with RunRecorder(str(tmp_path), session()) as rec:
        simulated = UNIX_EPOCH + timedelta(hours=3)
        rec.clock = lambda: rec.last_reading_time if rec.last_reading_time is not None else datetime.now(timezone.utc)
        rec.reading(MeterReading(simulated, ReadingStatus.Ok, 20.0, "20"), None, None)
        assert rec.now == simulated


def test_the_verdict_and_the_residual_column_judge_a_missing_interval_alike():
    # Within tolerance, but with no ±95 %: neither can call it accurate.
    assert judge_offset(3.0, None, 8) == Verdict.Borderline
    assert judge_offset(9.0, None, 8) == Verdict.OutOfTolerance
    assert judge_offset(3.0, 1.0, 8) == Verdict.Accurate


def test_the_precision_target_is_one_test():
    assert within_precision(1.0, 1.0)
    assert within_precision(1.0 + 1e-12, 1.0)
    assert not within_precision(1.01, 1.0)


def test_setpoints_are_the_same_everywhere():
    # A °F run's 176.667 and a later run's exact 176.66666… are one setpoint, in every grouping.
    assert setpoint_key(176.667) == setpoint_key(176.66666666666666)
    assert setpoint_key(176.667) != setpoint_key(176.8)


def test_a_gain_with_a_missing_interval_has_an_unknown_uncertainty():
    points = [
        AdjustPoint(OvenMode.Bake, 180, 5, 0.5, 0),
        AdjustPoint(OvenMode.Bake, 180, 0, None, -5),
    ]
    g = mode_gains(points, OPTIONS)[OvenMode.Bake]
    assert g.source == GainSource.Measured
    assert g.gain == pytest.approx(1.0, abs=1e-9)
    assert g.half_width is None


def test_without_the_current_setting_the_search_doesnt_assume_it_is_zero():
    # 30 °C hot: the best change is −30 °C, beyond the ±19.4 °C range around 0, but possible from a setting of +15.
    points = [AdjustPoint(OvenMode.Bake, 180, 30, 0.5, 0)]
    (unknown,) = advise(points, OPTIONS, None)
    assert round(unknown.change_c) == -30
    assert not unknown.clamped
    (known,) = advise(points, OPTIONS, 0)
    assert known.clamped
    assert known.change_c >= -OPTIONS.range_c - 1e-9


def test_favouring_a_temperature_with_one_adjustment_weighs_each_mode_by_its_own_gain():
    # Bake runs 4 °C hot and convection 2 °C cold, flat across temperature; both move one for one.
    points = [
        AdjustPoint(OvenMode.Bake, 180, 4, 0.2, 0),
        AdjustPoint(OvenMode.Bake, 220, 4, 0.2, 0),
        AdjustPoint(OvenMode.Convection, 180, -2, 0.2, 0),
        AdjustPoint(OvenMode.Convection, 220, -2, 0.2, 0),
        # More convection points must not pull the answer towards convection: one mode, one say.
        AdjustPoint(OvenMode.Convection, 200, -2, 0.2, 0),
        AdjustPoint(OvenMode.Convection, 210, -2, 0.2, 0),
    ]
    (advice,) = advise(points, replace(OPTIONS, favour_c=200), 0)
    # Least squares over the two modes: −(4 − 2) / 2 = −1 °C, to the nearest step.
    assert round(advice.change_c) == -1


def test_each_mode_keeps_its_reason_for_more_setpoints():
    points = [
        AdjustPoint(OvenMode.Bake, 180, 4, 0.2, 0),
        AdjustPoint(OvenMode.Convection, 180, -2, 0.2, 0),
        AdjustPoint(OvenMode.Convection, 182, -2, 0.2, 0),
    ]
    (advice,) = advise(points, OPTIONS, 0)
    assert "Bake: With one setpoint per mode" in advice.too_few_setpoints
    assert "Convection: The setpoints are too close together" in advice.too_few_setpoints


def test_favour_and_an_objective_dont_go_together(tmp_path):
    s = AnalyzeSettings(paths=[str(tmp_path)], favour=350, objective="minimax")
    assert s.validate() is not None


def test_a_run_without_its_session_uses_runs_defaults_for_a_fahrenheit_oven(tmp_path):
    with open(os.path.join(tmp_path, "readings.csv"), "w", encoding="utf-8", newline="") as fh:
        fh.write(HEADER + "\n")
    run = load(str(tmp_path))
    assert run is not None
    assert run.info.unit == TempUnit.Fahrenheit
    assert run.info.options.precision_c == pytest.approx(2 / 1.8, abs=1e-9)


def test_combining_runs_keeps_an_unknown_adjustment_unknown(tmp_path):
    a = tmp_path / "a"
    b = tmp_path / "b"
    for d, days in ((a, 0), (b, 1)):
        info = replace(
            session(TestPoint(1, OvenMode.Bake, 180)), started=DateTimeOffset(_UNIX_EPOCH_TICKS + days * _TICKS_PER_DAY, 0)
        )
        RunRecorder(str(d), info).close()
    runs = [load(str(a)), load(str(b))]
    assert runs[0] is not None and runs[1] is not None
    info, _ = combine(runs, lambda i: i.options, None)  # type: ignore[arg-type]
    assert info.adjustment_c is None
