"""The results as text: the rows of the results tables and the Markdown report (Session/Report.cs)."""

from __future__ import annotations

from typing import TYPE_CHECKING, Iterable, Iterator

from .. import ovens
from ..analysis import stats
from ..analysis.adjustment_advisor import (
    WORTH_C,
    AdjustmentAdvice,
    AdjustmentOptions,
    AdjustModel,
    AdjustObjective,
    AdjustPoint,
    advise,
)
from ..analysis.session_summary import PointResult, Verdict, by_mode, compare, judge, judge_offset
from ..analysis.setpoint_analyzer import CompletionReason, PointMetrics, UnitKind
from ..jsonio import NEWLINE
from ..netfmt import custom, fixed, round_even
from ..units import TempUnit, UnitFormatter, mode_name

if TYPE_CHECKING:
    from .recording import SessionInfo

PEAK_ABOVE_SETPOINT_C = 25 / 1.8
"""How far the oven's first peak and swing take it above a setpoint, roughly (RunCommand.PeakAboveSetpointC)."""

NOTABLE_GAP_SECONDS = 60.0
"""A gap in the readings longer than this (a minute) is worth mentioning."""

POINT_COLUMNS = ["Mode", "Set", "Measured", "Offset", "±95 %", "Verdict", "Swing", "Range", "Cycle", "To temp", "First peak", "Time", "Used"]
COMPARISON_COLUMNS = ["Set", "Bake", "Convection", "Convection − Bake", "Real difference?", "Swing bake / conv", "To temp bake / conv"]
RESIDUAL_COLUMNS = ["Mode", "Set", "Now", "After adjusting", "±95 %", "Within tolerance?"]
DIAL_COLUMNS = ["Mode", "For", "Set the oven to"]


def minutes(m: float | None) -> str:
    return f"{fixed(m, 0)} min" if m is not None else "—"


def verdict_text(v: Verdict) -> str:
    return {
        Verdict.Accurate: "Accurate",
        Verdict.Borderline: "Borderline",
        Verdict.OutOfTolerance: "Out of tolerance",
    }.get(v, "Inconclusive")


class _Text:
    """A StringBuilder with AppendLine's Environment.NewLine."""

    def __init__(self) -> None:
        self._parts: list[str] = []

    def append(self, s: str) -> None:
        self._parts.append(s)

    def line(self, s: str = "") -> None:
        self._parts.append(s + NEWLINE)

    def __str__(self) -> str:
        return "".join(self._parts)


def _table(sb: _Text, headers: list[str], rows: Iterable[list[str]]) -> None:
    sb.line("| " + " | ".join(headers) + " |")
    sb.line("|" + "".join(" --- |" for _ in headers))
    for r in rows:
        sb.line("| " + " | ".join(c.replace("|", "\\|") for c in r) + " |")


