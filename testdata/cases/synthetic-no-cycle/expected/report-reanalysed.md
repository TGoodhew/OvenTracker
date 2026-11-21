# Oven temperature accuracy

Run 2026-01-10 10:00 with synthetic on none. A setpoint is accurate when its mean, including the ±95 % interval, is within ±15.0 °F of the setting.

## Findings

- Bake: runs 4.3 °F hot on average over 1 setpoint (worst 4.3 °F). The thermostat swings 0.3 °F peak to peak.
- Bake: set the oven's thermostat adjustment to −4 °F (it's 0 °F now). Details are under Best adjustment.

## Each setpoint

| Mode | Set | Measured | Offset | ±95 % | Verdict | Swing | Range | Cycle | To temp | First peak | Time | Used |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Bake | 350 °F | 354.3 °F | +4.3 °F | 0.2 °F | Accurate | 0.3 °F | 354–355 | — | 29 min | — | 58 min | 5 blocks |

*Measured* is the time-weighted mean over whole thermostat cycles once the oven had settled; *±95 %* is how sure that mean is. *Swing* is the average peak-to-peak of a cycle and *Range* the lowest and highest readings while measuring. *To temp* is from pressing Start to first reaching the setting (or the thermostat first turning round, for an oven that runs cold); *First peak* is the first high point against the setting, which is the overshoot when it is positive.


## Best adjustment

**Bake: set the oven's thermostat adjustment to −4 °F (it's 0 °F now).**

- This makes the overall error across the setpoints measured as small as it can.
- Afterwards every setpoint measured should be within 0.3 °F of its setting, which is about the probe's own accuracy.
- This assumes the adjustment moves the oven by the same amount at every setting. That hasn't been measured for this oven: after adjusting, run the same setpoints again and analyze both runs together (see docs/oven-control-model.md).
- With one setpoint per mode, the app can't tell an error that's the same at every setting from one that grows with temperature. Worth adding: 300 °F, 400 °F.

| Mode | Set | Now | After adjusting | ±95 % | Within tolerance? |
| --- | --- | --- | --- | --- | --- |
| Bake | 350 °F | +4.3 °F | +0.3 °F | 0.2 °F | Yes |

[Reading the results and adjusting the oven](https://github.com/TGoodhew/OvenTracker/blob/main/docs/adjusting-the-oven.md) explains how to enter the adjustment.

## How long each setpoint needed

- The first setpoint took 58 min (44 min to settle, then 15 min measuring).

| Mode | Set | Settling | Measuring | Total | Ended |
| --- | --- | --- | --- | --- | --- |
| Bake | 350 °F | 44 min | 15 min | 58 min | settled and measured |

## How far to trust it

- The ±95 % column is the statistical uncertainty only. The probe and the meter each add their own error, given in their data sheets. An offset smaller than these may be the probe, not the oven.
- The probe measures the air where you put it. Ovens are hotter near the elements and walls, so keep it in the centre for every run if you want to compare runs.
- If your oven converts convection temperatures down automatically (often by 25 °F / 15 °C), the convection setting entered here should be the one the oven actually targets.

Settings: precision ±2.0 °F, at least 4 cycles, up to 120 min per setpoint. Oven's thermostat adjustment during the run: 0 °F. Raw data: `readings.csv`.
