"""A cancellation token, as the C# app's CancellationToken: the first Ctrl+C cancels it (Program.cs)."""

from __future__ import annotations

import threading
import time


class CancellationToken:
    def __init__(self) -> None:
        self._event = threading.Event()

    @property
    def is_cancellation_requested(self) -> bool:
        return self._event.is_set()

    def cancel(self) -> None:
        self._event.set()

    def wait(self, seconds: float) -> bool:
        """Task.Delay(seconds, ct), without the exception: waits, or returns early (True) once cancelled. Short
        waits sleep in small steps, which is far more precise than an event's timeout on Windows."""
        end = time.perf_counter() + max(0.0, seconds)
        while True:
            if self._event.is_set():
                return True
            left = end - time.perf_counter()
            if left <= 0:
                return False
            time.sleep(min(left, 0.05))


NONE = CancellationToken()
