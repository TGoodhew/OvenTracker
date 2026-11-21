# Oven temperature accuracy

Run 2026-11-21 09:07 with Agilent Technologies U1253B on COM5. A setpoint is accurate when its mean, including the ±95 % interval, is within ±15.0 °F of the setting.

## Each setpoint

| Mode | Set | Measured | Offset | ±95 % | Verdict | Swing | Range | Cycle | To temp | First peak | Time | Used |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Bake | 350 °F | — | — | — | Inconclusive | — | — | — | 14 min | +13.3 °F | 15 min | — |

*Measured* is the time-weighted mean over whole thermostat cycles once the oven had settled; *±95 %* is how sure that mean is. *Swing* is the average peak-to-peak of a cycle and *Range* the lowest and highest readings while measuring. *To temp* is from pressing Start to first reaching the setting (or the thermostat first turning round, for an oven that runs cold); *First peak* is the first high point against the setting, which is the overshoot when it is positive.

## How far to trust it

- The ±95 % column is the statistical uncertainty only. The probe itself adds its own: the Fluke 80BK-A type K probe is good to the greater of ±4.0 °F and ±2 % of the temperature, ±6.4 °F at 350 °F, and the meter's own accuracy (in its data sheet) adds to that. An offset smaller than these may be the probe, not the oven.
- The probe measures the air where you put it. Ovens are hotter near the elements and walls, so keep it in the centre for every run if you want to compare runs.
- If your oven converts convection temperatures down automatically (often by 25 °F / 15 °C), the convection setting entered here should be the one the oven actually targets.

Settings: precision ±2.0 °F, at least 4 cycles, up to 180 min per setpoint. Oven's thermostat adjustment during the run: 0 °F. Raw data: `readings.csv`.
