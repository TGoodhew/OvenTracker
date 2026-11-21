"""Watches the oven between sittings, or between setpoints when it needs to cool first (Session/ResetWaiter.cs).

Shows its temperature, predicts when it will be ready for the next setpoint and when it will be cold, and says
when it gets there."""

from __future__ import annotations

import enum
import math
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Any

from ..analysis.cooling_model import CoolingModel, CoolingState
from ..analysis.session_summary import TestPoint
from ..meter.auto_power_off import AutoPowerOff
from ..meter.base import ReadingStatus
from ..netfmt import fixed, net_floor
from ..units import UnitFormatter
from . import silence_watch
from .cancel import CancellationToken


class ResetOutcome(enum.IntEnum):
    Ready = 0  # the oven reached the target
    CarryOn = 1  # the user chose to carry on before it did
    Quit = 2  # the user stopped the session


@dataclass(frozen=True)
class ResetTarget:
    """A temperature the oven has to get down to, and what it's for."""

    label: str
    target_c: float


#: How far above the previous setpoint (or room temperature) still counts as ready: 10 °F.
MARGIN = 10 / 1.8


def target_for(plan: list[TestPoint], point: TestPoint, ambient_c: float) -> float:
    """The temperature a setpoint should start from: just above the setpoint before it in the plan, or near room
    temperature for the first."""
    i = next((k for k, p in enumerate(plan) if p.index == point.index), -1)
    return plan[i - 1].setpoint_c + MARGIN if i > 0 else ambient_c + MARGIN


def minutes_text(minutes: float) -> str:
    if minutes < 1:
        return "under a minute"
    if minutes < 90:
        return f"{fixed(minutes, 0)} min"
    return f"{fixed(net_floor(minutes / 60), 0)} h {fixed(math.fmod(minutes, 60), 0)} min"


def in_about(minutes: float) -> str:
    """ResetWaiter.InAbout: "in about 12 min", or "in under a minute" (not "in about under a minute")."""
    return "in under a minute" if minutes < 1 else f"in about {minutes_text(minutes)}"


def short_time(t: datetime) -> str:
    """DateTime.ToString("t") in the invariant culture, as the C# app (InvariantGlobalization) shows it: 14:05."""
    return t.strftime("%H:%M")


