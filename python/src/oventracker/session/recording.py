"""A run's recording (Session/Recording.cs): its session.json, the results JSON, and the replay of readings.csv."""

from __future__ import annotations

import os
import re
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any

from .. import jsonio
from ..netfmt import fixed
from ..analysis.adjustment_advisor import AdjustmentOptions
from ..analysis.characterisation import CharacteriseInfo
from ..analysis.session_summary import PointResult, TestPoint
from ..analysis.setpoint_analyzer import AnalysisOptions, CompletionReason, SetpointAnalyzer
from ..jsonio import DateTimeOffset, opt_float, parse_enum
from ..ovens import OvenProfile
from ..probes import Sensor
from ..units import OvenMode, TempUnit

HEADER = "time_utc,point,mode,setpoint_c,elapsed_s,status,temp_c,raw,event"


@dataclass(frozen=True)
class MeterIdentity:
    manufacturer: str
    model: str
    serial: str
    firmware: str

    def __str__(self) -> str:
        return f"{self.manufacturer} {self.model} (s/n {self.serial}, fw {self.firmware})"

    def to_json(self) -> dict[str, Any]:
        return {"manufacturer": self.manufacturer, "model": self.model, "serial": self.serial, "firmware": self.firmware}

    @staticmethod
    def from_json(d: dict[str, Any] | None) -> "MeterIdentity | None":
        if d is None:
            return None
        return MeterIdentity(d.get("manufacturer"), d.get("model"), d.get("serial"), d.get("firmware"))  # type: ignore[arg-type]


@dataclass(frozen=True)
class SessionInfo:
    """session.json: what the run was, and the settings its analysis used."""

    app_version: str
    started: DateTimeOffset
    meter: str
    location: str
    unit: TempUnit
    tolerance_c: float
    options: AnalysisOptions
    plan: list[TestPoint]
    driver: str | None = None
    identity: MeterIdentity | None = None
    sensor: Sensor | None = None
    adjustment_c: float | None = None
    adjust: AdjustmentOptions | None = None
    ambient_c: float | None = None
    sittings: list[DateTimeOffset] | None = None
    oven: OvenProfile | None = None
    characterise: CharacteriseInfo | None = None

    def to_json(self) -> dict[str, Any]:
        return {
            "appVersion": self.app_version,
            "started": self.started,
            "meter": self.meter,
            "location": self.location,
            "unit": self.unit,
            "toleranceC": self.tolerance_c,
            "options": self.options,
            "plan": self.plan,
            "driver": self.driver,
            "identity": self.identity,
            "sensor": self.sensor,
            "adjustmentC": self.adjustment_c,
            "adjust": self.adjust,
            "ambientC": self.ambient_c,
            "sittings": self.sittings,
            "oven": self.oven,
            "characterise": self.characterise,
        }

    @staticmethod
    def from_json(d: dict[str, Any]) -> "SessionInfo":
        started = d.get("started")
        sittings = d.get("sittings")
        return SessionInfo(
            app_version=d.get("appVersion"),  # type: ignore[arg-type]
            started=DateTimeOffset.parse(started) if isinstance(started, str) else DateTimeOffset(0, 0),
            meter=d.get("meter"),  # type: ignore[arg-type]
            location=d.get("location"),  # type: ignore[arg-type]
            unit=parse_enum(TempUnit, d.get("unit", 0)),
            tolerance_c=float(d.get("toleranceC", 0)),
            options=AnalysisOptions.from_json(d.get("options")),
            plan=[TestPoint.from_json(p) for p in (d.get("plan") or [])],
            driver=d.get("driver"),
            identity=MeterIdentity.from_json(d.get("identity")),
            sensor=Sensor.from_json(d.get("sensor")),
            adjustment_c=opt_float(d.get("adjustmentC")),
            adjust=AdjustmentOptions.from_json(d.get("adjust")),
            ambient_c=opt_float(d.get("ambientC")),
            sittings=None if sittings is None else [DateTimeOffset.parse(s) for s in sittings],
            oven=OvenProfile.from_json(d.get("oven")),
            characterise=CharacteriseInfo.from_json(d.get("characterise")),
        )


