"""run and characterise: measure the oven at each setpoint (Commands/RunCommand.cs and CharacteriseCommand.cs)."""

from __future__ import annotations

import math
import os
import sys
import traceback
from dataclasses import dataclass, field, replace
from datetime import datetime, timedelta, timezone
from typing import Any, Callable

from .. import __version__, ovens
from ..analysis.characterisation import CharacteriseInfo
from ..analysis.session_summary import PointResult, TestPoint, judge
from ..analysis.setpoint_analyzer import AnalysisOptions, CompletionReason
from ..jsonio import DateTimeOffset
from ..meter import driver as drivers
from ..meter.auto_power_off import AutoPowerOffSetting, AutoPowerOffState, AutoPowerOffWarningKind, advice_for
from ..meter.base import MeterReading, OvenSimulation, ReadingStatus, TemperatureMeter
from ..meter.driver import MeterDriver, MeterRequest, NotPortedDriver, ProbeResult
from ..meter.resource_spec import ResourceSpec
from ..meter.simulated import DRIVER_NAME as SIMULATED
from ..netfmt import clamp, custom, fixed, round_even
from ..ovens import OvenProfile
from ..probes import Sensor
from ..session.app_settings import AppSettings
from ..session.cancel import CancellationToken
from ..session.change_prompt import ChangeChoice, ChangePrompt, should_ask
from ..session.meter_poller import MeterPoller
from ..session.point_tracker import PointOutcome, PointTracker
from ..session.recording import RunRecorder, SessionInfo, replay
from ..session.report import PEAK_ABOVE_SETPOINT_C, Report, verdict_text
from ..session.reset_waiter import ResetOutcome, ResetTarget, ResetWaiter, target_for
from ..units import OvenMode, TempUnit, UnitFormatter, mode_name, parse_unit
from .analyze import load as load_run
from .analyze import save_characterisation_rich
from .names import APP_NAME
from .shared import SharedSettings

#: Room temperature to assume when it wasn't measured or given: 70 °F.
ASSUMED_AMBIENT_C = 21.0


@dataclass
class RunSettings(SharedSettings):
    meter: str | None = None
    resource: str | None = None
    meter_options: list[str] = field(default_factory=list)
    port: str | None = None
    simulate: bool = False
    speed: float = 60.0
    unit: str | None = None
    modes: str | None = None
    setpoints: str | None = None
    grouped: bool = False
    precision: float | None = None
    tolerance: float | None = None
    min_cycles: int = 4
    max_minutes: float | None = None
    interval: float = 1.0
    out: str | None = None
    resume: str | None = None
    ambient: float | None = None
    sim_hot: float | None = None
    remind: float = 1.0
    sim_forget: int | None = None
    sim_apo: float | None = None
    yes: bool = False

    def validate(self) -> str | None:
        error = super().validate()
        if error is not None:
            return error
        if self.meter is not None and drivers.find(self.meter) is None:
            return f"--meter must be one of: {drivers.names()}."
        if self.simulate and self.meter is not None and not (drivers.find(self.meter) or MeterDriver()).is_simulation:
            return "--simulate can't be used with another --meter."
        where = self.resource if self.resource is not None else self.port
        if where is not None and ResourceSpec.parse(where) is None:
            return (
                f'--resource "{where}" isn\'t one the app understands. Examples: COM5, COM5:9600,E,7,2 (with serial settings), '
                "/dev/ttyUSB0, GPIB0::22::INSTR, ble:C4:BE:84:12:34:56."
            )
        if drivers.parse_options(self.meter_options) is None:
            return "--meter-option takes key=value, e.g. --meter-option rtd=pt100-b."
        if self.port is not None and self.resource is not None and self.port.lower() != self.resource.lower():
            return "Give the meter's location once, with --resource."
        if self.unit is not None and parse_unit(self.unit) is None:
            return "--unit must be F or C."
        if self.interval < 0.5 or self.interval > 30:
            return "--interval must be between 0.5 and 30 seconds."
        if self.min_cycles < 2 or self.min_cycles > 20:
            return "--min-cycles must be between 2 and 20."
        if self.max_minutes is not None and (self.max_minutes < 10 or self.max_minutes > 600):
            return "--max-minutes must be between 10 and 600."
        if self.remind < 0 or self.remind > 60:
            return "--remind must be between 0 and 60 minutes."
        if self.precision is not None and self.precision <= 0:
            return "--precision must be positive."
        if self.tolerance is not None and self.tolerance <= 0:
            return "--tolerance must be positive."
        if self.modes is not None and parse_modes(self.modes) is None:
            return "--modes must be bake, convection, or both."
        if self.resume is not None and (
            not os.path.isdir(self.resume)
            or not os.path.isfile(os.path.join(self.resume, "session.json"))
            or not os.path.isfile(os.path.join(self.resume, "readings.csv"))
        ):
            return f"--resume needs a run's folder, with session.json and readings.csv: {self.resume}"
        if self.setpoints is not None and parse_numbers(self.setpoints) is None:
            return "--setpoints must be numbers, e.g. 350,400,450."
        return None


@dataclass
class CharacteriseSettings(RunSettings):
    step: float = 10.0

    def validate(self) -> str | None:
        error = super().validate()
        if error is not None:
            return error
        if self.step < 2 or self.step > 100:
            return "--step must be between 2 and 100 degrees."
        return None


def _split(text: str) -> list[str]:
    out = [text]
    for sep in (",", " ", ";"):
        out = [p for part in out for p in part.split(sep)]
    return [p for p in out if p]


