"""Spectre.Console's prompts, with the same choices and defaults (SelectionPrompt, MultiSelectionPrompt, TextPrompt
and ConfirmationPrompt).

The selection prompts are drawn as Spectre draws them (a title, a blank line, then the choices with ">" at the
one highlighted in blue) and answered with the arrow keys, space and Enter. Without a keyboard they fall back to a
typed answer. Text prompts are Rich's console.input, worded and coloured as Spectre's: "Title (default): "."""

from __future__ import annotations

from typing import Callable, Sequence, TypeVar

from rich.live import Live
from rich.table import Table
from rich.text import Text

from . import console as ui
from .keys import KEYS
from .widgets import markup, rows

T = TypeVar("T")


class PromptCancelled(Exception):
    """Ctrl+C at a prompt: the C# app's prompts throw OperationCanceledException."""


#: True while a typed answer is awaited: Ctrl+C then interrupts the input at once (cli's handler raises).
BLOCKING = False


def _input(shown: str) -> str:
    global BLOCKING
    BLOCKING = True
    try:
        return ui.get().input(shown)
    finally:
        BLOCKING = False


def _check(token: object | None) -> None:
    if token is not None and getattr(token, "is_cancellation_requested", False):
        raise PromptCancelled()


def _list_view(title: str, labels: list[str], cursor: int, selected: list[bool] | None) -> object:
    g = Table.grid(padding=(0, 1, 0, 0), pad_edge=False)
    g.add_column(no_wrap=True)
    g.add_row("")
    for i, label in enumerate(labels):
        arrow = ">" if i == cursor else " "
        box = "" if selected is None else ("[X] " if selected[i] else "[ ] ")
        style = "blue" if i == cursor else ""
        g.add_row(Text(f"{arrow} {box}{label}", style=style))
    parts: list[object] = [markup(title), g]
    if selected is not None:
        parts.append(markup("[grey](Press [blue]<space>[/] to toggle a choice, [green]<enter>[/] to accept)[/]"))
    return rows(*parts)  # type: ignore[arg-type]


def _choose(title: str, labels: list[str], selected: list[bool] | None, token: object | None) -> int:
    cursor = 0
    console = ui.get()
    with KEYS, Live(_list_view(title, labels, cursor, selected), console=console, auto_refresh=False, transient=True) as live:
        while True:
            _check(token)
            if not KEYS.available():
                if token is not None:
                    token.wait(0.05)  # type: ignore[attr-defined]
                else:
                    import time

                    time.sleep(0.05)
                continue
            key = KEYS.read()
            if key in ("UP", "k", "K"):
                cursor = (cursor - 1) % len(labels)
            elif key in ("DOWN", "j", "J"):
                cursor = (cursor + 1) % len(labels)
            elif key == " " and selected is not None:
                selected[cursor] = not selected[cursor]
            elif key == "\r":
                return cursor
            live.update(_list_view(title, labels, cursor, selected), refresh=True)


def _typed_choice(title: str, labels: list[str], token: object | None) -> int:
    """Without a keyboard to steer with: the choices numbered, and a number or a name typed."""
    console = ui.get()
    console.print(title)
    for i, label in enumerate(labels, 1):
        console.print(Text(f"  {i}. {label}"))
    while True:
        answer = text(f"Choose 1–{len(labels)}", default="1", token=token).strip()
        for i, label in enumerate(labels):
            if answer == str(i + 1) or answer.lower() == label.lower():
                return i
        console.print("[red]Please choose one of the numbers.[/]")


def selection(title: str, choices: Sequence[T], converter: Callable[[T], str] = str, token: object | None = None) -> T:
    """SelectionPrompt: one of the choices."""
    labels = [converter(c) for c in choices]
    if ui.live() and ui.keys():
        return choices[_choose(title, labels, None, token)]
    return choices[_typed_choice(title, labels, token)]


def multi_selection(
    title: str, choices: Sequence[T], selected: Sequence[T], converter: Callable[[T], str] = str, token: object | None = None
) -> list[T]:
    """MultiSelectionPrompt: some of the choices (those in ``selected`` to start with). Spectre requires one."""
    labels = [converter(c) for c in choices]
    marks = [c in selected for c in choices]
    if ui.live() and ui.keys():
        while True:
            _choose(title, labels, marks, token)
            if any(marks):
                return [c for c, m in zip(choices, marks) if m]
    console = ui.get()
    default = ",".join(str(i + 1) for i, m in enumerate(marks) if m)
    console.print(title)
    for i, label in enumerate(labels, 1):
        console.print(Text(f"  {i}. {label}"))
    while True:
        answer = text("Choose (numbers, separated by commas)", default=default, token=token)
        picked = []
        for part in answer.replace(" ", ",").split(","):
            if part.strip().isdigit() and 1 <= int(part) <= len(choices):
                picked.append(choices[int(part) - 1])
        if picked:
            return [c for c in choices if c in picked]
        console.print("[red]Please choose at least one.[/]")


def _echo(shown: str, value: str) -> None:
    """Spectre writes the default after the prompt when Enter is pressed alone: back up to the prompt's line and
    write it there (only in a terminal, and only when the prompt fitted on one line)."""
    console = ui.get()
    if not ui.live():
        return
    width = Text.from_markup(shown).cell_len
    if width >= console.width:
        return
    console.file.write(f"\x1b[1A\x1b[{width + 1}G{value}\n")
    console.file.flush()


def text(
    prompt: str,
    default: str | None = None,
    allow_empty: bool = False,
    validate: Callable[[str], str | None] | None = None,
    token: object | None = None,
) -> str:
    """TextPrompt<string>: the prompt (markup), the default in green; ``validate`` returns an error message or None.
    An empty answer gives the default, or (``allow_empty``) the empty string."""
    console = ui.get()
    suffix = f" [prompt.default]({ui.escape(default)})[/]" if default is not None else ""
    shown = prompt.strip() + suffix
    shown = shown.strip() + (":" if default is not None else "") + " "
    while True:
        _check(token)
        try:
            answer = _input(shown)
        except (KeyboardInterrupt, EOFError) as ex:
            console.print()  # end the prompt's line
            raise PromptCancelled() from ex
        _check(token)
        if answer == "" and default is not None:
            _echo(shown, default)
            answer = default
        elif answer.strip() == "" and not allow_empty:
            continue
        if validate is not None:
            error = validate(answer)
            if error is not None:
                console.print(f"[red]{ui.escape(error)}[/]")
                continue
        return answer


def confirm(prompt: str, default: bool = True, token: object | None = None) -> bool:
    """ConfirmationPrompt: "Ready to start? [y/n] (y): "."""
    console = ui.get()
    shown = f"{prompt} [prompt.choices]\\[y/n][/] [prompt.default]({'y' if default else 'n'})[/]: "
    while True:
        _check(token)
        try:
            answer = _input(shown).strip().lower()
        except (KeyboardInterrupt, EOFError) as ex:
            console.print()  # end the prompt's line
            raise PromptCancelled() from ex
        _check(token)
        if answer == "":
            _echo(shown, "y" if default else "n")
            return default
        if answer in ("y", "n"):
            return answer == "y"
        console.print("[red]Please select one of the available options[/]")
