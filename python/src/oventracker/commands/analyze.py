"""analyze: re-runs the analysis over saved runs, alone or together (Commands/AnalyzeCommand.cs)."""

from __future__ import annotations

import json
import os
import sys
from dataclasses import dataclass, field, replace
from typing import Callable, TextIO

from .. import __version__, ovens
from ..analysis.characterisation import Characterisation
from ..analysis.session_summary import PointResult, TestPoint, judge
from ..analysis.setpoint_analyzer import AnalysisOptions
from ..jsonio import DateTimeOffset, dumps
from ..ovens import OvenProfile
from ..session.recording import SessionInfo, replay, results_json
from ..session.report import Report, verdict_text
from ..units import TempUnit, UnitFormatter, mode_name
from .shared import SharedSettings


@dataclass
class AnalyzeSettings(SharedSettings):
    paths: list[str] = field(default_factory=list)
    precision: float | None = None
    tolerance: float | None = None
    min_cycles: int | None = None
    out: str | None = None
    save_gains: bool = False

    def validate(self) -> str | None:
        error = super().validate()
        if error is not None:
            return error
        if not self.paths:
            return "Give at least one run's folder."
        for p in self.paths:
            if not os.path.isfile(p) and not os.path.isdir(p):
                return f"No such file or folder: {p}"
        return None


@dataclass(frozen=True)
class SavedRun:
    """One saved run: its folder, its readings and its session."""

    folder: str
    csv: str
    info: SessionInfo


def _write_text(path: str, text: str) -> None:
    """File.WriteAllText: UTF-8 without a BOM, the text as it is."""
    with open(path, "w", encoding="utf-8", newline="") as fh:
        fh.write(text)


def _creation_time(path: str) -> DateTimeOffset:
    st = os.stat(path)
    ts = getattr(st, "st_birthtime", None)
    if ts is None:
        ts = st.st_ctime if sys.platform == "win32" else st.st_mtime
    return DateTimeOffset.from_timestamp_local(ts)


def load(path: str) -> SavedRun | None:
    is_dir = os.path.isdir(path)
    directory = path if is_dir else os.path.dirname(os.path.abspath(path))
    csv = os.path.join(path, "readings.csv") if is_dir else path
    if not os.path.isfile(csv):
        return None
    session_path = os.path.join(directory, "session.json")
    if os.path.isfile(session_path):
        with open(session_path, encoding="utf-8-sig") as fh:
            info = SessionInfo.from_json(json.load(fh))
    else:
        info = SessionInfo(
            __version__, _creation_time(csv), "unknown meter", "?", TempUnit.Fahrenheit, 15 / 1.8, AnalysisOptions(), []
        )
    return SavedRun(directory, csv, info)


def _latest(runs: list[SavedRun]) -> SavedRun:
    """runs.OrderBy(r => r.Info.Started).Last(): the latest, the last given of any tie."""
    return sorted(runs, key=lambda r: r.info.started.utc_ticks)[-1]


def combine(
    runs: list[SavedRun], options: Callable[[SessionInfo], AnalysisOptions], adjustment_c: float | None
) -> tuple[SessionInfo, list[PointResult]]:
    """The runs' results, together. With several runs, each point carries its run's adjustment, and the latest
    run's session (and adjustment) stands for the whole. ``adjustment_c`` (--adjustment): for a single run, the
    adjustment it had (over what it recorded); for several, the adjustment of those that recorded none."""
    if len(runs) == 1:
        first = runs[0].info
        only = replace(first, adjustment_c=adjustment_c if adjustment_c is not None else first.adjustment_c)
        # Points keep the plan's adjustment (a characterise run has two).
        replayed = []
        for r in replay(runs[0].csv, options(only)):
            planned = next((p for p in only.plan if p.index == r.point.index), None)
            replayed.append(replace(r, point=planned if planned is not None else r.point))
        return only, replayed

    ordered = sorted(runs, key=lambda r: r.info.started.utc_ticks)
    results: list[PointResult] = []
    for run in ordered:
        if run.info.adjustment_c is not None:
            adj = run.info.adjustment_c
        else:
            adj = adjustment_c if adjustment_c is not None else 0.0
        for r in replay(run.csv, options(run.info)):
            # A point keeps the adjustment its plan gave it (a characterisation has two); otherwise the run's.
            plan = next((p for p in run.info.plan if p.index == r.point.index), None)
            planned = None if plan is None else plan.adjustment_c
            point: TestPoint = replace(r.point, index=len(results) + 1, adjustment_c=planned if planned is not None else adj)
            results.append(replace(r, point=point))
    latest = ordered[-1].info
    if latest.adjustment_c is not None:
        adj_latest = latest.adjustment_c
    else:
        adj_latest = adjustment_c if adjustment_c is not None else 0.0
    return replace(latest, adjustment_c=adj_latest, plan=[r.point for r in results]), results


