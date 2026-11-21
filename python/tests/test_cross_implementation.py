"""The two implementations agree on runs the Python app records.

- Python's analyze of a Python-recorded simulated run reproduces that run's own results.json and report.md, as
  the C# app's analyze does for its runs. The one difference is by design: a run's results use the plan's
  setpoint, and analyze the one in readings.csv, written with three decimals (spec/recording.md), so in °F the
  setpoint and what is measured against it differ in the fourth decimal. In °C, with whole setpoints, nothing
  differs at all.
- When the .NET SDK is installed, the C# app's analyze of the same run gives the same results as Python's
  (testdata/tools/compare.py), and the same console report. Skipped without dotnet.

testdata/cases/py-sim-bake-convection-f also replays a Python-recorded run against the C# app's expected output in
both implementations' shared-test-data tests. Needs Rich (the app records the runs)."""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

from conftest import REPO, SRC, load_compare

pytest.importorskip("rich")


def _env() -> dict[str, str]:
    env = dict(os.environ)
    env["PYTHONPATH"] = str(SRC) + (os.pathsep + env["PYTHONPATH"] if env.get("PYTHONPATH") else "")
    env["PYTHONIOENCODING"] = "utf-8"
    env["NO_COLOR"] = "1"
    env.pop("FORCE_COLOR", None)
    env.pop("COLUMNS", None)
    return env


def python_app(*args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, "-m", "oventracker", *args], stdin=subprocess.DEVNULL, capture_output=True, text=True, encoding="utf-8", env=_env(), timeout=600
    )


@pytest.fixture(scope="module")
def runs(tmp_path_factory) -> dict[str, Path]:
    """Two runs recorded by the Python app: bake and convection at 350 °F, and bake at 180 °C."""
    base = tmp_path_factory.mktemp("python-runs")
    made = {}
    for name, args in {
        "f": ["--unit", "F", "--modes", "bake,convection", "--setpoints", "350"],
        "c": ["--unit", "C", "--modes", "bake", "--setpoints", "180"],
    }.items():
        out = base / name
        p = python_app("run", "--simulate", "-y", "--speed", "3000", "--interval", "5", *args, "--out", str(out))
        assert p.returncode == 0, p.stdout + p.stderr
        made[name] = out
    return made


def _as_pair(tmp: Path, run: Path, analysed: Path) -> tuple[Path, Path]:
    """The run's own results and report, and analyze's, under the same names, for compare_dirs."""
    a, b = tmp / "run", tmp / "analysed"
    a.mkdir()
    b.mkdir()
    shutil.copy(run / "results.json", a / "results.json")
    shutil.copy(run / "report.md", a / "report.md")
    shutil.copy(analysed / "results-reanalysed.json", b / "results.json")
    shutil.copy(analysed / "report-reanalysed.md", b / "report.md")
    return a, b


def test_in_celsius_python_analyze_reproduces_the_runs_own_results_and_report(runs, tmp_path):
    analysed = tmp_path / "out"
    p = python_app("analyze", str(runs["c"]), "--out", str(analysed))
    assert p.returncode == 0, p.stdout + p.stderr
    diffs = load_compare().compare_dirs(*map(str, _as_pair(tmp_path, runs["c"], analysed)))
    assert not diffs, "\n".join(diffs)


def test_in_fahrenheit_only_the_three_decimal_setpoint_differs(runs, tmp_path):
    compare = load_compare()
    analysed = tmp_path / "out"
    p = python_app("analyze", str(runs["f"]), "--out", str(analysed))
    assert p.returncode == 0, p.stdout + p.stderr
    run_dir, analysed_dir = _as_pair(tmp_path, runs["f"], analysed)
    expected = json.loads((run_dir / "results.json").read_text(encoding="utf-8"))
    actual = json.loads((analysed_dir / "results.json").read_text(encoding="utf-8"))
    allowed = ("metrics.setpointC", "metrics.offsetC", "metrics.overshootC")
    for diff in compare.compare_json(expected, actual):
        path = diff.split(":")[0]
        assert path.endswith(allowed), diff
    for e, a in zip(expected["results"], actual["results"]):
        for key in ("setpointC", "offsetC", "overshootC"):
            if key in e["metrics"]:
                assert abs(e["metrics"][key] - a["metrics"][key]) < 5e-4
    # The report shows one decimal at most, so it is the same.
    assert not compare.compare_text((run_dir / "report.md").read_text(encoding="utf-8"), (analysed_dir / "report.md").read_text(encoding="utf-8"))


@pytest.fixture(scope="module")
def csharp(tmp_path_factory) -> list[str]:
    """The C# app, built from src/ into a temporary folder: the command to run it. Skips without dotnet."""
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        pytest.skip("the .NET SDK isn't installed")
    out = tmp_path_factory.mktemp("csharp")
    p = subprocess.run(
        [dotnet, "build", str(REPO / "src" / "OvenTracker"), "-c", "Release", "-o", str(out), "--nologo", "-v", "q"],
        capture_output=True,
        text=True,
        timeout=900,
    )
    if p.returncode != 0:
        pytest.skip(f"the C# app didn't build: {p.stdout[-2000:]}")
    return [dotnet, str(out / "OvenTracker.dll")]


def csharp_app(command: list[str], *args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run([*command, *args], stdin=subprocess.DEVNULL, capture_output=True, text=True, encoding="utf-8", env=_env(), timeout=600)


@pytest.mark.parametrize("unit", ["f", "c"])
def test_the_c_sharp_app_analyses_a_python_recorded_run_the_same(runs, csharp, tmp_path, unit):
    py_out, cs_out = tmp_path / "python", tmp_path / "csharp"
    p = python_app("analyze", str(runs[unit]), "--out", str(py_out))
    assert p.returncode == 0, p.stdout + p.stderr
    c = csharp_app(csharp, "analyze", str(runs[unit]), "--out", str(cs_out))
    assert c.returncode == 0, c.stdout + c.stderr
    diffs = load_compare().compare_dirs(str(cs_out), str(py_out))
    assert not diffs, "\n".join(diffs)
    # And the console shows the same tables and findings, line for line, up to what was written where.
    def report(text: str) -> list[str]:
        lines = text.replace("\r\n", "\n").split("\n")
        return lines[: next(i for i, line in enumerate(lines) if line.startswith("Wrote "))]

    assert report(p.stdout) == report(c.stdout)


@pytest.mark.parametrize("run", ["sim-bake-convection-f", "sim-characterise", "real-2026-11-20-1118", "sim-started-warm"])
def test_the_console_report_is_the_c_sharp_apps(csharp, tmp_path, run):
    folder = REPO / "testdata" / "runs" / run
    p = python_app("analyze", str(folder), "--out", str(tmp_path / "python"))
    c = csharp_app(csharp, "analyze", str(folder), "--out", str(tmp_path / "csharp"))
    assert p.returncode == 0 and c.returncode == 0, p.stdout + p.stderr + c.stdout + c.stderr

    def before_wrote(text: str) -> list[str]:
        lines = text.replace("\r\n", "\n").split("\n")
        return lines[: next(i for i, line in enumerate(lines) if line.startswith("Wrote "))]

    assert before_wrote(p.stdout) == before_wrote(c.stdout)
