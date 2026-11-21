"""Tracks one setpoint until the analyzer says it has enough, or the user stops it (Session/PointTracker.cs).

On the live display in a terminal, or with a progress line each minute when the output isn't one (a file, a
pipe, no console)."""

from __future__ import annotations

import enum
from datetime import datetime, timezone
from typing import Any

from ..analysis.heading_check import HeadingCheck, HeadingState
from ..analysis.session_summary import PointResult, TestPoint
from ..analysis.setpoint_analyzer import AnalysisOptions, CompletionReason, PointPhase, SetpointAnalyzer
from ..meter.auto_power_off import AutoPowerOff
from ..meter.base import ReadingStatus
from ..units import UnitFormatter, mode_name
from . import silence_watch
from .cancel import CancellationToken
from .silence_watch import SilenceChange, SilenceWatch


class PointOutcome(enum.IntEnum):
    Next = 0
    Quit = 1


#: How often a progress line is written without the live display: each minute of the setpoint.
PLAIN_EVERY_SECONDS = 60.0


def plain_line(a: SetpointAnalyzer, f: UnitFormatter, battery_percent: float | None = None) -> str:
    """One progress line for plain output: time, temperature, phase, mean so far and time left."""
    from ..ui import battery_text
    from ..ui.dashboard import clock, phase_text, remaining

    parts = [clock(a.now)]
    c = a.latest_c
    if c is not None:
        parts.append(f"{f.T(c)} ({f.D(c - a.setpoint_c)})")
    parts.append(phase_text(a))
    e = a.current_estimate
    if e is not None:
        parts.append(f"mean so far {f.T(e.mean_c)}" + (f" ±{f.span(e.half_width_c)}" if e.half_width_c is not None else ""))
    if a.phase != PointPhase.Complete:
        parts.append(f"time left {remaining(a.estimate_remaining())}")
    battery = battery_text.plain(battery_percent)
    if battery is not None:
        parts.append(battery)
    return "  ·  ".join(parts)


def over_rating_message(reading_c: float, max_c: float, f: UnitFormatter) -> str:
    """What to say when the probe reads above its rating."""
    return (
        f"The probe reads {f.T(reading_c, 0)}, above its rated {f.T(max_c, 0)}: it may be damaged, and readings this "
        "hot can't be trusted. Lower the setting."
    )


def not_heading_message(h: HeadingCheck, f: UnitFormatter) -> str:
    """What to say when the oven isn't heading for its new setting."""
    return (
        f"The oven isn't {'heating up' if h.heating else 'cooling down'} towards {f.setpoint(h.setpoint_c)}: it's still near "
        f"{f.T(h.from_c, 0)}. Did you change the setting? If not, change it now and press R twice to restart this setpoint."
    )


