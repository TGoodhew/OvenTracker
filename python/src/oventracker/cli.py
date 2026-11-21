"""The command line (Program.cs and the commands' settings): run (the default), characterise, ovens, meters and
analyze, with the same options, help and examples as the C# app's ``oventracker``.

The help is laid out as Spectre.Console.Cli lays it out. Ctrl+C behaves as in Program.cs: the first stops the
session cleanly and still writes the report; a second ends the app."""

from __future__ import annotations

import signal
import sys
from dataclasses import dataclass
from typing import Any, Callable, Sequence

from . import __version__
from .commands.names import APP_NAME

_ANALYZE_HELP = "Re-run the analysis over saved runs, alone or together."


def _utf8_console() -> None:
    """Console.OutputEncoding = UTF8: the report's °, ± and − must print even when the output is redirected."""
    for stream in (sys.stdout, sys.stderr):
        try:
            "°±−–⣿╭".encode(stream.encoding or "ascii")
        except (UnicodeEncodeError, LookupError):
            try:
                stream.reconfigure(encoding="utf-8")  # type: ignore[union-attr]
            except Exception:
                pass


# The options ----------------------------------------------------------------------------------------


@dataclass(frozen=True)
class Opt:
    """One option: its names (e.g. "-u|--unit"), its value's name (None for a flag), its type, the settings
    field it sets, its description (markup; the trailing full stop is dropped in the help, as Spectre does),
    its default for the help, and whether it is hidden."""

    names: str
    value: str | None
    kind: str  # "str", "float", "int", "flag", "list"
    dest: str
    description: str = ""
    default: str | None = None
    hidden: bool = False

    @property
    def long(self) -> str:
        return next(n for n in self.names.split("|") if n.startswith("--"))

    @property
    def short(self) -> str | None:
        return next((n for n in self.names.split("|") if not n.startswith("--")), None)


SHARED = [
    Opt("--oven", "KEY", "str", "oven", f"The oven, so the app knows its adjustment's steps and range, how to read and change it, its convection conversion and how slowly it cycles: generic, ge-phs925, or one described in a file ({APP_NAME} ovens lists them). Remembered."),
    Opt("--probe", "NAME", "str", "probe", "The probe in use, so the report uses its tolerance and range: type-k (standard limits, the default for a type K meter), type-k-special, or 80bk-a (Fluke 80BK-A)."),
    Opt("--probe-tolerance", "SPEC", "str", "probe_tolerance", "A probe's tolerance from its datasheet: the greater of degrees and a percentage, e.g. 2.2C,2% or 4F,2% (or 0.3C+0.5% for the two added). Without C or F, in the oven's unit."),
    Opt("--probe-max", "TEMP", "str", "probe_max", "The highest temperature the probe is rated for, e.g. 260C or 500F. The app warns before a run if a setpoint would take it past that."),
    Opt("--adjustment", "DEG", "float", "adjustment", "The oven's thermostat adjustment (offset) setting during the run, in the oven's unit, e.g. -10. Recorded, so the app can say what to set it to, and can measure how far the adjustment moves the oven from runs at different settings."),
    Opt("--objective", "AIM", "str", "objective", "What the recommended adjustment makes smallest: balanced (the overall error, the default) or minimax (the worst setpoint's error)."),
    Opt("--favour", "TEMP", "float", "favour", "Make the oven right at this temperature, in the oven's unit (e.g. the one you bake at most), rather than across all of them."),
    Opt("--adjust-step", "DEG", "float", "adjust_step", "The oven's adjustment step, in its unit. Default 1."),
    Opt("--adjust-range", "DEG", "float", "adjust_range", "How far the oven's adjustment goes either way, in its unit. Default 35 °F / 19 °C (the GE PHS925 goes to ±35 °F)."),
    Opt("--adjust-model", "MODEL", "str", "adjust_model", "How the adjustment is assumed to act until runs at different settings measure it: shift (the same at every setting, the default) or gain (scaled by the oven's own error slope)."),
    Opt("--separate-adjustments", None, "flag", "separate_adjustments", "Bake and convection each have their own adjustment (most ovens have one for both)."),
]