def results_json(info: SessionInfo, results: list[PointResult]) -> str:
    """results.json: the session and each setpoint's result (spec/results.schema.json)."""
    return jsonio.dumps({"session": info, "results": results})


# Recording -----------------------------------------------------------------------------------------


def utc_round_trip(time: datetime) -> str:
    """``time.UtcDateTime.ToString("O")``: yyyy-MM-ddTHH:mm:ss.fffffffZ, always seven fractional digits."""
    if time.tzinfo is not None:
        time = time.astimezone(timezone.utc)
    return f"{time:%Y-%m-%dT%H:%M:%S}.{time.microsecond:06d}0Z"


def _write_all_text(path: str, text: str) -> None:
    """File.WriteAllText: UTF-8 without a byte-order mark, the text as it is."""
    with open(path, "w", encoding="utf-8", newline="") as fh:
        fh.write(text)


class RunRecorder:
    """Writes every reading to readings.csv as it arrives (flushed each line, so nothing is lost if the PC sleeps
    or the app is closed), with the events the analysis needs to be replayed (Session/Recording.cs)."""

    def __init__(self, directory: str, info: SessionInfo, append: bool = False):
        """``append``: carry on a run saved earlier, adding to its readings.csv instead of starting a new one."""
        self.directory = directory
        os.makedirs(directory, exist_ok=True)
        _write_all_text(os.path.join(directory, "session.json"), jsonio.dumps(info))
        path = self.csv_path
        fresh = not append or not os.path.isfile(path) or os.path.getsize(path) == 0
        self._csv = open(path, "w" if fresh else "a", encoding="utf-8", newline="")
        if fresh:
            self._line(HEADER)

    @property
    def csv_path(self) -> str:
        return os.path.join(self.directory, "readings.csv")

    def _line(self, text: str) -> None:
        # StreamWriter.WriteLine with AutoFlush: the platform's newline, flushed at once.
        self._csv.write(text + os.linesep)
        self._csv.flush()

    def note(self, time: datetime, name: str) -> None:
        """Something that happened outside any setpoint: "resume", "reset", "reset-start", "reset-end:…", …"""
        self._line(",".join([utc_round_trip(time), "0", "", "", "", "Event", "", "", name]))

    def reading(self, r: Any, point: TestPoint | None, elapsed: float | None) -> None:
        """A reading (a meter.base.MeterReading), in a setpoint or (point None) outside one."""
        self._line(
            ",".join(
                [
                    utc_round_trip(r.time),
                    str(point.index) if point is not None else "0",
                    point.mode.name if point is not None else "",
                    "" if point is None else fixed(point.setpoint_c, 3),
                    fixed(elapsed, 1) if elapsed is not None else "",
                    r.status.name,
                    fixed(r.celsius, 2) if r.celsius is not None else "",
                    r.raw.replace(",", ";").replace('"', ""),
                    "",
                ]
            )
        )

    def event(self, time: datetime, point: TestPoint, elapsed: float, name: str) -> None:
        self._line(
            ",".join(
                [
                    utc_round_trip(time),
                    str(point.index),
                    point.mode.name,
                    fixed(point.setpoint_c, 3),
                    fixed(elapsed, 1),
                    "Event",
                    "",
                    "",
                    name,
                ]
            )
        )

    def update_session(self, info: SessionInfo) -> None:
        """Rewrites session.json, e.g. when characterise adds its second phase to the plan."""
        _write_all_text(os.path.join(self.directory, "session.json"), jsonio.dumps(info))

    def write_results(self, info: SessionInfo, results: list[PointResult], markdown: str) -> None:
        _write_all_text(os.path.join(self.directory, "results.json"), results_json(info, results))
        _write_all_text(os.path.join(self.directory, "report.md"), markdown)

    def close(self) -> None:
        if not self._csv.closed:
            self._csv.close()

    def __enter__(self) -> "RunRecorder":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()


# Replay --------------------------------------------------------------------------------------------