def parse_modes(text: str) -> list[OvenMode] | None:
    modes: list[OvenMode] = []
    for p in _split(text):
        s = p.strip().lower()
        if "bake".startswith(s):
            modes.append(OvenMode.Bake)
        elif "convection".startswith(s) or s == "conv":
            modes.append(OvenMode.Convection)
        elif s == "both":
            modes.extend([OvenMode.Bake, OvenMode.Convection])
        else:
            return None
    if not modes:
        return None
    seen: list[OvenMode] = []
    for m in modes:
        if m not in seen:
            seen.append(m)
    return seen


def parse_numbers(text: str) -> list[float] | None:
    from ..session.recording import _try_float  # double.TryParse with the invariant culture

    values: list[float] = []
    for p in _split(text):
        v = _try_float(p)
        if v is None or not v > 0:
            return None
        values.append(v)
    if not values:
        return None
    return sorted(set(values))


def build_plan(modes: list[OvenMode], setpoints_c: list[float], grouped: bool) -> list[TestPoint]:
    order = [(m, s) for m in modes for s in setpoints_c] if grouped else [(m, s) for s in setpoints_c for m in modes]
    return [TestPoint(i + 1, m, s) for i, (m, s) in enumerate(order)]


def runs_folder() -> str:
    """Where runs are saved by default: Documents/OvenTracker (on Linux ~/Documents/OvenTracker, or ~/OvenTracker)."""
    return ovens.runs_folder()


def choose_driver(s: RunSettings, settings: AppSettings) -> MeterDriver:
    """The driver to use: the one asked for, or the one that answered last time, or the default. (A driver the
    Python version doesn't have yet is used only when asked for, not because the C# app used it last.)"""
    if s.simulate:
        return drivers.find(SIMULATED)  # type: ignore[return-value]
    asked = drivers.find(s.meter)
    if asked is not None:
        return asked
    last = drivers.find(settings.last_meter)
    if last is not None and not last.is_simulation and not last.not_ported:
        return last
    return drivers.find(drivers.DEFAULT_NAME)  # type: ignore[return-value]


def starting_from(idle: list[MeterReading], latest_c: float | None) -> float | None:
    """Where the oven was before a setpoint started: the mean of the last two minutes of readings between
    setpoints (an oven cycling on its thermostat swings either side of it), or the latest."""
    good = [r for r in idle if r.status == ReadingStatus.Ok and r.celsius is not None]
    if not good:
        return latest_c
    frm = good[-1].time - timedelta(minutes=2)
    recent = [r.celsius for r in good if r.time >= frm]
    total = 0.0
    for c in recent:
        total += c  # type: ignore[operator]
    return total / len(recent)


def probe_range_warning(sensor: Sensor | None, setpoints_c: list[float], f: UnitFormatter) -> tuple[str, bool] | None:
    """A warning, in markup, if any setpoint is above the probe's rating or within its peak of it; None if none."""
    from ..ui.console import escape

    if sensor is None or sensor.max_c is None:
        return None
    mx = sensor.max_c
    sps = sorted(set(setpoints_c))
    over = [sp for sp in sps if sp > mx]
    near = [sp for sp in sps if sp <= mx and sp + PEAK_ABOVE_SETPOINT_C > mx]
    if not over and not near:
        return None
    lines = [f"The {escape(sensor.name)} is rated to {escape(f.T(mx, 0))}."]
    if over:
        lines.append(
            f"[red]{escape(', '.join(f.setpoint(x) for x in over))} {'is' if len(over) == 1 else 'are'} above that: the probe may be damaged, "
            f"and its readings can't be trusted.[/] Leave {'it' if len(over) == 1 else 'them'} out, or use a probe rated higher."
        )
    if near:
        lines.append(
            f"At {escape(', '.join(f.setpoint(x) for x in near))}, the oven's first peak and thermostat swing (about "
            f"{escape(f.span(PEAK_ABOVE_SETPOINT_C, 0))}) may take the probe past it. The app will say if a reading does."
        )
    return "\n".join(lines), bool(over)


def convection_note(oven: OvenProfile, f: UnitFormatter) -> str:
    """The "Before you start" line about convection conversion: the oven's own, with how to turn it off, or a general one."""
    from ..ui.console import escape

    if oven.convection_conversion_c is not None:
        steps = oven.how_to_turn_off_conversion
        off = f" To turn it off: {' '.join(f'{i + 1}. {step}' for i, step in enumerate(steps))}" if steps else ""
        return (
            f"• The {escape(oven.name)} lowers convection settings by about {escape(f.span(oven.convection_conversion_c, 0))} by itself. "
            f"Turn that off for the test, or enter the temperature it actually targets.{escape(off)}\n"
        )
    return "• If your oven lowers convection temperatures automatically (e.g. by 25 °F), turn that off, or enter the temperature it actually targets.\n"


def _missing_without_terminal(s: RunSettings) -> str | None:
    """Without a terminal nothing can be asked, so a new run needs these given. None if nothing is missing."""
    if s.resume is not None:
        return None
    missing = [name for name, value in (("--unit", s.unit), ("--modes", s.modes), ("--setpoints", s.setpoints)) if value is None]
    return None if not missing else ", ".join(missing)


def _drain_idle(poller: MeterPoller, recorder: RunRecorder) -> list[MeterReading]:
    """Records the readings that came in between setpoints, and returns them."""
    out = []
    for r in poller.readings.drain():
        recorder.reading(r, None, None)
        out.append(r)
    return out