RUN = SHARED + [
    Opt("--meter", "DRIVER", "str", "meter", f"Which kind of meter: u1253b (the default), 34401a (with a type K thermocouple), 121gw (EEVblog, Bluetooth LE), or simulated. [bold]{APP_NAME} meters[/] lists them and what each can see."),
    Opt("-r|--resource", "WHERE", "str", "resource", "Where the meter is, in the driver's terms: COM5 for the U1253B (found automatically if left out), GPIB0::22::INSTR or COM3 for the 34401A, ble:<address> for the 121GW. Remembered."),
    Opt("--meter-option", "KEY=VALUE", "list", "meter_options", "A setting for the meter's driver, e.g. cj=22C (the cold junction's temperature) or nplc=100 for the 34401A. Repeat it for more than one. Remembered for that meter."),
    Opt("-p|--port", "PORT", "str", "port", "The same as --resource: the name it had when there was only a serial meter.", hidden=True),
    Opt("--simulate", None, "flag", "simulate", "Use a simulated oven and meter instead of the real ones: the same as --meter simulated."),
    Opt("--speed", "X", "float", "speed", "With --simulate, how many times faster than real time to run.", "60"),
    Opt("-u|--unit", "F|C", "str", "unit", "The unit your oven's dial or display uses."),
    Opt("-m|--modes", "LIST", "str", "modes", "Modes to test: bake, convection, or bake,convection."),
    Opt("-t|--setpoints", "LIST", "str", "setpoints", "Setpoints to test, in the oven's unit, e.g. 350,400,450."),
    Opt("--grouped", None, "flag", "grouped", "Do every setpoint in bake, then every one in convection (the default alternates the modes at each setpoint, so the oven never has to cool down)."),
    Opt("--precision", "DEG", "float", "precision", "How closely to pin down each mean (95 % interval half-width), in the oven's unit. Default 2 °F / 1 °C."),
    Opt("--tolerance", "DEG", "float", "tolerance", "How far from the setting still counts as accurate, in the oven's unit. Default 15 °F / 8 °C."),
    Opt("--min-cycles", "N", "int", "min_cycles", "Fewest full thermostat cycles a result is based on.", "4"),
    Opt("--max-minutes", "MIN", "float", "max_minutes", "Longest to spend at one setpoint. Default 120, or longer for an oven known to cycle slowly (an hour plus six cycles: 180 for the GE PHS925)."),
    Opt("--interval", "SEC", "float", "interval", "Seconds between readings.", "1"),
    Opt("-o|--out", "DIR", "str", "out", "Folder for the readings and report. Default: OvenTracker/<date time> in your Documents folder (on Linux, ~/Documents, or ~ if there is none)."),
    Opt("--resume", "FOLDER", "str", "resume", "Carry on a run saved earlier: measure the setpoints it didn't finish, adding to the same folder and report. If the oven is still hot, the app waits for it to cool first."),
    Opt("--ambient", "TEMP", "float", "ambient", "Room temperature, in the oven's unit. Normally the probe's first reading with the oven cold; give it if the oven is hot when you start."),
    Opt("--sim-hot", "TEMP", "float", "sim_hot", "With --simulate, start as if the oven had just been used at this temperature, in the oven's unit.", hidden=True),
    Opt("--remind", "MINUTES", "float", "remind", "While waiting for the oven to be changed (or turned off), beep again every this many minutes. 0 for no repeats.", "1"),
    Opt("--sim-forget", "N", "int", "sim_forget", "With --simulate, leave the simulated oven at its old setting for setpoint N, as if you'd forgotten to change it.", hidden=True),
    Opt("--sim-apo", "MINUTES", "float", "sim_apo", "With --simulate, give the simulated meter an auto power-off after this many minutes (it wakes 2 minutes later).", hidden=True),
    Opt("--sim-battery", "MINUTES", "float", "sim_battery", "With --simulate, make the simulated meter's battery run down over this many minutes, its readings going wrong near the end; the app pauses for a swap.", hidden=True),
    Opt("-y|--yes", None, "flag", "yes", "Don't ask: start at once and begin each setpoint straight away (mainly for --simulate)."),
]

CHARACTERISE = RUN + [
    Opt("--step", "DEG", "float", "step", "The least change of adjustment to test, in the oven's unit. Bigger measures the gain better. Default 10. The app goes the way that brings the oven towards correct, and further if the best adjustment is further.", "10"),
]

OVENS = [
    Opt("--show", "KEY", "str", "show", "Show everything the app knows about one oven: how to read and change its adjustment, its convection conversion, measured gains and sources."),
    Opt("-u|--unit", "F|C", "str", "unit", "The unit to show temperatures in. Default °F."),
]

METERS = [
    Opt("--probe", None, "flag", "probe", "Ask each place a meter is likely to be (for the U1253B, each outgoing Bluetooth port) what it is."),
    Opt("--all", None, "flag", "all", "With --probe, ask every place, not just the likely ones (for the U1253B, every serial port)."),
    Opt("--meter", "DRIVER", "str", "meter", "Only look for this kind of meter."),
]

