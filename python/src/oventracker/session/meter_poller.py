"""Reads the meter on its own thread, at a steady interval, and survives the link dropping (Session/MeterPoller.cs).

A Bluetooth link will drop: the poller reconnects and carries on, and the gap simply has no readings in it."""

from __future__ import annotations

import enum
import queue
import threading
import time
from datetime import datetime, timezone
from typing import Callable

from ..meter.base import MeterConfig, MeterReading, ReadingStatus, TemperatureMeter


class LinkState(enum.IntEnum):
    Connecting = 0
    Connected = 1
    Reconnecting = 2
    Stopped = 3


class _Readings:
    """The channel the readings come through: written by the poller's thread, read by the app."""

    def __init__(self) -> None:
        self._q: queue.SimpleQueue[MeterReading] = queue.SimpleQueue()

    def try_read(self) -> MeterReading | None:
        try:
            return self._q.get_nowait()
        except queue.Empty:
            return None

    def write(self, r: MeterReading) -> None:
        self._q.put(r)

    def drain(self):  # type: ignore[no-untyped-def]
        """Every reading waiting, in order."""
        while True:
            r = self.try_read()
            if r is None:
                return
            yield r


#: The shortest time between two readings of the simulator, in real seconds. The C# poller waits in whole
#: milliseconds on the system timer, so at a large speed-up (--speed 3000) it reads a few hundred times a second:
#: some 20 readings for each pass of the tracker (one each 100 ms). Python's finer sleep would read ten times as
#: many. The tracker looks at the battery and the meter's silence once a pass, after the readings, so that many
#: in one pass would let the simulated battery go from low to giving false readings before the app noticed it
#: was low, and run a pause's end into the meter coming back (#51).
MIN_SIMULATED_PERIOD = 0.004


