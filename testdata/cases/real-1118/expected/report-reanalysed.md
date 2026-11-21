# Oven temperature accuracy

Run 2026-11-20 11:18 with Agilent Technologies U1253B on COM5. A setpoint is accurate when its mean, including the ±95 % interval, is within ±15.0 °F of the setting.

## Findings

- Bake: runs 15.1 °F hot on average over 1 setpoint (worst 15.1 °F). The thermostat swings 19.1 °F peak to peak, one cycle every 16.6 min.
- Bake: change the oven's thermostat adjustment by −15 °F, from whatever it is set to now. Details are under Best adjustment.

## Each setpoint

| Mode | Set | Measured | Offset | ±95 % | Verdict | Swing | Range | Cycle | To temp | First peak | Time | Used |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Bake | 350 °F | 365.1 °F | +15.1 °F | — | Out of tolerance | 19.1 °F | 355–374 | 16.6 min | 9 min | — | 61 min | 1 cycles |

*Measured* is the time-weighted mean over whole thermostat cycles once the oven had settled; *±95 %* is how sure that mean is. *Swing* is the average peak-to-peak of a cycle and *Range* the lowest and highest readings while measuring. *To temp* is from pressing Start to first reaching the setting (or the thermostat first turning round, for an oven that runs cold); *First peak* is the first high point against the setting, which is the overshoot when it is positive.

## Best adjustment

**Bake: change the oven's thermostat adjustment by −15 °F, from whatever it is set to now.**

- This makes the overall error across the setpoints measured as small as it can.
- Afterwards every setpoint measured should be within 0.1 °F of its setting, which is about the probe's own accuracy.
- This assumes the adjustment moves the oven by the same amount at every setting. That hasn't been measured for this oven: after adjusting, run the same setpoints again and analyze both runs together (see docs/oven-control-model.md).
- The run didn't record the oven's current adjustment. Give it with --adjustment and the app will say what to set it to.
- With one setpoint per mode, the app can't tell an error that's the same at every setting from one that grows with temperature. Worth adding: 300 °F, 400 °F.

| Mode | Set | Now | After adjusting | ±95 % | Within tolerance? |
| --- | --- | --- | --- | --- | --- |
| Bake | 350 °F | +15.1 °F | +0.1 °F | — | Borderline |

[Reading the results and adjusting the oven](https://github.com/TGoodhew/OvenTracker/blob/main/docs/adjusting-the-oven.md) explains how to enter the adjustment.

## Preheat signal

| Mode | Set | Signalled after | Oven was at | Against setting |
| --- | --- | --- | --- | --- |
| Bake | 350 °F | 2 min | 356.4 °F | +6.4 °F |

## How far to trust it

- The ±95 % column is the statistical uncertainty only. The probe and the meter each add their own error, given in their data sheets. An offset smaller than these may be the probe, not the oven.
- Bake 350 °F has a 16 min gap in its readings (the meter went quiet, perhaps its auto power-off). The cycle means across the gap are interpolated, so treat it with a little more care.
- The probe measures the air where you put it. Ovens are hotter near the elements and walls, so keep it in the centre for every run if you want to compare runs.
- If your oven converts convection temperatures down automatically (often by 25 °F / 15 °C), the convection setting entered here should be the one the oven actually targets.

Settings: precision ±2.0 °F, at least 4 cycles, up to 120 min per setpoint. Oven's thermostat adjustment during the run: not recorded. Raw data: `readings.csv`.
