# Oven temperature accuracy

Run 2026-11-20 15:58 with OvenTracker simulated meter on simulator. A setpoint is accurate when its mean, including the ±95 % interval, is within ±8.0 °C of the setting.

## Findings

- Bake: runs 4.4 °C hot on average over 2 setpoints (worst 4.4 °C). The error is about the same at every setpoint, so the oven's thermostat adjustment can correct it. The thermostat swings 18.7 °C peak to peak, one cycle every 3.0 min.
- Bake: set the oven's thermostat adjustment to −4 °C (it's 0 °C now). Details are under Best adjustment.

## Each setpoint

| Mode | Set | Measured | Offset | ±95 % | Verdict | Swing | Range | Cycle | To temp | First peak | Time | Used |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Bake | 180 °C | 184.4 °C | +4.4 °C | 0.1 °C | Accurate | 18.8 °C | 175–194 | 2.9 min | 22 min | +8.4 °C | 55 min | 4 cycles |
| Bake | 220 °C | 224.3 °C | +4.3 °C | 0.1 °C | Accurate | 18.7 °C | 215–234 | 3.0 min | 5 min | +9.6 °C | 37 min | 4 cycles |

*Measured* is the time-weighted mean over whole thermostat cycles once the oven had settled; *±95 %* is how sure that mean is. *Swing* is the average peak-to-peak of a cycle and *Range* the lowest and highest readings while measuring. *To temp* is from pressing Start to first reaching the setting (or the thermostat first turning round, for an oven that runs cold); *First peak* is the first high point against the setting, which is the overshoot when it is positive.

## Best adjustment

**Bake: set the oven's thermostat adjustment to −4 °C (it's 0 °C now).**

- This keeps the worst of the setpoints measured as close to its setting as it can.
- Afterwards every setpoint measured should be within 0.2 °C of its setting, which is about the probe's own accuracy.
- This assumes the adjustment moves the oven by the same amount at every setting. That hasn't been measured for this oven: after adjusting, run the same setpoints again and analyze both runs together (see docs/oven-control-model.md).
- To set it on the GE Profile PHS925ST1SS: 1. Touch BROIL HI/LO and BAKE at the same time until the display shows SF. 2. Touch BAKE. The display shows the adjustment set now. 3. Touch BAKE again to switch between positive (hotter) and negative (cooler). 4. Type the amount on the number pads, e.g. 1 then 0 for 10. 5. Check the display shows the right sign, then touch START.

| Mode | Set | Now | After adjusting | ±95 % | Within tolerance? |
| --- | --- | --- | --- | --- | --- |
| Bake | 180 °C | +4.4 °C | ±0.0 °C | 0.1 °C | Yes |
| Bake | 220 °C | +4.3 °C | −0.2 °C | 0.1 °C | Yes |

[Reading the results and adjusting the oven](https://github.com/TGoodhew/OvenTracker/blob/main/docs/adjusting-the-oven.md) explains how to enter the adjustment.

## How long each setpoint needed

- The first setpoint took 55 min (43 min to settle, then 12 min measuring).
- The next setpoint took 37 min, since the oven was already hot.
- Each result needed 4 full thermostat cycles to pin the mean to ±1.0 °C; the app counts these as it goes, so the time adapts to your oven.

| Mode | Set | Settling | Measuring | Total | Ended |
| --- | --- | --- | --- | --- | --- |
| Bake | 180 °C | 43 min | 12 min | 55 min | settled and measured |
| Bake | 220 °C | 25 min | 12 min | 37 min | settled and measured |

## How far to trust it

- The ±95 % column is the statistical uncertainty only. The probe itself adds its own: a standard-grade type K thermocouple is good to the greater of ±2.2 °C and ±0.75 % of the temperature, ±2.2 °C at 220 °C, and the meter's own accuracy (in its data sheet) adds to that. An offset smaller than these may be the probe, not the oven.
- The probe measures the air where you put it. Ovens are hotter near the elements and walls, so keep it in the centre for every run if you want to compare runs.
- If your oven converts convection temperatures down automatically (often by 25 °F / 15 °C), the convection setting entered here should be the one the oven actually targets.

Settings: precision ±1.0 °C, at least 4 cycles, up to 180 min per setpoint. Oven's thermostat adjustment during the run: 0 °C. Raw data: `readings.csv`.