ANALYZE = SHARED + [
    Opt("--precision", "DEG", "float", "precision", "Re-run with a different precision target, in the oven's unit."),
    Opt("--tolerance", "DEG", "float", "tolerance", "Judge against a different tolerance, in the oven's unit."),
    Opt("--min-cycles", "N", "int", "min_cycles"),
    Opt("-o|--out", "DIR", "str", "out", "Folder for the report and results. Default: the (latest) run's own folder."),
    Opt("--save-gains", None, "flag", "save_gains", "Save the gains measured by these runs (the same setpoints at different adjustments) to the oven's profile (--oven), so later runs use them."),
]

_FOLDERS_HELP = (
    "One or more runs' folders (each with readings.csv and session.json), or readings.csv files. Several runs are analysed "
    "together: runs of the same setpoints at different thermostat adjustments measure how far the adjustment moves the oven."
)


@dataclass(frozen=True)
class Command:
    name: str
    aliases: tuple[str, ...]
    description: str
    examples: tuple[tuple[str, ...], ...]
    options: list[Opt]
    argument: str | None = None  # "FOLDERS" for analyze


COMMANDS = [
    Command(
        "run",
        (),
        "Measure the oven at each setpoint, in bake and convection (the default).",
        (
            ("run",),
            ("run", "--unit", "F", "--modes", "bake,convection", "--setpoints", "350,400,450"),
            ("run", "--meter", "u1253b", "--resource", "COM5"),
            ("run", "--simulate", "--speed", "120"),
        ),
        RUN,
    ),
    Command(
        "characterise",
        ("characterize",),
        "Measure how the oven's adjustment moves it: every setpoint, then (after you change the adjustment) the lowest and "
        "highest again. Saves the gains to the oven's profile.",
        (("characterise", "--oven", "ge-phs925", "--unit", "F", "--modes", "bake,convection", "--setpoints", "325,375,425"),),
        CHARACTERISE,
    ),
    Command(
        "ovens",
        (),
        "List the ovens the app knows (built in, and described in files), or show one in full.",
        (("ovens", "--show", "ge-phs925"),),
        OVENS,
    ),
    Command(
        "meters",
        ("ports",),
        "List the meters each kind of meter's driver can see, and which one answers.",
        (("meters", "--probe"),),
        METERS,
    ),
    Command(
        "analyze",
        (),
        _ANALYZE_HELP,
        (
            ("analyze", '"Documents\\OvenTracker\\2026-11-20 1400"', "--tolerance", "10"),
            ("analyze", '"Documents\\OvenTracker\\2026-11-20 1400"', '"Documents\\OvenTracker\\2026-11-22 0900"', "--favour", "350"),
        ),
        ANALYZE,
        "FOLDERS",
    ),
]


def find_command(name: str) -> Command | None:
    return next((c for c in COMMANDS if name == c.name or name in c.aliases), None)


# Parsing --------------------------------------------------------------------------------------------


class UsageError(Exception):
    pass


def _convert(opt: Opt, text: str) -> Any:
    from .session.recording import _try_float, _try_int

    if opt.kind == "float":
        v = _try_float(text)
        if v is None:
            raise UsageError(f"Failed to convert '{text}' to Double.")
        return v
    if opt.kind == "int":
        i = _try_int(text)
        if i is None:
            raise UsageError(f"Failed to convert '{text}' to Int32.")
        return i
    return text


def _looks_like_number(a: str) -> bool:
    from .session.recording import _try_float

    return _try_float(a) is not None


def parse(options: list[Opt], argv: Sequence[str], has_argument: bool) -> tuple[dict[str, Any], list[str], bool]:
    """The options given (by settings field), the arguments, and whether help was asked for. An unknown option is
    an error (Spectre.Console.Cli lets one through unnoticed, so a mistyped option is silently ignored)."""
    by_name: dict[str, Opt] = {}
    for o in options:
        for n in o.names.split("|"):
            by_name[n] = o
    values: dict[str, Any] = {}
    args: list[str] = []
    want_help = False
    i = 0
    argv = list(argv)
    while i < len(argv):
        a = argv[i]
        i += 1
        if a in ("-h", "--help", "-?"):
            want_help = True
            continue
        if a.startswith("-") and len(a) > 1 and not _looks_like_number(a):
            name, eq, inline = a.partition("=")
            opt = by_name.get(name)
            if opt is None:
                raise UsageError(f"Unknown option '{name.lstrip('-')}'.")
            if opt.kind == "flag":
                if eq:
                    if inline.lower() not in ("true", "false"):
                        raise UsageError(f"Failed to convert '{inline}' to Boolean.")
                    values[opt.dest] = inline.lower() == "true"
                else:
                    values[opt.dest] = True
                continue
            if eq:
                value = inline
            elif i < len(argv):
                value = argv[i]
                i += 1
            else:
                raise UsageError(f"Option '{opt.long.lstrip('-')}' is defined but no value has been provided.")
            if opt.kind == "list":
                values.setdefault(opt.dest, []).append(value)
            else:
                values[opt.dest] = _convert(opt, value)
            continue
        if not has_argument:
            raise UsageError(f"Unknown command '{a}'.")
        args.append(a)
    return values, args, want_help


