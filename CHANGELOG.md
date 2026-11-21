# Changelog

Both implementations share one version number: the C# app (`src/`) and the Python package (`python/`, the
analysis only for now). Each entry says which implementations have the change: **both**, **C#** (the Python
port doesn't cover it, or a `parity` issue is open), or **Python**.

## Unreleased

- The UNI-T UT60BT, over Bluetooth LE: `--meter ut60bt` (#54). Has: **both**.
  - **How it reads:** in its temperature mode (°C or °F) with a type K thermocouple. The app asks the meter for
    its name, then for data. It retries with the other known data command while a request goes unanswered,
    since the open-source decoders disagree on which one to use.
  - **Battery:** its low-battery sign pauses the run for a battery swap.
  - **Auto power-off:** 15 minutes. The meter doesn't report it, so the app always warns, with the steps from
    the manual.
  - **Bluetooth LE links:** they can now write to the meter as well as listen.
  - **Not tried on a real meter yet.**

- Battery swaps without starting the setpoint again (#51). Has: **both**.
  - **What pauses it:** a battery meter running low (checked every minute), a reading jumping faster than an
    oven can change, or **B**.
  - **What happens:** the app tells you to swap the battery, then carries on with the same setpoint, its cycles
    so far kept.
  - **Gaps:** in the analysis, a gap of more than two minutes in the readings breaks the cycle it falls in. That
    cycle is left out, rather than joined across the gap. So the real 20 November run now has one clean cycle;
    its second "cycle" crossed a 16-minute gap.

- An oven that heats in steps (its elements taking turns, with dips) no longer has a step taken for reaching the
  setting and the rest for thermostat cycles: a turn short of the setting is withdrawn if the oven goes on well
  past it (#49). Found on a GE PHS925. Has: **both**.

- The Python app has the EEVblog 121GW, through bleak (`pip install "./python[ble]"`), on Windows, Linux and
  macOS, with no pairing needed (#39). Both apps: a 121GW reading can no longer be skipped, or take the next
  packet's time, when a packet arrives just as it's read; a link that fails part-way through opening is
  released; and the not-found help isn't printed twice. Has: **both**.

- The 34401A reads a type K thermocouple instead of a PT100: DC millivolts with cold-junction compensation
  (`--meter-option cj=22C`, 23 °C assumed when left off). The probe is a generic type K, and `--probe` picks a
  specific one. The run records its meter options in `session.json` (`meterOptions`). Has: **C#** (the Python
  app's 34401A is #38).

- Display fixes (#41). Has: **both**.
  - The big "set the adjustment" prompt keeps its minus sign: it showed "10 F" for −10 °F.
  - The dashboard fits the window: it's measured, not estimated, and a small window gets a compact layout.
  - With output redirected, "the oven is ready" is said once, not on every pass, and "ready in about under a minute" reads properly.
  - `analyze` of a real characterise run says to add `--save-gains`, not that it's a simulation.
  - A U1253B reading taken as the dial moves is judged by the new setting rather than lost.

- Stopping a session no longer crashes the app if the meter is busy (a Bluetooth write timing out after Stop), and `results.json` and `report.md` are saved as each setpoint finishes, so a crash, a closed window or a second Ctrl+C loses at most the setpoint in progress (#43). Has: **both**.

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
