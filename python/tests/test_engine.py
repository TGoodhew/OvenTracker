"""Whole simulated sessions, as the app runs them without a terminal (output to a pipe), like the C# app's
NoTerminalTests: they write all four files, show progress lines, and never move the cursor or redraw the screen.
Needs Rich."""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
from pathlib import Path

import pytest

from conftest import SRC

pytest.importorskip("rich")

#: Any escape sequence that isn't a colour (SGR, which ends in "m"): cursor movement, erasing, the screen,
#: hyperlinks and the like. Case-sensitive: "m" only.
CONTROL = re.compile(r"\x1b(?!\[[0-9;]*m)")


def run_app(*args: str, extra_env: dict[str, str] | None = None, timeout: float = 300) -> subprocess.CompletedProcess[str]:
    env = dict(os.environ)
    env["PYTHONPATH"] = str(SRC) + (os.pathsep + env["PYTHONPATH"] if env.get("PYTHONPATH") else "")
    env["PYTHONIOENCODING"] = "utf-8"
    env["COLUMNS"] = "160"
    env.pop("FORCE_COLOR", None)
    env.update(extra_env or {})
    return subprocess.run(
        [sys.executable, "-m", "oventracker", *args],
        stdin=subprocess.DEVNULL,
        capture_output=True,
        text=True,
        encoding="utf-8",
        env=env,
        timeout=timeout,
    )


@pytest.mark.parametrize("colour", [False, True])
def test_a_whole_session_runs_without_a_terminal_and_writes_the_report(tmp_path: Path, colour: bool):
    out = tmp_path / "run"
    p = run_app(
        "--simulate", "--speed", "3000", "--interval", "10", "-y", "--unit", "F", "--modes", "bake", "--setpoints", "350", "--out", str(out),
        extra_env={"FORCE_COLOR": "1"} if colour else None,
    )
    output = p.stdout + p.stderr
    assert p.returncode == 0, output
    for name in ("report.md", "results.json", "readings.csv", "session.json"):
        assert (out / name).is_file(), f"no {name}"
    plain = re.sub(r"\x1b\[[0-9;]*m", "", output)
    progress = [line for line in plain.splitlines() if "  ·  " in line and "°F" in line]
    assert len(progress) >= 5, f"only {len(progress)} progress lines:\n{plain}"
    assert "Measuring" in plain
    assert "Saved to" in plain
    assert not CONTROL.search(output), "a cursor or screen control code in a log"
    if colour:
        assert "\x1b[" in output  # the colours are still there
    else:
        assert "\x1b" not in output
    results = json.loads((out / "results.json").read_text(encoding="utf-8"))
    assert results["results"][0]["metrics"]["reason"] == "Converged"
    assert results["session"]["driver"] == "simulated"


def test_without_a_terminal_it_says_what_it_cant_ask():
    p = run_app("--simulate", "-y", "--unit", "F")
    assert p.returncode == 1
    assert "can't ask" in p.stdout
    assert "--modes, --setpoints" in p.stdout


def test_a_characterise_session_measures_the_gain(tmp_path: Path):
    out = tmp_path / "char"
    p = run_app("characterise", "--simulate", "-y", "--speed", "3000", "--interval", "10", "--unit", "F", "--modes", "bake", "--setpoints", "325,425", "--out", str(out))
    assert p.returncode == 0, p.stdout + p.stderr
    for name in ("report.md", "results.json", "readings.csv", "session.json", "characterisation.md", "characterisation.json"):
        assert (out / name).is_file(), f"no {name}"
    assert "── Characterisation " in p.stdout
    assert "A simulation: the gains aren't saved to any oven's profile." in p.stdout
    session = json.loads((out / "session.json").read_text(encoding="utf-8"))
    assert [pt["index"] for pt in session["plan"]] == [1, 2, 3, 4]  # phase 2: the highest, then the lowest again
    assert session["characterise"]["changedToC"] == session["adjustmentC"]
    csv = (out / "readings.csv").read_text(encoding="utf-8")
    assert ",adjustment-changed:" in csv


