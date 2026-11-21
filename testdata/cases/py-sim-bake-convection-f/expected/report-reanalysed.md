# Oven temperature accuracy

Run 2026-11-20 17:13 with OvenTracker simulated meter on simulator. A setpoint is accurate when its mean, including the ±95 % interval, is within ±15.0 °F of the setting.

## Findings

- Bake: runs 7.9 °F hot on average over 1 setpoint (worst 7.9 °F). The thermostat swings 33.8 °F peak to peak, one cycle every 2.9 min.
- Convection: runs 8.3 °F cold on average over 1 setpoint (worst 8.3 °F). The thermostat swings 17.1 °F peak to peak, one cycle every 2.3 min.
- Bake and convection (one adjustment for both): leave the thermostat adjustment as it is; no step would make the oven more accurate. Details are under Best adjustment.
- At 350 °F, convection is 16.2 °F cooler than bake.

## Each setpoint

| Mode | Set | Measured | Offset | ±95 % | Verdict | Swing | Range | Cycle | To temp | First peak | Time | Used |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Bake | 350 °F | 357.9 °F | +7.9 °F | 0.4 °F | Accurate | 33.8 °F | 341–375 | 2.9 min | 21 min | +15.2 °F | 51 min | 4 cycles |
| Convection | 350 °F | 341.7 °F | −8.3 °F | 0.1 °F | Accurate | 17.1 °F | 333–350 | 2.3 min | 1 min | — | 13 min | 4 cycles |

*Measured* is the time-weighted mean over whole thermostat cycles once the oven had settled; *±95 %* is how sure that mean is. *Swing* is the average peak-to-peak of a cycle and *Range* the lowest and highest readings while measuring. *To temp* is from pressing Start to first reaching the setting (or the thermostat first turning round, for an oven that runs cold); *First peak* is the first high point against the setting, which is the overshoot when it is positive.

## Best adjustment

**Bake and convection (one adjustment for both): leave the thermostat adjustment as it is; no step would make the oven more accurate.**

- This makes the overall error across the setpoints measured as small as it can.
- Afterwards the worst setpoint is still 8.3 °F out, so the table below says what to set to make up for it.
- This assumes the adjustment moves the oven by the same amount at every setting. That hasn't been measured for this oven: after adjusting, run the same setpoints again and analyze both runs together (see docs/oven-control-model.md).
- With one setpoint per mode, the app can't tell an error that's the same at every setting from one that grows with temperature. Worth adding: 300 °F, 400 °F.

| Mode | Set | Now | After adjusting | ±95 % | Within tolerance? |
| --- | --- | --- | --- | --- | --- |
| Bake | 350 °F | +7.9 °F | +7.9 °F | 0.4 °F | Yes |
| Convection | 350 °F | −8.3 °F | −8.3 °F | 0.1 °F | Yes |

What to set after adjusting, where the adjustment alone leaves the oven 5 °F (3 °C) or more out:

| Mode | For | Set the oven to |
| --- | --- | --- |
| Bake | 350 °F | 342 °F |
| Convection | 350 °F | 358 °F |

[Reading the results and adjusting the oven](https://github.com/TGoodhew/OvenTracker/blob/main/docs/adjusting-the-oven.md) explains how to enter the adjustment.

## Bake and convection

| Set | Bake | Convection | Convection − Bake | Real difference? | Swing bake / conv | To temp bake / conv |
| --- | --- | --- | --- | --- | --- | --- |
| 350 °F | 357.9 °F | 341.7 °F | −16.2 °F | Yes | 33.8 °F / 17.1 °F | 21 min / 1 min |

## How long each setpoint needed

- The first setpoint took 51 min (39 min to settle, then 12 min measuring).
- The next setpoint took 13 min, since the oven was already hot.
- Each result needed 4 full thermostat cycles to pin the mean to ±2.0 °F; the app counts these as it goes, so the time adapts to your oven.

| Mode | Set | Settling | Measuring | Total | Ended |
| --- | --- | --- | --- | --- | --- |
| Bake | 350 °F | 39 min | 12 min | 51 min | settled and measured |
| Convection | 350 °F | 3 min | 9 min | 13 min | settled and measured |

## How far to trust it

- The ±95 % column is the statistical uncertainty only. The probe itself adds its own: a standard-grade type K thermocouple is good to the greater of ±4.0 °F and ±0.75 % of the temperature, ±4.0 °F at 350 °F, and the meter's own accuracy (in its data sheet) adds to that. An offset smaller than these may be the probe, not the oven.
- The probe measures the air where you put it. Ovens are hotter near the elements and walls, so keep it in the centre for every run if you want to compare runs.
- If your oven converts convection temperatures down automatically (often by 25 °F / 15 °C), the convection setting entered here should be the one the oven actually targets.

Settings: precision ±2.0 °F, at least 4 cycles, up to 120 min per setpoint. Oven's thermostat adjustment during the run: not recorded. Raw data: `readings.csv`.
