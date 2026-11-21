"""The parts of a live run that need no terminal: the heading check, the cooling model, the reset wait's targets,
the change prompt's rules, the silence watch, auto power-off, and resuming a run (the C# app's HeadingCheckTests,
CoolingModelTests, ResumeTests, ChangePromptTests and AutoPowerOffTests). The simulator's random numbers differ
from the C# app's, so the checks are of behaviour, not of exact values."""

from __future__ import annotations

import math
import os
import time
from datetime import datetime, timedelta, timezone

import pytest

import conftest  # noqa: F401  (puts python/src on the path)
from oventracker.analysis.cooling_model import CoolingModel, CoolingState
from oventracker.analysis.heading_check import HeadingCheck, HeadingState
from oventracker.analysis.session_summary import PointResult, TestPoint
from oventracker.analysis.setpoint_analyzer import AnalysisOptions, CompletionReason, PointPhase, SetpointAnalyzer
from oventracker.jsonio import DateTimeOffset
from oventracker.meter.auto_power_off import (
    AutoPowerOff,
    AutoPowerOffSetting,
    AutoPowerOffState,
    AutoPowerOffWarningKind,
    advice_for,
)
from oventracker.meter.base import MeterReading, ReadingStatus
from oventracker.meter.simulated import SimulatedDriver, SimulatedMeter, SimulatedOven
from oventracker.session.change_prompt import reminders_due, should_ask
from oventracker.session.meter_poller import MeterPoller
from oventracker.session.recording import RunRecorder, SessionInfo, replay
from oventracker.session.report import Report
from oventracker.session.reset_waiter import MARGIN, target_for
from oventracker.session.silence_watch import SilenceChange, SilenceWatch, message
from oventracker.units import OvenMode, TempUnit, UnitFormatter

OPTIONS = AnalysisOptions()


def build_plan(modes, setpoints, grouped=False):
    from oventracker.commands.run import build_plan as plan

    return plan(modes, setpoints, grouped)


# Heading check ------------------------------------------------------------------------------------


def settled(mode: OvenMode, setpoint_c: float, seed: int) -> tuple[SimulatedOven, float]:
    """The simulated oven settled at one setting, and the mean of its last two minutes (where the app starts from)."""
    oven = SimulatedOven(seed)
    oven.set(mode, setpoint_c)
    oven.step(90 * 60)
    total = 0.0
    for _ in range(120):
        oven.step(1)
        total += oven.probe_c()
    return oven, total / 120


def watch(oven: SimulatedOven, check: HeadingCheck, seconds: int = 10 * 60) -> tuple[HeadingState, int]:
    for t in range(seconds):
        oven.step(1)
        if check.update(t, oven.probe_c()) != HeadingState.Unknown:
            return check.state, t
    return check.state, seconds


@pytest.mark.parametrize(
    "mode,frm,to,seed",
    [
        (OvenMode.Bake, 176.67, 204.44, 1),  # 350 → 400 °F
        (OvenMode.Convection, 176.67, 204.44, 2),
        (OvenMode.Bake, 204.44, 232.22, 3),  # 400 → 450 °F
        (OvenMode.Convection, 162.78, 182.22, 4),  # 325 → 360 °F
    ],
)
def test_forgetting_to_raise_the_setting_is_noticed(mode, frm, to, seed):
    oven, start = settled(mode, frm, seed)
    state, _ = watch(oven, HeadingCheck(start, to))  # the oven is left at its old setting
    assert state == HeadingState.NotHeading


@pytest.mark.parametrize(
    "mode,frm,to,seed",
    [
        (OvenMode.Bake, 176.67, 204.44, 5),
        (OvenMode.Convection, 176.67, 204.44, 6),
        (OvenMode.Bake, 204.44, 232.22, 7),
        (OvenMode.Bake, 176.67, 193.33, 8),  # 350 → 380 °F: small steps never cry wolf
        (OvenMode.Bake, 176.67, 187.78, 13),  # 350 → 370 °F
    ],
)
def test_raising_the_setting_is_on_its_way(mode, frm, to, seed):
    oven, start = settled(mode, frm, seed)
    oven.set(mode, to)
    check = HeadingCheck(start, to)
    state, _ = watch(oven, check)
    assert state != HeadingState.NotHeading
    if check.applies and abs(to - frm) >= 25:
        assert state == HeadingState.OnItsWay