def view(
    m: CoolingModel,
    nxt: ResetTarget,
    cold: ResetTarget,
    ready: bool,
    watched_seconds: float,
    notice: str | None,
    auto_carry_on: bool,
    f: UnitFormatter,
    ambient_assumed: bool,
    battery_percent: float | None,
    silent_for: float,
    auto_power_off: AutoPowerOff | None,
    width: int,
) -> Any:
    """The reset screen, as the live display draws it."""
    from rich import box

    from ..ui import battery_text, braille_plot
    from ..ui.console import escape
    from ..ui.widgets import Panel, markup, rows, table

    current = f"[bold]{f.T(m.current_c)}[/]" if m.current_c is not None else "[grey]waiting for a reading…[/]"
    if ready:
        state = "[green]Ready.[/]"
    elif m.state == CoolingState.Learning:
        state = "[grey]Learning how fast this oven cools (a few minutes)…[/]"
    elif m.state == CoolingState.Cooling:
        state = f"[cyan]Cooling[/] [grey](time constant {minutes_text((m.tau_seconds or 0) / 60)})[/]"
    elif m.state == CoolingState.Heating:
        state = "[yellow]Getting hotter: the oven is in use, or hasn't been turned off. Predictions resume once it cools.[/]"
    elif m.state == CoolingState.DoorOpen:
        state = "[yellow]Dropping fast: the door is open. Shut it and the prediction carries on.[/]"
    else:
        state = ""

    targets = table(["Ready to", "Below", "Time", "At"])
    # From cold is worth showing only when it's a different target (it's the same for the first setpoint).
    for t in [nxt] if abs(cold.target_c - nxt.target_c) < 1 else [nxt, cold]:
        eta = m.time_until(t.target_c)
        if eta is None:
            when = "[grey]—[/]"
        elif eta == 0:
            when = "[green]now[/]"
        elif eta / 60 < 1:
            when = "under a minute"
        else:
            when = f"about {minutes_text(eta / 60)}"
        at = short_time(datetime.now() + timedelta(seconds=eta)) if eta is not None and eta > 0 else ""
        targets.add_row(*(markup(x) for x in (escape(t.label), escape(f.T(t.target_c, 0)), when, at)))

    samples = list(m.samples)
    chart_width = max(40, width - 4)
    if len(samples) < 2:
        chart: Any = markup("[grey]The chart starts with the first readings.[/]")
    else:
        chart = markup(
            braille_plot.markup(samples, samples[0][0], max(samples[-1][0], samples[0][0] + 60), nxt.target_c, 0, f, chart_width, 10)
        )

    battery = battery_text.markup(battery_percent)
    if ready:
        line3 = "Carrying on with the next setpoint." if auto_carry_on else f"[bold]The oven is ready for {escape(nxt.label)}.[/] Press [bold]C[/] to carry on."
    else:
        line3 = f"Waiting until the oven is below {escape(f.T(nxt.target_c, 0))} for [bold]{escape(nxt.label)}[/]. Leave the door shut."
    parts: list[Any] = [
        Panel(
            rows(
                markup(
                    f"[bold][orange1]OVEN RESET[/][/]   [grey]watching for {minutes_text(watched_seconds / 60)}[/]"
                    + (f"   [grey]meter battery[/] {battery}" if battery is not None else "")
                ),
                markup(f"{current}   {state}"),
                markup(line3),
            ),
            border=box.ROUNDED,
            border_style="orange1",
            expand=True,
        ),
        targets,
        Panel(chart, header="[grey]oven temperature while cooling · the target dotted[/]", border=box.ROUNDED, border_style="grey35", expand=True),
        markup(
            "[grey]Keys:[/] [bold]C[/] carry on now   [bold]Q[/] stop here (saved; carry on later with [bold]--resume[/])"
            + (f"   [grey]room temperature assumed {escape(f.T(m.ambient_c, 0))} (--ambient to set it)[/]" if ambient_assumed else "")
        ),
    ]
    if notice is not None:
        parts.append(markup(f"[yellow]{escape(notice)}[/]"))
    if silent_for >= silence_watch.DEFAULT_THRESHOLD:
        parts.insert(1, markup(f"[bold][red]{escape(silence_watch.message(silent_for, auto_power_off))}[/][/]"))
    return rows(*parts)


