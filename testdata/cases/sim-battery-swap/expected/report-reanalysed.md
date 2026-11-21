# Oven temperature accuracy

Run 2026-11-21 09:56 with OvenTracker simulated meter on simulator. A setpoint is accurate when its mean, including the ±95 % interval, is within ±15.0 °F of the setting.

## Findings

- Bake: runs 7.6 °F hot on average over 1 setpoint (worst 7.6 °F). The thermostat swings 33.9 °F peak to peak, one cycle every 2.9 min.
- Bake: change the oven's thermostat adjustment by −8 °F, from whatever it is set to now. Details are under Best adjustment.

## Each setpoint

| Mode | Set | Measured | Offset | ±95 % | Verdict | Swing | Range | Cycle | To temp | First peak | Time | Used |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Bake | 350 °F | 357.6 °F | +7.6 °F | 0.8 °F | Accurate | 33.9 °F | 340–375 | 2.9 min | 21 min | +15.0 °F | 48 min | 4 cycles |

*Measured* is the time-weighted mean over whole thermostat cycles once the oven had settled; *±95 %* is how sure that mean is. *Swing* is the average peak-to-peak of a cycle and *Range* the lowest and highest readings while measuring. *To temp* is from pressing Start to first reaching the setting (or the thermostat first turning round, for an oven that runs cold); *First peak* is the first high point against the setting, which is the overshoot when it is positive.

## Best adjustment

**Bake: change the oven's thermostat adjustment by −8 °F, from whatever it is set to now.**

- This makes the overall error across the setpoints measured as small as it can.
- Afterwards every setpoint measured should be within 0.4 °F of its setting, which is about the probe's own accuracy.
- This assumes the adjustment moves the oven by the same amount at every setting. That hasn't been measured for this oven: after adjusting, run the same setpoints again and analyze both runs together (see docs/oven-control-model.md).
- The run didn't record the oven's current adjustment. Give it with --adjustment and the app will say what to set it to.
- To set it on the GE Profile PHS925ST1SS: 1. Touch BROIL HI/LO and BAKE at the same time until the display shows SF. 2. Touch BAKE. The display shows the adjustment set now. 3. Touch BAKE again to switch between positive (hotter) and negative (cooler). 4. Type the amount on the number pads, e.g. 1 then 0 for 10. 5. Check the display shows the right sign, then touch START.
- With one setpoint per mode, the app can't tell an error that's the same at every setting from one that grows with temperature. Worth adding: 300 °F, 400 °F.

| Mode | Set | Now | After adjusting | ±95 % | Within tolerance? |
| --- | --- | --- | --- | --- | --- |
| Bake | 350 °F | +7.6 °F | −0.4 °F | 0.8 °F | Yes |

[Reading the results and adjusting the oven](https://github.com/TGoodhew/OvenTracker/blob/main/docs/adjusting-the-oven.md) explains how to enter the adjustment.

## How long each setpoint needed

- The first setpoint took 48 min (36 min to settle, then 12 min measuring).
- Each result needed 4 full thermostat cycles to pin the mean to ±2.0 °F; the app counts these as it goes, so the time adapts to your oven.

| Mode | Set | Settling | Measuring | Total | Ended |
| --- | --- | --- | --- | --- | --- |
| Bake | 350 °F | 36 min | 12 min | 48 min | settled and measured |

## How far to trust it

- The ±95 % column is the statistical uncertainty only. The probe itself adds its own: a standard-grade type K thermocouple is good to the greater of ±4.0 °F and ±0.75 % of the temperature, ±4.0 °F at 350 °F, and the meter's own accuracy (in its data sheet) adds to that. An offset smaller than these may be the probe, not the oven.
- Bake 350 °F has a 3 min gap in its readings (the meter went quiet, perhaps its auto power-off). The cycle means across the gap are interpolated, so treat it with a little more care.
- The probe measures the air where you put it. Ovens are hotter near the elements and walls, so keep it in the centre for every run if you want to compare runs.
- If your oven converts convection temperatures down automatically (often by 25 °F / 15 °C), the convection setting entered here should be the one the oven actually targets.

Settings: precision ±2.0 °F, at least 4 cycles, up to 180 min per setpoint. Oven's thermostat adjustment during the run: not recorded. Raw data: `readings.csv`.
