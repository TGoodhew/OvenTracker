"""What the app shows (issue #41), as the C# app's DisplayTests: the big text keeps its sign, times left read
properly, the dashboard fits the window, analyze of a real characterise run says how to save the gains, and the
plain reset screen says "ready" once. Needs Rich."""

from __future__ import annotations

import io
import os
import shutil
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest

from conftest import SRC, TESTDATA

pytest.importorskip("rich")

from oventracker.analysis.session_summary import TestPoint  # noqa: E402
from oventracker.analysis.setpoint_analyzer import AnalysisOptions, SetpointAnalyzer  # noqa: E402
from oventracker.meter.base import MeterReading, ReadingStatus  # noqa: E402
from oventracker.meter.simulated import SimulatedMeter, SimulatedOven  # noqa: E402
from oventracker.session import change_prompt, reset_waiter  # noqa: E402
from oventracker.session.cancel import CancellationToken  # noqa: E402
from oventracker.session.meter_poller import MeterPoller  # noqa: E402
from oventracker.ui import console as ui  # noqa: E402
from oventracker.ui import dashboard  # noqa: E402
from oventracker.ui.widgets import FigletText, figlet_lines  # noqa: E402
from oventracker.units import OvenMode, TempUnit, UnitFormatter  # noqa: E402

F = UnitFormatter(TempUnit.Fahrenheit)


def render(renderable, width: int) -> str:
    buf = io.StringIO()
    c = ui.make(file=buf, width=width, color_system=None)
    c.print(renderable)
    return buf.getvalue()


# The big text ---------------------------------------------------------------------------------------


@pytest.mark.parametrize(
    "text,drawn",
    [
        ("−10 °F", "-10 F"),  # the minus sign the big font can't draw: it showed "10 F", the opposite
        ("+10 °F", "+10 F"),
        ("–5 °C", "-5 C"),  # an en dash
        ("—3 °C", "-3 C"),  # an em dash
        ("CONVECTION", "CONVECTION"),
        ("350 °F ✓", "350 F "),
        ("é5\t°F", "5F"),  # anything else outside ' '..'~' is left out
    ],
)
def test_the_big_text_keeps_its_sign(text, drawn):
    assert change_prompt.for_figlet(text) == drawn


def test_the_big_adjustment_prompt_shows_the_minus_sign():
    assert render(FigletText(change_prompt.for_figlet("−10 °F")), 120) == render(FigletText("-10 F"), 120)
    assert figlet_lines(change_prompt.for_figlet("−10 °F"), 120) != figlet_lines("10 F", 120)
    keys = "[bold]Enter[/] it's set   [bold]F[/] stop here (carry on later with --resume)"
    prompt = render(change_prompt.view("SET THE OVEN'S THERMOSTAT ADJUSTMENT TO", ["−10 °F"], None, keys, 12, 60, F, 176.0, None), 80)
    assert "  _____  | | | | | |   | |_" in prompt  # the minus sign, then 10 F


# Times left -----------------------------------------------------------------------------------------


@pytest.mark.parametrize("minutes,text", [(0.4, "in under a minute"), (12, "in about 12 min"), (125, "in about 2 h 5 min")])
def test_times_left_read_properly(minutes, text):
    assert reset_waiter.in_about(minutes) == text


# The dashboard --------------------------------------------------------------------------------------

LONG_ALERT = "No reading for 45 s. The meter has stopped answering: check it's on and in range, and that the link is up."


def model(width: int, height: int, alert: str | None, poller: MeterPoller | None = None) -> dashboard.DashboardModel:
    oven = SimulatedOven(3)
    oven.set(OvenMode.Bake, 176.67)
    a = SetpointAnalyzer(176.67, AnalysisOptions())
    for t in range(0, 40 * 60, 2):
        oven.step(2)
        a.add(t, oven.probe_c())
    if poller is None:
        poller = MeterPoller(SimulatedMeter(SimulatedOven(1), 1), 1, connected=True)
    return dashboard.DashboardModel(
        TestPoint(1, OvenMode.Bake, 176.67), 4, a, F, poller, "simulated meter", "", 15 / 1.8, dashboard.ChartView.Auto, ReadingStatus.Ok,
        "Preheat signal noted at 12:00, oven at 350.0 °F.", width, height, alert,
    )