def test_lowering_the_setting_is_checked_too():
    forgot, start = settled(OvenMode.Bake, 232.22, 9)
    assert watch(forgot, HeadingCheck(start, 204.44))[0] == HeadingState.NotHeading
    changed, start2 = settled(OvenMode.Bake, 232.22, 10)
    changed.set(OvenMode.Bake, 204.44)
    assert watch(changed, HeadingCheck(start2, 204.44))[0] == HeadingState.OnItsWay


@pytest.mark.parametrize("mode,seed", [(OvenMode.Bake, 14), (OvenMode.Bake, 15), (OvenMode.Convection, 16)])
def test_a_forgotten_change_stays_noticed_through_the_thermostats_peaks(mode, seed):
    # As the app does it: from the previous setpoint's measured mean, beyond half its swing.
    oven = SimulatedOven(seed)
    oven.set(mode, 176.67)
    a = SetpointAnalyzer(176.67)
    t = 0
    while a.phase != PointPhase.Complete:
        oven.step(1)
        a.add(t, oven.probe_c())
        t += 1
    m = a.metrics()
    check = HeadingCheck(m.mean_c, 204.44, m.swing_c)
    ever_on_its_way = False
    for t in range(20 * 60):
        oven.step(1)  # still at 350 °F
        ever_on_its_way |= check.update(t, oven.probe_c()) == HeadingState.OnItsWay
    assert not ever_on_its_way
    assert check.state == HeadingState.NotHeading


def test_from_cold_the_first_setpoint_is_on_its_way():
    oven = SimulatedOven(11)
    oven.set(OvenMode.Bake, 176.67)
    assert watch(oven, HeadingCheck(oven.ambient_c, 176.67))[0] == HeadingState.OnItsWay


def test_a_small_step_or_a_mode_change_isnt_checked():
    oven, start = settled(OvenMode.Bake, 176.67, 12)
    check = HeadingCheck(start, 176.67)
    assert not check.applies
    assert watch(oven, check)[0] == HeadingState.Unknown
    assert not HeadingCheck(176.67, 176.67 + 15 / 1.8).applies  # 15 °F


# Cooling model ------------------------------------------------------------------------------------


def hot_oven(seed: int = 4) -> SimulatedOven:
    """The simulated oven, heated at bake 400 °F for an hour, then turned off."""
    oven = SimulatedOven(seed)
    oven.set(OvenMode.Bake, 204.44)
    oven.step(3600)
    oven.set(OvenMode.Bake, None)
    return oven


def test_a_pure_exponential_is_predicted_exactly():
    m = CoolingModel(22)
    tau = 1800.0
    for t in range(0, 601):
        m.add(t, 22 + 180 * math.exp(-t / tau))
    assert m.state == CoolingState.Cooling
    assert m.tau_seconds == pytest.approx(tau, abs=0.05)
    now = 22 + 180 * math.exp(-600 / tau)
    expected = tau * math.log((now - 22) / 78)
    assert expected - 20 <= m.time_until(100) <= expected + 20  # current_c is a 30 s average
    assert m.time_until(22.2) is None  # ambient is only reached in the limit
    assert m.time_until(250) == 0


def test_it_learns_before_predicting():
    m = CoolingModel(22)
    for t in range(60):
        m.add(t, 200 - t * 0.05)
    assert m.state == CoolingState.Learning
    assert m.time_until(150) is None


@pytest.mark.parametrize("target_c,after_minutes", [(150, 8), (121.1, 10), (65.6, 20)])
def test_the_simulated_ovens_cool_down_is_predicted_within_ten_percent(target_c, after_minutes):
    oven = hot_oven()
    m = CoolingModel(oven.ambient_c)
    for t in range(after_minutes * 60):
        oven.step(1)
        m.add(t, oven.probe_c())
    predicted = m.time_until(target_c)
    actual = 0
    while oven.air_c > target_c:
        oven.step(1)
        actual += 1
    assert actual * 0.9 <= predicted <= actual * 1.1