class PointTracker:
    def __init__(
        self,
        poller: Any,
        recorder: Any,
        format: UnitFormatter,
        options: AnalysisOptions,
        tolerance_c: float,
        meter_label: str,
        not_temperature_hint: str,
        point_count: int,
        fast: bool,
        auto_power_off: AutoPowerOff | None = None,
        probe_max_c: float | None = None,
    ):
        self._poller = poller
        self._recorder = recorder
        self._f = format
        self._options = options
        self._tolerance_c = tolerance_c
        self._meter_label = meter_label
        self._hint = not_temperature_hint
        self._point_count = point_count
        self._fast = fast
        self._apo = auto_power_off
        self._probe_max_c = probe_max_c

    def run(
        self, point: TestPoint, token: CancellationToken, starting_from_c: float | None = None, swing_c: float | None = None
    ) -> tuple[PointResult, PointOutcome]:
        """``starting_from_c``: where the oven was before this setpoint, so the app can check it heads for the new
        setting. ``swing_c``: the thermostat's swing at the setpoint before, if measured."""
        from ..ui import console as ui
        from ..ui import dashboard
        from ..ui.keys import KEYS, upper

        f = self._f
        poller = self._poller
        recorder = self._recorder
        options = self._options
        heading = HeadingCheck(starting_from_c, point.setpoint_c, swing_c) if starting_from_c is not None else None
        st: dict[str, Any] = {
            "analyzer": SetpointAnalyzer(point.setpoint_c, options),
            "start": None,
            "started": False,
            "last_time": datetime.now(timezone.utc),
            "last_status": None,
            "notice": None,
            "armed": None,  # keys that need pressing twice
            "view": dashboard.ChartView.Auto,
            "outcome": PointOutcome.Next,
            "alert": None,
            "heading": heading,
        }
        can_read_keys = ui.keys()
        silence = SilenceWatch()
        console = ui.get()

        def render() -> Any:
            return dashboard.render(
                dashboard.DashboardModel(
                    point,
                    self._point_count,
                    st["analyzer"],
                    f,
                    poller,
                    self._meter_label,
                    self._hint,
                    self._tolerance_c,
                    st["view"],
                    st["last_status"],
                    st["notice"],
                    ui.width(120),
                    ui.height(40),
                    st["alert"],
                )
            )

        def tick() -> bool:
            """One pass: take the new readings, watch for a quiet meter or an oven not heading for the setting,
            and handle keys. True once the setpoint is done. The live display and plain output share it."""
            analyzer: SetpointAnalyzer = st["analyzer"]
            while True:
                r = poller.readings.try_read()
                if r is None:
                    break
                if st["start"] is None:
                    st["start"] = r.time
                    # Replay starts a setpoint here; after R the "restart" event has already done that.
                    if not st["started"]:
                        recorder.event(r.time, point, 0, "start")
                    st["started"] = True
                st["last_time"] = r.time
                elapsed = (r.time - st["start"]).total_seconds()
                recorder.reading(r, point, elapsed)
                st["last_status"] = r.status
                if r.status == ReadingStatus.Ok and r.celsius is not None:
                    c = r.celsius
                    analyzer.add(elapsed, c)
                    # Is the oven heading for the new setting? If not, it probably wasn't changed.
                    h: HeadingCheck | None = st["heading"]
                    if h is not None:
                        was = h.state
                        now = h.update(elapsed, c)
                        if now == HeadingState.NotHeading and was != HeadingState.NotHeading:
                            recorder.event(r.time, point, elapsed, "not-heading")
                            poller.enqueue(lambda m: m.beep())
                            ui.bell()
                        elif now == HeadingState.OnItsWay and was == HeadingState.NotHeading:
                            recorder.event(r.time, point, elapsed, "heading")
                            st["notice"] = "The oven is heading for the setting now."
                if analyzer.phase == PointPhase.Complete:
                    return True

            # A meter that has gone quiet (often its auto power-off): say so, beep once, and mark the gap.
            change = silence.update(poller.silent_for)
            if change == SilenceChange.WentQuiet:
                recorder.event(st["last_time"], point, analyzer.now, "meter-silent")
                ui.bell()
            elif change == SilenceChange.CameBack:
                recorder.event(st["last_time"], point, analyzer.now, "meter-back")
                st["notice"] = "The meter is answering again; tracking carries on."
            h = st["heading"]
            latest = analyzer.latest_c
            if silence.silent:
                st["alert"] = silence_watch.message(poller.silent_for, self._apo)
            elif h is not None and h.state == HeadingState.NotHeading:
                st["alert"] = not_heading_message(h, f)
            elif self._probe_max_c is not None and latest is not None and latest > self._probe_max_c:
                st["alert"] = over_rating_message(latest, self._probe_max_c, f)
            else:
                st["alert"] = None

            if token.is_cancellation_requested:
                analyzer.finish(CompletionReason.Aborted)
                st["outcome"] = PointOutcome.Quit
                return True

            while can_read_keys and KEYS.available():
                key = upper(KEYS.read())
                confirmed = st["armed"] == key
                st["armed"] = None
                if key == "P":
                    analyzer.mark_preheat_signal()
                    recorder.event(st["last_time"], point, analyzer.now, "preheat-signal")
                    pc = analyzer.smoothed_c
                    st["notice"] = f"Preheat signal noted at {dashboard.clock(analyzer.now)}" + (f", oven at {f.T(pc)}." if pc is not None else ".")
                elif key == "Z":
                    st["view"] = dashboard.ChartView((int(st["view"]) + 1) % 3)
                elif key in ("N", "R", "S", "Q") and not confirmed:
                    st["armed"] = key
                    st["notice"] = {
                        "N": "Press N again to finish this setpoint now with the data so far.",
                        "R": "Press R again to throw this setpoint's data away and start it again.",
                        "S": "Press S again to skip this setpoint.",
                    }.get(key, "Press Q again to stop the session and write the report.")
                elif key == "N":
                    analyzer.finish(CompletionReason.Manual)
                elif key == "S":
                    analyzer.finish(CompletionReason.Skipped)
                elif key == "Q":
                    analyzer.finish(CompletionReason.Aborted)
                    st["outcome"] = PointOutcome.Quit
                elif key == "R":
                    recorder.event(st["last_time"], point, analyzer.now, "restart")
                    analyzer = SetpointAnalyzer(point.setpoint_c, options)
                    st["analyzer"] = analyzer
                    # Check again from where the oven is now (e.g. after changing the setting).
                    if h is not None:
                        last = poller.last_celsius
                        st["heading"] = HeadingCheck(last if last is not None else h.from_c, point.setpoint_c, swing_c)
                    st["start"] = None
                    st["notice"] = "Restarted: close the door and leave it; the clock starts again from the next reading."
            return st["analyzer"].phase == PointPhase.Complete

        delay = 0.1 if self._fast else 0.25

        def run_plain() -> None:
            """Without a live display (output to a file or pipe, or no console): a line now and then instead."""
            from ..ui.widgets import plain_print

            console.print(
                f"[bold]{point.index} of {self._point_count}: {ui.escape(mode_name(point.mode))} {ui.escape(f.setpoint(point.setpoint_c))}[/]"
                + ("   [grey]keys: P preheated · N N finish · R R restart · S S skip · Q Q quit[/]" if can_read_keys else "")
            )
            last_line = float("-inf")
            last_phase = st["analyzer"].phase
            last_alert: str | None = None
            last_notice: str | None = None
            while not tick():
                a: SetpointAnalyzer = st["analyzer"]
                due = a.now - last_line >= PLAIN_EVERY_SECONDS or a.phase != last_phase
                if st["alert"] != last_alert:
                    if st["alert"] is not None:
                        console.print(f"[red]{ui.escape(st['alert'])}[/]")
                    last_alert = st["alert"]
                if st["notice"] != last_notice:
                    if st["notice"] is not None:
                        console.print(f"[yellow]{ui.escape(st['notice'])}[/]")
                    last_notice = st["notice"]
                if due and a.latest_c is not None:
                    plain_print(console, plain_line(a, f, poller.battery_percent))
                    last_line = a.now
                    last_phase = a.phase
                token.wait(delay)
            plain_print(console, plain_line(st["analyzer"], f, poller.battery_percent))

        live = ui.live()
        with KEYS:
            if live:
                try:
                    from rich.live import Live

                    from ..ui.widgets import CropTop

                    # Start the live display at the top of a clear screen, so it never has to scroll.
                    if can_read_keys:
                        ui.clear_screen()
                    with Live(
                        CropTop(render()),
                        console=console,
                        auto_refresh=False,
                        transient=False,
                        redirect_stdout=False,
                        redirect_stderr=False,
                        vertical_overflow="visible",
                    ) as lv:
                        while not tick():
                            lv.update(CropTop(render()), refresh=True)
                            token.wait(delay)
                        lv.update(CropTop(render()), refresh=True)
                    # (Rich ends the live display with a new line, as the C# app's WriteLine does here.)
                except OSError as ex:
                    # The console can't draw the live display after all: carry on, with plain lines.
                    console.print()
                    console.print(f"[yellow]The live display stopped ({ui.escape(str(ex))}); carrying on with progress lines.[/]")
                    live = False
            if not live and st["analyzer"].phase != PointPhase.Complete:
                run_plain()

        analyzer = st["analyzer"]
        recorder.event(st["last_time"], point, analyzer.now, f"end:{analyzer.reason.name}")
        return PointResult(point, analyzer.metrics()), st["outcome"]
