# Changelog

Both implementations share one version number: the C# app (`src/`) and the Python package (`python/`, the
analysis only for now). Each entry says which implementations have the change: **both**, **C#** (the Python
port doesn't cover it, or a `parity` issue is open), or **Python**.

## Unreleased

- Python port of the analysis: `oventracker-py analyze` reads a run's `readings.csv` and `session.json` and
  writes the same `results.json` and report as the C# app. Has: **Python**.
- `spec/`: the recording formats, with JSON Schemas, and the analysis, written down for both
  implementations. `testdata/`: recorded runs with their expected results, which both must reproduce. Has: **both**.
- `analyze --out <DIR>`, and `analyze` also writes `results-reanalysed.json` (or `results-combined.json`). Has: **both**.
- A driver for the EEVblog 121GW over Bluetooth LE (Windows; Linux is #34). Has: **C#**.
- A driver for the HP/Agilent 34401A with a PT100, through GPIB or RS-232, and `--meter-option`. Has: **C#**.
- The report shows times in the offset they were recorded with, not the PC's time zone. Has: **both**.
- Choose the connection type with `--resource` (serial settings, GPIB, VISA). Has: **C#**.
- `characterise`, oven profiles, and the best adjustment from measured gains. Has: **C#** (the analysis of a
  characterise run's readings: **both**).

## 1.0.0

- The first version: the U1253B through a U1177A, bake and convection, the live display, the report and the
  recommended adjustment. Has: **C#**.
