"""The console: the banner, the progress line, the battery, the dashboard, the change prompt and the reset screen
(the C# app's BatteryTextTests and NoTerminalTests.AProgressLineSaysWhereTheSetpointIs), drawn as Spectre draws
them. Needs Rich."""

from __future__ import annotations

import io

import pytest

import conftest  # noqa: F401  (puts python/src on the path)

pytest.importorskip("rich")

from rich.text import Text  # noqa: E402

from oventracker.analysis.cooling_model import CoolingModel  # noqa: E402
from oventracker.analysis.session_summary import TestPoint  # noqa: E402
from oventracker.analysis.setpoint_analyzer import AnalysisOptions, SetpointAnalyzer  # noqa: E402
from oventracker.meter.base import ReadingStatus  # noqa: E402
from oventracker.meter.simulated import SimulatedMeter, SimulatedOven  # noqa: E402
from oventracker.session import change_prompt, reset_waiter  # noqa: E402
from oventracker.session.meter_poller import MeterPoller  # noqa: E402
from oventracker.session.point_tracker import plain_line  # noqa: E402
from oventracker.ui import battery_text, dashboard  # noqa: E402
from oventracker.ui import console as ui  # noqa: E402
from oventracker.ui.figlet_font import BANNER  # noqa: E402
from oventracker.ui.widgets import Panel, Rule, figlet_lines  # noqa: E402
from oventracker.units import OvenMode, TempUnit, UnitFormatter  # noqa: E402

F = UnitFormatter(TempUnit.Fahrenheit)


def render(renderable, width: int = 120, height: int = 40, color: bool = False) -> str:
    buf = io.StringIO()
    c = ui.make(file=buf, width=width, color_system="256" if color else None, force_terminal=True if color else None)
    c.height = height
    c.print(renderable)
    return buf.getvalue()


def test_the_banner_is_the_c_sharp_apps():
    assert figlet_lines("OvenTracker", 200) == list(BANNER)
    # In a window narrower than the banner, Spectre breaks it between letters: "OvenTracke", then "r".
    lines = figlet_lines("OvenTracker", 80)
    assert len(lines) == 12
    assert lines[6:] == [g[-7:] for g in BANNER]


def test_figlet_text_leaves_out_what_the_font_doesnt_have():
    # Spectre's font has no "−" (U+2212), so FigletText on its own draws "−10 F" as "10 F"; the change prompt makes
    # it "-10 F" first (change_prompt.for_figlet, test_display.py).
    assert figlet_lines("−10 F", 80) == figlet_lines("10 F", 80)
    assert figlet_lines("SET THE OVEN TO BAKE 350", 80)[:6][0].startswith("  ____    _____   _____ ")
    assert len(figlet_lines("SET THE OVEN TO BAKE 350", 80)) == 18  # three rows of words


def test_panels_rules_and_tables_are_drawn_as_spectre_draws_them(monkeypatch):
    monkeypatch.delenv("NO_COLOR", raising=False)
    assert render(Rule("[orange1]1 of 2: Bake 350 °F[/]"), 40).splitlines()[0] == "── 1 of 2: Bake 350 °F " + "─" * 17
    assert render(Panel(Text("body"), header="[bold]Head[/]"), 60).splitlines() == ["╭─Head─╮", "│ body │", "╰──────╯"]
    from oventracker.ui.widgets import table

    t = table(["A", "Bee"], title="Accuracy")
    t.add_row("1", "two")
    assert render(t, 60).splitlines() == [" Accuracy  ", "╭───┬─────╮", "│ A │ Bee │", "├───┼─────┤", "│ 1 │ two │", "╰───┴─────╯"]
    coloured = render(Panel(Text("x"), header="[bold]H[/]", border_style="grey35"), 60, color=True)
    assert "\x1b[1;38;5;240mH" in coloured  # the header in the border's colour, bold


def test_a_battery_is_shown_and_a_low_one_stands_out():
    assert battery_text.markup(None) is None and battery_text.plain(None) is None
    assert battery_text.markup(64) == "64 %"
    assert battery_text.plain(64) == "battery 64 %"
    low = battery_text.markup(15)
    assert "[red]" in low and "15 % (low)" in low
    Text.from_markup(low)  # valid markup
    assert battery_text.plain(15) == "battery 15 % (low)"
    assert not battery_text.is_low(20) and battery_text.is_low(19.9)


def heating(seconds: int) -> SetpointAnalyzer:
    a = SetpointAnalyzer(176.67)
    for t in range(seconds):
        a.add(t, 100 + t * 0.2)
    return a


def test_a_progress_line_says_where_the_setpoint_is():
    line = plain_line(heating(300), F)
    assert line.startswith("4:59")
    assert "°F" in line and "Heating to the setting" in line and "time left" in line