class Report:
    def __init__(self, info: "SessionInfo", results: list[PointResult]):
        self.info = info
        self.results = results
        self.f = UnitFormatter(info.unit)

    def reason_text(self, m: PointMetrics) -> str:
        r = m.reason
        if r == CompletionReason.Converged:
            return "settled and measured"
        if r == CompletionReason.TimedOut:
            return f"stopped at the {fixed(self.info.options.max_point_minutes, 0)} min limit"
        if r == CompletionReason.Manual:
            return "finished early by you"
        if r == CompletionReason.Aborted:
            return "interrupted"
        if r == CompletionReason.Skipped:
            return "skipped"
        return ""

    def point_rows(self) -> Iterator[tuple[PointResult, Verdict, list[str]]]:
        f = self.f
        info = self.info
        for r in self.results:
            m = r.metrics
            v = judge(m, info.tolerance_c)
            used = "—" if m.units == 0 else f"{m.units} {'blocks' if m.kind == UnitKind.Block else 'cycles'}"
            adj = r.point.adjustment_c
            session_adj = info.adjustment_c if info.adjustment_c is not None else 0.0
            setpoint = (
                f.setpoint(r.point.setpoint_c)
                + (f" (adj {self.adjustment(adj)})" if adj is not None and adj != session_adj else "")
                + (" (after a reset)" if r.after_reset else "")
                + (" (started warm)" if r.started_warm else "")
            )
            rng = (
                f"{fixed(f.to_display(m.min_c), 0)}–{fixed(f.to_display(m.max_c), 0)}"
                if m.min_c is not None and m.max_c is not None
                else "—"
            )
            yield (
                r,
                v,
                [
                    mode_name(r.point.mode),
                    setpoint,
                    f.T(m.mean_c) if m.mean_c is not None else "—",
                    f.D(m.offset_c) if m.offset_c is not None else "—",
                    f.span(m.half_width_c) if m.half_width_c is not None else "—",
                    verdict_text(v),
                    f.span(m.swing_c) if m.swing_c is not None else "—",
                    rng,
                    f"{fixed(m.cycle_minutes, 1)} min" if m.kind == UnitKind.Cycle and m.cycle_minutes is not None else "—",
                    minutes(m.time_to_setpoint_minutes),
                    f.D(m.overshoot_c) if m.overshoot_c is not None else "—",
                    minutes(m.total_minutes),
                    used,
                ],
            )

    def comparison_rows(self) -> list[list[str]]:
        f = self.f
        rows = []
        for c in compare(self.results):
            a = f.span(c.bake.swing_c) if c.bake.swing_c is not None else "—"
            b = f.span(c.convection.swing_c) if c.convection.swing_c is not None else "—"
            rows.append(
                [
                    f.setpoint(c.setpoint_c),
                    f.T(c.bake.mean_c),  # type: ignore[arg-type]
                    f.T(c.convection.mean_c),  # type: ignore[arg-type]
                    f.D(c.delta_mean_c),
                    "Yes" if c.significant else "No (within uncertainty)",
                    f"{a} / {b}",
                    f"{minutes(c.bake.time_to_setpoint_minutes)} / {minutes(c.convection.time_to_setpoint_minutes)}",
                ]
            )
        return rows

    def findings(self) -> Iterator[str]:
        """Plain-language findings for each mode."""
        f = self.f
        info = self.info
        for s in by_mode(self.results):
            mode = mode_name(s.mode)
            direction = "hot" if s.mean_offset_c >= 0 else "cold"
            text = (
                f"{mode}: runs {f.span(s.mean_offset_c)} {direction} on average over {s.points} "
                f"setpoint{'' if s.points == 1 else 's'} (worst {f.span(s.max_abs_offset_c)})."
            )
            if s.gain_per_degree is not None and s.points >= 2:
                spread = [r for r in self.results if r.point.mode == s.mode and r.metrics.offset_c is not None]
                lo = min(spread, key=lambda r: r.point.setpoint_c)
                hi = max(spread, key=lambda r: r.point.setpoint_c)
                change = hi.metrics.offset_c - lo.metrics.offset_c  # type: ignore[operator]
                if abs(change) >= max(info.options.precision_c * 2, 1.0):
                    text += (
                        f" The error {'grows' if change > 0 else 'shrinks'} with temperature: "
                        f"{f.D(lo.metrics.offset_c)} at {f.setpoint(lo.point.setpoint_c)}, "  # type: ignore[arg-type]
                        f"{f.D(hi.metrics.offset_c)} at {f.setpoint(hi.point.setpoint_c)}, "  # type: ignore[arg-type]
                        "so a single adjustment can't correct all of it."
                    )
                else:
                    text += " The error is about the same at every setpoint, so the oven's thermostat adjustment can correct it."
            if s.mean_swing_c is not None:
                text += f" The thermostat swings {f.span(s.mean_swing_c)} peak to peak" + (
                    f", one cycle every {fixed(s.mean_cycle_minutes, 1)} min." if s.mean_cycle_minutes is not None else "."
                )
            yield text

        for a in self.advice():
            yield self.advice_headline(a) + " Details are under Best adjustment."

        for c in compare(self.results):
            if c.significant:
                yield f"At {f.setpoint(c.setpoint_c)}, convection is {f.span(c.delta_mean_c)} {'hotter' if c.delta_mean_c > 0 else 'cooler'} than bake."
            else:
                yield f"At {f.setpoint(c.setpoint_c)}, bake and convection are the same within the measurement's uncertainty ({f.D(c.delta_mean_c)})."

    @property
    def adjust_options(self) -> AdjustmentOptions:
        if self.info.adjust is not None:
            return self.info.adjust
        return AdjustmentOptions.for_oven(self.info.oven if self.info.oven is not None else ovens.DEFAULT, self.info.unit)

    def adjust_points(self) -> list[AdjustPoint]:
        """Each measured setpoint, with the adjustment the oven had for it."""
        info = self.info
        out = []
        for r in self.results:
            if r.metrics.offset_c is None:
                continue
            adj = r.point.adjustment_c
            if adj is None:
                adj = info.adjustment_c if info.adjustment_c is not None else 0.0
            out.append(AdjustPoint(r.point.mode, r.point.setpoint_c, r.metrics.offset_c, r.metrics.half_width_c, adj))
        return out

    def advice(self) -> list[AdjustmentAdvice]:
        return advise(self.adjust_points(), self.adjust_options, self.info.adjustment_c)

    def adjustment(self, c: float) -> str:
        """An adjustment setting as the oven shows it: whole degrees, signed, and plain 0."""
        f = self.f
        return f"0 {f.symbol}" if abs(f.delta_to_display(c)) < 0.5 else f.D(c, 0)

    @staticmethod
    def _scope(a: AdjustmentAdvice) -> str:
        return mode_name(a.mode) if a.mode is not None else "Bake and convection (one adjustment for both)"

    def advice_headline(self, a: AdjustmentAdvice) -> str:
        """The recommendation in a sentence."""
        f = self.f
        scope = self._scope(a)
        if abs(f.delta_to_display(a.change_c)) < 0.5:
            now = f" ({self.adjustment(a.current_c)})" if a.current_c is not None else ""
            return f"{scope}: leave the thermostat adjustment as it is{now}; no step would make the oven more accurate."
        if a.new_setting_c is not None:
            return f"{scope}: set the oven's thermostat adjustment to {self.adjustment(a.new_setting_c)} (it's {self.adjustment(a.current_c)} now)."  # type: ignore[arg-type]
        return f"{scope}: change the oven's thermostat adjustment by {f.D(a.change_c, 0)}, from whatever it is set to now."

    def advice_details(self, a: AdjustmentAdvice) -> Iterator[str]:
        """Everything worth saying about a recommendation, after the headline."""
        f = self.f
        info = self.info
        o = a.options
        if o.favour_c is not None:
            yield f"This makes the oven right at {f.setpoint(o.favour_c)}, the temperature you favour."
        elif o.objective == AdjustObjective.Minimax:
            yield "This keeps the worst of the setpoints measured as close to its setting as it can."
        else:
            yield "This makes the overall error across the setpoints measured as small as it can."
        if a.worst_after_c < WORTH_C:
            yield f"Afterwards every setpoint measured should be within {f.span(a.worst_after_c)} of its setting, which is about the probe's own accuracy."
        else:
            yield f"Afterwards the worst setpoint is still {f.span(a.worst_after_c)} out, so the table below says what to set to make up for it."
        if a.gain_measured:
            yield (
                f"Measured from runs at different adjustments: each degree of adjustment moves this oven {fixed(a.gain, 2)} degrees"
                + (f" (±{fixed(a.gain_half_width_c, 2)})." if a.gain_half_width_c is not None else ".")
            )
        elif o.model == AdjustModel.Gain and abs(a.gain - 1) > 1e-6:
            yield (
                f"This assumes the adjustment is scaled by the oven's own error slope, so each degree moves it {fixed(a.gain, 2)} degrees. "
                "That hasn't been measured; see docs/oven-control-model.md."
            )
        else:
            yield (
                "This assumes the adjustment moves the oven by the same amount at every setting. That hasn't been measured for this oven: "
                "after adjusting, run the same setpoints again and analyze both runs together (see docs/oven-control-model.md)."
            )
        if a.clamped:
            yield (
                f"The oven's adjustment only goes to ±{f.span(o.range_c, 0)}, so this is its limit. "
                "An error this large is worth a service call: the oven's sensor may be faulty."
            )
        if a.current_c is None and abs(f.delta_to_display(a.change_c)) >= 0.5:
            yield "The run didn't record the oven's current adjustment. Give it with --adjustment and the app will say what to set it to."
        if a.curved:
            yield "The error bends with temperature rather than following a straight line, so where it matters, use the table of what to set."
        if a.gain_from_profile:
            yield f"Using the gain measured on this oven before (its profile, from characterise): each degree of adjustment moves it {fixed(a.gain, 2)} degrees."
        oven = info.oven
        if (
            abs(f.delta_to_display(a.change_c)) >= 0.5
            and oven is not None
            and not oven.is_generic
            and oven.how_to_change is not None
            and len(oven.how_to_change) > 0
        ):
            steps = " ".join(f"{i + 1}. {step}" for i, step in enumerate(oven.how_to_change))
            yield f"To set it on the {oven.name}: {steps}"
        if a.too_few_setpoints is not None:
            yield f"{a.too_few_setpoints} Worth adding: {', '.join(self._rounded_setpoint(c) for c in a.add_setpoints_c)}."

    def _rounded_setpoint(self, c: float) -> str:
        """A setpoint to suggest, rounded to one people would set: 25 °F or 10 °C."""
        f = self.f
        step = 25.0 if self.info.unit == TempUnit.Fahrenheit else 10.0
        return f.setpoint(f.from_display(round_even(f.to_display(c) / step) * step))

    def residual_rows(self, a: AdjustmentAdvice) -> list[list[str]]:
        f = self.f
        tol = self.info.tolerance_c
        rows = []
        for r in a.residuals:
            # The same judgement as the verdicts: without a ±95 %, an error within tolerance is only Borderline.
            v = judge_offset(r.after_c, r.half_width_c, tol)
            ok = "Yes" if v == Verdict.Accurate else "No" if v == Verdict.OutOfTolerance else "Borderline"
            rows.append(
                [
                    mode_name(r.mode),
                    f.setpoint(r.setpoint_c),
                    f.D(r.now_c),
                    f.D(r.after_c),
                    f.span(r.half_width_c) if r.half_width_c is not None else "—",
                    ok,
                ]
            )
        return rows

    def dial_rows(self, a: AdjustmentAdvice) -> list[list[str]]:
        f = self.f
        return [
            [mode_name(d.mode), f.setpoint(d.want_c), f"{custom(round_even(f.to_display(d.set_c)), 0)} {f.symbol}"]
            for d in a.dial_corrections
        ]

    def durations(self) -> Iterator[str]:
        """What the tracking time came to, and what to allow next time."""
        f = self.f
        # The setpoints that settled and met this analysis's precision (as Report.Durations).
        done = [r for r in self.results if r.metrics.reason == CompletionReason.Converged and r.metrics.met_precision]
        if not done:
            return
        first = done[0]
        rest = [r.metrics.total_minutes for r in done[1:]]
        yield (
            f"The first setpoint took {minutes(first.metrics.total_minutes)} ({minutes(first.metrics.settle_minutes)} to settle, "
            f"then {minutes(first.metrics.measure_minutes)} measuring)."
        )
        if len(rest) == 1:
            yield f"The next setpoint took {minutes(rest[0])}, since the oven was already hot."
        elif len(rest) > 1:
            yield (
                f"Later setpoints took {minutes(min(rest))} to {minutes(max(rest))} (median {minutes(stats.median(rest))}), "
                "since the oven was already hot."
            )
        cycles = [r.metrics.units for r in done if r.metrics.kind == UnitKind.Cycle]
        if cycles:
            n = str(min(cycles)) if min(cycles) == max(cycles) else f"{min(cycles)}–{max(cycles)}"
            yield (
                f"Each result needed {n} full thermostat cycles to pin the mean to ±{f.span(self.info.options.precision_c)}; "
                "the app counts these as it goes, so the time adapts to your oven."
            )

    def markdown(self) -> str:
        f = self.f
        info = self.info
        results = self.results
        sb = _Text()
        sb.line("# Oven temperature accuracy")
        sb.line()
        if info.sittings is not None and len(info.sittings) > 1:
            # Each in the offset it was saved with, not this computer's time zone.
            when = ", ".join(d.format("%Y-%m-%d %H:%M") for d in info.sittings)
            sittings = f" in {len(info.sittings)} sittings ({when})"
        else:
            sittings = ""
        sb.line(
            f"Run {info.started.format('%Y-%m-%d %H:%M')}{sittings} with {info.meter} on {info.location}. "
            f"A setpoint is accurate when its mean, including the ±95 % interval, is within ±{f.span(info.tolerance_c)} of the setting."
        )
        sb.line()

        findings = list(self.findings())
        if findings:
            sb.line("## Findings")
            sb.line()
            for line in findings:
                sb.line(f"- {line}")
            sb.line()

        sb.line("## Each setpoint")
        sb.line()
        _table(sb, POINT_COLUMNS, (cells for _, _, cells in self.point_rows()))
        sb.line()
        # One paragraph: the notes on started-warm and after-reset setpoints follow on the same line.
        sb.append(
            "*Measured* is the time-weighted mean over whole thermostat cycles once the oven had settled; *±95 %* is how sure that mean is. "
            "*Swing* is the average peak-to-peak of a cycle and *Range* the lowest and highest readings while measuring. "
            "*To temp* is from pressing Start to first reaching the setting (or the thermostat first turning round, for an oven that runs cold); "
            "*First peak* is the first high point against the setting, which is the overshoot when it is positive."
        )
        if any(r.started_warm for r in results):
            sb.append(
                " The setpoint marked *started warm* began with the oven still warm, not from cold, so its *To temp* and *First peak* "
                "aren't from cold. Its mean is as good as any."
            )
        if any(r.after_reset for r in results):
            sb.append(
                " Setpoints marked *after a reset* were measured after a break (the run was resumed, or the oven was left to cool first), "
                "so their *To temp* and *First peak* aren't comparable with the others. Their mean is."
            )
        sb.line()
        sb.line()

        advice = self.advice()
        if advice:
            sb.line("## Best adjustment")
            sb.line()
            for a in advice:
                sb.line(f"**{self.advice_headline(a)}**")
                sb.line()
                for line in self.advice_details(a):
                    sb.line(f"- {line}")
                sb.line()
                _table(sb, RESIDUAL_COLUMNS, self.residual_rows(a))
                sb.line()
                dial = self.dial_rows(a)
                if dial:
                    sb.line("What to set after adjusting, where the adjustment alone leaves the oven 5 °F (3 °C) or more out:")
                    sb.line()
                    _table(sb, DIAL_COLUMNS, dial)
                    sb.line()
            sb.line(
                "[Reading the results and adjusting the oven](https://github.com/TGoodhew/OvenTracker/blob/main/docs/adjusting-the-oven.md) "
                "explains how to enter the adjustment."
            )
            sb.line()

        preheat = [r for r in results if r.metrics.preheat_signal_minutes is not None]
        if preheat:
            sb.line("## Preheat signal")
            sb.line()
            _table(
                sb,
                ["Mode", "Set", "Signalled after", "Oven was at", "Against setting"],
                (
                    [
                        mode_name(r.point.mode),
                        f.setpoint(r.point.setpoint_c),
                        minutes(r.metrics.preheat_signal_minutes),
                        f.T(r.metrics.preheat_signal_c) if r.metrics.preheat_signal_c is not None else "—",
                        f.D(r.metrics.preheat_signal_c - r.point.setpoint_c) if r.metrics.preheat_signal_c is not None else "—",
                    ]
                    for r in preheat
                ),
            )
            sb.line()

        comparison = self.comparison_rows()
        if comparison:
            sb.line("## Bake and convection")
            sb.line()
            _table(sb, COMPARISON_COLUMNS, comparison)
            sb.line()

        durations = list(self.durations())
        if durations:
            sb.line("## How long each setpoint needed")
            sb.line()
            for line in durations:
                sb.line(f"- {line}")
            sb.line()
            _table(
                sb,
                ["Mode", "Set", "Settling", "Measuring", "Total", "Ended"],
                (
                    [
                        mode_name(r.point.mode),
                        f.setpoint(r.point.setpoint_c),
                        minutes(r.metrics.settle_minutes),
                        minutes(r.metrics.measure_minutes),
                        minutes(r.metrics.total_minutes),
                        self.reason_text(r.metrics),
                    ]
                    for r in results
                ),
            )
            sb.line()

        sb.line("## How far to trust it")
        sb.line()
        top = max(r.point.setpoint_c for r in results) if results else 200.0
        sb.line(f"- {self.sensor_accuracy(top)}")
        for line in self.gaps():
            sb.line(f"- {line}")
        rng = self.probe_range()
        if rng is not None:
            sb.line(f"- {rng}")
        sb.line(
            "- The probe measures the air where you put it. Ovens are hotter near the elements and walls, "
            "so keep it in the centre for every run if you want to compare runs."
        )
        sb.line(
            "- If your oven converts convection temperatures down automatically (often by 25 °F / 15 °C), "
            "the convection setting entered here should be the one the oven actually targets."
        )
        sb.line()
        adj = self.adjustment(info.adjustment_c) if info.adjustment_c is not None else "not recorded"
        sb.line(
            f"Settings: precision ±{f.span(info.options.precision_c)}, at least {info.options.min_cycles} cycles, "
            f"up to {fixed(info.options.max_point_minutes, 0)} min per setpoint. "
            f"Oven's thermostat adjustment during the run: {adj}. Raw data: `readings.csv`."
        )
        return str(sb)

    def probe_range(self) -> str | None:
        """How close the readings came to the probe's rated maximum, when they came within the oven's peak of it."""
        f = self.f
        sensor = self.info.sensor
        if sensor is None or sensor.max_c is None or not self.results:
            return None
        mx = sensor.max_c
        highest: float | None = None
        for m in (r.metrics for r in self.results):
            peak = m.setpoint_c + m.overshoot_c if m.overshoot_c is not None else None
            for v in (m.max_c, peak):
                if v is not None:
                    highest = max(highest, v) if highest is not None else v
        if highest is None or highest < mx - PEAK_ABOVE_SETPOINT_C:
            return None
        if highest > mx:
            return (
                f"The {sensor.name} is rated to {f.T(mx, 0)}, but the readings reached {f.T(highest, 0)}: "
                "those above its rating can't be trusted, and the probe may be damaged."
            )
        return f"The {sensor.name} is rated to {f.T(mx, 0)}; the readings reached {f.T(highest, 0)}, close to that."

    def gaps(self) -> list[str]:
        """Setpoints whose readings have a long gap, e.g. while the meter was switched off."""
        f = self.f
        return [
            f"{mode_name(r.point.mode)} {f.setpoint(r.point.setpoint_c)} has a {minutes(r.metrics.longest_gap_seconds / 60)} gap in its readings "  # type: ignore[operator]
            "(the meter went quiet, perhaps its auto power-off). The cycle means across the gap are interpolated, so treat it with a little more care."
            for r in self.results
            if r.metrics.longest_gap_seconds is not None and r.metrics.longest_gap_seconds >= NOTABLE_GAP_SECONDS
        ]

    def sensor_accuracy(self, top_c: float) -> str:
        """What the sensor and meter add to the statistical uncertainty, at the highest setpoint."""
        f = self.f
        start = "The ±95 % column is the statistical uncertainty only. "
        end = "An offset smaller than these may be the probe, not the oven."
        s = self.info.sensor
        if s is None:
            return start + "The probe and the meter each add their own error, given in their data sheets. " + end
        pct = custom(s.limit_fraction * 100, 3)
        limit = (
            f"±{f.span(s.limit_c)} plus {pct} % of the temperature"
            if s.limits_add
            else f"the greater of ±{f.span(s.limit_c)} and ±{pct} % of the temperature"
        )
        what = f"a standard-grade {s.name}" if s.is_generic_type else f"the {s.name}"
        return (
            start
            + f"The probe itself adds its own: {what} is good to {limit}, ±{f.span(s.tolerance_c(top_c))} at {f.setpoint(top_c)}, "
            + "and the meter's own accuracy (in its data sheet) adds to that. "
            + end
        )
