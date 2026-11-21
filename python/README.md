# OvenTracker for Python (the analysis)

A Python port of OvenTracker's analysis. `oventracker-py analyze` reads a run that the OvenTracker app recorded
(`readings.csv` and `session.json`) and writes the same report and results as the app's own `analyze`: the
Markdown report, the results JSON and, for a characterise run, the characterisation.

It has no dependencies beyond the Python standard library, and runs on Python 3.11 or later.

The C# app is the reference implementation. This package follows it, and the shared test data in `testdata/`
checks that the two agree (see `spec/`, in particular `spec/testdata.md`). Measuring an oven (`run`,
`characterise`), the meters and the live display are only in the C# app.

## Install

From a clone of the repository:

```
pip install ./python
```

or, to keep it apart from other packages:

```
pipx install ./python
```

## Use

```
oventracker-py analyze <FOLDERS...> [options] [--out DIR]
```

`python -m oventracker analyze ...` does the same. The options are the C# app's, with the same meanings:

| Option | What it does |
| --- | --- |
| `--precision <DEG>` | Re-run with a different precision target, in the oven's unit. |
| `--tolerance <DEG>` | Judge against a different tolerance, in the oven's unit. |
| `--min-cycles <N>` | Fewest full thermostat cycles a result is based on. |
| `-o`, `--out <DIR>` | Folder for the report and results. Default: the (latest) run's own folder. |
| `--oven <KEY>` | The oven: `generic`, `ge-phs925`, or one described in a file in `Documents/OvenTracker/ovens`. |
| `--probe <NAME>` | The probe: `type-k`, `type-k-special` or `80bk-a`. |
| `--probe-tolerance <SPEC>`, `--probe-max <TEMP>` | A probe's tolerance (e.g. `2.2C,2%`) and rated maximum (e.g. `260C`). |
| `--adjustment <DEG>` | The oven's thermostat adjustment during the run. |
| `--objective <AIM>`, `--favour <TEMP>` | What the recommended adjustment makes smallest (`balanced` or `minimax`), or a temperature to make right. |
| `--adjust-step`, `--adjust-range`, `--adjust-model`, `--separate-adjustments` | How the oven's adjustment works. |
| `--save-gains` | Accepted, but not supported yet: use the C# app to save gains to an oven's profile. |

One run writes `report-reanalysed.md` and `results-reanalysed.json`; several runs together write
`report-combined.md` and `results-combined.json`. A characterise run also writes `characterisation.md` and
`characterisation.json`. For example:

```
oventracker-py analyze testdata/runs/sim-bake-c --out out
```

The report's times are shown in the computer's own time zone, as the app shows them.

## Tests

```
pip install "./python[test]"
python -m pytest python/tests -q
```

The tests replay every case in `testdata/cases` and compare the output with the C# app's, using
`testdata/tools/compare.py`.
