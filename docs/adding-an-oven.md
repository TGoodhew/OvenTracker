# Describing your oven

OvenTracker works with any oven. It works better when it knows yours:
- the steps and range of its thermostat adjustment, and whether bake and convection share it;
- how to read and change the adjustment;
- whether it quietly lowers convection settings;
- how slowly its thermostat cycles.

These facts make up an **oven profile**. Choose one with `--oven <key>`; the app remembers it for later runs,
and saves it with each run so `analyze` and `--resume` use it too.

```powershell
OvenTracker ovens                      # the ovens the app knows
OvenTracker ovens --show ge-phs925     # everything about one
OvenTracker run --oven ge-phs925 ...
```

## What a profile does

| From the profile | Used for |
| --- | --- |
| Adjustment step and range | The best adjustment is rounded to the oven's steps and kept within its range (unless `--adjust-step` / `--adjust-range` say otherwise). |
| One adjustment for both modes | Whether the best adjustment suits bake and convection together, or each on its own. |
| How to read it | Shown before the run, with the question about the oven's current adjustment. |
| How to change it | Shown in the report's *Best adjustment*: "To set it on the GE Profile PHS925ST1SS: 1. Touch BROIL HI/LO and BAKE …". |
| Convection conversion | The "Before you start" note: by how much, and how to turn it off for the test. |
| Typical thermostat cycle | The default time per setpoint: at least an hour plus six cycles (180 minutes for a 20-minute cycle) instead of 120. |
| Measured gains | How far one degree of adjustment really moves the oven, per mode. The best adjustment uses them instead of assuming 1:1. `characterise` measures them and saves them to the profile. |

Without `--oven`, the **generic** profile is used. It assumes 1° steps up to ±35 °F (common on electronic ovens),
one adjustment for both modes, and no conversion, and it points to the oven's manual for how to change the
adjustment.

## Built in

| `--oven` | Oven | Source |
| --- | --- | --- |
| `generic` | any oven | general practice |
| `ge-phs925` | GE Profile PHS925ST1SS | owner's manual pp. 15, 17, 26; measured on the oven |

## Adding yours

Put a JSON file in the `ovens` folder beside your runs: `Documents\OvenTracker\ovens\` on Windows,
`~/Documents/OvenTracker/ovens/` on Linux. `OvenTracker ovens` prints the exact folder. Use one file per
oven; the file name doesn't matter, but the `key` does. A file with the same key as a built-in profile
replaces it, which is how measured gains are saved.

```json
{
  "key": "whirlpool-wfe505",
  "name": "Whirlpool WFE505W0HS",
  "adjustStepC": 0.5556,
  "adjustRangeC": 16.667,
  "sharedAdjustment": true,
  "howToRead": [
    "Press and hold Bake for 5 seconds.",
    "The display shows the offset; press Cancel to leave."
  ],
  "howToChange": [
    "Press and hold Bake for 5 seconds.",
    "Use the number pads to enter the offset (−30 to +30 °F).",
    "Press Start to save it."
  ],
  "convectionConversionC": 13.889,
  "howToTurnOffConversion": [
    "Press Settings, choose Convect Conversion, choose Off."
  ],
  "typicalCycleMinutes": 8,
  "notes": ["The offset applies to every mode."],
  "source": "Whirlpool WFE505W0HS use and care guide, p. 12"
}
```

The example's values are an illustration, not checked against that oven. Use your own oven's manual.

| Field | | Notes |
| --- | --- | --- |
| `key` | required | What `--oven` takes: lower case, no spaces. |
| `name` | required | For people. |
| `adjustStepC`, `adjustRangeC` | required | In °C. For °F, divide by 1.8: 1 °F = 0.5556, 30 °F = 16.667, 35 °F = 19.444. |
| `sharedAdjustment` | default `true` | `false` if bake and convection each have their own. |
| `howToRead`, `howToChange` | optional | One step per item, as the oven's manual gives them. Say how the display shows the sign, if the manual doesn't. |
| `convectionConversionC` | optional | How much the oven lowers a convection setting by itself (25 °F = 13.889 °C). |
| `howToTurnOffConversion` | optional | Steps to turn that off. |
| `typicalCycleMinutes` | optional | How often the thermostat cycles. A run's report gives it, as *Cycle* in the setpoint table. |
| `gains` | written by `characterise` | Measured gains per mode, with uncertainty and date. Leave it out. |
| `notes` | optional | Anything else worth knowing, e.g. what the adjustment doesn't affect. |
| `source` | optional, but please give it | Where the facts come from: manual and pages, or what you measured. |

`OvenTracker ovens` warns about a file it can't read. If a built-in profile would suit other owners of
that oven, it belongs in `Ovens/OvenProfile.cs`: send a pull request with the manual's pages as the
source.

## Where to find the values

- **The adjustment** is usually under *"Adjust the oven thermostat"*, *"Temperature offset"*, *"Oven
  calibration"* or *"Oven temperature adjustment"* in the use and care guide. It's often in *Special
  features* or *Settings*.
- **Convection conversion** is often called *Auto Recipe Conversion*, *Convect Conversion* or *Auto
  Convection Conversion*. Most convert by 25 °F.
- **The cycle** comes from a first run: the report's *Cycle* column.
