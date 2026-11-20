# Reading the results and adjusting the oven

This page explains what OvenTracker's report means and how to use it to correct the oven's temperature.
It is written for the GE Profile **PHS925ST1SS** range, and the steps come from its owner's manual. Most
GE electronic ovens of that era work the same way, and the first half of this page applies to any oven.

## The short version

- Look at **Offset** (measured minus set). If the oven runs **+10 °F hot**, set the thermostat adjustment
  **10 °F cooler**. If it runs **−10 °F cold**, set it **10 °F hotter**. The correction is always the
  offset with its sign reversed.
- The report works this out for you. Its **Best adjustment** section says, for example, *"set the oven's
  thermostat adjustment to −10 °F (it's 0 °F now)"*. That number is what to enter.
- Tell the app the oven's current adjustment. `run` asks at the start, or give it with `--adjustment`. The
  adjustment is **added to whatever is already set**, so without it the report can only say how much to
  change it by (see [Correcting an oven that already has an adjustment](#correcting-an-oven-that-already-has-an-adjustment)).
- Don't adjust for offsets of a few degrees. The probe itself is only good to about ±4 °F (see
  [Is it worth adjusting?](#is-it-worth-adjusting)).
- After any adjustment, run the tracker again to check it.

## What the report says

Each run writes `report.md` to `Documents\OvenTracker\<date time>\`. The parts that matter for
adjusting the oven are these:

### The setpoint table

| Column | What it means | Does it matter for adjusting? |
| --- | --- | --- |
| **Set** | The temperature you set on the oven. | |
| **Measured** | The oven's true average temperature, taken over whole thermostat cycles once the oven had settled. | Yes, this is the real oven temperature. |
| **Offset** | Measured minus Set. Positive means the oven runs hot, negative means it runs cold. | **Yes, this is the number to correct.** |
| **±95 %** | How sure the Measured figure is. A Measured of 360.0 °F ±1.5 °F means the true mean is almost certainly between 358.5 and 361.5 °F. | It tells you whether the offset is real. |
| **Verdict** | Accurate, Borderline, Out of tolerance or Inconclusive. See below. | Yes. |
| **Swing** | How far the temperature rises and falls in each thermostat cycle, peak to peak. | No. This is the thermostat switching the element on and off. The adjustment moves the average, not the swing. |
| **Range** | The lowest and highest readings while measuring. | No, for the same reason. |
| **Cycle** | How long one heat-on, heat-off cycle takes. | No. |
| **To temp** | How long from pressing Start until the oven first reached the setting. | No. |
| **First peak** | How far the first high point went past the setting: the overshoot after preheating. | No. Overshoot is normal and dies away after the first cycle. |

An oven that swings from 340 to 380 °F with a setting of 360 °F is **not** 20 °F out. It holds an average
of 360 °F, and that average is what bakes the food. Only the Offset should be corrected.

### The verdict

The tolerance is ±15 °F (±8 °C) unless you set `--tolerance`.

| Verdict | Meaning | What to do |
| --- | --- | --- |
| **Accurate** | The offset is within tolerance, including its ±95 % uncertainty. | Nothing is required. You can still trim a consistent offset of 5 °F or more if you want (see the table below). |
| **Borderline** | The offset is close to the tolerance and the uncertainty overlaps it. | Adjust if the report suggests a correction of 5 °F or more. Otherwise re-run, perhaps with `--precision 1`, to firm it up. |
| **Out of tolerance** | The oven is clearly outside tolerance at this setting. | Adjust it. |
| **Inconclusive** | There was not enough settled data (the setpoint was skipped, interrupted or finished too early). | Run that setpoint again. |

### The findings

The Findings at the top of the report summarise each mode, for example:

> Bake: runs 10.3 °F hot on average over 3 setpoints (worst 11.8 °F). The error is about the same at every
> setpoint, so the oven's thermostat adjustment can correct it.

"About the same at every setpoint" means the oven is off by a fixed amount, and the adjustment will
correct it everywhere. "The error grows with temperature" (say +4 °F at 325 °F but +16 °F at 450 °F) means
one adjustment can't fix all of it. The *Best adjustment* section then shows what's left at each setting.

### The best adjustment

This section is the one to act on. For example:

> **Bake and convection (one adjustment for both): set the oven's thermostat adjustment to −8 °F (it's 0 °F now).**
>
> - This makes the overall error across the setpoints measured as small as it can.
> - Afterwards the worst setpoint is still 7 °F out, so the table below says what to set to make up for it.

Then come a table of each setpoint's error now and after adjusting, and, where the adjustment alone still
leaves a setting 5 °F or more out, a table of **what to set**. For example, *"Bake, for 425 °F, set the
oven to 418 °F"*.

Things worth knowing:

- **The PHS925 has one adjustment for bake and convection**, so by default the app finds the one value that
  suits both (see [Convection](#convection-and-auto-recipe-conversion)). If the two need different
  corrections, the table of what to set covers the difference.
- **"Best" is the smallest overall error** across the setpoints you ran. If most of your baking is at one
  temperature, `--favour 350` makes the oven right there instead, and `--objective minimax` keeps the worst
  setpoint as close as it can. Both work with `analyze`, so you can try them on a run you've already done:

  ```powershell
  OvenTracker analyze "Documents\OvenTracker\2026-11-20 1400" --favour 350
  ```

- **How far the adjustment moves the oven is assumed** (1 °F per 1 °F) until it has been measured. Run the
  same setpoints again after adjusting, and analyze the two runs together; the app then measures it. See
  [How the oven's control responds to its adjustment](oven-control-model.md).
- **If the report suggests extra setpoints**, the ones you ran can't show whether the error changes with
  temperature, so add them to the next run.

### The preheat signal

If you pressed **P** when the oven beeped to say it was preheated, the report shows what the oven was
really at. The PHS925 beeps three times when it thinks it has reached the setting (owner's manual,
p. 17). The display counts up from 100 °F while it heats.

It is common for the beep to come early, while the oven air is hot but the walls and racks are not. If the
report shows the oven 20–30 °F short when it beeped, the thermostat adjustment won't fix that. Wait
another 10–15 minutes after the beep before baking anything sensitive. The **To temp** column shows how
long the oven really needs.

## Is it worth adjusting?

The measurement has limits. A standard type K thermocouple is accurate to ±4 °F (±2.2 °C) at normal
baking temperatures, and the U1253B adds a little more. So an offset of 3 °F may come from the probe,
not the oven. The report's *How far to trust it* section gives the figure at your highest setpoint.

The PHS925 manual says "Use your new oven for a few weeks to become more familiar with it. If you still
think your new oven is too hot or too cold, you can adjust the thermostat yourself" (owner's manual,
p. 15). The tracker removes the guesswork, but the advice still holds: if your food comes out right, a
small offset doesn't matter.

| Offset (average over your setpoints) | Suggested action |
| --- | --- |
| Under 5 °F either way | Leave it. This is within the probe's own accuracy. |
| 5 to 15 °F | Optional. Adjust if your baking shows it (cakes doming or browning too fast, or under-done centres), or if you want it exact. |
| Over 15 °F | Adjust. This is enough to affect most recipes. |
| Over 35 °F | The PHS925 can only adjust 35 °F. Set the maximum, re-run the tracker, and consider a service call: the oven sensor may be faulty. |

## Adjusting the thermostat on the PHS925ST1SS

These steps are from the owner's manual, *Adjust the oven thermostat—Do it yourself!*, p. 15:

1. With the oven off, touch the **BROIL HI/LO** and **BAKE** pads at the same time and hold them until the
   display shows **SF** (Special Features).
2. Touch **BAKE**. The display shows a number: the adjustment that is set now. **Write it down.** It is
   `0` if the oven has never been adjusted.
3. Touch **BAKE** again to switch the sign between positive (hotter) and negative (cooler). If the oven
   runs hot, you want negative; if it runs cold, positive. So the display should match the suggested
   setting in the report: −10 °F means the display shows a negative number.
4. Type the amount on the number pads, the way you would read it. For 10 °F, touch **1** then **0**. The
   range is up to 35 °F hotter or 35 °F cooler, in 1 °F steps. Check that the display shows the
   right sign before going on.
5. Touch **START**. The display goes back to the time of day. Use the oven as normal.

According to the manual:

- The adjustment changes **baking and roasting** temperatures. It does not change broil or self-clean.
- It is kept in memory through a power cut, so it only needs doing once.
- On models with two ovens, each oven has its own adjustment: after **SF**, touch the upper or lower
  oven's **BAKE** pad to choose which one to adjust.

The oven's display keeps showing the temperature you set, not the corrected one. That is expected: the
adjustment changes what the oven aims for behind the scenes.

### Working in °C

The PHS925 adjusts in °F. If you ran the tracker in °C, multiply the suggested correction by 1.8 and
round it. A correction of −6 °C is −11 °F. Or run the tracker with `--unit F`.

## Correcting an oven that already has an adjustment

The tracker measures the oven **as it is set now**, including any adjustment already made. So tell it
what the adjustment is: `run` asks at the start, or give it with `--adjustment -5`. The report then gives
the number to enter, *"set it to −15 °F (it's −5 °F now)"*, and does the arithmetic below for you.

If the run didn't record it, the report gives a change to make instead (*"change it by −10 °F"*), and:

> new adjustment = current adjustment + the change

The oven shows the adjustment as a signed number, positive for hotter and negative for cooler, so this
is plain arithmetic on what the display shows:

| Display shows now | Report says | Set the display to |
| --- | --- | --- |
| 0 | change it by −10 °F | −10 |
| −5 | change it by −10 °F | −15 (−5 − 10) |
| +10 | change it by −4 °F | +6 (+10 − 4) |
| −15 | change it by +6 °F | −9 (−15 + 6) |

You can also add it afterwards: `OvenTracker analyze "<run folder>" --adjustment -5`. That is why step 2
says to write the current number down. It is also the reason to re-run the tracker after every
adjustment, rather than adjusting twice from the same report. Give the new adjustment on the next run,
and analyse the two runs together: the app then measures how far the adjustment really moves your oven.

## Convection and Auto Recipe Conversion

The PHS925 has one thermostat adjustment, and the manual does not say it treats convection separately. So
expect the adjustment to move convection by the same amount as bake. Run both modes again afterwards to
confirm it.

**Turn Auto Recipe Conversion off before testing convection, or allow for it.** With this feature on, the
oven quietly lowers a convection setting by about 25 °F, while the display still shows the temperature
you entered (owner's manual, pp. 17 and 26). If you enter 350 °F with it on, the oven aims for about
325 °F, and the tracker will report convection as roughly 25 °F cold when the oven is behaving exactly as
designed. Either:

- turn it off for the test: hold **BAKE** and **BROIL HI/LO** until the display shows **SF**, touch
  **CONVECTION COOK** until it shows **CON OFF**, then touch **START**; or
- leave it on, and give the tracker the temperature the oven really targets (for 350 °F entered, track
  `--setpoints 325` in convection).

Turn it back on afterwards if you like recipes converted for you. It does not affect the thermostat
adjustment.

If bake and convection need different corrections, a single adjustment can't fix both. The report gives
the value that suits both best, and a table of what to set in each mode. Alternatively, set the adjustment
for the mode you use most (`--separate-adjustments` shows each mode's own best value) and allow for the
other one in your recipes. For example, if convection still runs 10 °F hot after you have corrected bake,
set convection recipes 10 °F lower.

## Checking the adjustment

After adjusting, run the tracker again at the setpoint you use most:

```powershell
OvenTracker.exe run --unit F --modes bake,convection --setpoints 350 --adjustment -11
```

The Offset should now be within a few degrees of zero, and the verdict should be Accurate. If it moved by
less (or more) than you adjusted, analyse this run together with the one before it. The app then measures
how far the adjustment moves your oven and gives a better setting:

```powershell
OvenTracker.exe analyze "Documents\OvenTracker\2026-11-20 1400" "Documents\OvenTracker\2026-11-22 0900"
```

Hang the probe in the same place (the centre of the oven, clear of the racks and walls) for every run, so
that runs can be compared. Heat near the elements and walls is very different from the centre.

## A worked example

Suppose a run at 325, 350 and 400 °F in bake gives:

| Mode | Set | Measured | Offset | ±95 % | Verdict | Swing |
| --- | --- | --- | --- | --- | --- | --- |
| Bake | 325 °F | 334.8 °F | +9.8 °F | 1.4 °F | Accurate | 28.0 °F |
| Bake | 350 °F | 361.2 °F | +11.2 °F | 1.6 °F | Accurate | 30.1 °F |
| Bake | 400 °F | 410.9 °F | +10.9 °F | 1.8 °F | Accurate | 33.5 °F |

Findings: *"Bake: runs 10.6 °F hot on average … The error is about the same at every setpoint, so the
oven's thermostat adjustment can correct it."* Best adjustment: *"Bake: set the oven's thermostat
adjustment to −11 °F (it's 0 °F now)."*

- Every setpoint is within the ±15 °F tolerance, so this is optional. But the oven is a steady 10 to
  11 °F hot, well beyond the probe's ±4 °F, so it is a real error and worth correcting.
- The 28 to 34 °F swing is the thermostat cycling. Ignore it.
- The error is the same at every setting, so one adjustment fixes all of them.
- The oven was at 0, so: **SF**, **BAKE**, **BAKE** again until the sign is negative, **1 1**, check the
  display reads −11, **START**.
- Re-run at 350 °F. The Offset should now be about 0 °F.