class MeterPoller:
    def __init__(self, meter: TemperatureMeter, interval_seconds: float, speed: float = 1, connected: bool = False):
        """``speed``: real-time speed-up, for the simulator only."""
        self._meter = meter
        self._interval = interval_seconds
        self._speed = max(1.0, speed)
        self.readings = _Readings()
        self._commands: queue.SimpleQueue[Callable[[TemperatureMeter], None]] = queue.SimpleQueue()
        self._stop = threading.Event()
        self._clock0 = time.perf_counter()
        self._last_good_at: float | None = None
        self._last_link_attempt = 0.0
        self._attempts = 0
        self._attempt_at_last_good = -1
        self._attempt_at_last_link = 0
        self._thread: threading.Thread | None = None
        self.is_connected = connected
        self.state = LinkState.Connected if connected else LinkState.Connecting
        self.last_error: str | None = None
        self.last_error_at: datetime | None = None
        self.battery_percent: float | None = None
        self.config: MeterConfig | None = None
        #: The latest good temperature, in °C.
        self.last_celsius: float | None = None
        self.reconnects = 0
        self.read_count = 0
        #: How long (in the meter's time) it can be quiet, with the link up, before the poller tries reconnecting.
        self.quiet_reconnect_after = 120.0
        #: Battery is checked every this many readings (each minute at the usual interval), and on (re)connecting.
        self.battery_every = 60
        #: MIN_SIMULATED_PERIOD, or less for a test that drives the poller itself rather than through the tracker.
        self.min_simulated_period = MIN_SIMULATED_PERIOD
        self._battery_due = True
        self._low_battery_flag: bool | None = None
        #: How many times the battery has been read: a pause waits for a fresh reading before trusting it again.
        self.battery_reads = 0

    def request_battery(self) -> None:
        """Checks the battery at the next reading: after a battery swap, say."""
        self._battery_due = True

    @property
    def low_battery(self) -> bool | None:
        """Whether the meter's battery is low: its own flag where it has one (the 121GW), otherwise its level against
        battery_text.LOW_PERCENT. None for a meter that says nothing about a battery."""
        from ..ui import battery_text

        if self._low_battery_flag is not None:
            return self._low_battery_flag
        p = self.battery_percent
        return None if p is None else battery_text.is_low(p)

    def _elapsed(self) -> float:
        return time.perf_counter() - self._clock0

    def _meter_time_since(self, real_then: float, attempt_then: int) -> float:
        """Seconds passed in the meter's own terms: real time for a real meter; for the simulator, the reads since
        then times the interval, which is its clock (the timer can't keep up with a large speed-up)."""
        if self._speed > 1:
            return self._interval * (self._attempts - attempt_then)
        return self._elapsed() - real_then

    @property
    def silent_for(self) -> float:
        """Seconds since the last reading, in the meter's time. Zero until the first reading."""
        g = self._last_good_at
        return self._meter_time_since(g, self._attempt_at_last_good) if g is not None else 0.0

    def start(self) -> None:
        self._clock0 = time.perf_counter()
        self._thread = threading.Thread(target=self._loop, name="Meter poller", daemon=True)
        self._thread.start()

    def enqueue(self, command: Callable[[TemperatureMeter], None]) -> None:
        self._commands.put(command)

    def _wait(self, seconds: float) -> None:
        """WaitHandle.WaitOne(seconds), precise for the simulator's short periods and woken by stop."""
        end = time.perf_counter() + seconds
        while not self._stop.is_set():
            left = end - time.perf_counter()
            if left <= 0:
                return
            time.sleep(min(left, 0.05))

    def _loop(self) -> None:
        """The thread's body (MeterPoller.Loop). Nothing escapes it, and it always ends Stopped (#43)."""
        try:
            self._poll()
        except Exception as ex:
            self.last_error = str(ex)
            self.last_error_at = datetime.now(timezone.utc)
        finally:
            self.state = LinkState.Stopped

    def _poll(self) -> None:
        period = self._interval / self._speed
        if self._speed > 1:
            period = max(period, self.min_simulated_period)
        nxt = 0.0
        failures = 0
        meter = self._meter
        while not self._stop.is_set():
            try:
                if not self.is_connected:
                    meter.connect()
                    self._battery_due = True  # a meter back from being off may have a new battery
                    self.is_connected = True
                    self.state = LinkState.Connected
                    failures = 0
                while True:
                    try:
                        command = self._commands.get_nowait()
                    except queue.Empty:
                        break
                    try:
                        command(meter)
                    except Exception:
                        pass  # a beep that fails is not worth losing the link over
                if self._battery_due or self.read_count % self.battery_every == 0:
                    b = meter.read_battery_percent()
                    self.battery_percent = b if b is not None else self.battery_percent
                    self._low_battery_flag = meter.read_low_battery()
                    self._battery_due = False
                    self.battery_reads += 1
                self._attempts += 1
                r = meter.read()
                self.read_count += 1
                failures = 0
                self._last_good_at = self._elapsed()
                self._attempt_at_last_good = self._attempts
                if r.status != ReadingStatus.Error:
                    self.config = meter.last_config
                if r.status == ReadingStatus.Ok and r.celsius is not None:
                    self.last_celsius = r.celsius
                self.readings.write(r)
            except Exception as ex:
                if self._stop.is_set():
                    break
                failures += 1
                self.last_error = str(ex)
                self.last_error_at = datetime.now(timezone.utc)
                timeout = isinstance(ex, TimeoutError)
                # Quiet for long enough that the link may be gone after all: try it again, now and then.
                quiet_too_long = (
                    timeout
                    and self.silent_for >= self.quiet_reconnect_after
                    and self._meter_time_since(self._last_link_attempt, self._attempt_at_last_link) >= self.quiet_reconnect_after
                )
                link_down = (
                    not self.is_connected
                    or (not timeout and isinstance(ex, (OSError, RuntimeError, PermissionError)))
                    or (not timeout and failures >= 3)
                    or quiet_too_long
                )
                if link_down:
                    if self.is_connected:
                        self.reconnects += 1
                    self.is_connected = False
                    self.state = LinkState.Reconnecting
                    self._last_link_attempt = self._elapsed()
                    self._attempt_at_last_link = self._attempts
                    try:
                        meter.disconnect()
                    except Exception:
                        pass
                    # A meter that doesn't answer (switched off) is retried soon; a failing link backs off further.
                    backoff = min(2 if timeout else 10, 2 * failures)
                    self._wait(backoff / self._speed)
                    nxt = self._elapsed()
                    continue

            nxt += period
            wait = nxt - self._elapsed()
            if wait > 0:
                self._wait(wait)
            else:
                nxt = self._elapsed()  # fell behind (slow reply): do not try to catch up in a burst

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(5)

    def dispose(self) -> None:
        self.stop()

    def __enter__(self) -> "MeterPoller":
        return self

    def __exit__(self, *exc: object) -> None:
        self.stop()