@pytest.mark.parametrize("width,height", [(80, 24), (100, 30), (120, 40), (160, 50)])
@pytest.mark.parametrize("alert", [None, LONG_ALERT])
def test_the_dashboard_fits_the_window(width, height, alert):
    # A long alert wraps, and in a narrow window the two tables stack: the frame must still fit, or the live
    # display crops its header off the top.
    m = model(width, height, alert)
    frame = dashboard.render(m)
    out = render(frame, width)
    lines = out.splitlines()
    assert dashboard.height_of(frame, width) == len(lines)
    assert len(lines) <= height - 2, f"{len(lines)} lines in a {height}-line window\n{out}"
    assert all(len(line) <= width for line in lines)
    assert lines[1].startswith("│ BAKE 350 °F   setpoint 1 of 4   elapsed 39:58")
    if alert is not None:
        assert "No reading for 45 s." in out
    compact = width == 80
    # A small window has one table, the meter and its link in a row of it, and the short keys.
    assert ("Meter           simulated meter, connected" in out) == compact
    assert ("Keys: P preheated  N finish  R restart  S skip  Z view  Q quit" in out) == compact
    assert ("Keys: P oven says preheated" in out) != compact
    assert ("Link        connected" in out) != compact
    # The chart's view doesn't change the frame's height.
    for view in (dashboard.ChartView.Whole, dashboard.ChartView.Recent):
        m.view = view
        assert len(render(dashboard.render(m), width).splitlines()) == len(lines)


def test_a_small_window_still_shows_the_meters_battery():
    # Compact mode leaves the meter table out; the battery (and drops, and the last error) move into the meter row.
    import time

    poller = MeterPoller(SimulatedMeter(SimulatedOven(1), 1), 1, connected=True)
    poller.start()
    for _ in range(200):
        if poller.battery_percent is not None:
            break
        time.sleep(0.01)
    poller.stop()
    assert "simulated meter, stopped, battery 81 %" in render(dashboard.render(model(80, 24, None, poller)), 80)


def test_a_big_window_still_gives_the_chart_room():
    m = model(160, 50, None)
    used = dashboard.height_of(dashboard.render(m), 160)
    assert 40 <= used <= 48  # grown to fill the window, up to the chart's largest


def test_a_very_small_window_gets_the_smallest_compact_frame():
    m = model(80, 10, None)
    out = render(dashboard.render(m), 80)
    assert "Keys: P preheated" in out
    assert dashboard.height_of(dashboard.render(m), 80) == dashboard.height_of(dashboard._render(m, dashboard.MIN_PLOT_ROWS, True), 80)


def test_the_compact_table_has_no_border_lines():
    # Spectre's TableBorder.None: no blank lines above and below, no space at the edges, one between the columns.
    out = render(dashboard.render(model(80, 24, None)), 80).splitlines()
    i = next(k for k, line in enumerate(out) if line.startswith("Mean so far"))
    assert out[i - 1].startswith("╰")  # straight after the chart
    assert out[i + 5].startswith("Reached setting after ")


# Analyze of a real characterise run ----------------------------------------------------------------


def _analyze(folder: Path, *more: str) -> subprocess.CompletedProcess[str]:
    env = dict(os.environ)
    env["PYTHONPATH"] = str(SRC) + (os.pathsep + env["PYTHONPATH"] if env.get("PYTHONPATH") else "")
    env["PYTHONIOENCODING"] = "utf-8"
    env["COLUMNS"] = "200"
    return subprocess.run(
        [sys.executable, "-m", "oventracker", "analyze", str(folder), "--out", str(folder / "out"), *more],
        stdin=subprocess.DEVNULL, capture_output=True, text=True, encoding="utf-8", env=env, timeout=600,
    )


def test_analyze_of_a_real_characterise_run_says_how_to_save_the_gains(tmp_path: Path):
    # A copy of the simulated characterise run, as if a real meter had made it.
    for f in (TESTDATA / "runs" / "sim-characterise").iterdir():
        shutil.copy(f, tmp_path / f.name)
    session = tmp_path / "session.json"
    text = session.read_text(encoding="utf-8")
    assert '"driver": "simulated"' in text
    session.write_text(text.replace('"driver": "simulated"', '"driver": "u1253b"'), encoding="utf-8")
    p = _analyze(tmp_path)
    assert p.returncode == 0, p.stdout + p.stderr
    assert "A simulation" not in p.stdout
    assert "--save-gains" in p.stdout
    # The run's oven is known (a GE PHS925), so only --save-gains is needed; for the generic oven, --oven too.
    assert "The gains aren't saved to the oven's profile. To save them, run analyze again with --save-gains.\n" in p.stdout
    p = _analyze(tmp_path, "--oven", "generic")
    assert p.returncode == 0, p.stdout + p.stderr
    assert (
        "The gains aren't saved to the oven's profile. To save them, run analyze again with --save-gains and --oven <yours> "
        "(docs/adding-an-oven.md)." in p.stdout
    )