_INT = re.compile(r"^\s*[+-]?\d+\s*$")
_FLOAT = re.compile(r"^\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?\s*$")
_SPECIAL = {"nan": float("nan"), "infinity": float("inf"), "+infinity": float("inf"), "-infinity": float("-inf")}


def _try_int(text: str) -> int | None:
    """int.TryParse with the invariant culture."""
    if not _INT.match(text):
        return None
    v = int(text)
    return v if -(2**31) <= v < 2**31 else None


def _try_float(text: str) -> float | None:
    """double.TryParse with NumberStyles.Float and the invariant culture."""
    if _FLOAT.match(text):
        return float(text)
    return _SPECIAL.get(text.strip().lower())


def _parse_float(text: str) -> float:
    v = _try_float(text)
    if v is None:
        raise ValueError(f"The input string '{text}' was not in a correct format.")
    return v


def _parse_mode(text: str) -> OvenMode:
    """Enum.Parse<OvenMode>: the exact name, or its number."""
    t = text.strip()
    for m in OvenMode:
        if m.name == t:
            return m
    if _INT.match(t):
        return OvenMode(int(t))
    raise ValueError(f"Requested value '{text}' was not found.")


def _parse_reason(text: str) -> Any:
    t = text.strip()
    for m in CompletionReason:
        if m.name == t:
            return m
    if _INT.match(t):
        return CompletionReason(int(t))
    raise ValueError(f"Requested value '{text}' was not found.")


def replay(csv_path: str, options: AnalysisOptions) -> list[PointResult]:
    """Re-runs the analysis over a saved readings.csv, exactly as it ran live."""
    results: list[PointResult] = []
    state: dict[str, Any] = {"analyzer": None, "point": None}
    after_reset = False  # the setpoint being replayed came after a break
    reset_pending = False  # a break has happened since the last setpoint began
    started_warm = False
    warm_pending = False

    def close(if_open: Any) -> None:
        analyzer = state["analyzer"]
        point = state["point"]
        if analyzer is None or point is None:
            return
        analyzer.finish(if_open)
        results.append(PointResult(point, analyzer.metrics(), after_reset, started_warm))
        state["analyzer"] = None
        state["point"] = None

    with open(csv_path, encoding="utf-8-sig", newline=None) as fh:
        # Universal newlines: \r\n, \r and \n end a line, as File.ReadLines has it.
        lines = fh.read().split("\n")
    for line in lines[1:]:
        f = line.split(",")
        if len(f) < 9:
            continue
        index = _try_int(f[1])
        if index is None:
            continue
        if index == 0:
            if f[5] == "Event" and f[8] in ("resume", "reset"):
                reset_pending = True
            if f[5] == "Event" and f[8] == "started-warm":
                warm_pending = True
            continue
        mode = _parse_mode(f[2])
        sp = _parse_float(f[3])
        elapsed = _try_float(f[4])
        if elapsed is None:
            elapsed = 0.0

        if f[5] == "Event":
            ev = f[8]
            if ev in ("start", "restart"):
                if ev == "start":
                    close(CompletionReason.Aborted)
                    # A setpoint measured again in a later sitting replaces the earlier attempt.
                    results[:] = [r for r in results if r.point.index != index]
                    after_reset = reset_pending
                    reset_pending = False
                    started_warm = warm_pending
                    warm_pending = False
                state["point"] = TestPoint(index, mode, sp)
                state["analyzer"] = SetpointAnalyzer(sp, options)
            elif ev == "preheat-signal":
                if state["analyzer"] is not None:
                    state["analyzer"].mark_preheat_signal()
            elif ev.startswith("end:") and state["analyzer"] is not None:
                reason = _parse_reason(ev[4:])
                # Converged and TimedOut come back by themselves; the others were the user's choice.
                close(
                    CompletionReason.Manual
                    if reason in (CompletionReason.Converged, CompletionReason.TimedOut)
                    else reason
                )
            continue

        analyzer = state["analyzer"]
        point = state["point"]
        if analyzer is None or point is None or point.index != index or f[5] != "Ok":
            continue
        c = _try_float(f[6])
        if c is not None:
            analyzer.add(elapsed, c)
    close(CompletionReason.Aborted)
    return results
