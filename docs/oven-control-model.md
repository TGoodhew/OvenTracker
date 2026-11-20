# How the oven's control responds to its adjustment

This page explains how OvenTracker works out the best thermostat adjustment
([#2](https://github.com/TGoodhew/OvenTracker/issues/2)), the assumptions behind it, and how to measure
those assumptions on your own oven. [Reading the results and adjusting the oven](adjusting-the-oven.md)
covers how to use the result.

## The problem

The thermostat adjustment (the PHS925's **SF** setting, owner's manual p. 15) is one number for every
temperature. If the oven is off by the same amount at every setting, that number fixes it exactly. Often,
though, the error changes with temperature, for example +2 °F at 350 °F but +15 °F at 425 °F. Then no
single number fixes it all, and the question becomes which number is best, and what's left over.

## The model

For each mode, OvenTracker describes the oven's error at a setting *s*, with the adjustment at *k*, as:

> error(*s*, *k*) = error₀(*s*) + *g*·*k*

- **error₀(*s*)** is the error with no adjustment, as measured at each setpoint. With three or more setpoints,
  the app also checks whether it follows a straight line in *s* or bends.
- ***g*** (the gain) is how far the oven moves for each degree of adjustment. There are three
  possibilities:
  - ***g* = 1**: the adjustment is added to the thermostat's target, and moves the oven by exactly that
    much at every setting. This is the default assumption (`--adjust-model shift`).
  - ***g* = *b***, where *b* is the slope of measured against set: the adjustment is added to the target,
    but the oven's own gain error scales it too (`--adjust-model gain`).
  - ***g* measured**: when runs of the same setpoint at different adjustments are analysed together, the
    app measures *g* directly, as the change in error divided by the change in adjustment, and uses that
    instead of either assumption. This is the reliable way.

## The recommendation

Given the errors at the current adjustment, the app picks the change *Δ* to the adjustment that best meets
an objective:

| Objective | Option | What it does |
| --- | --- | --- |
| Balanced (default) | `--objective balanced` | Makes the sum of squared errors across the measured setpoints as small as it can (least squares). |
| Worst case | `--objective minimax` | Makes the largest error at any measured setpoint as small as it can. |
| A favoured temperature | `--favour 350` | Makes the oven right at that temperature (from a straight line through the setpoints), and lets the others fall where they may. |

*Δ* is then rounded to the oven's steps (`--adjust-step`, 1 °F by default) and kept within its range
(`--adjust-range`, ±35 °F by default, as on the PHS925). For balanced and worst case, the objective is
convex in *Δ*, so the app takes whichever of the two neighbouring steps is better. On a tie it takes the
smaller change.

Most ovens have one adjustment for bake and convection, so by default the app fits both modes' setpoints
together. `--separate-adjustments` gives each mode its own recommendation.

The report then shows:

- **the setting to enter.** If the run recorded the current adjustment (`--adjustment`, or the answer to the
  question at the start of `run`), it gives the value to set it to. Otherwise it gives how much to change it by.
- **each setpoint's error now and after adjusting**, with its ±95 % uncertainty, and whether it would be
  within tolerance.
- **what to set**, for any setpoint the adjustment still leaves 5 °F (about the probe's own accuracy) or
  more out. For example: "after adjusting, set 418 °F for 425 °F".
- **setpoints to add**, when the ones measured can't show the error's shape. One setpoint can't tell a fixed
  error from a growing one, and nor can setpoints less than 50 °F apart. Two setpoints with different
  errors can't show whether the change is a straight line.

## Measuring it on your oven

The model's weakest point is *g*, until it's measured. `characterise` measures it, for any oven, in one
session:

```powershell
OvenTracker characterise --oven <yours> --unit F --modes bake,convection --setpoints 325,375,425 --probe 80bk-a
```

1. **Phase 1:** every setpoint in each mode at the current adjustment (it asks what that is; 0 if it has
   never been changed).
2. **The change:** when phase 1 is done, it asks you to change the adjustment, using your oven profile's
   steps (`--oven`, see [Describing your oven](adding-an-oven.md)). The change is at least `--step`
   (default 10 °F). It goes the way that brings the oven towards correct, and further if the best
   adjustment is further, within the oven's range. A bigger change measures the gain better.
3. **Phase 2:** the highest and lowest setpoints again in each mode (highest first, so the oven cools only
   once).
4. **The result,** written to `characterisation.md` and shown at the end:
   - each mode's gain, with its uncertainty;
   - how its error changes with temperature;
   - repeatability, where a setpoint was measured more than once at one adjustment;
   - the best adjustment using the measured gains.

   It saves the gains to the oven's profile (`OvenTracker/ovens/<key>.json`), so later runs and recommendations
   use them; the report says *"Using the gain measured on this oven before"*. A simulation never saves.
   The generic profile stands for every oven, so it doesn't either: describe your oven first.

It's one session, so you can stop at any point (**F**) and carry on later with `run --resume`; it remembers
which phase it was in. With a slow oven it's long: allow two hours a setpoint, and split it across days.
Keep the probe in the same place throughout, since the results depend on where it is.

**From runs you've already made.** Two runs of the same setpoints at different adjustments give the same
thing. Analyse them together, and add `--save-gains` to keep the result:

```powershell
OvenTracker analyze "Documents\OvenTracker\2026-11-20 1400" "Documents\OvenTracker\2026-11-22 0900" --oven <yours> --save-gains
```

If one of the runs didn't record its adjustment, `--adjustment` fills it in for any run that didn't record one.

**Repeatability.** Run the same setpoint again on another day, without changing anything, and analyse it with
the others: the characterisation lists each repeat and its spread. That spread is the real limit on how
finely the oven can be adjusted.

## What the GE PHS925ST1SS does

Not measured yet: see [#11](https://github.com/TGoodhew/OvenTracker/issues/11). The manual says only that the
adjustment goes up to 35 °F hotter or cooler, affects baking and roasting but not broil or self-clean, and is
kept through a power cut. It doesn't say whether it shifts every temperature by the same amount, or whether it
affects convection the same way as bake. `characterise --oven ge-phs925` measures both, and saves the gains to
the PHS925's profile. Record the results here:

| Measured | Result |
| --- | --- |
| Gain *g*, bake | *to measure* |
| Gain *g*, convection | *to measure* |
| Shape of the error, 300–500 °F | *to measure* |
| Run-to-run repeatability at 350 °F | *to measure* |

What is known: its thermostat cycles about every 20 minutes, with an 18 °F swing at 350 °F (2026-11-20), so
`characterise` with three setpoints in both modes takes the best part of a day.

## Checking the method

The simulator is a check on the method rather than on any real oven. Its adjustment moves the oven's target 1:1,
but the air it measures doesn't follow exactly. Its thermostat's sensor lags the air, so a cooler setting moves
the air about 0.9 degrees per degree. `characterise` on the simulator measured bake at 1.00 ±0.03 for a 10 °F
hotter change, and 0.89 ±0.04 for a 10 °F cooler one: a real effect, found rather than assumed.

The tests (`AdjustmentAdvisorTests`, `CombinedRunsTests`, `CharacteriseTests`) cover:

- a constant error;
- an error that grows linearly;
- the +2 °F at 350 °F / +15 °F at 425 °F example;
- each objective;
- the adjustment's range;
- a gain measured from two runs;
- both modes sharing one adjustment.
