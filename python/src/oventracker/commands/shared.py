"""The options shared by run and analyze (Commands/SharedSettings.cs): the oven's adjustment, and the probe."""

from __future__ import annotations

from dataclasses import dataclass, replace

from .. import ovens, probes
from ..analysis.adjustment_advisor import AdjustModel, AdjustmentOptions, AdjustObjective
from ..ovens import OvenProfile
from ..probes import Sensor
from ..units import TempUnit, UnitFormatter


def parse_objective(text: str | None) -> AdjustObjective | None:
    if text is None:
        return None
    t = text.strip().lower()
    if t in ("balanced", "least-squares"):
        return AdjustObjective.Balanced
    if t in ("minimax", "worst"):
        return AdjustObjective.Minimax
    return None


def parse_model(text: str | None) -> AdjustModel | None:
    if text is None:
        return None
    t = text.strip().lower()
    if t == "shift":
        return AdjustModel.Shift
    if t == "gain":
        return AdjustModel.Gain
    return None


@dataclass
class SharedSettings:
    oven: str | None = None
    probe: str | None = None
    probe_tolerance: str | None = None
    probe_max: str | None = None
    adjustment: float | None = None
    objective: str | None = None
    favour: float | None = None
    adjust_step: float | None = None
    adjust_range: float | None = None
    adjust_model: str | None = None
    separate_adjustments: bool = False

    def validate(self) -> str | None:
        """An error message, or None if the options are good."""
        if self.objective is not None and parse_objective(self.objective) is None:
            return "--objective must be balanced or minimax."
        if self.adjust_model is not None and parse_model(self.adjust_model) is None:
            return "--adjust-model must be shift or gain."
        if self.adjust_step is not None and self.adjust_step <= 0:
            return "--adjust-step must be positive."
        if self.adjust_range is not None and self.adjust_range <= 0:
            return "--adjust-range must be positive."
        if self.favour is not None and self.favour <= 0:
            return "--favour must be a temperature, e.g. 350."
        if self.favour is not None and self.objective is not None:
            return "--favour and --objective don't go together: --favour makes the oven right at one temperature, in place of an objective."
        if self.oven is not None and ovens.find(self.oven) is None:
            return f"--oven must be one of: {ovens.keys()}. To add yours, see docs/adding-an-oven.md."
        if self.probe is not None and probes.find(self.probe) is None:
            return f"--probe must be one of: {probes.keys()}. For another, give --probe-tolerance (and --probe-max)."
        if self.probe_tolerance is not None and probes.parse_tolerance(self.probe_tolerance, TempUnit.Celsius) is None:
            return "--probe-tolerance must look like 2.2C,2% (the greater of ±2.2 °C and ±2 %) or 0.3C+0.5% (the two added)."
        if self.probe_max is not None and probes.parse_temperature(self.probe_max, TempUnit.Celsius) is None:
            return "--probe-max must be a temperature, e.g. 260C or 500F."
        return None

    def build_options(
        self, unit: TempUnit, saved: AdjustmentOptions | None = None, oven: OvenProfile | None = None
    ) -> AdjustmentOptions:
        """The adjustment options: the ones given here, over ``saved`` (from a run), over the defaults for the unit."""
        f = UnitFormatter(unit)
        o = saved if saved is not None else AdjustmentOptions.for_oven(oven if oven is not None else ovens.DEFAULT, unit)
        # A newly chosen oven brings its own measured gains, even to options saved with a run.
        if saved is not None and self.oven is not None and oven is not None:
            o = replace(o, known_gains=AdjustmentOptions.for_oven(oven, unit).known_gains)
        if self.adjust_step is not None:
            o = replace(o, step_c=f.delta_from_display(self.adjust_step))
        if self.adjust_range is not None:
            o = replace(o, range_c=f.delta_from_display(self.adjust_range))
        objective = parse_objective(self.objective)
        if objective is not None:
            o = replace(o, objective=objective, favour_c=None if self.favour is None else o.favour_c)
        if self.favour is not None:
            o = replace(o, favour_c=f.from_display(self.favour))
        model = parse_model(self.adjust_model)
        if model is not None:
            o = replace(o, model=model)
        if self.separate_adjustments:
            o = replace(o, shared_across_modes=False)
        return o

    @property
    def gives_probe(self) -> bool:
        return self.probe is not None or self.probe_tolerance is not None or self.probe_max is not None

    def resolve_sensor(self, reported: Sensor | None, unit: TempUnit) -> Sensor | None:
        """The sensor for the report: the probe given, over what the meter (or the saved run) says."""
        if self.gives_probe:
            return probes.resolve(reported, self.probe, self.probe_tolerance, self.probe_max, unit)
        return reported

    def adjustment_c(self, unit: TempUnit) -> float | None:
        """The adjustment given, in °C."""
        return None if self.adjustment is None else UnitFormatter(unit).delta_from_display(self.adjustment)
