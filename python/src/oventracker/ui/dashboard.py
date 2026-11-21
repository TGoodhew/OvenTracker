"""The live display while a setpoint is measured (Ui/Dashboard.cs)."""

from __future__ import annotations

import enum
import math
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any

from rich import box
from rich.console import RenderableType
from rich.text import Text

from ..analysis.setpoint_analyzer import Eta, PointPhase, SetpointAnalyzer, UnitKind
from ..analysis.session_summary import TestPoint
from ..meter.base import ReadingStatus
from ..netfmt import clamp, fixed, net_ceiling
from ..units import UnitFormatter, mode_name
from . import battery_text, braille_plot
from .console import escape
from .widgets import Panel, columns, markup, rows, simple_table

_TABLE_ROWS = 6


class ChartView(enum.IntEnum):
    Auto = 0
    Whole = 1
    Recent = 2


@dataclass
class DashboardModel:
    point: TestPoint
    point_count: int
    analyzer: SetpointAnalyzer
    format: UnitFormatter
    poller: Any  # session.meter_poller.MeterPoller
    meter_label: str
    not_temperature_hint: str
    tolerance_c: float
    view: ChartView
    last_status: ReadingStatus | None
    notice: str | None
    width: int
    height: int
    alert: str | None = None


def _timespan(seconds: float) -> tuple[int, int, int, float]:
    """TimeSpan.FromSeconds: (whole hours in total, minutes, seconds, total hours)."""
    ticks = int(max(0.0, seconds) * 10_000_000)
    total_seconds = ticks // 10_000_000
    return total_seconds // 3600, (total_seconds // 60) % 60, total_seconds % 60, ticks / 36_000_000_000


def clock(seconds: float) -> str:
    hours, minutes, secs, total_hours = _timespan(seconds)
    return f"{hours}:{minutes:02d}:{secs:02d}" if total_hours >= 1 else f"{minutes}:{secs:02d}"


def remaining(eta: Eta | None) -> str:
    if eta is None:
        return "learning your oven…"
    if eta.seconds < 30:
        return "under a minute"
    m = fixed(net_ceiling(eta.seconds / 60), 0)
    return f"≈ {m} min (rough)" if eta.rough else f"≈ {m} min"


def phase_text(a: SetpointAnalyzer) -> str:
    if a.phase == PointPhase.Heating:
        return "Cooling to the setting" if a.cooling else "Heating to the setting"
    if a.phase == PointPhase.Settling:
        return "Settling (letting the first cycle pass)"
    if a.phase == PointPhase.Measuring:
        if a.kind == UnitKind.Block:
            return f"Measuring: block {len(a.units) + 1} of ~{a.required_units} (no thermostat cycle seen)"
        return f"Measuring: cycle {len(a.units) + 1} of ~{a.required_units}"
    return "Complete"


def offset_markup(offset_c: float, tolerance_c: float, f: UnitFormatter) -> str:
    color = "green" if abs(offset_c) <= tolerance_c / 2 else "yellow" if abs(offset_c) <= tolerance_c else "red"
    return f"[{color}]({escape(f.D(offset_c))})[/]"


_KEYS_TEXT = "Keys: P oven says preheated   N finish this setpoint now   R restart it (door opened)   S skip   Z chart view   Q quit"