class ResetWaiter:
    def __init__(
        self,
        poller: Any,
        recorder: Any,
        f: UnitFormatter,
        ambient_c: float,
        ambient_assumed: bool,
        fast: bool,
        auto_power_off: AutoPowerOff | None = None,
    ):
        self._poller = poller
        self._recorder = recorder
        self._f = f
        self._ambient_c = ambient_c
        self._ambient_assumed = ambient_assumed
        self._fast = fast
        self._apo = auto_power_off

    def run(self, nxt: ResetTarget, auto_carry_on: bool, token: CancellationToken) -> ResetOutcome:
        """``nxt``: what the oven has to cool to for the next setpoint. ``auto_carry_on``: carry on by itself when
        ready (--yes), rather than waiting for a key."""
        from ..ui import console as ui
        from ..ui.keys import KEYS, upper

        f = self._f
        poller = self._poller
        recorder = self._recorder
        model = CoolingModel(self._ambient_c)
        cold = ResetTarget("Start again from cold", self._ambient_c + MARGIN)
        start: datetime | None = None
        now = recorder.now  # then each reading's time
        ready = False
        outcome = ResetOutcome.Ready
        can_read_keys = ui.keys()
        auto_carry_on = auto_carry_on or not can_read_keys  # nobody can press C
        notice: str | None = None
        delay = 0.1 if self._fast else 0.5
        console = ui.get()

        recorder.note(now, "reset-start")

        def take() -> None:
            nonlocal now, start
            for r in poller.readings.drain():
                recorder.reading(r, None, None)
                now = r.time
                if start is None:
                    start = r.time
                if r.status == ReadingStatus.Ok and r.celsius is not None:
                    model.add((r.time - start).total_seconds(), r.celsius)

        def render() -> Any:
            return view(
                model,
                nxt,
                cold,
                ready,
                (now - start).total_seconds() if start is not None else 0.0,
                notice,
                auto_carry_on,
                f,
                self._ambient_assumed,
                poller.battery_percent,
                poller.silent_for,
                self._apo,
                ui.width(100),
            )

        def run_plain() -> ResetOutcome:
            nonlocal ready
            from ..ui.widgets import plain_print

            console.print(
                f"[bold]Oven reset:[/] waiting until the oven is below {ui.escape(f.T(nxt.target_c, 0))} for {ui.escape(nxt.label)}."
                + (" [grey]C carry on now · Q stop here[/]" if can_read_keys else "")
            )
            last_line: datetime | None = None
            last_state: CoolingState | None = None
            while True:
                take()
                current = model.current_c
                # Said (and beeped) once, when it becomes ready, not on every pass while waiting for C.
                if not ready and current is not None and current <= nxt.target_c:
                    poller.enqueue(lambda m: m.beep())
                    ui.bell()
                    plain_print(
                        console, f"The oven is ready for {nxt.label} ({f.T(current, 0)})." + ("" if auto_carry_on else " Press C to carry on.")
                    )
                    if auto_carry_on:
                        return ResetOutcome.Ready
                    ready = True
                if token.is_cancellation_requested:
                    return ResetOutcome.Quit
                while can_read_keys and KEYS.available():
                    key = upper(KEYS.read())
                    if key in ("C", "\r"):
                        return ResetOutcome.Ready if ready else ResetOutcome.CarryOn
                    if key == "Q":
                        return ResetOutcome.Quit
                shown = model.current_c
                if shown is not None and (
                    last_line is None or datetime.now(timezone.utc) - last_line >= timedelta(minutes=1) or model.state != last_state
                ):
                    e = None if ready else model.time_until(nxt.target_c)
                    eta = "ready" if ready else f"ready {in_about(e / 60)}" if e is not None else "learning how fast it cools"
                    ce = model.time_until(cold.target_c)
                    cold_eta = f"; cold {in_about(ce / 60)}" if ce is not None else ""
                    silent = (
                        f"  {silence_watch.message(poller.silent_for, self._apo)}"
                        if poller.silent_for >= silence_watch.DEFAULT_THRESHOLD
                        else ""
                    )
                    from ..ui import battery_text

                    b = battery_text.plain(poller.battery_percent)
                    battery = f" Meter {b}." if b is not None else ""
                    plain_print(console, f"{f.T(shown, 0)}, {model.state.name.lower()}: {eta}{cold_eta}.{battery}{silent}")
                    last_line = datetime.now(timezone.utc)
                    last_state = model.state
                token.wait(delay)

        with KEYS:
            if not ui.live():
                outcome = run_plain()
                console.print()
                recorder.note(now, f"reset-end:{outcome.name}")
                return outcome

            from rich.live import Live

            from ..ui.widgets import CropTop

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
            ) as live:
                while True:
                    take()
                    current = model.current_c
                    if not ready and current is not None and current <= nxt.target_c:
                        ready = True
                        poller.enqueue(lambda m: m.beep())
                        ui.bell()
                        if auto_carry_on:
                            break
                    if token.is_cancellation_requested:
                        outcome = ResetOutcome.Quit
                        break
                    done = False
                    while can_read_keys and KEYS.available():
                        key = upper(KEYS.read())
                        if key in ("C", "\r"):
                            outcome = ResetOutcome.Ready if ready else ResetOutcome.CarryOn
                            done = True
                        elif key == "Q":
                            outcome = ResetOutcome.Quit
                            done = True
                        else:
                            notice = "Keys: C (or Enter) carry on with the next setpoint · Q stop here (the run is saved; carry on later with --resume)"
                    if done:
                        break
                    live.update(CropTop(render()), refresh=True)
                    token.wait(delay)
                live.update(CropTop(render()), refresh=True)
        # (Rich ends the live display with a new line, as the C# app's WriteLine does here.)
        recorder.note(now, f"reset-end:{outcome.name}")
        return outcome
