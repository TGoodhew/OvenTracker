# OvenTracker specification

OvenTracker has two implementations in this repository:

- **The C# app** (`src/`): the whole tool. It covers the meters, the live display, `run`, `characterise` and
  `analyze`, on Windows and Linux. It is the **reference implementation**.
- **The Python package** (`python/`): the analysis only, for now. Its `analyze` reads a run's recording and
  writes the same results and report. It's for people who'd rather `pip install` than download a program, or
  who want to use the analysis from their own scripts or notebooks.

They stay aligned by this specification and the shared test data, not by reading each other's code.

| Document | What it covers |
| --- | --- |
| [recording.md](recording.md) | The files a run writes: `readings.csv` (columns and events), `session.json`, `results.json`. |
| [session.schema.json](session.schema.json), [results.schema.json](results.schema.json) | JSON Schemas for those files. |
| [analysis.md](analysis.md) | How a setpoint's readings become a result: smoothing, phases, turning points, cycles and blocks, the mean and its interval, the trend test, the stopping rule, the verdict, sensor tolerances, combining runs, the recommended adjustment and characterisation. |
| [testdata.md](testdata.md) | The shared test data, and how closely the two implementations must agree. |

The U1253B's protocol is described in the main README ("Protocol notes").

## The rules

1. **C# is the reference.** `testdata/*/expected` comes from the C# app. When the two disagree, the port is
   wrong, unless this spec says otherwise. Then the C# app is fixed and the expected files regenerated.
2. **A change to covered behaviour updates the spec, the test data and both implementations**, or opens a
   follow-up issue labelled `parity`. The pull request template asks.
3. **One version.** Both carry the same version number and are released together.
   `CHANGELOG.md` says which implementations each change is in.
