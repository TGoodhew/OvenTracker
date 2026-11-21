# Shared test data

`testdata/` holds recorded runs and what the analysis must make of them. Both implementations replay every
case in their tests: the C# app in `tests/OvenTracker.Tests/SharedTestDataTests.cs`, the Python port in
`python/tests/test_shared_testdata.py`. CI also compares the two implementations' outputs directly
(`testdata/tools/compare.py`).

## Layout

```
testdata/
  runs/<run>/readings.csv, session.json   recordings, exactly as the app wrote them (or made by tools/)
  cases/<case>/args.txt                   the arguments to analyze, one per line ("runs/…" is relative to testdata/)
  cases/<case>/expected/                  what analyze --out writes: report-*.md, results-*.json, and for a
                                          characterise run characterisation.md and characterisation.json
  regenerate.ps1                          rewrites every expected/ folder from the C# app
  tools/make_synthetic.py                 makes the synthetic runs
  tools/compare.py                        compares two output folders within the tolerances below
```

## The runs

| Run | What it covers |
| --- | --- |
| `real-2026-11-20-1118` | A real U1253B and U1177A on a GE PHS925, bake at 350 °F, stopped after two cycles (Aborted). |
| `sim-bake-convection-f` | The simulator, °F, bake then convection at 350 and 400 °F, grouped, so convection 350 follows bake 400 and the oven has to cool to a lower setpoint. |
| `sim-bake-c` | The simulator, °C, bake at 180 and 220 °C, with the GE PHS925 profile. |
| `sim-bake-c-adj-5` | The same as `sim-bake-c`, with the adjustment at −5 °C, for the combined case. |
| `sim-started-warm` | The simulator starting hot (450 °F) at an adjustment of +5: the reset wait, and a point that started warm. |
| `sim-characterise` | A characterise run: three setpoints, the adjustment changed, then the highest and lowest again. |
| `synthetic-no-cycle` | Made by `tools/make_synthetic.py`: heats and then holds steady with no visible cycle, and ends with no end event. |
| `synthetic-manual-finish` | `sim-bake-c` cut 60 % of the way through its first setpoint, finished by hand (`end:Manual`). |
| `py-sim-bake-convection-f` | Recorded by the **Python** app's simulator, °F, bake and convection, so the C# app is checked on the Python app's recordings too. |

The cases replay each run alone. Some also change options (`--precision`, `--tolerance`, `--probe`,
`--favour`), and `combined` analyses two runs together.

The simulators' random numbers differ between implementations, so the cases use recorded files, never a live
simulation.

## Matching

The C# app is the reference: `expected/` comes from it. A difference is a bug in the port, unless the spec
says otherwise.

- **results-*.json and characterisation.json:** the same keys and structure.
  - Strings, enums, booleans, integers and nulls are exactly equal.
  - Other numbers are equal within 1e-6 absolute or 1e-9 relative, whichever is looser. This allows for
    differences in the last bits of library functions such as `exp` and `log`. The Student-t quantile is a
    fixed table in the spec, not a library function, so it must match exactly.
- **report-*.md and characterisation.md:** equal line by line once line endings are normalised. The one
  exception is a line whose only differences are numbers one unit apart in their last shown digit. That's
  a value rounded either side of a boundary, and it's allowed only if the JSON matches.

## Changing the expected results

After a deliberate change to the analysis or the report:

1. Run `pwsh testdata/regenerate.ps1`.
2. Read the diff.
3. Commit it with the change.
4. Bring the Python port into line in the same PR, or open a follow-up issue labelled `parity`.
