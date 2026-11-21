"""Pausing a setpoint for a battery swap, and gaps in the readings (#51): the C# app's BatterySwapTests."""

from __future__ import annotations

import json
import math
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest

from oventracker.analysis.setpoint_analyzer import AnalysisOptions, PointPhase, SetpointAnalyzer
from oventracker.commands import run as run_command
from oventracker.meter import driver as drivers
from oventracker.meter.base import MeterIdentity, MeterReading, ReadingStatus, TemperatureMeter
from oventracker.meter.simulated import SimulatedMeter, SimulatedOven
from oventracker.session import point_tracker
from oventracker.session.meter_poller import MeterPoller
from oventracker.session.point_tracker import PauseReason, pause_message
from oventracker.units import TempUnit, UnitFormatter

SETPOINT = 176.67


def cycle(t: float, lo: float, hi: float, period: float) -> float:
    x = math.fmod(t, period) / period
    return lo + (hi - lo) * 2 * x if x < 0.5 else hi - (hi - lo) * 2 * (x - 0.5)


def run(gap_from: float, gap_to: float, seconds: float = 5 * 3600) -> SetpointAnalyzer:
    """An oven at temperature cycling 172–185 °C every 12 minutes, with readings missing between gap_from and
    gap_to (s)."""
    a = SetpointAnalyzer(SETPOINT, AnalysisOptions(precision_c=0.2))
    t = 0.0
    while t <= seconds and a.phase != PointPhase.Complete:
        if not (gap_from < t < gap_to):
            a.add(t, 172 + 13 * cycle(t + 360, 0, 1, 720))  # starts at a peak
        t += 1
    return a


def test_a_gap_drops_the_cycle_it_broke_and_keeps_the_rest():
    whole = run(math.inf, math.inf)
    gapped = run(3000, 3300)  # five minutes off, mid-cycle, after a few cycles
    gm = gapped.metrics()
    # The same oven: the same mean, from cycles either side of the gap, none of them across it.
    assert gm.mean_c == pytest.approx(whole.metrics().mean_c, abs=0.05)
    for u in gapped.units:
        assert not (u.start < 3300 and u.end > 3000), f"a cycle {u.start}–{u.end} crosses the gap"
    assert any(u.end <= 3000 for u in gapped.units)
    assert any(u.start >= 3300 for u in gapped.units)
    assert round(gm.longest_gap_seconds) == 300


def test_a_short_dropout_doesnt_break_a_cycle():
    whole = run(math.inf, math.inf)
    dropped = run(3000, 3090)  # 90 s: a Bluetooth link coming back
    assert sum(u.end < 3600 for u in whole.units) == sum(u.end < 3600 for u in dropped.units)


def test_the_time_limit_doesnt_count_the_gap():
    # A one-hour limit, with a 40-minute gap: still measuring at 90 minutes, as only 50 of them had readings.
    a = SetpointAnalyzer(SETPOINT, AnalysisOptions(max_point_minutes=60, precision_c=0.001))
    for t in range(0, 90 * 60 + 1):
        if 20 * 60 < t < 60 * 60:
            continue
        a.add(float(t), 172 + 13 * cycle(t + 360, 0, 1, 720))
    assert a.phase != PointPhase.Complete
    # ...and the time left is capped by the time with readings, not the clock.
    eta = a.estimate_remaining()
    assert eta is not None and eta.seconds <= 60 * 60 - (90 * 60 - 40 * 60) + 1


def test_a_session_pauses_for_a_battery_swap_and_carries_on_with_the_same_setpoint(tmp_path: Path):
    from test_engine import run_app

    out = tmp_path / "swap"
    p = run_app("--simulate", "--speed", "3000", "-y", "--unit", "F", "--modes", "bake", "--setpoints", "350",
                "--sim-battery", "40", "--out", str(out))
    output = p.stdout + p.stderr
    assert p.returncode == 0, output
    assert "PAUSED for a battery swap" in output
    assert "Carrying on with this setpoint" in output
    lines = (out / "readings.csv").read_text(encoding="utf-8").splitlines()
    events = [line.split(",")[8] for line in lines if ",Event," in line]
    assert events[:2] == ["start", "pause:LowBattery"]
    assert "pause-end" in events
    assert sum(e.startswith("end:") for e in events) == 1  # one setpoint, not restarted
    # The readings while paused are outside the setpoint, so replay leaves them out.
    paused_at = next(i for i, line in enumerate(lines) if line.endswith("pause:LowBattery"))
    ended_at = next(i for i, line in enumerate(lines) if line.endswith("pause-end"))
    idle = [line for line in lines[paused_at + 1 : ended_at] if ",Event," not in line]
    assert idle and all(line.split(",")[1] == "0" for line in idle)
    result = json.loads((out / "results.json").read_text(encoding="utf-8"))["results"][0]["metrics"]
    assert result["reason"] == "Converged"


def test_the_pause_says_what_to_do():
    f = UnitFormatter(TempUnit.Fahrenheit)
    battery = pause_message(PauseReason.LowBattery, True, None, f, can_read_keys=True)
    assert "fresh battery" in battery
    assert "cycles so far are kept" in battery
    assert "C carries on without a swap" in battery
    mains_jump = pause_message(PauseReason.ImpossibleReading, False, -50, f, can_read_keys=True)
    assert "−90.0 °F" in mains_jump
    assert "Check the probe" in mains_jump
    assert "battery" not in mains_jump
    assert "battery" in pause_message(PauseReason.ImpossibleReading, True, -50, f, can_read_keys=True)
    assert point_tracker.MAX_RATE_C_PER_SECOND == 10
    # Without a keyboard, neither Enter nor C is offered.
    keyless = pause_message(PauseReason.LowBattery, True, None, f, can_read_keys=False)
    assert "press Enter" not in keyless and "C carries on" not in keyless


