# Changelog

Both implementations share one version number: the C# app (`src/`) and the Python package (`python/`, the
analysis only for now). Each entry says which implementations have the change: **both**, **C#** (the Python
port doesn't cover it, or a `parity` issue is open), or **Python**.

## Unreleased

- Consistency fixes in the analysis and recording (#36). Has: **both**.
  - **Recording:**
    - `readings.csv` keeps full precision, so re-analysing a run gives exactly its live results.
    - Event names are cleaned like `raw`.
    - A simulation's events are on its own clock, so the file stays in time order.
  - **Re-analysis:**
    - It keeps the recorded reason, rather than calling a point "finished early by you".
    - Results are in order of index.
    - Combining runs keeps an unknown adjustment unknown.
    - A recording with no `session.json` gets the °F defaults.
    - A resumed run keeps the earlier sittings' unfinished points.
  - **Cycles:** a trough within 20 s of the last now joins the next cycle instead of being lost.
  - **The recommended adjustment:**
    - The same test for precision everywhere, and the same notion of "the same setpoint".
    - The "Within tolerance?" column judges a missing ±95 % as the verdict does.
    - A measured gain whose points lack a ±95 % has an unknown uncertainty.
    - Without the current adjustment, the search no longer assumes it's 0.
    - `--favour` with a shared adjustment weighs each mode once, with its own gain.
    - `--favour` and `--objective` can't be given together.
    - Each mode's reason for more setpoints is kept.

- The whole app in Python, drawn with Rich: `run`, `characterise`, `analyze`, `ovens` and `meters`, with the
  U1253B and the simulator. It has the same screens as the C# app, checked against the C# app's own rendering,
  and the same recording files. Has: **Python** (the 34401A and 121GW drivers: **C#** only, #38 and #39).
- Python port of the analysis: `oventracker-py analyze` reads a run's `readings.csv` and `session.json` and
  writes the same `results.json` and report as the C# app. Has: **Python**.
- `spec/`: the recording formats, with JSON Schemas, and the analysis, written down for both
  implementations. `testdata/`: recorded runs with their expected results, which both must reproduce. Has: **both**.
- `analyze --out <DIR>`, and `analyze` also writes `results-reanalysed.json` (or `results-combined.json`). Has: **both**.
- A driver for the EEVblog 121GW over Bluetooth LE, on Windows and (through BlueZ) Linux. Has: **C#**.
- A driver for the HP/Agilent 34401A with a PT100, through GPIB or RS-232, and `--meter-option`. Has: **C#**.
- The report shows times in the offset they were recorded with, not the PC's time zone. Has: **both**.
- Choose the connection type with `--resource` (serial settings, GPIB, VISA). Has: **C#**.
- `characterise`, oven profiles, and the best adjustment from measured gains. Has: **C#** (the analysis of a
  characterise run's readings: **both**).

## 1.0.0

- The first version: the U1253B through a U1177A, bake and convection, the live display, the report and the
  recommended adjustment. Has: **C#**.