def save_characterisation(info: SessionInfo, results: list[PointResult], folder: str, oven: OvenProfile, save: bool, out: TextIO) -> Characterisation:
    """Writes characterisation.md and .json beside the report (RunCommand.SaveCharacterisation). Saving the
    measured gains to the oven's profile (--save-gains) isn't supported by the Python port yet."""
    ch = Characterisation.from_results(info, results)
    md = ch.markdown(info)
    md_path = os.path.join(folder, "characterisation.md")
    json_path = os.path.join(folder, "characterisation.json")
    _write_text(md_path, md)
    _write_text(json_path, dumps(ch))
    print(f"Wrote {md_path} and {os.path.basename(json_path)}.", file=out)
    if not ch.measured_any:
        print("No gain could be measured: no setpoint was finished at both adjustments.", file=out)
    elif save:
        print(
            "--save-gains isn't supported by the Python port yet: the gains weren't saved to the oven's profile. "
            "Use the C# app (oventracker analyze ... --save-gains) to save them.",
            file=out,
        )
    return ch


def _summary(report: Report, out: TextIO) -> None:
    """A short summary for the console: each setpoint's result, and the recommendation."""
    info = report.info
    f = UnitFormatter(info.unit)
    for r in report.results:
        m = r.metrics
        verdict = verdict_text(judge(m, info.tolerance_c))
        mean = f.T(m.mean_c) if m.mean_c is not None else "—"
        off = f.D(m.offset_c) if m.offset_c is not None else "—"
        hw = f"±{f.span(m.half_width_c)}" if m.half_width_c is not None else ""
        print(f"  {mode_name(r.point.mode)} {f.setpoint(r.point.setpoint_c)}: {mean} ({off} {hw}) {verdict}".rstrip(), file=out)
    for a in report.advice():
        print(f"  {report.advice_headline(a)}", file=out)


def execute(s: AnalyzeSettings, out: TextIO | None = None, err: TextIO | None = None) -> int:
    out = out if out is not None else sys.stdout
    err = err if err is not None else sys.stderr
    error = s.validate()
    if error is not None:
        print(f"Error: {error}", file=err)
        return 1

    runs: list[SavedRun] = []
    for p in s.paths:
        run = load(p)
        if run is None:
            print(f"No readings.csv in {p}.", file=err)
            return 1
        runs.append(run)

    unit = _latest(runs).info.unit
    f = UnitFormatter(unit)

    def options(info: SessionInfo) -> AnalysisOptions:
        o = info.options
        if s.precision is not None:
            o = replace(o, precision_c=f.delta_from_display(s.precision))
        if s.min_cycles is not None:
            o = replace(o, min_cycles=s.min_cycles)
        return o

    info, results = combine(runs, options, s.adjustment_c(unit))
    chosen = ovens.find(s.oven)
    info = replace(
        info,
        options=options(info),
        tolerance_c=f.delta_from_display(s.tolerance) if s.tolerance is not None else info.tolerance_c,
        oven=chosen if chosen is not None else info.oven,
        adjust=s.build_options(unit, info.adjust, chosen if chosen is not None else info.oven),
        sensor=s.resolve_sensor(info.sensor, unit),  # e.g. --probe 80bk-a on a run made without it
    )

    report = Report(info, results)
    _summary(report, out)
    latest = _latest(runs).folder
    folder = s.out if s.out is not None else latest
    os.makedirs(folder, exist_ok=True)
    suffix = "reanalysed" if len(runs) == 1 else "combined"
    output = os.path.join(folder, f"report-{suffix}.md")
    _write_text(output, report.markdown())
    # The same shape as a run's results.json, so the two (and the C# app's) can be compared.
    json_path = os.path.join(folder, f"results-{suffix}.json")
    _write_text(json_path, results_json(info, results))
    print(f"\nWrote {output} and {os.path.basename(json_path)}.", file=out)

    if s.save_gains or (info.characterise is not None and info.characterise.changed_to_c is not None):
        oven = info.oven if info.oven is not None else ovens.DEFAULT
        if s.save_gains and oven.is_generic:
            print("--save-gains needs the oven: give --oven <key> (docs/adding-an-oven.md).", file=err)
            return 1
        save_characterisation(info, results, folder, oven, s.save_gains, out)
    return 0