def test_opening_the_door_is_noticed_and_the_prediction_carries_on():
    oven = hot_oven(5)
    m = CoolingModel(oven.ambient_c)
    clock = [0]

    def run(seconds: int) -> None:
        for _ in range(seconds):
            oven.step(1)
            m.add(clock[0], oven.probe_c())
            clock[0] += 1

    run(8 * 60)
    assert m.state == CoolingState.Cooling
    before = m.tau_seconds
    oven.door_open = True
    run(40)
    assert m.state == CoolingState.DoorOpen
    oven.door_open = False
    run(30)
    assert before * 0.9 <= m.tau_seconds <= before * 1.1
    assert m.time_until(100) is not None
    run(10 * 60)
    assert m.state == CoolingState.Cooling
    predicted = m.time_until(100)
    actual = 0
    while oven.air_c > 100:
        oven.step(1)
        actual += 1
    assert actual * 0.85 <= predicted <= actual * 1.15


def test_an_oven_that_is_on_is_in_use():
    oven = hot_oven(6)
    m = CoolingModel(oven.ambient_c)
    t = 0
    for _ in range(9 * 60):
        oven.step(1)
        m.add(t, oven.probe_c())
        t += 1
    assert m.state == CoolingState.Cooling
    oven.set(OvenMode.Bake, 218)  # someone turns it on to cook
    for _ in range(4 * 60):
        oven.step(1)
        m.add(t, oven.probe_c())
        t += 1
    assert m.state == CoolingState.Heating
    assert m.time_until(150) is None


# The reset wait's targets, and the change prompt's rules -----------------------------------------


def test_the_next_setpoint_starts_from_just_above_the_one_before_it_or_from_cold():
    plan = build_plan([OvenMode.Bake, OvenMode.Convection], [176.67, 204.44])
    assert target_for(plan, plan[0], 22) == pytest.approx(22 + MARGIN)  # the first: from cold
    assert target_for(plan, plan[1], 22) == pytest.approx(176.67 + MARGIN)  # convection 350 after bake 350
    assert target_for(plan, plan[2], 22) == pytest.approx(176.67 + MARGIN)  # bake 400 after convection 350
    assert MARGIN * 1.8 == pytest.approx(10)


@pytest.mark.parametrize(
    "yes,simulation,keys,asks",
    [
        (False, False, True, True),  # a real meter: ask
        (True, False, True, True),  # a real meter with --yes: still ask, since nothing else changes the oven
        (False, True, True, True),  # the simulator: ask
        (True, True, True, False),  # the simulator with --yes: carry on by itself
        (False, False, False, False),  # no keyboard: can't ask
    ],
)
def test_it_stops_to_ask_unless_nothing_needs_changing(yes, simulation, keys, asks):
    assert should_ask(yes, simulation, keys) == asks


def test_reminders_come_every_interval_until_answered():
    assert reminders_due(59, 60) == 0
    assert reminders_due(61, 60) == 1
    assert reminders_due(3.5 * 60, 60) == 3
    assert reminders_due(30 * 60, 0) == 0  # --remind 0


def test_the_oven_starts_from_the_mean_of_its_last_two_minutes():
    from oventracker.commands.run import starting_from

    t0 = datetime(1970, 1, 1, tzinfo=timezone.utc)
    idle = [MeterReading(t0, ReadingStatus.Ok, 100.0, "")]
    for s in range(3600, 3901):
        idle.append(MeterReading(t0 + timedelta(seconds=s), ReadingStatus.Ok, 180 + 10 * math.sin(2 * math.pi * s / 60), ""))
    idle.append(MeterReading(t0 + timedelta(seconds=3901), ReadingStatus.Overload, None, "OL"))
    assert 178 <= starting_from(idle, 999) <= 182
    assert starting_from([], 150) == 150
    assert starting_from([], None) is None


def test_the_warning_says_what_to_do():
    from oventracker.session.point_tracker import not_heading_message

    f = UnitFormatter(TempUnit.Fahrenheit)
    text = not_heading_message(HeadingCheck(176.67, 204.44), f)
    assert "isn't heating up towards 400 °F" in text
    assert "still near 350 °F" in text
    assert "press R twice" in text
    assert "cooling down" in not_heading_message(HeadingCheck(232.22, 204.44), f)


# Silence and auto power-off -----------------------------------------------------------------------

TEN_MINUTES = AutoPowerOff(timedelta(minutes=10), ["Do this.", "Then that."], "The guide, p. 1")


