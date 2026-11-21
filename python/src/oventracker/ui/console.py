"""The app's console (AnsiConsole and Ui/ConsoleCaps.cs): one Rich console, with Spectre's colours.

Spectre's colour names are mapped to the same palette entries, so ``[green]``, ``[grey]``, ``[yellow]`` and the
rest look as they do in the C# app (Spectre's "grey" is colour 8, "yellow" 11, "red" 9, "cyan" 14). Markup in this
package uses those names, and compound styles are written as nested tags (``[bold][red]…[/][/]``) so the theme
applies to them too."""

from __future__ import annotations

import os
import sys
from typing import Any

from rich.console import Console
from rich.markup import escape as _escape
from rich.theme import Theme

#: Spectre.Console's colours, by the names the C# app uses.
THEME = Theme(
    {
        "grey": "color(8)",
        "green": "color(2)",
        "red": "color(9)",
        "yellow": "color(11)",
        "cyan": "color(14)",
        "blue": "color(12)",
        "silver": "color(7)",
        "orange1": "color(214)",
        "grey35": "color(240)",
        "grey30": "color(239)",
        "deepskyblue1": "color(39)",
        # Spectre's prompts: choices in blue, the default in green.
        "prompt.choices": "color(12)",
        "prompt.default": "color(2)",
        "prompt.invalid": "color(9)",
    },
    inherit=True,
)

_console: Console | None = None


def make(file: Any = None, width: int | None = None, force_terminal: bool | None = None, color_system: str | None = "auto") -> Console:
    """A console with the app's settings: no automatic highlighting or emoji, as Spectre has none. Output that
    isn't a console window never takes Rich's legacy-Windows path (it would draw boxes as square ones)."""
    out = file if file is not None else sys.stdout
    legacy = None if (file is None and _isatty(out) and sys.platform == "win32") else False
    return Console(
        file=file,
        width=width,
        force_terminal=force_terminal,
        color_system=color_system,  # type: ignore[arg-type]
        highlight=False,
        emoji=False,
        theme=THEME,
        soft_wrap=False,
        legacy_windows=legacy,
    )


def get() -> Console:
    """AnsiConsole.Console: the console everything is written to."""
    global _console
    if _console is None:
        _console = make()
    return _console


def use(console: Console | None) -> Console | None:
    """Swaps the console (tests write to their own); returns the one it replaces."""
    global _console
    before = _console
    _console = console
    return before


def escape(text: str) -> str:
    """Markup.Escape."""
    return _escape(text)


#: Tests can force the answers: None asks the system.
FORCE_LIVE: bool | None = None
FORCE_KEYS: bool | None = None


def _windows_console(stream: Any) -> bool:
    """On Windows, isatty() is true for the NUL device too; .NET's IsInputRedirected and IsOutputRedirected ask
    whether the handle is a console, so this does the same."""
    try:
        import ctypes
        import msvcrt

        handle = msvcrt.get_osfhandle(stream.fileno())
        mode = ctypes.c_uint32()
        return bool(ctypes.windll.kernel32.GetConsoleMode(handle, ctypes.byref(mode)))  # type: ignore[attr-defined]
    except Exception:
        return False


def _isatty(stream: Any) -> bool:
    try:
        if stream is None or not stream.isatty():
            return False
    except Exception:
        return False
    return _windows_console(stream) if sys.platform == "win32" else True


def live() -> bool:
    """ConsoleCaps.Live: the live displays and prompts need a terminal, not output to a file or a pipe, and not
    a console that can't move the cursor. Otherwise the app writes plain progress lines."""
    if FORCE_LIVE is not None:
        return FORCE_LIVE
    c = get()
    return c.file is sys.stdout and _isatty(sys.stdout) and c.is_terminal and not c.is_dumb_terminal


def keys() -> bool:
    """ConsoleCaps.Keys: whether keys can be read, from a keyboard rather than a file or a pipe."""
    if FORCE_KEYS is not None:
        return FORCE_KEYS
    return _isatty(sys.stdin)


def bell() -> None:
    """Rings the PC's bell, where there's a terminal to ring it (a bell in a log file helps nobody)."""
    out = sys.__stdout__
    if FORCE_LIVE is False or not _isatty(out):
        return
    try:
        out.write("\a")
        out.flush()
    except Exception:
        pass


def clear_screen() -> None:
    """The screen only (cursor home, erase display): earlier output stays in the scrollback."""
    c = get()
    if live():
        c.file.write("\x1b[H\x1b[2J")
        c.file.flush()


def width(default: int = 120) -> int:
    w = get().width
    return w if w > 0 else default


def height(default: int = 40) -> int:
    h = get().height
    return h if h > 0 else default


def no_color() -> bool:
    return "NO_COLOR" in os.environ
