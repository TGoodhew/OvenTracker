"""The results on the console (Ui/ConsoleReport.cs): one setpoint's summary, and the whole report's tables."""

from __future__ import annotations

from rich import box
from rich.text import Text

from ..analysis.session_summary import PointResult, Verdict, judge
from ..analysis.setpoint_analyzer import CompletionReason
from ..netfmt import fixed
from ..session.report import COMPARISON_COLUMNS, DIAL_COLUMNS, POINT_COLUMNS, RESIDUAL_COLUMNS, Report, minutes, verdict_text
from ..units import mode_name
from . import console as ui
from .dashboard import offset_markup
from .widgets import Panel, Rule, cell, grid, table


def verdict_color(v: Verdict) -> str:
    return {Verdict.Accurate: "green", Verdict.Borderline: "yellow", Verdict.OutOfTolerance: "red"}.get(v, "grey")


def point_summary(report: Report, r: PointResult) -> None:
    c = ui.get()
    f = report.f
    m = r.metrics
    v = judge(m, report.info.tolerance_c)
    g = grid()
    if m.mean_c is not None:
        g.add_row(
            Text.from_markup("[grey]Result[/]"),
            Text.from_markup(
                f"[bold]{f.T(m.mean_c)}[/] ±{f.span(m.half_width_c if m.half_width_c is not None else 0)}  "
                f"{offset_markup(m.offset_c, report.info.tolerance_c, f)}  [{verdict_color(v)}]{verdict_text(v)}[/]"
            ),
        )
    else:
        g.add_row(Text.from_markup("[grey]Result[/]"), Text.from_markup("[grey]not enough steady data for a result[/]"))
    if m.swing_c is not None:
        g.add_row(
            Text.from_markup("[grey]Swing[/]"),
            Text(f"{f.span(m.swing_c)} peak to peak" + (f", {fixed(m.cycle_minutes, 1)} min cycle" if m.cycle_minutes is not None else "")),
        )
    g.add_row(Text.from_markup("[grey]Time[/]"), Text(f"{minutes(m.total_minutes)} ({report.reason_text(m)})"))
    if m.reason == CompletionReason.TimedOut and not m.met_precision:
        g.add_row("", Text.from_markup("[yellow]The mean did not settle within the time limit; treat this one with care.[/]"))
    c.print(
        Panel(
            g,
            header=f"[bold]{mode_name(r.point.mode)} {ui.escape(f.setpoint(r.point.setpoint_c))}[/]",
            border=box.ROUNDED,
            border_style="orange1",
        )
    )


def full(report: Report) -> None:
    c = ui.get()
    c.print(Rule("[orange1]Results[/]"))
    # The full row is too wide for most windows, so the console shows it as two tables.
    accuracy = [0, 1, 2, 3, 4, 5, 6, 8]
    behaviour = [0, 1, 9, 10, 7, 11, 12]
    point_rows = list(report.point_rows())
    t = table([POINT_COLUMNS[i] for i in accuracy], title="Accuracy", no_wrap=True)
    for _, v, cells in point_rows:
        row = [cell(cells[i], fold=True) for i in accuracy]
        row[5] = cell(cells[accuracy[5]], style=verdict_color(v), fold=True)
        t.add_row(*row)
    c.print(t)
    how = table([POINT_COLUMNS[i] for i in behaviour], title="Getting there", no_wrap=True)
    for _, _, cells in point_rows:
        how.add_row(*(cell(cells[i], fold=True) for i in behaviour))
    c.print(how)

    comparison = list(report.comparison_rows())
    if comparison:
        ct = table(COMPARISON_COLUMNS, title="Bake and convection")
        for r in comparison:
            ct.add_row(*(Text(x) for x in r))
        c.print(ct)

    for line in report.findings():
        c.print(Text.from_markup("[bold]•[/] ") + Text(line))
    for line in report.durations():
        c.print(Text.from_markup("[grey]•[/] ") + Text(line))

    for a in report.advice():
        c.print()
        c.print(Rule("[orange1]Best adjustment[/]"))
        c.print(Text(report.advice_headline(a), style="bold"))
        for line in report.advice_details(a):
            c.print(Text.from_markup("[grey]•[/] ") + Text(line))
        rt = table(RESIDUAL_COLUMNS)
        for r in report.residual_rows(a):
            rt.add_row(*(Text(x) for x in r))
        c.print(rt)
        dial = list(report.dial_rows(a))
        if dial:
            d = table(DIAL_COLUMNS, title="What to set after adjusting")
            for r in dial:
                d.add_row(*(Text(x) for x in r))
            c.print(d)