def test_the_watch_says_when_the_meter_goes_quiet_and_when_its_back():
    w = SilenceWatch(30)
    assert w.update(1) == SilenceChange.None_
    assert w.update(29) == SilenceChange.None_
    assert w.update(31) == SilenceChange.WentQuiet
    assert w.silent
    assert w.update(90) == SilenceChange.None_
    assert w.update(1) == SilenceChange.CameBack
    assert not w.silent


def test_the_alert_talks_about_auto_power_off_only_for_meters_that_have_it():
    with_apo = message(45, TEN_MINUTES)
    assert "No reading for 45 s" in with_apo
    assert "auto power-off" in with_apo
    assert "press any button" in with_apo
    without = message(180, None)
    assert "No reading for 3 min" in without
    assert "auto power-off" not in without


def test_auto_power_off_advice():
    assert advice_for(None, AutoPowerOffSetting.UNKNOWN) is None
    assert advice_for(None, AutoPowerOffSetting.OFF) is None
    w = advice_for(TEN_MINUTES, AutoPowerOffSetting.UNKNOWN)
    assert w.kind == AutoPowerOffWarningKind.Reminder
    assert "10 minutes" in w.headline
    assert w.steps == ["Do this.", "Then that."]
    assert w.source == "The guide, p. 1"
    assert advice_for(TEN_MINUTES, AutoPowerOffSetting.OFF) is None
    on = advice_for(TEN_MINUTES, AutoPowerOffSetting.on(timedelta(minutes=15)))
    assert on.kind == AutoPowerOffWarningKind.On
    assert "15 minutes" in on.headline and "middle of a setpoint" in on.headline
    # Saying it's off quietens the reminder, but not the meter's own word.
    assert advice_for(TEN_MINUTES, AutoPowerOffSetting.UNKNOWN, confirmed_off=True) is None
    assert advice_for(TEN_MINUTES, AutoPowerOffSetting.on(timedelta(minutes=10)), confirmed_off=True) is not None
    from dataclasses import replace

    assert advice_for(replace(TEN_MINUTES, remote_reads_keep_it_on=True), AutoPowerOffSetting.UNKNOWN) is None


def test_the_u1253b_declares_its_auto_power_off_with_the_steps_from_its_guide():
    from oventracker.meter.u1253b import U1253BDriver

    apo = U1253BDriver().auto_power_off
    assert apo.default_timeout == timedelta(minutes=10)
    assert len(apo.how_to_turn_off) == 4
    assert any("APO" in s for s in apo.how_to_turn_off)
    assert "126" in apo.source
    assert advice_for(apo, AutoPowerOffSetting.UNKNOWN).kind == AutoPowerOffWarningKind.Reminder


def test_the_simulated_meter_says_whether_it_will_switch_off():
    meter = SimulatedMeter(SimulatedOven(1), 1)
    assert meter.read_auto_power_off().state == AutoPowerOffState.Off
    assert advice_for(SimulatedDriver().auto_power_off, meter.read_auto_power_off()) is None
    meter.set_auto_power_off(timedelta(minutes=10))
    assert meter.read_auto_power_off() == AutoPowerOffSetting.on(timedelta(minutes=10))
    assert advice_for(SimulatedDriver().auto_power_off, meter.read_auto_power_off()).kind == AutoPowerOffWarningKind.On


def test_the_simulated_meter_switches_off_and_wakes_again():
    meter = SimulatedMeter(SimulatedOven(2), 1)
    meter.auto_power_off_after = timedelta(minutes=1)
    meter.wakes_after = timedelta(minutes=2)
    pattern: list[list] = []
    for _ in range(400):
        try:
            meter.read()
            on = True
        except TimeoutError:
            on = False
        if pattern and pattern[-1][0] == on:
            pattern[-1][1] += 1
        else:
            pattern.append([on, 1])
    assert pattern[0][0] and 59 <= pattern[0][1] <= 60
    assert not pattern[1][0] and 119 <= pattern[1][1] <= 120
    assert pattern[2][0] and 59 <= pattern[2][1] <= 61