def _done(report: Report, r: PointResult) -> str:
    """One line on a finished setpoint, for the prompt."""
    f = report.f
    name = f"{mode_name(r.point.mode)} {f.setpoint(r.point.setpoint_c)}"
    off = r.metrics.offset_c
    if off is not None:
        return f"{name} is done: {f.T(r.metrics.mean_c)} ({f.D(off)}), {verdict_text(judge(r.metrics, report.info.tolerance_c))}."  # type: ignore[arg-type]
    return f"{name} is done, without a result."


def _now_offset() -> DateTimeOffset:
    """DateTimeOffset.Now."""
    import time

    return DateTimeOffset.from_timestamp_local(time.time())


def _status(text: str, work: Callable[[Callable[[str], None]], Any]) -> Any:
    """AnsiConsole.Status().Start(text, …): a spinner in a terminal; otherwise each status as a line."""
    from ..ui import console as ui

    c = ui.get()
    if ui.live():
        with c.status(text, spinner="dots") as status:
            return work(lambda t: status.update(t))
    c.print(text)
    return work(lambda t: c.print(t))


def _check_auto_power_off(
    driver: MeterDriver, meter: TemperatureMeter, identity: Any, settings: AppSettings, yes: bool, token: CancellationToken
) -> bool:
    """Warns, before a run, about a meter that will switch itself off mid-setpoint. False if you chose to stop."""
    from rich import box

    from ..ui import console as ui
    from ..ui import prompts
    from ..ui.widgets import Panel, markup

    c = ui.get()
    key = f"{driver.name}:{identity.serial}"

    def read() -> AutoPowerOffSetting:
        try:
            return meter.read_auto_power_off()
        except Exception:
            return AutoPowerOffSetting.UNKNOWN

    setting = read()
    while True:
        warning = advice_for(driver.auto_power_off, setting, settings.is_auto_power_off_turned_off(key))
        if warning is None:
            if setting.state == AutoPowerOffState.Off:
                c.print("[grey]Auto power-off is off.[/]")
            elif driver.auto_power_off is not None and settings.is_auto_power_off_turned_off(key):
                c.print("[grey]Auto power-off: you said it's turned off on this meter.[/]")
            return True

        body = [f"[bold]{ui.escape(warning.headline)}[/]"]
        if warning.steps:
            body.append("")
            body.append("To turn it off:")
            body.extend(f"  {i + 1}. {ui.escape(step)}" for i, step in enumerate(warning.steps))
        if warning.source is not None:
            body.append(f"[grey]({ui.escape(warning.source)})[/]")
        on = warning.kind == AutoPowerOffWarningKind.On
        c.print(
            Panel(
                markup("\n".join(body)),
                header="[red]Auto power-off is on[/]" if on else "[yellow]Auto power-off[/]",
                border=box.ROUNDED,
                border_style="red" if on else "yellow",
            )
        )
        if yes or driver.is_simulation:
            c.print("[grey]If the meter switches itself off anyway, the live display says so and tracking carries on once it's woken.[/]")
            return True

        turned_off = "I've turned it off: don't remind me again for this meter"
        check_again = "I've changed it: check again"
        carry_on = "Carry on anyway (the display will say if the meter switches off)"
        stop = "Stop"
        choices = [turned_off, carry_on, stop] if setting.state == AutoPowerOffState.Unknown else [check_again, carry_on, stop]
        choice = prompts.selection("Auto power-off:", choices, token=token)
        c.print(f"Auto power-off: [bold]{ui.escape(choice)}[/]")
        if choice == turned_off:
            settings.remember_auto_power_off_turned_off(key)
            settings.save()
            return True
        if choice == check_again:
            setting = read()
            continue
        return choice != stop


def execute(s: RunSettings, token: CancellationToken | None = None) -> int:
    from ..ui import prompts

    token = token or CancellationToken()
    try:
        return _execute(s, token)
    except prompts.PromptCancelled:
        from ..ui import console as ui

        ui.get().print("[red]Error:[/] The operation was canceled.")
        return -1


