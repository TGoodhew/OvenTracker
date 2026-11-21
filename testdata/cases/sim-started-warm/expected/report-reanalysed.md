# Oven temperature accuracy

Run 2026-11-20 15:59 with OvenTracker simulated meter on simulator. A setpoint is accurate when its mean, including the ±95 % interval, is within ±15.0 °F of the setting.

## Findings

- Bake: runs 12.9 °F hot on average over 2 setpoints (worst 13.5 °F). The error is about the same at every setpoint, so the oven's thermostat adjustment can correct it. The thermostat swings 33.6 °F peak to peak, one cycle every 3.0 min.
- Bake: set the oven's thermostat adjustment to −8 °F (it's +5 °F now). Details are under Best adjustment.

## Each setpoint

| Mode | Set | Measured | Offset | ±95 % | Verdict | Swing | Range | Cycle | To temp | First peak | Time | Used |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Bake | 350 °F (started warm) | 363.5 °F | +13.5 °F | 0.3 °F | Accurate | 33.8 °F | 346–380 | 2.9 min | 7 min | — | 34 min | 4 cycles |
| Bake | 425 °F | 437.4 °F | +12.4 °F | 0.2 °F | Accurate | 33.5 °F | 420–454 | 3.0 min | 4 min | +22.3 °F | 34 min | 4 cycles |

*Measured* is the time-weighted mean over whole thermostat cycles once the oven had settled; *±95 %* is how sure that mean is. *Swing* is the average peak-to-peak of a cycle and *Range* the lowest and highest readings while measuring. *To temp* is from pressing Start to first reaching the setting (or the thermostat first turning round, for an oven that runs cold); *First peak* is the first high point against the setting, which is the overshoot when it is positive. The setpoint marked *started warm* began with the oven still warm, not from cold, so its *To temp* and *First peak* aren't from cold. Its mean is as good as any.

## Best adjustment

**Bake: set the oven's thermostat adjustment to −8 °F (it's +5 °F now).**

- This makes the overall error across the setpoints measured as small as it can.
- Afterwards every setpoint measured should be within 0.6 °F of its setting, which is about the probe's own accuracy.
- This assumes the adjustment moves the oven by the same amount at every setting. That hasn't been measured for this oven: after adjusting, run the same setpoints again and analyze both runs together (see docs/oven-control-model.md).

| Mode | Set | Now | After adjusting | ±95 % | Within tolerance? |
| --- | --- | --- | --- | --- | --- |
| Bake | 350 °F | +13.5 °F | +0.5 °F | 0.3 °F | Yes |
| Bake | 425 °F | +12.4 °F | −0.6 °F | 0.2 °F | Yes |

[Reading the results and adjusting the oven](https://github.com/TGoodhew/OvenTracker/blob/main/docs/adjusting-the-oven.md) explains how to enter the adjustment.

## How long each setpoint needed

- The first setpoint took 34 min (22 min to settle, then 12 min measuring).
- The next setpoint took 34 min, since the oven was already hot.
- Each result needed 4 full thermostat cycles to pin the mean to ±2.0 °F; the app counts these as it goes, so the time adapts to your oven.

| Mode | Set | Settling | Measuring | Total | Ended |
| --- | --- | --- | --- | --- | --- |
| Bake | 350 °F | 22 min | 12 min | 34 min | settled and measured |
| Bake | 425 °F | 22 min | 12 min | 34 min | settled and measured |

## How far to trust it

- The ±95 % column is the statistical uncertainty only. The probe itself adds its own: a standard-grade type K thermocouple is good to the greater of ±4.0 °F and ±0.75 % of the temperature, ±4.0 °F at 425 °F, and the meter's own accuracy (in its data sheet) adds to that. An offset smaller than these may be the probe, not the oven.
- The probe measures the air where you put it. Ovens are hotter near the elements and walls, so keep it in the centre for every run if you want to compare runs.
- If your oven converts convection temperatures down automatically (often by 25 °F / 15 °C), the convection setting entered here should be the one the oven actually targets.

Settings: precision ±2.0 °F, at least 4 cycles, up to 120 min per setpoint. Oven's thermostat adjustment during the run: +5 °F. Raw data: `readings.csv`.