def test_the_pause_messages_are_the_c_sharp_apps():
    f = UnitFormatter(TempUnit.Celsius)
    assert pause_message(PauseReason.LowBattery, True, None, f, can_read_keys=True) == (
        "PAUSED for a battery swap: the meter's battery is low, and a low battery gives false readings. Switch the meter "
        "off, fit a fresh battery (or a charged one), and switch it back on at temperature. Tracking carries on by itself "
        "once the meter has been off and is back with sensible readings, or press Enter. The cycles so far are kept. "
        "C carries on without a swap."
    )
    assert pause_message(PauseReason.ImpossibleReading, True, 62.5, f, can_read_keys=False) == (
        "PAUSED: a reading jumped +62.5 °C in a second, faster than an oven can change, so the meter is failing, most "
        "likely its battery. Switch it off, fit a fresh battery, and switch it back on. Tracking carries on by itself "
        "once the meter's readings make sense again. The cycles so far are kept."
    )


@pytest.mark.parametrize("percent, mentioned", [(41.0, True), (59.0, True), (85.0, False)])
def test_a_lowish_battery_is_mentioned_before_a_run(percent: float, mentioned: bool):
    assert (run_command.battery_note(percent) is not None) == mentioned


def test_the_battery_note_is_the_c_sharp_apps():
    assert run_command.battery_note(41.4) == (
        "The meter's battery is at 41 %, and a long session will probably need a fresh one. "
        "When it runs low the app pauses, so you can swap it, then carries on with the same setpoint."
    )
    assert run_command.battery_note(60.0) is None


def test_only_battery_meters_have_a_battery():
    assert drivers.find("u1253b").has_battery
    assert drivers.find("121gw").has_battery
    assert drivers.find("simulated").has_battery
    assert not drivers.find("34401a").has_battery  # mains powered
    assert run_command.battery_note(None) is None


class FlagMeter(TemperatureMeter):
    def __init__(self, low: bool | None, percent: float | None):
        self._low = low
        self._percent = percent

    @property
    def location(self) -> str:
        return "flag"

    def connect(self) -> MeterIdentity:
        return MeterIdentity("Test", "Flag", "1", "1")

    def disconnect(self) -> None:
        pass

    def read(self) -> MeterReading:
        return MeterReading(datetime.now(timezone.utc), ReadingStatus.Ok, 180.0, "180")

    def read_battery_percent(self) -> float | None:
        return self._percent

    def read_low_battery(self) -> bool | None:
        return self._low


@pytest.mark.parametrize(
    "flag, percent, low",
    [
        (True, None, True),  # the 121GW's flag
        (False, 5.0, False),  # its flag wins over a level
        (None, 15.0, True),  # the U1253B's level
        (None, 60.0, False),
        (None, None, None),  # a bench meter says nothing
    ],
)
def test_the_poller_knows_when_the_battery_is_low(flag: bool | None, percent: float | None, low: bool | None):
    poller = MeterPoller(FlagMeter(flag, percent), 1, speed=100, connected=True)
    poller.start()
    for _ in range(200):
        if poller.battery_reads > 0:
            break
        time.sleep(0.005)
    poller.stop()
    assert poller.low_battery == low


def test_the_poller_reads_the_battery_at_once_on_request_and_each_minute():
    meter = FlagMeter(None, 50.0)
    poller = MeterPoller(meter, 1, speed=1000, connected=True)
    assert poller.battery_every == 60
    poller.start()
    deadline = time.monotonic() + 5
    while poller.read_count < 125 and time.monotonic() < deadline:
        time.sleep(0.005)
    poller.stop()
    # Read before the first reading, then at 60 and 120.
    assert poller.battery_reads == 1 + (poller.read_count - 1) // 60
    before = poller.battery_reads
    poller.request_battery()
    poller._stop.clear()
    poller.start()
    deadline = time.monotonic() + 5
    while poller.battery_reads == before and time.monotonic() < deadline:
        time.sleep(0.005)
    poller.stop()
    assert poller.battery_reads > before


def test_the_simulated_battery_runs_down_goes_wrong_and_is_swapped():
    meter = SimulatedMeter(SimulatedOven(1), 1)
    assert meter.read_battery_percent() == 81.4  # for ever, without --sim-battery
    meter.set_battery(timedelta(minutes=10))
    assert meter.read_battery_percent() == pytest.approx(100)
    meter.oven.air_c = 180.0
    for _ in range(300):
        meter.read()
    assert meter.read_battery_percent() == pytest.approx(50)
    for _ in range(250):
        meter.read()
    # Below 10 %, readings jump about by tens of degrees.
    values = [meter.read().celsius for _ in range(40)]
    assert max(values) - min(values) > 20
    meter.swap_battery()
    with pytest.raises(TimeoutError):
        meter.read()  # off for the swap
    for _ in range(118):
        with pytest.raises(TimeoutError):
            meter.read()
    meter.read()  # back on after two minutes
    assert meter.read_battery_percent() == pytest.approx(100)
