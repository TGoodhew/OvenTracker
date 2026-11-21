# Oven temperature accuracy

Run 2026-11-20 15:58 with OvenTracker simulated meter on simulator. A setpoint is accurate when its mean, including the ±95 % interval, is within ±8.0 °C of the setting.

## Findings

- Bake: runs 2.2 °C hot on average over 1 setpoint (worst 2.2 °C). The thermostat swings 18.7 °C peak to peak, one cycle every 3.1 min.
- Bake: set the oven's thermostat adjustment to −2 °C (it's 0 °C now). Details are under Best adjustment.

## Each setpoint

| Mode | Set | Measured | Offset | ±95 % | Verdict | Swing | Range | Cycle | To temp | First peak | Time | Used |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Bake | 180 °C | 182.2 °C | +2.2 °C | 5.9 °C | Borderline | 18.7 °C | 172–192 | 3.1 min | 22 min | +8.4 °C | 33 min | 2 cycles |

*Measured* is the time-weighted mean over whole thermostat cycles once the oven had settled; *±95 %* is how sure that mean is. *Swing* is the average peak-to-peak of a cycle and *Range* the lowest and highest readings while measuring. *To temp* is from pressing Start to first reaching the setting (or the thermostat first turning round, for an oven that runs cold); *First peak* is the first high point against the setting, which is the overshoot when it is positive.

## Best adjustment

**Bake: set the oven's thermostat adjustment to −2 °C (it's 0 °C now).**

- This makes the overall error across the setpoints measured as small as it can.
- Afterwards every setpoint measured should be within 0.1 °C of its setting, which is about the probe's own accuracy.
- This assumes the adjustment moves the oven by the same amount at every setting. That hasn't been measured for this oven: after adjusting, run the same setpoints again and analyze both runs together (see docs/oven-control-model.md).
- To set it on the GE Profile PHS925ST1SS: 1. Touch BROIL HI/LO and BAKE at the same time until the display shows SF. 2. Touch BAKE. The display shows the adjustment set now. 3. Touch BAKE again to switch between positive (hotter) and negative (cooler). 4. Type the amount on the number pads, e.g. 1 then 0 for 10. 5. Check the display shows the right sign, then touch START.
- With one setpoint per mode, the app can't tell an error that's the same at every setting from one that grows with temperature. Worth adding: 150 °C, 210 °C.

| Mode | Set | Now | After adjusting | ±95 % | Within tolerance? |
| --- | --- | --- | --- | --- | --- |
| Bake | 180 °C | +2.2 °C | −0.1 °C | 5.9 °C | Yes |

[Reading the results and adjusting the oven](https://github.com/TGoodhew/OvenTracker/blob/main/docs/adjusting-the-oven.md) explains how to enter the adjustment.

## How far to trust it

- The ±95 % column is the statistical uncertainty only. The probe itself adds its own: a standard-grade type K thermocouple is good to the greater of ±2.2 °C and ±0.75 % of the temperature, ±2.2 °C at 180 °C, and the meter's own accuracy (in its data sheet) adds to that. An offset smaller than these may be the probe, not the oven.
- The probe measures the air where you put it. Ovens are hotter near the elements and walls, so keep it in the centre for every run if you want to compare runs.
- If your oven converts convection temperatures down automatically (often by 25 °F / 15 °C), the convection setting entered here should be the one the oven actually targets.

Settings: precision ±1.0 °C, at least 4 cycles, up to 180 min per setpoint. Oven's thermostat adjustment during the run: 0 °C. Raw data: `readings.csv`.
