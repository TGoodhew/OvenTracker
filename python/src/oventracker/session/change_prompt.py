"""Stops between setpoints and asks for the oven to be changed to the next setting (Session/ChangePrompt.cs).

In large text, with the oven's temperature live. Until a key is pressed it reminds (the meter beeps and the PC's
bell rings) every ``remind_every`` seconds, so a change isn't missed when nobody is at the oven."""

from __future__ import annotations

import enum
import math
from datetime import datetime, timezone
from typing import Any, Callable, TypeVar

from ..netfmt import fixed, round_even
from ..units import UnitFormatter
from .cancel import CancellationToken

T = TypeVar("T")


class ChangeChoice(enum.IntEnum):
    Start = 0
    Skip = 1
    Finish = 2
    Wait = 3


def should_ask(yes: bool, simulation: bool, can_read_keys: bool) -> bool:
    """Whether to stop and ask. With a real meter, always, even with --yes: nothing else changes the oven. Only the
    simulator carries on by itself with --yes. Without a keyboard, it can't ask."""
    return can_read_keys and not (yes and simulation)


def reminders_due(waited_seconds: float, every_seconds: float) -> int:
    """How many reminders are due after waiting this long (none if reminders are off)."""
    if every_seconds <= 0:
        return 0
    return int(round(waited_seconds * 10_000_000) // round(every_seconds * 10_000_000))


def waited(seconds: float) -> str:
    return f"{fixed(math.floor(seconds / 60), 0)} min" if seconds / 60 >= 1 else f"{fixed(math.floor(seconds), 0)} s"


def _answer(choice: Any) -> str:
    """How a choice is written in the answered: event: the enum's name, True or False, or nothing."""
    if choice is None:
        return ""
    if isinstance(choice, bool):
        return "True" if choice else "False"
    if isinstance(choice, enum.Enum):
        return choice.name
    return str(choice)


def view(
    heading: str,
    big: list[str],
    done: str | None,
    keys: str,
    waited_seconds: float,
    remind_every: float,
    f: UnitFormatter,
    oven_c: float | None,
    battery_percent: float | None,
) -> Any:
    """The prompt as the live display draws it."""
    from rich import box

    from ..ui import battery_text
    from ..ui.console import escape
    from ..ui.widgets import FigletText, Panel, markup, rows

    parts: list[Any] = []
    if done is not None:
        parts.append(markup(f"[green]{escape(done)}[/]"))
    parts.append(markup(f"[bold]{escape(heading)}[/]"))
    for line in big:
        parts.append(FigletText(line.replace("°", ""), "orange1"))
    battery = battery_text.markup(battery_percent)
    parts.append(
        markup(
            (f"The oven is at [bold]{escape(f.T(oven_c, 0))}[/] now.   " if oven_c is not None else "")
            + (f"Meter battery {battery}.   " if battery is not None else "")
            + f"[grey]Waiting {escape(waited(waited_seconds))}"
            + (f"; a reminder beeps every {escape(waited(remind_every))}.[/]" if remind_every > 0 else ".[/]")
        )
    )
    parts.append(markup(keys))
    return Panel(rows(*parts), border=box.DOUBLE, border_style="orange1", expand=True)


class ChangePrompt:
    def __init__(self, poller: Any, recorder: Any, f: UnitFormatter, remind_every_seconds: float, token: CancellationToken):
        self._poller = poller
        self._recorder = recorder
        self._f = f
        self._remind_every = remind_every_seconds
        self._token = token

    def ask(self, heading: str, mode_name: str, setpoint_c: float, done: str | None, wait_offer: str | None) -> ChangeChoice:
        """``heading``: "SET THE OVEN TO" or "CHANGE THE OVEN TO". ``done``: what was just finished. ``wait_offer``:
        if the oven should cool first, what W offers."""
        from ..ui.console import escape

        keys = "[bold]Enter[/] it's set and started: track it   [bold]S[/] skip this setpoint   [bold]F[/] finish the session" + (
            "" if wait_offer is None else f"   [bold]W[/] {escape(wait_offer)}"
        )
        f = self._f
        temp = f"{fixed(round_even(f.to_display(setpoint_c)), 0)} {f.symbol}"

        def choose(key: str) -> ChangeChoice | None:
            if key in ("\r", "\n", " "):
                return ChangeChoice.Start
            if key == "S":
                return ChangeChoice.Skip
            if key in ("F", "Q"):
                return ChangeChoice.Finish
            if key == "W" and wait_offer is not None:
                return ChangeChoice.Wait
            return None

        # The mode and the temperature on lines of their own, so neither wraps in a narrow window.
        result = self._wait_for_key(f"{heading} {mode_name.upper()} {temp}", heading, [mode_name.upper(), temp], done, keys, choose)
        return result if result is not None else ChangeChoice.Finish

    def ask_to_set_adjustment(self, done: str, to_text: str) -> bool:
        """Characterise: asks for the oven's adjustment to be changed, reminding until Enter (True) or F (False)."""

        def choose(key: str) -> bool | None:
            if key in ("\r", "\n", " "):
                return True
            if key in ("F", "Q"):
                return False
            return None

        r = self._wait_for_key(
            f"SET THE ADJUSTMENT TO {to_text}",
            "SET THE OVEN'S THERMOSTAT ADJUSTMENT TO",
            [to_text],
            done,
            "[bold]Enter[/] it's set   [bold]F[/] stop here (carry on later with --resume)",
            choose,
        )
        return bool(r) if r is not None else False

    def ask_turn_off(self, done: str | None) -> None:
        """At the end of a session: asks for the oven to be turned off, reminding until Enter."""
        self._wait_for_key(
            "TURN THE OVEN OFF", "TURN THE OVEN", ["OFF"], done, "[bold]Enter[/] it's off", lambda k: True if k in ("\r", "\n", " ") else None
        )

    def _wait_for_key(
        self, note: str, heading: str, big: list[str], done: str | None, keys: str, choose: Callable[[str], T | None]
    ) -> T | None:
        from ..ui import console as ui
        from ..ui.keys import KEYS, upper

        started = datetime.now(timezone.utc)
        reminded = 0
        choice: T | None = None
        token = self._token
        # The notes take the recorder's clock (a simulation's own); the waiting is timed on the PC's.
        self._recorder.note(self._recorder.now, f"prompt:{note}")
        console = ui.get()

        def since() -> float:
            return (datetime.now(timezone.utc) - started).total_seconds()

        def render() -> Any:
            return view(heading, big, done, keys, since(), self._remind_every, self._f, self._poller.last_celsius, self._poller.battery_percent)

        def remind() -> bool:
            nonlocal reminded
            due = reminders_due(since(), self._remind_every)
            if due > reminded:
                reminded = due
                self._poller.enqueue(lambda m: m.beep())
                ui.bell()
                return True
            return False

        with KEYS:
            if not ui.live():
                # Output to a file or pipe, with a keyboard still there: say it in plain lines, and wait.
                from ..ui.widgets import plain_print

                if done is not None:
                    plain_print(console, done)
                console.print(f"[bold]>>> {ui.escape(heading)} {ui.escape(' '.join(big))}[/]")
                console.print(keys)
                while not token.is_cancellation_requested and choice is None:
                    while KEYS.available() and choice is None:
                        choice = choose(upper(KEYS.read()))
                    if choice is not None:
                        break
                    if remind():
                        console.print(f"[yellow]Still waiting ({ui.escape(waited(since()))}): {ui.escape(heading)} {ui.escape(' '.join(big))}[/]")
                    token.wait(0.25)
            else:
                from rich.live import Live

                ui.clear_screen()
                with Live(render(), console=console, auto_refresh=False, transient=False, redirect_stdout=False, redirect_stderr=False) as live:
                    while not token.is_cancellation_requested and choice is None:
                        while KEYS.available() and choice is None:
                            choice = choose(upper(KEYS.read()))
                        if choice is not None:
                            break
                        remind()
                        live.update(render(), refresh=True)
                        token.wait(0.25)
                    live.update(render(), refresh=True)
        self._recorder.note(self._recorder.now, f"answered:{_answer(choice)}:{fixed(since(), 0)}s")
        return choice
