"""Keys pressed during a run, without waiting for Enter (Console.KeyAvailable and Console.ReadKey(intercept: true)).

Windows: msvcrt. Elsewhere: the terminal is put in cbreak mode (no echo, no line editing, Ctrl+C still a signal)
while keys are being read, and restored afterwards, on exit, and on Ctrl+C. Keys come back as the character
typed, with Enter as "\\r" (as .NET gives it) and the arrow keys as "UP", "DOWN", "LEFT" and "RIGHT"."""

from __future__ import annotations

import atexit
import os
import sys
from typing import Any

from . import console as ui

_WINDOWS = sys.platform == "win32"


class KeyReader:
    def __init__(self) -> None:
        self._depth = 0
        self._saved: Any = None
        self._fd: int | None = None
        self._pending: list[str] = []
        self._decoder: Any = None

    # The terminal's mode ------------------------------------------------------------------------------

    def __enter__(self) -> "KeyReader":
        self.enter()
        return self

    def __exit__(self, *exc: object) -> None:
        self.exit()

    def enter(self) -> None:
        """Starts reading keys: on POSIX, cbreak mode until the matching exit()."""
        self._depth += 1
        if self._depth > 1 or _WINDOWS or not ui.keys():
            return
        try:
            import codecs
            import termios
            import tty

            fd = sys.stdin.fileno()
            self._saved = termios.tcgetattr(fd)
            self._fd = fd
            self._decoder = codecs.getincrementaldecoder("utf-8")(errors="ignore")
            tty.setcbreak(fd, termios.TCSANOW)
        except Exception:
            self._saved = None
            self._fd = None

    def exit(self) -> None:
        if self._depth == 0:
            return
        self._depth -= 1
        if self._depth == 0:
            self.restore()

    def restore(self) -> None:
        """Puts the terminal back as it was (also on exit and on Ctrl+C)."""
        if self._saved is not None and self._fd is not None:
            try:
                import termios

                termios.tcsetattr(self._fd, termios.TCSADRAIN, self._saved)
            except Exception:
                pass
        self._saved = None
        self._fd = None
        self._depth = 0

    # Reading ------------------------------------------------------------------------------------------

    def available(self) -> bool:
        """Console.KeyAvailable."""
        if self._pending:
            return True
        if not ui.keys():
            return False
        if _WINDOWS:
            import msvcrt

            return bool(msvcrt.kbhit())
        self._fill(0)
        return bool(self._pending)

    def read(self) -> str:
        """Console.ReadKey(intercept: true): the next key, waiting for one if need be."""
        while not self._pending:
            if _WINDOWS:
                self._pending.append(self._read_windows())
            else:
                self._fill(None)
        return self._pending.pop(0)

    def _read_windows(self) -> str:
        import msvcrt

        ch = msvcrt.getwch()
        if ch in ("\x00", "\xe0"):
            code = msvcrt.getwch()
            return {"H": "UP", "P": "DOWN", "K": "LEFT", "M": "RIGHT"}.get(code, "")
        return "\r" if ch == "\n" else ch

    def _fill(self, timeout: float | None) -> None:
        """Reads what the terminal has (waiting up to ``timeout``; None waits for a key) into the pending keys."""
        import select

        fd = self._fd if self._fd is not None else sys.stdin.fileno()
        try:
            ready, _, _ = select.select([fd], [], [], timeout)
        except (OSError, ValueError):
            return
        if not ready:
            return
        data = os.read(fd, 64)
        if not data:
            return
        if self._decoder is None:
            import codecs

            self._decoder = codecs.getincrementaldecoder("utf-8")(errors="ignore")
        text = self._decoder.decode(data)
        i = 0
        while i < len(text):
            ch = text[i]
            if ch == "\x1b":
                seq = text[i : i + 3]
                if len(seq) < 3:
                    # The rest of an escape sequence may still be on its way.
                    try:
                        more, _, _ = select.select([fd], [], [], 0.03)
                        if more:
                            text += self._decoder.decode(os.read(fd, 64))
                            seq = text[i : i + 3]
                    except OSError:
                        pass
                if seq in ("\x1b[A", "\x1bOA"):
                    self._pending.append("UP")
                elif seq in ("\x1b[B", "\x1bOB"):
                    self._pending.append("DOWN")
                elif seq in ("\x1b[C", "\x1bOC"):
                    self._pending.append("RIGHT")
                elif seq in ("\x1b[D", "\x1bOD"):
                    self._pending.append("LEFT")
                else:
                    self._pending.append("\x1b")
                    i += 1
                    continue
                i += 3
                continue
            self._pending.append("\r" if ch == "\n" else ch)
            i += 1


KEYS = KeyReader()
atexit.register(KEYS.restore)


def upper(key: str) -> str:
    """char.ToUpperInvariant(key.KeyChar) for a character key; named keys as they are."""
    return key.upper() if len(key) == 1 else key