def _execute(s: RunSettings, token: CancellationToken) -> int:
    from rich import box

    from ..ui import console as ui
    from ..ui import console_report, prompts
    from ..ui.widgets import FigletText, Panel, Rule, markup, plain_print, table

    c = ui.get()
    error = s.validate()
    if error is not None:
        c.print(f"[red]Error:[/] {ui.escape(error)}")
        return -1

    app_settings = AppSettings.load()
    # The oven: the one asked for, the saved run's, the one used last time, or the generic one.
    resumed_oven = None
    if s.resume is not None:
        r0 = load_run(s.resume)
        resumed_oven = r0.info.oven if r0 is not None else None
    oven_profile = ovens.find(s.oven) or resumed_oven or ovens.find(app_settings.last_oven) or ovens.DEFAULT
    driver = choose_driver(s, app_settings)
    resource = s.resource if s.resource is not None else s.port
    # The driver's own settings: those given, or the ones used with it last time.
    given_options = drivers.parse_options(s.meter_options) or {}
    meter_options = given_options if given_options else app_settings.meter_options_for(driver.name)
    bad_option = driver.check_options(meter_options)
    if bad_option is not None:
        c.print(f"[red]{ui.escape(bad_option)}[/]")
        return 1
    if isinstance(driver, NotPortedDriver):
        c.print(f"[red]{ui.escape(driver.not_ported_message)}[/]")
        return 1
    c.print(FigletText("OvenTracker", "orange1"))
    c.print(f"[grey]v{__version__} · oven temperature accuracy · {ui.escape(driver.description)}[/]")
    c.print()

    # 1. The meter.
    found: ProbeResult | None = _status(
        "Looking for the meter…",
        lambda update: driver.open(
            MeterRequest(resource, app_settings.last_resource_for(driver.name), s.interval, lambda where: update(f"Trying {ui.escape(where)}…"), meter_options)
        ),
    )
    if found is None or found.meter is None or found.identity is None:
        if resource is not None:
            c.print(f"[red]No meter answered on {ui.escape(resource)}[/]: {ui.escape(found.error if found is not None and found.error else '')}")
        else:
            c.print(f"[red]No meter answered[/] (looking for a {ui.escape(driver.description)}).")
        if driver.not_found_help:
            c.print(ui.escape(driver.not_found_help))
        c.print(
            f"[bold]{APP_NAME} meters[/] lists what each kind of meter can see. Give the meter's location with [bold]--resource[/] "
            f"({ui.escape(driver.resource_help)}), or another kind of meter with [bold]--meter[/] ({ui.escape(drivers.names())})."
        )
        return 1
    meter = found.meter
    identity = found.identity

    if driver.is_simulation:
        config = meter.read_config()
        c.print(f"[yellow]Simulation[/]: a made-up oven, running {custom(s.speed, 1)}× real time.")
    else:
        app_settings.remember(driver.name, found.resource)
        app_settings.remember_meter_options(driver.name, meter_options)
        app_settings.save()
        c.print(f"[green]Connected[/] to {ui.escape(str(identity))} on {ui.escape(found.resource)}.")
        config = meter.read_config()
        if config is not None and not config.is_temperature:

            def wait_for_temperature(_: Callable[[str], None]) -> Any:
                cfg = config
                while cfg is not None and not cfg.is_temperature and not token.is_cancellation_requested:
                    token.wait(1.5)
                    cfg = meter.read_config()
                return cfg

            config = _status(f"The meter is on {ui.escape(config.description)}. {ui.escape(driver.not_temperature_help)}", wait_for_temperature)
        about = []
        if config is not None:
            about.append(f"Meter set to {ui.escape(config.description)}")
        b = meter.read_battery_percent()
        if b is not None:
            about.append(f"battery {fixed(b, 0)} %")
        if about:
            c.print(", ".join(about) + ".")
    oven = meter if isinstance(meter, OvenSimulation) else None
    if s.sim_apo is not None and oven is not None:
        oven.set_auto_power_off(timedelta(minutes=s.sim_apo))
    # Prompts need a terminal; without one (output to a file or pipe), the run goes on as with --yes.
    interactive = ui.live()
    missing = _missing_without_terminal(s) if not interactive else None
    if missing is not None:
        c.print(f"[red]The output isn't a terminal, so the app can't ask: give {ui.escape(missing)}.[/]")
        meter.dispose()
        return 1
    if not _check_auto_power_off(driver, meter, identity, app_settings, s.yes or not interactive, token):
        meter.dispose()
        return 0
    first = meter.read()
    if first.status == ReadingStatus.Overload:
        c.print("[yellow]The meter reads OL: the probe isn't connected (or is broken).[/]")
    elif first.celsius is not None and not driver.is_simulation:
        c.print(f"The probe reads {ui.escape(UnitFormatter(parse_unit(s.unit) or TempUnit.Celsius).T(first.celsius))} now.")
    c.print()

    # 2. What to test: asked, or the saved run's when resuming.
    resumed = load_run(s.resume) if s.resume is not None else None
    if resumed is not None:
        unit = resumed.info.unit
        plan = list(resumed.info.plan)
        modes: list[OvenMode] = []
        for p in plan:
            if p.mode not in modes:
                modes.append(p.mode)
        options = resumed.info.options
        tolerance_c = resumed.info.tolerance_c
    else:
        parsed_unit = parse_unit(s.unit)
        unit = (
            parsed_unit
            if parsed_unit is not None
            else prompts.selection(
                "What unit does your [orange1]oven[/] use?",
                [TempUnit.Fahrenheit, TempUnit.Celsius],
                lambda u: "°F" if u == TempUnit.Fahrenheit else "°C",
                token,
            )
        )
        fu = UnitFormatter(unit)
        modes = (
            parse_modes(s.modes)  # type: ignore[assignment]
            if s.modes is not None
            else prompts.multi_selection(
                "Which modes do you want to test?", [OvenMode.Bake, OvenMode.Convection], [OvenMode.Bake, OvenMode.Convection], mode_name, token
            )
        )
        default_setpoints = "350, 400, 450" if unit == TempUnit.Fahrenheit else "175, 200, 230"
        if s.setpoints is not None:
            setpoints = parse_numbers(s.setpoints)
        else:
            setpoints = parse_numbers(
                prompts.text(
                    f"Setpoints to test, in {fu.symbol}?",
                    default=default_setpoints,
                    validate=lambda t: "Numbers separated by commas, please." if parse_numbers(t) is None else None,
                    token=token,
                )
            )
        assert setpoints is not None
        precision_c = fu.delta_from_display(s.precision) if s.precision is not None else (2 / 1.8 if unit == TempUnit.Fahrenheit else 1.0)
        tolerance_c = fu.delta_from_display(s.tolerance) if s.tolerance is not None else (15 / 1.8 if unit == TempUnit.Fahrenheit else 8.0)
        options = AnalysisOptions(
            precision_c=precision_c,
            min_cycles=s.min_cycles,
            max_point_minutes=s.max_minutes if s.max_minutes is not None else oven_profile.default_max_minutes,
        )
        plan = build_plan(modes, [fu.from_display(x) for x in setpoints], s.grouped)
    f = UnitFormatter(unit)

    # The oven's thermostat adjustment now, so the report can say what to set it to.
    adjustment_c = s.adjustment_c(unit)
    if adjustment_c is None and resumed is not None:
        adjustment_c = resumed.info.adjustment_c
    if adjustment_c is None and not driver.is_simulation and not s.yes and interactive:
        if not oven_profile.is_generic and oven_profile.how_to_read:
            steps = " ".join(f"{i + 1}. {step}" for i, step in enumerate(oven_profile.how_to_read))
            c.print(f"[grey]To see it on the {ui.escape(oven_profile.name)}: {ui.escape(steps)}[/]")
        from ..session.recording import _try_float

        answer = prompts.text(
            f"What is the oven's thermostat adjustment set to now, in {f.symbol}? [grey](0 if it has never been changed; Enter if you don't know)[/]",
            allow_empty=True,
            validate=lambda t: None if t.strip() == "" or _try_float(t) is not None else "A number such as -10, or Enter.",
            token=token,
        )
        given = _try_float(answer)
        if given is not None:
            adjustment_c = f.delta_from_display(given)
        if adjustment_c is not None:
            text = Report(SessionInfo("", _now_offset(), "", "", unit, 0, AnalysisOptions(), []), []).adjustment(adjustment_c)
            c.print(f"Thermostat adjustment: [bold]{ui.escape(text)}[/].")
        else:
            c.print("Thermostat adjustment: [bold]not known[/] (the report will say how much to change it by, rather than what to set it to).")
    if adjustment_c is not None and oven is not None:
        oven.set_adjustment(adjustment_c)  # the simulated oven obeys --adjustment too
    if s.sim_hot is not None and oven is not None:
        oven.pretend_used(f.from_display(s.sim_hot))
        first = meter.read()  # the simulated oven is warm now, as a real one would be when the app starts

    # Room temperature, for knowing when the oven is cold again: given, saved, or the first reading if the oven was cold.
    if s.ambient is not None:
        ambient_c: float | None = f.from_display(s.ambient)
    elif resumed is not None and resumed.info.ambient_c is not None:
        ambient_c = resumed.info.ambient_c
    else:
        ambient_c = first.celsius if first.celsius is not None and -10 < first.celsius < 45 else None

    # Characterise: phase 1's points are measured at the current adjustment (0 when it isn't known).
    characterising = isinstance(s, CharacteriseSettings) or (resumed is not None and resumed.info.characterise is not None)
    if resumed is None and isinstance(s, CharacteriseSettings):
        plan = [replace(p, adjustment_c=adjustment_c if adjustment_c is not None else 0.0) for p in plan]

    # When resuming, everything the earlier sittings measured, with the plan's adjustment for each. The setpoints
    # that finished (by themselves or by choice) are done; the others (interrupted or skipped) are measured again,
    # and until they are, their earlier result stays in the results, as analyze shows it.
    earlier: list[PointResult] = []
    if resumed is not None:
        for r in replay(resumed.csv, options, plan):
            planned = next((p for p in plan if p.index == r.point.index), None)
            earlier.append(replace(r, point=planned if planned is not None else r.point))
    done = [
        r
        for r in earlier
        if r.metrics.reason in (CompletionReason.Converged, CompletionReason.TimedOut, CompletionReason.Manual)
    ]
    todo = [p for p in plan if all(d.point.index != p.index for d in done)]
    if resumed is not None and not todo and not (resumed.info.characterise is not None and resumed.info.characterise.changed_to_c is None):
        c.print(f"[green]Every setpoint in {ui.escape(resumed.folder)} is already done.[/] [bold]{APP_NAME} analyze[/] re-reads it.")
        meter.dispose()
        return 0

    # 3. Explain, then go.
    c.print(
        Panel(
            markup(
                "[bold]Before you start[/]\n"
                "• Put the thermocouple tip in the [bold]centre of the oven[/], on the middle shelf, clear of the racks and walls (clip the lead to a rack so the tip hangs free). Run the lead out under the door seal; the meter stays outside.\n"
                "• Check the thermocouple's insulation is rated for your highest setpoint.\n"
                "• Leave the door shut while a setpoint is being measured. If it gets opened, press [bold]R[/] twice to restart that setpoint.\n"
                "• When the oven beeps or shows it is preheated, press [bold]P[/]: the report shows how true that signal was.\n"
                + (convection_note(oven_profile, f) if OvenMode.Convection in modes else "")
                + "\n[bold]How long it takes[/]\n"
                f"The app finds when the oven has settled, then measures whole thermostat cycles until the mean is known to ±{ui.escape(f.span(options.precision_c))} (at least {options.min_cycles} cycles). "
                "It learns your oven's cycle as it goes and shows the time left. As a rough guide, the first setpoint from cold takes about an hour and each one after it 20–45 min."
            ),
            border=box.ROUNDED,
            border_style="grey35",
        )
    )

    if resumed is not None:
        c.print(f"[bold]Carrying on[/] {ui.escape(resumed.folder)}: {len(plan) - len(todo)} of {len(plan)} setpoints done.")
    plan_table = table(["#", "Mode", "Setting"] + ([""] if resumed is not None else []))
    for tp in plan:
        row = [str(tp.index), mode_name(tp.mode), ui.escape(f.setpoint(tp.setpoint_c))]
        if resumed is not None:
            row.append("to do" if tp in todo else "[green]done[/]")
        plan_table.add_row(*(markup(x) for x in row))
    c.print(plan_table)
    if not s.grouped and len(modes) > 1:
        c.print("[grey]The modes alternate at each setpoint, so the oven only ever heats up between them and bake and convection are compared under the same conditions.[/]")

    # The probe: the one given, or what the meter (or the saved run) says. Warn about setpoints near its rating.
    reported = resumed.info.sensor if resumed is not None and resumed.info.sensor is not None else (config.sensor if config is not None else None)
    sensor = s.resolve_sensor(reported, unit)
    range_warning = probe_range_warning(sensor, [p.setpoint_c for p in todo], f)
    if range_warning is not None:
        text, over = range_warning
        c.print(
            Panel(
                markup(text),
                header="[red]Probe rating[/]" if over else "[yellow]Probe rating[/]",
                border=box.ROUNDED,
                border_style="red" if over else "yellow",
            )
        )

    # A warm oven at the start of a new session: the first setpoint is meant to start from cold.
    assumed = ambient_c if ambient_c is not None else ASSUMED_AMBIENT_C
    starts_warm_c = (
        first.celsius
        if resumed is None and first.celsius is not None and todo and first.celsius > target_for(plan, todo[0], assumed)
        else None
    )
    if starts_warm_c is not None:
        cold_c = target_for(plan, todo[0], assumed)
        c.print(
            Panel(
                markup(
                    f"[bold]The oven is warm: {ui.escape(f.T(starts_warm_c, 0))}.[/] The first setpoint is meant to start from cold "
                    f"(below {ui.escape(f.T(cold_c, 0))}), so that its time to temperature and first peak are like-for-like.\n"
                    "On the next screen, press [bold]W[/] to let it cool first: the app shows it cooling and, after a few minutes, predicts how long it will take. "
                    "Or start anyway, and the report marks the first setpoint [italic]started warm[/] (its mean is unaffected).\n"
                    + (
                        f"[grey]Room temperature is assumed to be {ui.escape(f.T(ASSUMED_AMBIENT_C, 0))}, since the oven isn't cold. If it isn't, give it with --ambient.[/]"
                        if ambient_c is None
                        else f"[grey]Room temperature: {ui.escape(f.T(ambient_c, 0))}.[/]"
                    )
                ),
                header="[yellow]Warm oven[/]",
                border=box.ROUNDED,
                border_style="yellow",
            )
        )

    if not s.yes and interactive and not prompts.confirm("Ready to start?", True, token):
        return 0

    started_at = _now_offset()
    if resumed is not None:
        out_dir = resumed.folder
        info = replace(
            resumed.info,
            adjustment_c=adjustment_c,
            adjust=s.build_options(unit, resumed.info.adjust, oven_profile),
            oven=oven_profile,
            ambient_c=ambient_c,
            sensor=sensor,
            sittings=list(resumed.info.sittings if resumed.info.sittings is not None else [resumed.info.started]) + [started_at],
        )
    else:
        out_dir = s.out if s.out is not None else os.path.join(runs_folder(), datetime.now().strftime("%Y-%m-%d %H%M"))
        info = SessionInfo(
            __version__,
            started_at,
            f"{identity.manufacturer} {identity.model}",
            meter.location,
            unit,
            tolerance_c,
            options,
            plan,
            driver.name,
            identity,
            sensor,
            (adjustment_c if adjustment_c is not None else 0.0) if characterising else adjustment_c,
            s.build_options(unit, None, oven_profile),
            ambient_c,
            [started_at],
            oven_profile,
            CharacteriseInfo(f.delta_from_display(s.step), adjustment_c if adjustment_c is not None else 0.0)
            if isinstance(s, CharacteriseSettings)
            else None,
        )
    if not driver.is_simulation:
        app_settings.last_oven = oven_profile.key
        app_settings.save()

    recorder = RunRecorder(out_dir, info, append=resumed is not None)
    # A simulation's readings carry its own, faster clock: its events take the time of the latest reading, so
    # readings.csv stays in time order.
    if driver.is_simulation:
        recorder.clock = lambda: (
            recorder.last_reading_time if recorder.last_reading_time is not None else datetime.now(timezone.utc)
        )
    if resumed is not None:
        recorder.note(recorder.now, "resume")
    poller = MeterPoller(meter, s.interval, s.speed if driver.is_simulation else 1, connected=True)
    poller.start()

    results: list[PointResult] = list(earlier)
    meter_label = "simulated meter" if driver.is_simulation else f"{identity.model} on {meter.location}"
    tracker = PointTracker(
        poller,
        recorder,
        f,
        options,
        tolerance_c,
        meter_label,
        driver.not_temperature_help,
        len(plan),
        driver.is_simulation,
        driver.auto_power_off,
        sensor.max_c if sensor is not None else None,
    )
    waiter = ResetWaiter(poller, recorder, f, assumed, ambient_c is None, driver.is_simulation, driver.auto_power_off)
    st: dict[str, Any] = {
        "after_reset": resumed is not None,  # the first setpoint of a resumed run follows a break
        "stopped": False,
        "last_done": None,  # what was just finished, for the prompt
        "previous": None,  # the setpoint just measured in this sitting, if the oven went straight on from it
        "plan": plan,
        "info": info,
    }
    failure: BaseException | None = None
    prompt = ChangePrompt(poller, recorder, f, s.remind * 60, token)
    ask = should_ask(s.yes, driver.is_simulation, ui.keys())
    first_prompt = True

    def save_results() -> Report:
        """results.json and report.md as they stand: after each setpoint, and at the end (RunCommand.SaveResults)."""
        results[:] = sorted(results, key=lambda r: r.point.index)
        now = Report(st["info"], results)
        recorder.write_results(st["info"], results, now.markdown())
        return now

    def plan_phase2() -> bool:
        """Characterise, phase 2: change the adjustment (towards correct, by at least --step), then the lowest and
        highest setpoints again in each mode, highest first so the oven cools only once. False if it can't go on."""
        info_now: SessionInfo = st["info"]
        ch = info_now.characterise
        if ch is None or ch.changed_to_c is not None:
            return False
        measured = [r for r in results if r.metrics.offset_c is not None]
        if not measured:
            return False
        report = Report(info_now, results)
        adjust = report.adjust_options
        frm = info_now.adjustment_c if info_now.adjustment_c is not None else 0.0
        advice = report.advice()
        wanted = advice[0].change_c if advice else 0.0
        if abs(wanted) >= adjust.step_c / 2:
            sign = (wanted > 0) - (wanted < 0)
        else:
            total = 0.0
            for r in measured:
                total += r.metrics.offset_c  # type: ignore[operator]
            sign = -1 if total / len(measured) > 0 else 1
        size = round_even(max(ch.step_c, abs(wanted)) / adjust.step_c) * adjust.step_c
        to = clamp(frm + sign * size, -adjust.range_c, adjust.range_c)
        if abs(to - frm) < 2 * adjust.step_c:
            to = clamp(frm - sign * size, -adjust.range_c, adjust.range_c)  # no room that way
        to_text = report.adjustment(to)

        if ask:
            steps = (
                f" To set it on the {oven_profile.name}: {' '.join(f'{i + 1}. {x}' for i, x in enumerate(oven_profile.how_to_change))}"
                if oven_profile.how_to_change and not oven_profile.is_generic
                else ""
            )
            done_text = (
                f"Phase 1 is done. Now change the oven's thermostat adjustment from {report.adjustment(frm)} to {to_text}, "
                f"and leave it there for phase 2.{steps}"
            )
            if not prompt.ask_to_set_adjustment(done_text, to_text):
                c.print("[yellow]Stopped before phase 2. Carry on later with --resume: it asks for the change again.[/]")
                return False
        elif not driver.is_simulation:
            c.print(f"[yellow]Phase 1 is done. Change the adjustment to {ui.escape(to_text)}, then carry on with --resume.[/]")
            return False
        if oven is not None:
            oven.set_adjustment(to)  # the simulated oven

        plan_now: list[TestPoint] = st["plan"]
        nxt = max(p.index for p in plan_now) + 1
        phase2: list[TestPoint] = []
        sps = sorted(set(p.setpoint_c for p in plan_now))
        for sp in dict.fromkeys([sps[-1], sps[0]]):
            for mode in modes:
                phase2.append(TestPoint(nxt, mode, sp, to))
                nxt += 1
        st["plan"] = plan_now + phase2
        st["info"] = replace(info_now, plan=st["plan"], adjustment_c=to, characterise=replace(ch, changed_to_c=to, changed_at=_now_offset()))
        recorder.update_session(st["info"])
        recorder.note(recorder.now, f"adjustment-changed:{fixed(to, 3)}")
        todo.extend(phase2)
        st["previous"] = None
        st["last_done"] = f"The adjustment is {to_text} now. Phase 2: {len(phase2)} setpoints."
        return True

    try:
        try:
            w = 0
            while w < 50 and poller.last_celsius is None:  # a first reading, to know how hot the oven is
                if token.wait(0.1):
                    raise _Cancelled()
                w += 1
            ti = 0
            while True:
                while ti < len(todo):
                    point = todo[ti]
                    plan_now = st["plan"]
                    c.print()
                    c.print(Rule(f"[orange1]{point.index} of {len(plan_now)}: {mode_name(point.mode)} {ui.escape(f.setpoint(point.setpoint_c))}[/]"))
                    label = f"{mode_name(point.mode)} {f.setpoint(point.setpoint_c)}"
                    target_c = target_for(plan_now, point, assumed)
                    hot_at_prompt = False  # how the oven was when asked, before anyone started it for this setpoint
                    while True:
                        # Too hot to start from? Going straight on from a setpoint, the oven cycles around it, so allow half its swing.
                        previous: PointResult | None = st["previous"]
                        swing = previous.metrics.swing_c if previous is not None and previous.metrics.swing_c is not None else 0.0
                        now_c = poller.last_celsius
                        hot = now_c is not None and now_c > target_c + swing / 2
                        hot_at_prompt = hot
                        wait_offer = f"wait for it to cool below {f.T(target_c, 0)} first" if hot else None
                        if hot and st["after_reset"] and s.yes:
                            choice = ChangeChoice.Wait  # resumed with --yes: cool first, by itself
                        elif not ask:
                            choice = ChangeChoice.Start  # the simulator with --yes, or no keyboard
                        else:
                            if hot and (st["after_reset"] or first_prompt):
                                heading = (
                                    f"THE OVEN IS {'STILL HOT' if st['after_reset'] else 'WARM'} ({f.T(poller.last_celsius, 0)}). "  # type: ignore[arg-type]
                                    "PRESS W TO LET IT COOL, OR SET IT TO"
                                )
                            else:
                                heading = "SET THE OVEN TO" if first_prompt else "CHANGE THE OVEN TO"
                            choice = prompt.ask(heading, mode_name(point.mode), point.setpoint_c, st["last_done"], wait_offer)
                        if choice != ChangeChoice.Wait:
                            break

                        if not s.yes:
                            c.print("[grey]Turn the oven off and leave the door shut.[/]")
                        if oven is not None:
                            oven.set_oven(point.mode, None)
                        _drain_idle(poller, recorder)
                        outcome = waiter.run(ResetTarget(label, target_c), s.yes, token)
                        if outcome == ResetOutcome.Quit:
                            st["stopped"] = True
                            break
                        st["after_reset"] = True
                        st["previous"] = None  # the oven has cooled since
                        st["last_done"] = f"The oven is ready for {label}."
                    if st["stopped"] or choice == ChangeChoice.Finish:
                        st["stopped"] = True
                        break
                    if choice == ChangeChoice.Skip:
                        st["last_done"] = f"{label} skipped."
                        st["previous"] = None
                        ti += 1
                        continue
                    first_prompt = False

                    if s.sim_forget != point.index and oven is not None:
                        oven.set_oven(point.mode, point.setpoint_c)
                    idle = _drain_idle(poller, recorder)
                    if st["after_reset"]:
                        recorder.note(recorder.now, "reset")
                    # The first setpoint of a new session, started without letting a warm oven cool. Judged by the
                    # oven when it was asked, not now: by now it may be heating for this setpoint.
                    started_warm = resumed is None and not st["after_reset"] and point.index == plan_now[0].index and hot_at_prompt
                    if started_warm:
                        recorder.note(recorder.now, "started-warm")
                    # Where the oven starts from: the setpoint just measured, if it went straight on from it, or its last two minutes.
                    previous = st["previous"]
                    frm = previous.metrics.mean_c if previous is not None and previous.metrics.mean_c is not None else starting_from(idle, poller.last_celsius)
                    result, point_outcome = tracker.run(point, token, frm, previous.metrics.swing_c if previous is not None else None)
                    result = replace(result, after_reset=st["after_reset"], started_warm=started_warm)
                    st["after_reset"] = False
                    results[:] = [r for r in results if r.point.index != point.index]
                    results.append(result)
                    # Saved as each setpoint finishes, so a crash, a closed window or a second Ctrl+C loses at most the
                    # setpoint in progress, not the report (#43). A save that fails (a full disk) mustn't stop the measuring.
                    try:
                        save_results()
                    except OSError as ex:
                        c.print(f"[yellow]Couldn't save the results so far: {ui.escape(str(ex))}. The readings are still being recorded.[/]")
                    poller.enqueue(lambda m: m.beep())
                    ui.bell()
                    console_report.point_summary(Report(st["info"], results), result)
                    st["last_done"] = _done(Report(st["info"], results), result)
                    st["previous"] = result if result.metrics.mean_c is not None else None
                    if point_outcome == PointOutcome.Quit:
                        st["stopped"] = True
                        break
                    ti += 1
                # Characterise: with phase 1 done, change the adjustment and add phase 2.
                if st["stopped"] or not characterising or not plan_phase2():
                    break

            # Finished (or stopped): ask for the oven to be turned off, with the same reminders.
            if ask and not driver.is_simulation and results and not token.is_cancellation_requested:
                prompt.ask_turn_off("The session is stopped." if st["stopped"] else "All the setpoints are done.")
        except (_Cancelled, prompts.PromptCancelled):
            c.print("[yellow]Stopped.[/]")
        except KeyboardInterrupt:
            raise
        except Exception as ex:
            # Whatever went wrong, the setpoints already measured are written up below.
            failure = ex
            c.print(f"[red]The session stopped with an error: {ui.escape(str(ex))}[/]")
            if os.environ.get("OVENTRACKER_DEBUG"):
                traceback.print_exc()
        finally:
            # Each step on its own: a meter or link failing as it closes must not stop the results being written.
            for step in (
                lambda: oven.set_oven(OvenMode.Bake, None) if oven is not None else None,
                poller.stop,
                lambda: _drain_idle(poller, recorder),
                meter.dispose,
            ):
                try:
                    step()
                except Exception:
                    pass

        info = st["info"]
        plan = st["plan"]
        report = save_results()
        results = list(report.results)
    finally:
        recorder.close()
    c.print()
    if results:
        console_report.full(report)
    c.print()
    # [link]: a hyperlink to the folder in a terminal that shows them (none in a log).
    where = f"[link={_file_uri(out_dir)}]{ui.escape(out_dir)}[/link]" if ui.live() else ui.escape(out_dir)
    c.print(f"Saved to {where}: report.md, results.json and readings.csv.")
    if info.characterise is not None and info.characterise.changed_to_c is not None:
        save_characterisation_rich(info, results, out_dir, oven_profile, save=not driver.is_simulation)
    left = sum(
        1
        for p in plan
        if not any(
            r.point.index == p.index and r.metrics.reason in (CompletionReason.Converged, CompletionReason.TimedOut, CompletionReason.Manual)
            for r in results
        )
    )
    if left > 0:
        c.print(
            f"{left} setpoint{'' if left == 1 else 's'} still to do. To carry on later, even after using the oven: "
            f'[bold]{APP_NAME} run --resume "{ui.escape(out_dir)}"[/]'
        )
    c.print("[grey]Turn the oven off.[/]")
    if failure is not None:
        c.print(f"[red]Stopped by an error ({ui.escape(type(failure).__name__)}: {ui.escape(str(failure))}). The setpoints before it are saved.[/]")
        return 1
    return 0


class _Cancelled(Exception):
    """OperationCanceledException: Ctrl+C while waiting."""


def _file_uri(path: str) -> str:
    try:
        from pathlib import Path

        return Path(os.path.abspath(path)).as_uri()
    except ValueError:
        return path