# Help -----------------------------------------------------------------------------------------------


def _strip_period(text: str) -> str:
    return text[:-1] if text.endswith(".") else text


def print_help(command: Command | None) -> None:
    """Spectre.Console.Cli's help: DESCRIPTION, USAGE, EXAMPLES, ARGUMENTS, OPTIONS (with a DEFAULT column when
    an option has one) and, at the top level, COMMANDS."""
    from rich.table import Table
    from rich.text import Text

    from .ui import console as ui

    c = ui.get()
    options = command.options if command is not None else RUN
    if command is not None:
        c.print("[yellow]DESCRIPTION:[/]")
        c.print(Text(_strip_period(command.description)))
        c.print()
    c.print("[yellow]USAGE:[/]")
    if command is None:
        c.print(f"    {APP_NAME} [grey]\\[OPTIONS][/] [cyan]\\[COMMAND][/]")
    else:
        arg = f" [cyan]<{command.argument}>[/]" if command.argument else ""
        c.print(f"    {APP_NAME} [underline]{command.name}[/]{arg} [grey]\\[OPTIONS][/]")
        c.print()
        c.print("[yellow]EXAMPLES:[/]")
        for ex in command.examples:
            c.print(Text("    " + APP_NAME + " ") + Text(" ".join(ex), style="grey"))
    if command is not None and command.argument:
        c.print()
        c.print("[yellow]ARGUMENTS:[/]")
        g = Table.grid()
        g.add_column(no_wrap=True)
        g.add_column()
        g.add_row(Text.from_markup(f"    [silver]<{command.argument}>[/]    "), Text(_strip_period(_FOLDERS_HELP)))
        c.print(g)
    c.print()
    c.print("[yellow]OPTIONS:[/]")
    shown = [o for o in options if not o.hidden]
    rows: list[tuple[Text, str | None, Text]] = [(Text("-h, --help"), None, Text("Prints help information"))]
    if command is None:
        rows.append((Text("-v, --version"), None, Text("Prints version information")))
    for o in shown:
        t = Text((f"{o.short}, " if o.short else "    ") + o.long)
        if o.value is not None:
            t.append(" ")
            t.append(f"<{o.value}>", style="silver")
        rows.append((t, o.default, Text.from_markup(_strip_period(o.description))))
    defaults = any(o.default is not None for o in shown)
    g = Table.grid()
    g.add_column(no_wrap=True)
    if defaults:
        g.add_column(no_wrap=True)
    g.add_column()
    if defaults:
        g.add_row(Text(""), Text("DEFAULT", style="color(10)") + Text("    "), Text(""))
    for names, default, description in rows:
        first = Text("    ") + names + Text("    ")
        if defaults:
            g.add_row(first, (Text(default, style="bold") + Text("    ")) if default else Text(""), description)
        else:
            g.add_row(first, description)
    c.print(g)
    if command is None:
        c.print()
        c.print("[yellow]COMMANDS:[/]")
        cg = Table.grid()
        cg.add_column(no_wrap=True)
        cg.add_column()
        for cmd in COMMANDS:
            name = Text(cmd.name, style="silver")
            if cmd.argument:
                name.append(" ")
                name.append(f"<{cmd.argument}>", style="silver")
            cg.add_row(Text("    ") + name + Text("    "), Text(_strip_period(cmd.description)))
        c.print(cg)


# Running --------------------------------------------------------------------------------------------


def _settings(cls: type, values: dict[str, Any]) -> Any:
    s = cls()
    for k, v in values.items():
        setattr(s, k, v)
    return s