def test_the_poller_keeps_asking_while_the_meter_is_off_and_carries_on_when_it_wakes():
    # At 600× real time, the simulated meter goes off after a minute and is woken 90 s later.
    meter = SimulatedMeter(SimulatedOven(3), 1)
    meter.auto_power_off_after = timedelta(minutes=1)
    meter.wakes_after = timedelta(seconds=90)
    poller = MeterPoller(meter, 1, speed=600, connected=True)
    w = SilenceWatch()
    changes = []
    poller.start()
    deadline = time.monotonic() + 20
    readings = 0
    try:
        while time.monotonic() < deadline and readings < 200:
            readings += sum(1 for _ in poller.readings.drain())
            c = w.update(poller.silent_for)
            if c != SilenceChange.None_:
                changes.append(c)
            time.sleep(0.005)
    finally:
        poller.stop()
    assert len(changes) >= 2, changes
    for i, c in enumerate(changes):
        assert c == (SilenceChange.WentQuiet if i % 2 == 0 else SilenceChange.CameBack)
    assert readings >= 200
    assert poller.reconnects == 0  # 90 s quiet isn't long enough to suspect the link (2 min is)


# Resuming -----------------------------------------------------------------------------------------


def measure(rec: RunRecorder, meter: SimulatedMeter, point: TestPoint, after_reset: bool, abort_after: float | None = None) -> PointResult:
    """Measures one setpoint on the simulated oven, recording it as the app does."""
    meter.oven.set(point.mode, point.setpoint_c)
    if after_reset:
        rec.note(datetime.now(timezone.utc), "reset")
    a = SetpointAnalyzer(point.setpoint_c, OPTIONS)
    start = None
    r = None
    while a.phase != PointPhase.Complete:
        r = meter.read()
        start = start or r.time
        el = (r.time - start).total_seconds()
        if el == 0:
            rec.event(r.time, point, 0, "start")
        rec.reading(r, point, el)
        a.add(el, r.celsius)
        if abort_after is not None and el >= abort_after:
            a.finish(CompletionReason.Aborted)
    rec.event(r.time, point, a.now, f"end:{a.reason.name}")
    return PointResult(point, a.metrics(), after_reset)


def cool(rec: RunRecorder, meter: SimulatedMeter, target_c: float) -> int:
    meter.oven.set(OvenMode.Bake, None)
    rec.note(datetime.now(timezone.utc), "reset-start")
    seconds = 0
    while meter.oven.air_c > target_c:
        rec.reading(meter.read(), None, None)
        seconds += 1
    rec.note(datetime.now(timezone.utc), "reset-end:Ready")
    return seconds


def now_offset() -> DateTimeOffset:
    return DateTimeOffset.from_timestamp_local(time.time())


def test_a_run_resumed_after_using_the_oven_replays_as_one_run(tmp_path):
    from dataclasses import replace

    plan = build_plan([OvenMode.Bake, OvenMode.Convection], [176.67, 204.44])
    info = SessionInfo("test", now_offset(), "sim", "simulator", TempUnit.Fahrenheit, 15 / 1.8, OPTIONS, plan, ambient_c=22)
    meter = SimulatedMeter(SimulatedOven(31), 1)
    with RunRecorder(str(tmp_path), info) as rec:
        measure(rec, meter, plan[0], False)
        measure(rec, meter, plan[1], False)
        measure(rec, meter, plan[2], False, abort_after=600)
    csv = os.path.join(tmp_path, "readings.csv")
    assert [r.metrics.reason for r in replay(csv, OPTIONS)] == [CompletionReason.Converged, CompletionReason.Converged, CompletionReason.Aborted]

    meter.pretend_used(232.2)  # someone uses the oven at 450 °F, then the run is resumed
    with RunRecorder(str(tmp_path), replace(info, sittings=[info.started, now_offset()]), append=True) as rec:
        rec.note(datetime.now(timezone.utc), "resume")
        cool(rec, meter, target_for(plan, plan[2], 22))
        measure(rec, meter, plan[2], True)
        measure(rec, meter, plan[3], False)

    replayed = replay(csv, OPTIONS)
    assert [r.point.index for r in replayed] == [1, 2, 3, 4]
    assert all(r.metrics.reason == CompletionReason.Converged for r in replayed)
    assert [r.after_reset for r in replayed] == [False, False, True, False]
    with open(csv, encoding="utf-8") as fh:
        assert sum(1 for line in fh if line.startswith("time_utc")) == 1  # one header
    later = DateTimeOffset(info.started.ticks + 864_000_000_000, info.started.offset_minutes)
    md = Report(replace(info, sittings=[info.started, later]), replayed).markdown()
    assert "(after a reset)" in md
    assert "in 2 sittings" in md


