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

The model's weakest point is *g*, until it's measured. To measure it:

1. **Run at the current adjustment**, at three or four setpoints across the range you use (e.g. 325, 375 and
   425 °F), giving the adjustment with `--adjustment` (0 if it has never been changed).
2. **Change the adjustment** by a clear amount, 10 °F or more (or to the recommended setting, if that is
   that far).
3. **Run the same setpoints again**, giving the new adjustment.
4. **Analyse both runs together:**

   ```powershell
   OvenTracker analyze "Documents\OvenTracker\2026-11-20 1400" "Documents\OvenTracker\2026-11-22 0900"
   ```

   The report says *"Measured from runs at different adjustments: each degree of adjustment moves this oven
   0.97 degrees (±0.01)"*, and bases its recommendation on that. It writes `report-combined.md` in the
   later run's folder.

If one of the runs didn't record its adjustment, `--adjustment` fills it in for any run that didn't record
one. Keep the probe in the same place for both runs, since the results depend on where it is.

To tell how repeatable the results are, run the same setpoint on different days. The spread between runs is
the real limit on how finely the oven can be adjusted.

## What the GE PHS925ST1SS does

Not measured yet. The manual says only that the adjustment goes up to 35 °F hotter or cooler, affects
baking and roasting but not broil or self-clean, and is kept through a power cut. It doesn't say whether
it shifts every temperature by the same amount, or whether it affects convection the same way as bake.
The steps above measure both. Record the results here:

| Measured | Result |
| --- | --- |
| Gain *g*, bake | *to measure* |
| Gain *g*, convection | *to measure* |
| Shape of the error, 300–500 °F | *to measure* |
| Run-to-run repeatability at 350 °F | *to measure* |

## Checking the method

The simulator's adjustment moves its oven 1:1, so it's a check on the method rather than on any real oven.
Two simulated runs, at adjustment 0 and at −8 °F, analysed together, gave a measured gain of 0.97 (±0.01),
and a recommendation that brought bake to within 0.5 °F. The tests (`AdjustmentAdvisorTests`,
`CombinedRunsTests`) cover:

- a constant error;
- an error that grows linearly;
- the +2 °F at 350 °F / +15 °F at 425 °F example;
- each objective;
- the adjustment's range;
- a gain measured from two runs;
- both modes sharing one adjustment.