def _run_command(command: Command, values: dict[str, Any], args: list[str], token: Any) -> int:
    if command.name in ("run", "characterise"):
        from .commands.run import CharacteriseSettings, RunSettings, execute

        cls = CharacteriseSettings if command.name == "characterise" else RunSettings
        return execute(_settings(cls, values), token)
    if command.name == "ovens":
        from .commands.ovens import OvensSettings
        from .commands.ovens import execute as ovens_execute

        return ovens_execute(_settings(OvensSettings, values))
    if command.name == "meters":
        from .commands.meters import MetersSettings
        from .commands.meters import execute as meters_execute

        return meters_execute(_settings(MetersSettings, values))
    from .commands.analyze import AnalyzeSettings
    from .commands.analyze import execute as analyze_execute

    if not args:
        raise UsageError("Command 'analyze' is missing required argument 'FOLDERS'.")
    return analyze_execute(_settings(AnalyzeSettings, {**values, "paths": args}))


def _install_ctrl_c(token: Any) -> Callable[[], None]:
    """The first Ctrl+C stops the session cleanly and still writes the report; a second one ends the app."""

    def handler(signum: int, frame: Any) -> None:
        from .ui import prompts

        if token.is_cancellation_requested:
            from .ui.keys import KEYS

            KEYS.restore()
            raise KeyboardInterrupt
        token.cancel()
        if prompts.BLOCKING:
            raise KeyboardInterrupt  # a typed answer is awaited: the prompt is cancelled now, not at the next key

    try:
        before = signal.signal(signal.SIGINT, handler)
    except ValueError:  # not the main thread (a test)
        return lambda: None
    return lambda: signal.signal(signal.SIGINT, before)


def main(argv: Sequence[str] | None = None) -> int:
    _utf8_console()
    argv = list(sys.argv[1:] if argv is None else argv)

    # analyze needs only the standard library: without Rich it still runs, with a short summary.
    if argv and argv[0] == "analyze":
        try:
            import rich  # noqa: F401
        except ImportError:
            return _plain_analyze(argv[1:])

    try:
        import rich  # noqa: F401
    except ImportError:
        print("OvenTracker needs Rich to draw its console: pip install rich (it comes with pip install ./python).", file=sys.stderr)
        return 1
    from .session.cancel import CancellationToken
    from .ui import console as ui
    from .ui.keys import KEYS

    c = ui.get()
    if argv and argv[0] in ("-v", "--version"):
        c.print(__version__, markup=False)
        return 0
    command = find_command(argv[0]) if argv and not argv[0].startswith("-") else None
    if argv and not argv[0].startswith("-") and command is None:
        c.print(f"[red]Error:[/] Unknown command '{ui.escape(argv[0])}'.")
        c.print()
        c.print(f"       {ui.escape(argv[0])}")
        c.print(f"       [red]{'^' * len(argv[0])} No such command[/]")
        c.print()
        return -1
    rest = argv[1:] if command is not None else argv
    effective = command if command is not None else COMMANDS[0]  # run is the default
    try:
        values, args, want_help = parse(effective.options, rest, effective.argument is not None)
    except UsageError as ex:
        c.print(f"[red]Error:[/] {ui.escape(str(ex))}")
        return -1
    if want_help:
        print_help(command)
        return 0

    token = CancellationToken()
    restore = _install_ctrl_c(token)
    try:
        return _run_command(effective, values, args, token)
    except UsageError as ex:
        c.print(f"[red]Error:[/] {ui.escape(str(ex))}")
        return -1
    except KeyboardInterrupt:
        return 130
    finally:
        KEYS.restore()
        restore()


def _plain_analyze(argv: list[str]) -> int:
    """analyze without Rich: the same options, and a short summary on the console."""
    from .commands.analyze import AnalyzeSettings, execute

    try:
        values, args, want_help = parse(ANALYZE, argv, True)
    except UsageError as ex:
        print(f"Error: {ex}", file=sys.stderr)
        return -1
    if want_help:
        print(f"USAGE:\n    {APP_NAME} analyze <FOLDERS> [OPTIONS]\n\n{_strip_period(_ANALYZE_HELP)}\n\nOPTIONS:")
        for o in ANALYZE:
            value = f" <{o.value}>" if o.value else ""
            print(f"    {o.names.replace('|', ', ')}{value}    {_strip_period(o.description)}")
        return 0
    if not args:
        print("Error: Command 'analyze' is missing required argument 'FOLDERS'.", file=sys.stderr)
        return -1
    return execute(_settings(AnalyzeSettings, {**values, "paths": args}), sys.stdout, sys.stderr)


if __name__ == "__main__":
    sys.exit(main())
