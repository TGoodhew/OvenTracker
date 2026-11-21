# OvenTracker for Python

OvenTracker in Python. It measures how accurately an oven holds its setpoints, works out what to set its
thermostat adjustment to, and re-analyses saved runs. It's the same app as the C# one in this repository, with
the same commands, options, screens and files, drawn with [Rich](https://github.com/Textualize/rich) instead of
Spectre.Console. It runs on Python 3.11 or later, on Windows, Linux and macOS.

| Command | What it does |
| --- | --- |
| `oventracker-py run` (the default) | Measure the oven at each setpoint, in bake and convection, with the live display. |
| `oventracker-py characterise` | Measure how the oven's adjustment moves it, and save the gains to its profile. |
| `oventracker-py analyze <runs…>` | Re-run the analysis over saved runs, alone or together. |
| `oventracker-py ovens` | List the oven profiles, or show one (`--show ge-phs925`). |
| `oventracker-py meters` | List the meters each driver can see (`--probe` asks them). |

The meters:
- **Keysight U1253B:** through a U1177A Bluetooth adapter or a U1173 cable, on a serial port (through pyserial).
- **HP/Agilent 34401A with a type K thermocouple:** `--meter 34401a`, on GPIB (`--resource GPIB0::22::INSTR`)
  or RS-232 (`--resource COM3`, through pyserial). It reads the thermocouple's voltage on the 100 mV DC range
  and converts it with the NIST type K reference function, as the C# app does.
  - Give the cold junction's temperature (the room's, where the thermocouple meets the meter's terminals) with
    `--meter-option cj=22C` or `cj=72F`. Without it the app assumes 23 °C and says so.
  - `--meter-option nplc=` sets the integration time.
  - Both are remembered, and the run's `session.json` records them (`meterOptions`).
  - GPIB goes through PyVISA, an optional extra: `pip install "./python[visa]"`, with a VISA library (NI-VISA
    or Keysight IO Libraries Suite) installed. Without them, the app says what's missing.
  - The main README's "The 34401A with a type K thermocouple" section has the rest.
- **EEVblog 121GW:** `--meter 121gw`, over Bluetooth LE, in its temperature mode with a type K thermocouple.
  - Bluetooth LE goes through bleak, an optional extra: `pip install "./python[ble]"`. Without it, the app
    says what to install.
  - The meter needn't be paired: the app scans for it for a few seconds (`--resource ble:<address>` goes
    straight to it), on Windows, Linux (through BlueZ, so `bluetoothd` must be running) and macOS.
  - `meters --meter 121gw` lists the Bluetooth LE devices a scan finds, the 121GWs first.
  - The main README's "The EEVblog 121GW" section has the rest.
- **The simulator:** `--simulate`.

## Install

From a clone of the repository:

```
pip install ./python          # or, to keep it apart from other packages: pipx install ./python
```

It needs `rich` and `pyserial`, which pip installs. For a 34401A on GPIB, add PyVISA with
`pip install "./python[visa]"`; for a 121GW, add bleak with `pip install "./python[ble]"`. On Linux the U1177A pairs and binds as for the C# app; see
the main README's Linux section.

## Try it without a meter

```
oventracker-py run --simulate --speed 600
oventracker-py run --simulate --speed 600 --sim-hot 450 --unit F --modes bake,convection --setpoints 350,400
oventracker-py characterise --simulate -y --speed 600 --unit F --modes bake --setpoints 325,425
```

- The first asks for the unit, modes and setpoints, then runs a simulated oven 600 times faster than real time.
- The second starts with the oven still hot, so the change prompt offers to wait for it to cool.
- `python -m oventracker …` does the same as `oventracker-py …`. To run it from a clone without installing it
  (on Linux or WSL, with Rich installed):

  ```
  PYTHONPATH=python/src python3 -m oventracker run --simulate --speed 600
  ```

## How it relates to the C# app

The C# app is the reference implementation; this package follows it. The two are kept aligned by the
specification in `spec/` and the shared test data in `testdata/`:
- each implementation replays every recording there;
- CI checks that the two give the same files;
- `testdata/` includes runs recorded by each app, so each analyses the other's.

The two are interchangeable in practice:
- a run recorded by either reads the same in both;
- they share the settings file (`settings.json`) and the oven profiles (`Documents/OvenTracker/ovens`).

A few things differ on purpose:
- The help and hints say `oventracker-py`.
- An unknown option is an error. The C# app ignores it.
- The simulator's random numbers differ.

## Tests

```
pip install "./python[test]"
python -m pytest python/tests -q
```

The tests cover:
- **the shared test data;**
- **the engine:** a whole simulated session with no terminal, which must write all four files with no cursor
  codes;
- **the screens;**
- **the C# app, where .NET is installed:** the console report compared with the C# app's, and the C# app's
  `analyze` of a Python-recorded run.