def test_a_resumed_run_waits_for_a_hot_oven_and_finishes_the_rest(tmp_path: Path):
    """A run stopped after its first setpoint (made here by recording one setpoint), then resumed with the oven
    hot: with --yes it waits for the oven to cool by itself, marks the next setpoint after a reset, and leaves the
    first alone."""
    out = tmp_path / "resume"
    first = run_app("--simulate", "-y", "--speed", "3000", "--interval", "10", "--unit", "F", "--modes", "bake", "--setpoints", "350,400",
                    "--max-minutes", "10", "--out", str(out))
    assert first.returncode == 0, first.stdout + first.stderr
    # Both setpoints stopped at the 10-minute limit (TimedOut): done, so nothing would be left. Make the second
    # look interrupted instead, as if the app had been closed a minute into it.
    csv = out / "readings.csv"
    lines = csv.read_text(encoding="utf-8").splitlines(keepends=True)
    start = next(i for i, line in enumerate(lines) if ",2,Bake," in line and line.rstrip().endswith(",start"))
    csv.write_text("".join(lines[: start + 7]), encoding="utf-8", newline="")
    p = run_app("run", "--simulate", "-y", "--speed", "3000", "--interval", "10", "--sim-hot", "450", "--resume", str(out))
    assert p.returncode == 0, p.stdout + p.stderr
    assert "Carrying on" in p.stdout and "1 of 2 setpoints done." in p.stdout
    assert "Oven reset: waiting until the oven is below" in p.stdout
    events = [line.rstrip().split(",")[8] for line in csv.read_text(encoding="utf-8").splitlines()[1:] if ",Event," in line]
    assert "resume" in events and "reset-start" in events and "reset-end:Ready" in events and "reset" in events
    results = json.loads((out / "results.json").read_text(encoding="utf-8"))["results"]
    assert [r["point"]["index"] for r in results] == [1, 2]
    assert [r["afterReset"] for r in results] == [False, True]
    session = json.loads((out / "session.json").read_text(encoding="utf-8"))
    assert len(session["sittings"]) == 2


def test_a_meter_the_python_version_doesnt_have_yet_says_so():
    p = run_app("run", "--meter", "34401a", "-y", "--unit", "F", "--modes", "bake", "--setpoints", "350")
    assert p.returncode == 1
    assert "isn't in the Python version yet" in p.stdout


@pytest.mark.parametrize(
    "args,message",
    [
        (["run", "--unit", "X"], "Error: --unit must be F or C."),
        (["run", "--speed", "abc"], "Error: Failed to convert 'abc' to Double."),
        (["run", "--simulate", "--meter", "u1253b"], "Error: --simulate can't be used with another --meter."),
        (["analyze"], "Error: Command 'analyze' is missing required argument 'FOLDERS'."),
        (["frob"], "Error: Unknown command 'frob'."),
    ],
)
def test_mistakes_are_errors_as_in_the_c_sharp_app(args, message):
    p = run_app(*args)
    assert p.returncode != 0
    assert message in p.stdout


def test_the_help_and_the_other_commands():
    top = run_app("--help")
    assert top.returncode == 0
    for text in ("USAGE:", "COMMANDS:", "characterise", "--remind <MINUTES>", "DEFAULT"):
        assert text in top.stdout
    assert "--sim-hot" not in top.stdout and "--port" not in top.stdout  # hidden, as in the C# app
    run = run_app("run", "--help")
    assert "EXAMPLES:" in run.stdout and "run --simulate --speed 120" in run.stdout
    assert run_app("--version").stdout.strip()
    ovens = run_app("ovens")
    assert ovens.returncode == 0 and "ge-phs925" in ovens.stdout
    show = run_app("ovens", "--show", "ge-phs925", "--unit", "C")
    assert show.returncode == 0 and "±19 °C in 1 °C steps" in show.stdout
    meters = run_app("ports", "--meter", "simulated")  # the alias
    assert meters.returncode == 0 and "Simulated oven and meter" in meters.stdout
