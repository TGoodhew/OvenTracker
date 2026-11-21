"""The two implementations agree on runs the Python app records.

- Python's analyze of a Python-recorded simulated run reproduces that run's own results.json and report.md
  exactly, in °F as in °C, as the C# app's analyze does for its runs: readings.csv keeps full-precision numbers
  (spec/recording.md), and analyze takes the setpoints from the plan, so nothing differs.
- The recording's events are in time order, on the simulator's clock like its readings.
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
    # Spectre.Console recognises CI services by these and then writes ANSI codes and wraps differently even when
    # redirected; Rich doesn't. Both apps should see the same plain, redirected console.
    for name in _CI_VARIABLES:
        env.pop(name, None)
    return env


#: The variables Spectre.Console's profile enrichers look for (GitHub Actions, Azure Pipelines, GitLab, …).
_CI_VARIABLES = (
    "GITHUB_ACTIONS", "TF_BUILD", "CI_SERVER", "GITLAB_CI", "APPVEYOR", "TRAVIS", "JENKINS_URL", "TEAMCITY_VERSION",
    "BITBUCKET_COMMIT", "BITBUCKET_REPO_OWNER", "BITBUCKET_REPO_SLUG", "BITRISE_BUILD_URL", "GO_SERVER_URL",
    "bamboo_buildNumber", "ContinuaCI.Version", "BuildRunner",
)


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


@pytest.mark.parametrize("unit", ["f", "c"])
def test_python_analyze_reproduces_the_runs_own_results_and_report(runs, tmp_path, unit):
    analysed = tmp_path / "out"
    p = python_app("analyze", str(runs[unit]), "--out", str(analysed))
    assert p.returncode == 0, p.stdout + p.stderr
    diffs = load_compare().compare_dirs(*map(str, _as_pair(tmp_path, runs[unit], analysed)))
    assert not diffs, "\n".join(diffs)


@pytest.mark.parametrize("unit", ["f", "c"])
def test_the_recording_keeps_full_precision_and_time_order(runs, unit):
    lines = (runs[unit] / "readings.csv").read_text(encoding="utf-8").splitlines()[1:]
    rows = [line.split(",") for line in lines]
    assert all(len(r) == 9 for r in rows)
    # Events and readings on one clock: the seven-digit UTC times sort as text.
    times = [r[0] for r in rows]
    assert times == sorted(times)
    # Every number reads back exactly: a °F setpoint is the plan's 176.66666666666666, not 176.667.
    plan = json.loads((runs[unit] / "session.json").read_text(encoding="utf-8"))["plan"]
    setpoints = {p["setpointC"] for p in plan}
    assert {float(r[3]) for r in rows if r[3]} == setpoints
    # Round-trip text, not a fixed number of decimals: 5 is "5", never "5.0".
    for r in rows:
        for text in (r[3], r[4], r[6]):
            assert not text.endswith(".0"), r


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


@pytest.mark.parametrize("meter_options", [{"cj": "22.5 °C", "nplc": "100"}, {}])
def test_analyze_writes_the_meter_options_as_the_c_sharp_app_does(csharp, tmp_path, meter_options):
    """session.json's meterOptions (the 34401A's cold junction) goes through analyze into results.json as it does in
    the C# app: the session's last property, written the same way, or left out with none."""
    run = tmp_path / "run"
    run.mkdir()
    source = REPO / "testdata" / "runs" / "sim-bake-convection-f"
    shutil.copy(source / "readings.csv", run / "readings.csv")
    session = json.loads((source / "session.json").read_text(encoding="utf-8-sig"))
    session["meterOptions"] = meter_options
    (run / "session.json").write_text(json.dumps(session, indent=2, ensure_ascii=False), encoding="utf-8")

    py_out, cs_out = tmp_path / "python", tmp_path / "csharp"
    p = python_app("analyze", str(run), "--out", str(py_out))
    assert p.returncode == 0, p.stdout + p.stderr
    c = csharp_app(csharp, "analyze", str(run), "--out", str(cs_out))
    assert c.returncode == 0, c.stdout + c.stderr
    assert not load_compare().compare_dirs(str(cs_out), str(py_out))

    def tail(folder: Path) -> str:
        """The session's text from its last property before meterOptions to the end of the session."""
        text = (folder / "results-reanalysed.json").read_text(encoding="utf-8")
        end = text.index('"results": [')
        start = text.rindex('"characterise"' if '"characterise"' in text[:end] else '"oven"', 0, end)
        return text[start:end]

    assert tail(py_out) == tail(cs_out)
    py_session = json.loads((py_out / "results-reanalysed.json").read_text(encoding="utf-8"))["session"]
    cs_session = json.loads((cs_out / "results-reanalysed.json").read_text(encoding="utf-8"))["session"]
    assert list(py_session) == list(cs_session)
    assert list(py_session)[-1] == "meterOptions"
    assert py_session["meterOptions"] == cs_session["meterOptions"] == meter_options
    if meter_options:
        assert '"cj": "22.5 \\u00B0C"' in tail(cs_out)