def test_a_session_split_by_a_reset_measures_the_same_as_one_straight_through(tmp_path):
    plan = build_plan([OvenMode.Bake], [176.67, 204.44, 232.22])
    info = SessionInfo("test", now_offset(), "sim", "simulator", TempUnit.Fahrenheit, 15 / 1.8, OPTIONS, plan, ambient_c=22)
    with RunRecorder(str(tmp_path / "straight"), info) as rec:
        meter = SimulatedMeter(SimulatedOven(41), 1)
        straight = [measure(rec, meter, p, False) for p in plan]
    with RunRecorder(str(tmp_path / "split"), info) as rec:
        meter = SimulatedMeter(SimulatedOven(42), 1)
        split = [measure(rec, meter, plan[0], False)]
        meter.pretend_used(246)  # a roast at 475 °F in between
        cool(rec, meter, target_for(plan, plan[1], 22))
        split.append(measure(rec, meter, plan[1], True))
        split.append(measure(rec, meter, plan[2], False))
    for a, b in zip(straight, split):
        ma, mb = a.metrics, b.metrics
        allowed = math.sqrt((ma.half_width_c or 1) ** 2 + (mb.half_width_c or 1) ** 2) + 0.75
        assert ma.mean_c - allowed <= mb.mean_c <= ma.mean_c + allowed


def test_a_first_setpoint_started_warm_is_marked(tmp_path):
    plan = build_plan([OvenMode.Bake], [176.67])
    info = SessionInfo("test", now_offset(), "sim", "simulator", TempUnit.Fahrenheit, 15 / 1.8, OPTIONS, plan)
    meter = SimulatedMeter(SimulatedOven(51), 1)
    meter.pretend_used(200)  # still warm from cooking
    with RunRecorder(str(tmp_path), info) as rec:
        rec.note(datetime.now(timezone.utc), "started-warm")
        measure(rec, meter, plan[0], False)
    (r,) = replay(os.path.join(tmp_path, "readings.csv"), OPTIONS)
    assert r.started_warm and not r.after_reset
    md = Report(info, [r]).markdown()
    assert "350 °F (started warm)" in md
    assert "aren't from cold" in md


def test_the_recording_is_in_the_specs_format(tmp_path):
    """readings.csv as spec/recording.md describes it: the header, seven fractional digits, F3, F1 and F2."""
    plan = build_plan([OvenMode.Bake], [176.66666666666666])
    info = SessionInfo("test", now_offset(), "sim", "simulator", TempUnit.Fahrenheit, 15 / 1.8, OPTIONS, plan)
    t = datetime(2026, 11, 20, 19, 18, 8, 697716, tzinfo=timezone.utc)
    with RunRecorder(str(tmp_path), info) as rec:
        rec.reading(MeterReading(t, ReadingStatus.Ok, 180.873, "+1.80873000E+02"), None, None)
        rec.event(t, plan[0], 0, "start")
        rec.reading(MeterReading(t, ReadingStatus.Error, None, 'TEMP:K CEL,"x"'), plan[0], 14.25)
        rec.note(t, "prompt:SET THE OVEN TO BAKE 350 °F")
    with open(os.path.join(tmp_path, "readings.csv"), encoding="utf-8", newline="") as fh:
        lines = fh.read().split(os.linesep)
    assert lines[0] == "time_utc,point,mode,setpoint_c,elapsed_s,status,temp_c,raw,event"
    assert lines[1] == "2026-11-20T19:18:08.6977160Z,0,,,,Ok,180.87,+1.80873000E+02,"
    assert lines[2] == "2026-11-20T19:18:08.6977160Z,1,Bake,176.667,0.0,Event,,,start"
    assert lines[3] == "2026-11-20T19:18:08.6977160Z,1,Bake,176.667,14.2,Error,,TEMP:K CEL;x,"
    assert lines[4] == "2026-11-20T19:18:08.6977160Z,0,,,,Event,,,prompt:SET THE OVEN TO BAKE 350 °F"