def render(m: DashboardModel) -> RenderableType:
    a = m.analyzer
    f = m.format
    now = a.now

    # Header: where we are and what the oven is doing.
    latest = a.latest_c
    current = (
        f"[bold]{f.T(latest)}[/]  {offset_markup(latest - a.setpoint_c, m.tolerance_c, f)} vs setting"
        if latest is not None
        else "[grey]waiting for a reading…[/]"
    )
    if m.last_status == ReadingStatus.Overload:
        status = "   [red]Probe open (OL): check the probe is plugged in[/]"
    elif m.last_status == ReadingStatus.NotTemperature:
        hint = " " + escape(m.not_temperature_hint) if m.not_temperature_hint else ""
        status = f"   [red]The meter is not measuring temperature.{hint}[/]"
    elif m.last_status == ReadingStatus.Error:
        status = "   [red]Meter sent something unexpected[/]"
    else:
        status = ""
    header = rows(
        markup(
            f"[bold][orange1]{mode_name(m.point.mode).upper()} {escape(f.setpoint(m.point.setpoint_c))}[/][/]"
            f"   [grey]setpoint {m.point.index} of {m.point_count}   elapsed {clock(now)}[/]"
        ),
        markup(f"{current}   [bold][red]{escape(m.alert)}[/][/]" if m.alert is not None else f"{current}{status}"),
        markup(
            f"[cyan]{escape(phase_text(a))}[/]"
            + ("" if a.phase == PointPhase.Complete else f"   time left at this setting: [bold]{escape(remaining(a.estimate_remaining()))}[/]")
        ),
    )

    # Chart. The frame keeps one height and stays inside the window, or the live display scrolls instead of
    # redrawing in place: header 5 + chart borders 4 + two tables of TableRows + 2 + notice 1 + keys.
    plot_width = max(40, m.width - 4)
    key_lines = int(math.ceil(len(_KEYS_TEXT) / max(20, m.width)))
    plot_rows = int(clamp(m.height - 1 - (5 + 4 + _TABLE_ROWS + 2 + 1 + key_lines) - 1, 3, 24))
    t1 = max(now, 60.0)
    settled_from = max(0.0, a.turning_points[0].t - 180) if a.turning_points else 0.0
    if m.view == ChartView.Recent:
        t0, view_name = max(0.0, t1 - 20 * 60), "last 20 minutes"
    elif m.view == ChartView.Whole:
        t0, view_name = 0.0, "whole setpoint"
    elif settled_from > 0 and t1 - settled_from > 120:
        t0, view_name = settled_from, "since it reached temperature"
    else:
        t0, view_name = 0.0, "whole setpoint"
    plot = Panel(
        markup(braille_plot.markup(a.series.items, t0, t1, a.setpoint_c, m.tolerance_c, f, plot_width, plot_rows)),
        header=f"[grey]{view_name} (Z changes)  ·  [deepskyblue1]oven[/]  [yellow]setting[/]  [grey30]±tolerance[/][/]",
        border=box.ROUNDED,
        border_style="grey35",
        expand=True,
    )

    # Statistics so far.
    stats = simple_table()
    e = a.current_estimate
    unit_name = "Blocks" if a.kind == UnitKind.Block else "Cycles"
    if e is not None:
        stats.add_row("Mean so far", markup(f"[bold]{f.T(e.mean_c)}[/]  {offset_markup(e.mean_c - a.setpoint_c, m.tolerance_c, f)}"))
        stats.add_row(
            "±95 %",
            markup(
                f"{f.span(e.half_width_c)}  [grey](target {f.span(a.options.precision_c)})[/]"
                if e.half_width_c is not None
                else "[grey]needs two cycles[/]"
            ),
        )
        swing = sum(u.swing_c for u in a.units) / len(a.units)
        stats.add_row("Swing", f"{f.span(swing)} peak to peak")
    else:
        stats.add_row("Mean so far", markup("[grey]after the first full cycle[/]"))
    stats.add_row(unit_name, f"{len(a.units)} measured" + (f", {a.discarded_units} dropped (still warming)" if a.discarded_units > 0 else ""))
    us = a.expected_unit_seconds()
    if us is not None and a.kind != UnitKind.Block:
        stats.add_row("Cycle length", f"{fixed(us / 60, 1)} min")
    reach = a.metrics().time_to_setpoint_minutes
    if reach is not None:
        stats.add_row("Reached setting", f"after {fixed(reach, 1)} min")

    # Meter and link.
    p = m.poller
    from ..session.meter_poller import LinkState

    link = {
        LinkState.Connected: "[green]connected[/]",
        LinkState.Reconnecting: "[yellow]link lost, reconnecting…[/]",
        LinkState.Connecting: "[yellow]connecting…[/]",
    }.get(p.state, "[grey]stopped[/]")
    meter = simple_table()
    meter.add_row("Meter", Text(m.meter_label))
    meter.add_row("Link", markup(link + (f" [grey]({p.reconnects} drop{'' if p.reconnects == 1 else 's'})[/]" if p.reconnects > 0 else "")))
    battery = battery_text.markup(p.battery_percent)
    if battery is not None:
        meter.add_row("Battery", markup(battery))
    meter.add_row("Readings", f"{a.series.count:,}")
    meter.add_row("Detecting", f"turns of {f.span(a.hysteresis)} or more")
    if p.last_error is not None and p.last_error_at is not None and (datetime.now(timezone.utc) - p.last_error_at).total_seconds() < 120:
        meter.add_row("Last error", markup(f"[yellow]{escape(p.last_error)}[/]"))

    while stats.row_count < _TABLE_ROWS:
        stats.add_row("", "")
    while meter.row_count < _TABLE_ROWS:
        meter.add_row("", "")
    bottom = columns(stats, meter)
    keys = markup(
        "[grey]Keys:[/] [bold]P[/] oven says preheated   [bold]N[/] finish this setpoint now   "
        "[bold]R[/] restart it (door opened)   [bold]S[/] skip   [bold]Z[/] chart view   [bold]Q[/] quit"
    )
    parts: list[RenderableType] = [Panel(header, border=box.ROUNDED, border_style="orange1", expand=True), plot, bottom]
    parts.append(Text(" ") if m.notice is None else markup(f"[yellow]{escape(m.notice)}[/]"))
    parts.append(keys)
    return rows(*parts)