def test_analyze_of_a_simulated_characterise_run_never_saves_its_gains(tmp_path: Path):
    for f in (TESTDATA / "runs" / "sim-characterise").iterdir():
        shutil.copy(f, tmp_path / f.name)
    p = _analyze(tmp_path)
    assert p.returncode == 0, p.stdout + p.stderr
    assert "A simulation: the gains aren't saved to any oven's profile." in p.stdout


# The reset screen without a live display ------------------------------------------------------------


class _Readings:
    def __init__(self, readings: list[MeterReading]):
        self.pending = readings

    def drain(self):
        out, self.pending = self.pending, []
        return out


class _Poller:
    battery_percent = None
    silent_for = 0.0

    def __init__(self, readings: list[MeterReading]):
        self.readings = _Readings(readings)
        self.beeps = 0

    def enqueue(self, action) -> None:
        self.beeps += 1


class _Recorder:
    def __init__(self):
        self.now = datetime(2026, 11, 20, 12, 0, tzinfo=timezone.utc)
        self.notes: list[str] = []

    def note(self, t, text: str) -> None:
        self.notes.append(text)

    def reading(self, r, a, b) -> None:
        pass


class _Keys:
    """A keyboard on which C is pressed after a few passes."""

    def __init__(self, after: int):
        self.after = after
        self.asked = 0

    def __enter__(self):
        return self

    def __exit__(self, *exc) -> None:
        pass

    def available(self) -> bool:
        self.asked += 1
        return self.asked > self.after

    def read(self) -> str:
        return "c"


def _wait(monkeypatch, auto_carry_on: bool, keys: bool) -> tuple[reset_waiter.ResetOutcome, str, _Poller]:
    from oventracker.ui import keys as keys_module

    start = datetime(2026, 11, 20, 12, 0, tzinfo=timezone.utc)
    readings = [MeterReading(start + timedelta(seconds=t), ReadingStatus.Ok, 120.0 - t * 0.05, "") for t in range(30)]
    poller = _Poller(readings)
    buf = io.StringIO()
    before = ui.use(ui.make(file=buf, width=200, color_system=None))
    monkeypatch.setattr(ui, "FORCE_LIVE", False)
    monkeypatch.setattr(ui, "FORCE_KEYS", keys)
    monkeypatch.setattr(keys_module, "KEYS", _Keys(after=5))
    try:
        waiter = reset_waiter.ResetWaiter(poller, _Recorder(), F, 22.0, False, True)
        outcome = waiter.run(reset_waiter.ResetTarget("Bake 275 °F", 135 + reset_waiter.MARGIN), auto_carry_on, CancellationToken())
    finally:
        ui.use(before)
    return outcome, buf.getvalue(), poller


def test_the_plain_reset_screen_says_ready_once_and_how_to_carry_on(monkeypatch):
    outcome, out, poller = _wait(monkeypatch, auto_carry_on=False, keys=True)
    assert outcome == reset_waiter.ResetOutcome.Ready
    said = [line for line in out.splitlines() if line.startswith("The oven is ready for")]
    assert said == ["The oven is ready for Bake 275 °F (247 °F). Press C to carry on."], out
    assert poller.beeps == 1  # and beeped once, not on every pass while waiting for C
    progress = [line for line in out.splitlines() if line.startswith("247 °F, ")]
    assert progress and all(": ready" in line and "ready in" not in line for line in progress), out


def test_the_plain_reset_screen_carries_on_by_itself_when_nobody_can_press_c(monkeypatch):
    outcome, out, poller = _wait(monkeypatch, auto_carry_on=False, keys=False)
    assert outcome == reset_waiter.ResetOutcome.Ready
    assert [line for line in out.splitlines() if line.startswith("The oven is ready for")] == ["The oven is ready for Bake 275 °F (247 °F)."]
    assert poller.beeps == 1
