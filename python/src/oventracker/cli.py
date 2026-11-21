"""The command line: ``oventracker-py analyze <FOLDERS...> [options]``, as the C# app's ``oventracker analyze``."""

from __future__ import annotations

import argparse
import sys
from typing import Sequence

from . import __version__
from .commands.analyze import AnalyzeSettings, execute

_ANALYZE_HELP = "Re-run the analysis over saved runs, alone or together."


def _utf8_console() -> None:
    """Console.OutputEncoding = UTF8: the report's °, ± and − must print even when the output is redirected."""
    for stream in (sys.stdout, sys.stderr):
        try:
            "°±−–".encode(stream.encoding or "ascii")
        except (UnicodeEncodeError, LookupError):
            try:
                stream.reconfigure(encoding="utf-8")  # type: ignore[union-attr]
            except Exception:
                pass


def _top_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="oventracker-py",
        description="OvenTracker's analysis in Python. The C# app (oventracker) is the reference implementation.",
        epilog="Commands: analyze (" + _ANALYZE_HELP + ") See oventracker-py analyze --help.",
    )
    parser.add_argument("--version", action="version", version=__version__)
    parser.add_argument("command", nargs="?", metavar="COMMAND", help="analyze")
    return parser


def _analyze_parser() -> argparse.ArgumentParser:
    # Its own parser, so the folders and the options can come in any order (parse_intermixed_args).
    a = argparse.ArgumentParser(prog="oventracker-py analyze", description=_ANALYZE_HELP)
    a.add_argument(
        "paths",
        nargs="+",
        metavar="FOLDERS",
        help="One or more runs' folders (each with readings.csv and session.json), or readings.csv files. "
        "Several runs are analysed together: runs of the same setpoints at different thermostat adjustments "
        "measure how far the adjustment moves the oven.",
    )
    a.add_argument("--precision", type=float, metavar="DEG", help="Re-run with a different precision target, in the oven's unit.")
    a.add_argument("--tolerance", type=float, metavar="DEG", help="Judge against a different tolerance, in the oven's unit.")
    a.add_argument("--min-cycles", type=int, metavar="N", dest="min_cycles")
    a.add_argument("-o", "--out", metavar="DIR", help="Folder for the report and results. Default: the (latest) run's own folder.")
    a.add_argument(
        "--save-gains",
        action="store_true",
        dest="save_gains",
        help="Save the measured gains to the oven's profile. Not supported by the Python port yet: use the C# app.",
    )
    a.add_argument("--oven", metavar="KEY", help="The oven, so the app knows its adjustment's steps and range: generic, ge-phs925, or one described in a file.")
    a.add_argument("--probe", metavar="NAME", help="The probe in use: type-k, type-k-special, or 80bk-a (Fluke 80BK-A).")
    a.add_argument("--probe-tolerance", metavar="SPEC", dest="probe_tolerance", help="A probe's tolerance, e.g. 2.2C,2%% or 0.3C+0.5%%.")
    a.add_argument("--probe-max", metavar="TEMP", dest="probe_max", help="The highest temperature the probe is rated for, e.g. 260C or 500F.")
    a.add_argument("--adjustment", type=float, metavar="DEG", help="The oven's thermostat adjustment (offset) setting during the run, in the oven's unit.")
    a.add_argument("--objective", metavar="AIM", help="What the recommended adjustment makes smallest: balanced (the default) or minimax.")
    a.add_argument("--favour", type=float, metavar="TEMP", help="Make the oven right at this temperature, in the oven's unit.")
    a.add_argument("--adjust-step", type=float, metavar="DEG", dest="adjust_step", help="The oven's adjustment step, in its unit. Default 1.")
    a.add_argument("--adjust-range", type=float, metavar="DEG", dest="adjust_range", help="How far the oven's adjustment goes either way, in its unit.")
    a.add_argument("--adjust-model", metavar="MODEL", dest="adjust_model", help="shift (the default) or gain.")
    a.add_argument(
        "--separate-adjustments",
        action="store_true",
        dest="separate_adjustments",
        help="Bake and convection each have their own adjustment.",
    )
    return a


def main(argv: Sequence[str] | None = None) -> int:
    _utf8_console()
    argv = list(sys.argv[1:] if argv is None else argv)
    if not argv or argv[0] != "analyze":
        top = _top_parser()
        args = top.parse_args(argv[:1])
        if args.command is None:
            top.print_help()
        else:
            print(f"Unknown command: {args.command}.", file=sys.stderr)
        print("\nOnly analyze is in the Python port; run, characterise, ovens and meters are in the C# app.", file=sys.stderr)
        return 1
    args = _analyze_parser().parse_intermixed_args(argv[1:])
    settings = AnalyzeSettings(
        paths=list(args.paths),
        precision=args.precision,
        tolerance=args.tolerance,
        min_cycles=args.min_cycles,
        out=args.out,
        save_gains=args.save_gains,
        oven=args.oven,
        probe=args.probe,
        probe_tolerance=args.probe_tolerance,
        probe_max=args.probe_max,
        adjustment=args.adjustment,
        objective=args.objective,
        favour=args.favour,
        adjust_step=args.adjust_step,
        adjust_range=args.adjust_range,
        adjust_model=args.adjust_model,
        separate_adjustments=args.separate_adjustments,
    )
    return execute(settings)


if __name__ == "__main__":
    sys.exit(main())
