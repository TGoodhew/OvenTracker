# Oven temperature accuracy

Run 2026-11-20 16:00 with OvenTracker simulated meter on simulator. A setpoint is accurate when its mean, including the ±95 % interval, is within ±15.0 °F of the setting.

## Findings

- Bake: runs 4.3 °F hot on average over 5 setpoints (worst 8.1 °F). The error is about the same at every setpoint, so the oven's thermostat adjustment can correct it. The thermostat swings 33.7 °F peak to peak, one cycle every 3.0 min.
- Bake: set the oven's thermostat adjustment to −9 °F (it's −10 °F now). Details are under Best adjustment.

## Each setpoint

| Mode | Set | Measured | Offset | ±95 % | Verdict | Swing | Range | Cycle | To temp | First peak | Time | Used |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Bake | 325 °F (adj 0 °F) | 333.1 °F | +8.1 °F | 0.4 °F | Accurate | 33.8 °F | 316–350 | 3.0 min | 18 min | +14.3 °F | 51 min | 4 cycles |
| Bake | 375 °F (adj 0 °F) | 382.9 °F | +7.9 °F | 0.2 °F | Accurate | 33.8 °F | 366–400 | 2.9 min | 2 min | +17.5 °F | 35 min | 4 cycles |
| Bake | 425 °F (adj 0 °F) | 432.6 °F | +7.6 °F | 0.4 °F | Accurate | 33.5 °F | 416–450 | 3.0 min | 3 min | +17.6 °F | 31 min | 4 cycles |
| Bake | 425 °F | 423.6 °F | −1.4 °F | 0.4 °F | Accurate | 33.8 °F | 407–441 | 2.9 min | 0 min | +14.9 °F | 20 min | 4 cycles |
| Bake | 325 °F | 324.1 °F | −0.9 °F | 0.2 °F | Accurate | 33.6 °F | 307–341 | 3.0 min | 9 min | — | 39 min | 4 cycles |

*Measured* is the time-weighted mean over whole thermostat cycles once the oven had settled; *±95 %* is how sure that mean is. *Swing* is the average peak-to-peak of a cycle and *Range* the lowest and highest readings while measuring. *To temp* is from pressing Start to first reaching the setting (or the thermostat first turning round, for an oven that runs cold); *First peak* is the first high point against the setting, which is the overshoot when it is positive.

## Best adjustment

**Bake: set the oven's thermostat adjustment to −9 °F (it's −10 °F now).**

- This makes the overall error across the setpoints measured as small as it can.
- Afterwards every setpoint measured should be within 0.5 °F of its setting, which is about the probe's own accuracy.
- Measured from runs at different adjustments: each degree of adjustment moves this oven 0.90 degrees (±0.03).
- To set it on the GE Profile PHS925ST1SS: 1. Touch BROIL HI/LO and BAKE at the same time until the display shows SF. 2. Touch BAKE. The display shows the adjustment set now. 3. Touch BAKE again to switch between positive (hotter) and negative (cooler). 4. Type the amount on the number pads, e.g. 1 then 0 for 10. 5. Check the display shows the right sign, then touch START.

| Mode | Set | Now | After adjusting | ±95 % | Within tolerance? |
| --- | --- | --- | --- | --- | --- |
| Bake | 325 °F | −0.9 °F | ±0.0 °F | 0.2 °F | Yes |
| Bake | 375 °F | −1.1 °F | −0.2 °F | 0.2 °F | Yes |
| Bake | 425 °F | −1.4 °F | −0.5 °F | 0.3 °F | Yes |

[Reading the results and adjusting the oven](https://github.com/TGoodhew/OvenTracker/blob/main/docs/adjusting-the-oven.md) explains how to enter the adjustment.

## How long each setpoint needed

- The first setpoint took 51 min (39 min to settle, then 12 min measuring).
- Later setpoints took 20 min to 39 min (median 33 min), since the oven was already hot.
- Each result needed 4 full thermostat cycles to pin the mean to ±2.0 °F; the app counts these as it goes, so the time adapts to your oven.

| Mode | Set | Settling | Measuring | Total | Ended |
| --- | --- | --- | --- | --- | --- |
| Bake | 325 °F | 39 min | 12 min | 51 min | settled and measured |
| Bake | 375 °F | 23 min | 12 min | 35 min | settled and measured |
| Bake | 425 °F | 19 min | 12 min | 31 min | settled and measured |
| Bake | 425 °F | 8 min | 12 min | 20 min | settled and measured |
| Bake | 325 °F | 27 min | 12 min | 39 min | settled and measured |

## How far to trust it

- The ±95 % column is the statistical uncertainty only. The probe itself adds its own: a standard-grade type K thermocouple is good to the greater of ±4.0 °F and ±0.75 % of the temperature, ±4.0 °F at 425 °F, and the meter's own accuracy (in its data sheet) adds to that. An offset smaller than these may be the probe, not the oven.
- The probe measures the air where you put it. Ovens are hotter near the elements and walls, so keep it in the centre for every run if you want to compare runs.
- If your oven converts convection temperatures down automatically (often by 25 °F / 15 °C), the convection setting entered here should be the one the oven actually targets.

Settings: precision ±2.0 °F, at least 4 cycles, up to 180 min per setpoint. Oven's thermostat adjustment during the run: −10 °F. Raw data: `readings.csv`.
