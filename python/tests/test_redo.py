"""run --resume <folder> --redo <list>: measuring finished setpoints of a saved run again (the C# app's RedoTests)."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from oventracker.analysis.session_summary import TestPoint
from oventracker.commands.run import build_plan, parse_indexes, resume_order
from oventracker.units import OvenMode

PLUS29 = 29 / 1.8


@pytest.mark.parametrize(
    "text, expected",
    [("5-8", [5, 6, 7, 8]), ("5,7", [5, 7]), ("8, 2,5-6", [2, 5, 6, 8]), ("3,3-4", [3, 4]), ("4-4", [4])],
)
def test_setpoint_numbers_and_ranges_are_read(text: str, expected: list[int]):
    assert parse_indexes(text) == expected


@pytest.mark.parametrize("text", ["", "0", "8-5", "5-", "-5", "1-2-3", "five", "2.5"])
def test_anything_else_is_refused(text: str):
    assert parse_indexes(text) is None


def _characterise_plan() -> list[TestPoint]:
    """350, 400, 450 in both modes at 0, then 450 and 350 at +29 °F."""
    from dataclasses import replace

    plan = [replace(p, adjustment_c=0.0) for p in build_plan([OvenMode.Bake, OvenMode.Convection], [176.67, 204.44, 232.22], False)]
    for sp in (232.22, 176.67):
        for mode in (OvenMode.Bake, OvenMode.Convection):
            plan.append(TestPoint(len(plan) + 1, mode, sp, PLUS29))
    return plan


def test_a_redo_at_another_adjustment_goes_first_so_the_oven_ends_at_the_runs_own():
    todo = resume_order(_characterise_plan(), list(range(1, 10)), [5, 6, 7, 8], PLUS29)
    assert [p.index for p in todo] == [5, 6, 7, 8, 10]


def test_a_redo_at_the_runs_adjustment_alone_needs_no_change():
    plan = _characterise_plan()
    assert [p.index for p in resume_order(plan, list(range(1, 11)), [7, 8], PLUS29)] == [7, 8]
    assert [p.index for p in resume_order(plan, list(range(1, 11)), [9, 2], PLUS29)] == [2, 9]


def test_without_its_own_adjustments_a_resume_keeps_the_plans_order():
    plan = build_plan([OvenMode.Bake, OvenMode.Convection], [176.67, 204.44], False)
    assert [p.index for p in resume_order(plan, [1, 2], [2], -5)] == [2, 3, 4]
    assert [p.index for p in resume_order(plan, [1, 2], [], None)] == [3, 4]


def test_redo_needs_resume_and_the_runs_own_setpoints(tmp_path: Path):
    pytest.importorskip("rich")
    from test_engine import run_app

    assert run_app("--simulate", "-y", "--redo", "2").returncode != 0
    out = tmp_path / "one"
    assert run_app("--simulate", "--speed", "3000", "--interval", "10", "-y", "--unit", "F", "--modes", "bake", "--setpoints", "350", "--out", str(out)).returncode == 0
    p = run_app("run", "--simulate", "-y", "--resume", str(out), "--redo", "1,4")
    assert p.returncode == 1
    assert "no setpoint 4" in p.stdout


def test_a_characterise_runs_setpoints_are_measured_again_at_their_own_adjustments(tmp_path: Path):
    pytest.importorskip("rich")
    from test_engine import run_app

    out = tmp_path / "char"
    p = run_app("characterise", "--simulate", "-y", "--speed", "3000", "--interval", "10", "--unit", "F", "--modes", "bake",
                "--setpoints", "325,425", "--adjustment", "0", "--step", "10", "--out", str(out))
    assert p.returncode == 0, p.stdout + p.stderr
    session = json.loads((out / "session.json").read_text(encoding="utf-8"))
    changed_to = session["characterise"]["changedToC"]
    first = json.loads((out / "results.json").read_text(encoding="utf-8"))["results"]
    assert [r["point"]["index"] for r in first] == [1, 2, 3, 4]

    p = run_app("run", "--simulate", "-y", "--speed", "3000", "--interval", "10", "--resume", str(out), "--redo", "2-3")
    assert p.returncode == 0, p.stdout + p.stderr
    assert "and 2 to measure again" in " ".join(p.stdout.split())  # however the long folder name wraps the line

    lines = (out / "readings.csv").read_text(encoding="utf-8").splitlines()
    resumed_at = max(i for i, line in enumerate(lines) if line.rstrip().endswith(",resume"))
    order = []
    for line in lines[resumed_at:]:
        f = line.rstrip().split(",")
        if f[5] != "Event":
            continue
        if f[8] == "start":
            order.append(f"start {f[1]}")
        elif f[8].startswith("adjustment-set:"):
            order.append(f[8])
    assert order == ["adjustment-set:0.000", "start 2", f"adjustment-set:{changed_to:.3f}", "start 3"]

    again = json.loads((out / "results.json").read_text(encoding="utf-8"))["results"]
    assert [r["point"]["index"] for r in again] == [1, 2, 3, 4]
    assert all(r["metrics"]["reason"] == "Converged" for r in again)
    assert again[1]["afterReset"] is True
    assert again[0]["metrics"]["meanC"] == first[0]["metrics"]["meanC"]
    # Setpoint 2 measured at 0 again, and 3 at the new adjustment: they still differ by about the change.
    assert abs(again[1]["metrics"]["meanC"] - first[1]["metrics"]["meanC"]) < 1.5
    assert abs(changed_to) * 0.6 < abs(again[2]["metrics"]["meanC"] - again[1]["metrics"]["meanC"]) < abs(changed_to) * 1.4
    assert (out / "characterisation.md").is_file()

    p = run_app("run", "--simulate", "-y", "--resume", str(out))
    assert p.returncode == 0
    assert "already done" in p.stdout
