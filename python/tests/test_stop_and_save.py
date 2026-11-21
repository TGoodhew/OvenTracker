"""Stopping while the meter is busy, and saving each setpoint as it finishes (#43), as the C# app's
MeterPollerStopTests and NoTerminalTests.EachSetpointIsSavedAsItFinishesNotOnlyAtTheEnd."""

from __future__ import annotations

import json
import os
import subprocess
import sys
import threading
import time
from pathlib import Path

import pytest

from conftest import SRC
from oventracker.meter.base import TemperatureMeter
from oventracker.session.meter_poller import LinkState, MeterPoller


class StuckMeter(TemperatureMeter):
    """A meter whose read blocks (as a Bluetooth write does, for its whole timeout), then fails."""

    def __init__(self) -> None:
        self.reading = threading.Event()
        self.release = threading.Event()

    @property
    def location(self) -> str:
        return "stuck"

    def connect(self):  # type: ignore[override]
        return None

    def disconnect(self) -> None:
        pass

    def read(self):  # type: ignore[override]
        self.reading.set()
        self.release.wait(10)
        raise TimeoutError("The write timed out.")


class BrokenMeter(TemperatureMeter):
    @property
    def location(self) -> str:
        return "broken"

    def connect(self):  # type: ignore[override]
        return None

    def disconnect(self) -> None:
        raise RuntimeError("can't even disconnect")

    def read(self):  # type: ignore[override]
        raise OSError("the link is gone")


def test_a_meter_that_fails_after_stop_ends_the_poller_quietly():
    meter = StuckMeter()
    poller = MeterPoller(meter, 1, connected=True)
    poller.start()
    assert meter.reading.wait(5), "the poller never read"
    stopper = threading.Thread(target=poller.stop)
    stopper.start()
    time.sleep(0.1)
    meter.release.set()
    stopper.join(10)
    assert not stopper.is_alive(), "stop didn't return"
    assert poller.state == LinkState.Stopped


def test_a_meter_that_fails_every_way_still_lets_the_poller_stop():
    poller = MeterPoller(BrokenMeter(), 1, speed=100, connected=True)
    poller.start()
    time.sleep(0.2)
    poller.stop()
    assert poller.state == LinkState.Stopped
    assert poller.last_error is not None


def _read(path: Path) -> str | None:
    try:
        return path.read_text(encoding="utf-8")
    except OSError:
        return None


def test_each_setpoint_is_saved_as_it_finishes_not_only_at_the_end(tmp_path: Path):
    pytest.importorskip("rich")
    out = tmp_path / "run"
    env = dict(os.environ)
    env["PYTHONPATH"] = str(SRC) + (os.pathsep + env["PYTHONPATH"] if env.get("PYTHONPATH") else "")
    env["PYTHONIOENCODING"] = "utf-8"
    session = subprocess.Popen(
        [sys.executable, "-m", "oventracker", "run", "--simulate", "--speed", "3000", "--interval", "10", "-y",
         "--unit", "F", "--modes", "bake", "--setpoints", "350,400", "--out", str(out)],
        stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, env=env,
    )
    seen = None
    try:
        while session.poll() is None and seen is None:
            time.sleep(0.005)
            csv = _read(out / "readings.csv")
            if csv is None or ",2,Bake," not in csv or "end:" in csv[csv.index(",2,Bake,"):]:
                continue
            seen = _read(out / "results.json")
    finally:
        _, err = session.communicate(timeout=300)
    assert session.returncode == 0, err
    assert seen is not None, "the second setpoint was never seen in progress"
    saved = json.loads(seen)["results"]
    assert [r["point"]["index"] for r in saved] == [1]
    assert (out / "report.md").exists()