def test_progress_lines_include_the_battery_when_the_meter_has_one():
    a = heating(120)
    assert plain_line(a, F, 15).endswith("battery 15 % (low)")
    assert "battery" not in plain_line(a, F, None)


def test_clock_and_time_left():
    assert dashboard.clock(299.9) == "4:59"
    assert dashboard.clock(3725) == "1:02:05"
    assert dashboard.remaining(None) == "learning your oven…"
    from oventracker.analysis.setpoint_analyzer import Eta

    assert dashboard.remaining(Eta(20, True)) == "under a minute"
    assert dashboard.remaining(Eta(61, True)) == "≈ 2 min (rough)"
    assert dashboard.remaining(Eta(600, False)) == "≈ 10 min"


@pytest.mark.parametrize("width,height", [(120, 40), (100, 30), (200, 60), (80, 24)])
def test_the_dashboard_fits_the_window(width, height):
    """The frame keeps one height and stays inside the window, so the live display redraws in place. At 80 columns
    the two tables would go one under the other and not fit a 24-line window, so the frame is compact: one table,
    with the meter in a row of it (#41; more in test_display.py)."""
    a = SetpointAnalyzer(176.667, AnalysisOptions(precision_c=2 / 1.8))
    oven = SimulatedOven(7)
    oven.set(OvenMode.Bake, 176.667)
    for t in range(0, 40 * 60):
        oven.step(1)
        a.add(t, oven.probe_c())
    poller = MeterPoller(SimulatedMeter(SimulatedOven(1), 1), 1, connected=True)
    m = dashboard.DashboardModel(TestPoint(1, OvenMode.Bake, 176.667), 4, a, F, poller, "simulated meter", "", 15 / 1.8,
                                 dashboard.ChartView.Auto, ReadingStatus.Ok, "A notice.", width, height)
    lines = render(dashboard.render(m), width, height).splitlines()
    assert len(lines) <= height - 2
    if width <= 80:
        assert any(line.startswith("Meter           simulated meter, connected") for line in lines)  # one table
    assert all(len(line) <= width for line in lines)
    assert lines[1].startswith("│ BAKE 350 °F   setpoint 1 of 4   elapsed 39:59")
    m.alert = "An alert."
    with_alert = render(dashboard.render(m), width, height).splitlines()
    assert any("An alert." in line for line in with_alert) and len(with_alert) <= height - 2
    for view in (dashboard.ChartView.Whole, dashboard.ChartView.Recent):
        m.view = view
        assert len(render(dashboard.render(m), width, height).splitlines()) == len(lines)


def test_the_change_prompt_shows_the_setting_in_large_text():
    keys = "[bold]Enter[/] it's set and started: track it   [bold]S[/] skip this setpoint   [bold]F[/] finish the session"
    out = render(change_prompt.view("CHANGE THE OVEN TO", ["CONVECTION", "350 °F"], "Bake 350 °F is done.", keys, 75, 60, F, 176.0, 81.4), 120)
    lines = out.splitlines()
    assert lines[0].startswith("╔═") and lines[-1].startswith("╚═")
    assert "║ Bake 350 °F is done." in lines[1] and "║ CHANGE THE OVEN TO" in lines[2]
    assert "Waiting 1 min; a reminder beeps every 1 min." in out
    assert "The oven is at 349 °F now.   Meter battery 81 %." in out
    assert any("|___ /  | ___|   / _ \\    |  ___|" in line for line in lines)  # 350 F
    assert change_prompt.waited(59.9) == "59 s" and change_prompt.waited(125) == "2 min"


def test_the_reset_screen_predicts_and_offers_to_carry_on():
    oven = SimulatedOven(3)
    oven.set(OvenMode.Bake, 232.2)
    oven.step(3600)
    oven.set(OvenMode.Bake, None)
    model = CoolingModel(22)
    for t in range(15 * 60):
        oven.step(1)
        model.add(t, round(oven.probe_c(), 1))
    nxt = reset_waiter.ResetTarget("Bake 275 °F", 135 + reset_waiter.MARGIN)
    cold = reset_waiter.ResetTarget("Start again from cold", 22 + reset_waiter.MARGIN)
    out = render(reset_waiter.view(model, nxt, cold, False, 900, None, False, F, True, 81.4, 0, None, 120), 120)
    assert "OVEN RESET   watching for 15 min   meter battery 81 %" in out
    assert "Cooling (time constant" in out
    assert "│ Bake 275 °F           │ 285 °F │ about" in out
    assert "Start again from cold" in out and "room temperature assumed 72 °F" in out
    assert reset_waiter.minutes_text(0.5) == "under a minute"
    assert reset_waiter.minutes_text(45) == "45 min"
    assert reset_waiter.minutes_text(135) == "2 h 15 min"
