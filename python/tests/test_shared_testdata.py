"""Replays every case in testdata/cases (spec/testdata.md) and compares the output with the C# app's expected
files, using testdata/tools/compare.py."""

from __future__ import annotations

import os
import subprocess
import sys

import pytest

from conftest import SRC, TESTDATA, load_compare

CASES = sorted(p.name for p in (TESTDATA / "cases").iterdir() if p.is_dir())

# Nothing in the output may depend on the computer's time zone: times are shown in the offset they were saved
# with. The cases run in UTC, as CI does, and one is checked in other zones too. TZ works on Windows too.
TIME_ZONE = "UTC0"


def arguments(case: str) -> list[str]:
    """A case's arguments to analyze, one per line; "runs/..." is relative to testdata/."""
    lines = (TESTDATA / "cases" / case / "args.txt").read_text(encoding="utf-8").splitlines()
    args = [line.strip() for line in lines]
    args = [a for a in args if a and not a.startswith("#")]
    return [str(TESTDATA / a.replace("/", os.sep)) if a.startswith("runs/") else a for a in args]


def run_case(case: str, out, time_zone: str) -> list[str]:
    """Runs a case's analyze into ``out`` and returns its differences from the expected files."""
    env = dict(os.environ)
    env["TZ"] = time_zone
    env["PYTHONPATH"] = str(SRC) + (os.pathsep + env["PYTHONPATH"] if env.get("PYTHONPATH") else "")
    env["PYTHONIOENCODING"] = "utf-8"
    p = subprocess.run(
        [sys.executable, "-m", "oventracker", "analyze", *arguments(case), "--out", str(out)],
        capture_output=True,
        text=True,
        encoding="utf-8",
        env=env,
    )
    assert p.returncode == 0, p.stdout + p.stderr
    return load_compare().compare_dirs(str(TESTDATA / "cases" / case / "expected"), str(out))


def test_there_are_cases():
    assert CASES


@pytest.mark.parametrize("case", CASES)
def test_the_analysis_gives_the_expected_files(case: str, tmp_path):
    diffs = run_case(case, tmp_path / "out", TIME_ZONE)
    assert not diffs, "\n".join(diffs)


@pytest.mark.parametrize("time_zone", ["PST8PDT", "JST-9"])
@pytest.mark.parametrize("case", ["real-1118", "sim-characterise"])
def test_the_output_keeps_the_offset_the_run_was_saved_with(case: str, time_zone: str, tmp_path):
    diffs = run_case(case, tmp_path / "out", time_zone)
    assert not diffs, "\n".join(diffs)
