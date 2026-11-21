# Analysis

How a setpoint's readings become a result, how results are judged and combined, and how the recommended
adjustment and the characterisation are worked out. It is written so the analysis can be reimplemented to
give the same numbers as the C# app, which is the reference (`src/OvenTracker/Analysis/`,
`src/OvenTracker/Session/Recording.cs` and `Report.cs`, `src/OvenTracker/Commands/AnalyzeCommand.cs`).

Each section gives the reasoning in a sentence or two, then the exact rules. Where the code does something
surprising, this spec says so and marks it *(as implemented)*. That is a description, not a licence to
differ: the port does the same.

[recording.md](recording.md) describes the files. [testdata.md](testdata.md) says how closely the two
implementations must agree.

## Contents

- [Conventions](#conventions)
- [Statistics](#statistics)
- [The sample series](#the-sample-series)
- [Analysing one setpoint](#analysing-one-setpoint): [each sample](#each-sample), [smoothing](#smoothing), [noise and hysteresis](#noise-and-hysteresis), [turning points](#turning-points), [phases](#phases), [cycles](#cycles), [blocks and the no-cycle rule](#blocks-and-the-no-cycle-rule), [closing a unit](#closing-a-unit), [discarded units](#discarded-units-the-trend-test), [the estimate](#the-estimate), [the stopping rule](#the-stopping-rule), [the preheat signal](#preheat-signal), [metrics](#metrics)
- [Replay](#replay)
- [Combining runs](#combining-runs)
- [Offset and verdict](#offset-and-verdict)
- [Sensor tolerance](#sensor-tolerance)
- [Mode summaries and comparisons](#mode-summaries-and-comparisons)
- [The recommended adjustment](#the-recommended-adjustment)
- [Characterisation](#characterisation)
- [The report](#the-report)
- [Live only](#live-only)
- [Porting notes](#porting-notes)

## Conventions

- Temperatures and temperature differences are in °C, as doubles. Times inside the analysis are seconds
  since the setpoint's first reading.
- All arithmetic is IEEE 754 double precision, in the order given here. Where the order matters to the last
  bit (the smoothing's running sum), the spec says so.
- `round(x)` without a qualifier is C#'s `Math.Round`, which rounds a midpoint to the even neighbour (banker's
  rounding), like Python's `round`. Where the code rounds half away from zero, the spec says so.
- "In order of first appearance" is C#'s `GroupBy`: groups come out in the order their first member appears.
  Modes sort Bake before Convection.
- An option written `o.name` is a field of [AnalysisOptions](recording.md#analysisoptions). Defaults:

| Option | Default |
| --- | --- |
| `precisionC` | 1.0 °C (`run` sets 2 °F = 1.111… °C for a °F oven) |
| `minCycles` | 4 |
| `minBlocks` | 5 |
| `blockMinutes` | 3 |
| `minHysteresisC` | 1.0 °C |
| `smoothingSeconds` | 5 |
| `maxPointMinutes` | 120 (`run` sets `--max-minutes`, or the oven's `defaultMaxMinutes`) |
| `noCycleMinutes` | 15 |
| `flatWindowMinutes` | 10 |
| `flatSlopeCPerMinute` | 0.15 °C/min |
| `driftHorizonMinutes` | 30 |
| `defaultCycleMinutes` | 6 |

A replay uses the options in the run's `session.json` (with any `analyze` overrides), not these defaults,
unless the run has no `session.json`.

## Statistics

`Stats.cs`. The sums are plain left-to-right sums.

**Mean.** `mean(x) = (x₀ + x₁ + …) / n`.

**Standard deviation** (sample, n − 1): 0 when n < 2; otherwise `m = mean(x)`,
`sqrt(Σ (xᵢ − m)² / (n − 1))`.

**Student-t critical value.** The two-sided 95 % value, `t₀.₉₇₅(df)`, from a table, not computed:

| df | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| t | 12.706 | 4.303 | 3.182 | 2.776 | 2.571 | 2.447 | 2.365 | 2.306 | 2.262 | 2.228 |

| df | 11 | 12 | 13 | 14 | 15 | 16 | 17 | 18 | 19 | 20 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| t | 2.201 | 2.179 | 2.160 | 2.145 | 2.131 | 2.120 | 2.110 | 2.101 | 2.093 | 2.086 |

| df | 21 | 22 | 23 | 24 | 25 | 26 | 27 | 28 | 29 | 30 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| t | 2.080 | 2.074 | 2.069 | 2.064 | 2.060 | 2.056 | 2.052 | 2.048 | 2.045 | 2.042 |

For df > 30 it is `1.96 + 2.4 / df` (an approximation, as implemented). For df < 1 it is +∞. A port must use
the table, not a library quantile function: they differ in the fourth decimal.

**95 % half-width of the mean.** None when n < 2; otherwise `t₀.₉₇₅(n − 1) · sd(x) / sqrt(n)`.

**Required count** (live only, for the time left). The smallest n from `max(min, 2)` to `max` with
`t₀.₉₇₅(n − 1) · sd / sqrt(n) ≤ target`; `max` if none; `min` if `sd ≤ 0`.

**Least-squares line** `fit(x, y)`: none if n < 2 or the lengths differ. `mx = mean(x)`, `my = mean(y)`,
`sxx = Σ (xᵢ − mx)²`, `sxy = Σ (xᵢ − mx)(yᵢ − my)` (one loop, both sums together). None if `sxx ≤ 0`.
`slope = sxy / sxx`, `intercept = my − slope · mx`. The slope's standard error, only when n ≥ 3:
`sse = Σ (yᵢ − (intercept + slope · xᵢ))²`, `se = sqrt(sse / (n − 2) / sxx)`; none when n = 2.

**Median.** Sort; the middle value, or `(a + b) / 2` of the two middle values.

**Noise sigma.** The white-noise σ of a series, from second differences so that a steady ramp doesn't count:
0 when n < 5; otherwise `dᵢ = |xᵢ − 2xᵢ₋₁ + xᵢ₋₂|` for i = 2 … n − 1, and
`σ = 1.4826 · median(d) / sqrt(6)`. (For white noise the second difference has variance 6σ², and 1.4826
turns a median absolute value into σ.)

## The sample series

`SampleSeries.cs`. The readings of one setpoint, as (t, c) pairs, raw (not smoothed).

- **Adding.** A sample is accepted only if its t is greater than the last accepted t. A rejected sample is
  ignored entirely: no smoothing, no noise, no turning point.
- **lowerBound(t):** the index of the first sample with `T ≥ t`.
- **valueAt(t):** linear interpolation. `i = lowerBound(t)`; if `i = 0`, the first value; if `i = n`, the last;
  otherwise, with `a = sᵢ₋₁` and `b = sᵢ`, `a.C + (b.C − a.C) · (t − a.T) / (b.T − a.T)`.
- **mean(t₀, t₁):** the time-weighted (trapezoidal) mean. If `t₁ ≤ t₀`, `valueAt(t₀)`. Otherwise start with
  `area = 0`, `prevT = t₀`, `prevV = valueAt(t₀)`; for each sample from `lowerBound(t₀)` while `T < t₁`,
  skipping any with `T ≤ t₀`: `area += (T − prevT) · (C + prevV) / 2`, then `prevT = T`, `prevV = C`.
  Finally `area += (t₁ − prevT) · (valueAt(t₁) + prevV) / 2`, and the mean is `area / (t₁ − t₀)`.
- **range(t₀, t₁):** the min and max of `valueAt(t₀)`, `valueAt(t₁)` and every sample with `t₀ ≤ T ≤ t₁`.
- **slope(t₀, t₁):** the samples with `t₀ ≤ T ≤ t₁`; none if fewer than 3; otherwise `fit(T, C).slope`
  (°C per second), or none if the fit is none.

## Analysing one setpoint

`SetpointAnalyzer.cs`. One analyser per setpoint attempt. Its input is the setpoint `S` (°C) and, in order,
each `Ok` reading as (t, c): t the elapsed seconds, c °C. In a replay these are the values as written in
`readings.csv` (`F1` seconds, `F2` °C, an `F3` setpoint), so a replay can differ slightly from the live run
that recorded it (as implemented; see [Porting notes](#porting-notes)).

### State

| Name | Initial | Meaning |
| --- | --- | --- |
| `phase` | Heating | Heating, Settling, Measuring or Complete. |
| `kind` | none | Cycle or Block, once measuring. |
| `reason` | None | The completion reason. |
| `completedAt` | none | `now` when it completed. |
| `startC` | none | The first accepted reading. |
| `turningPoints` | empty | Every turning point found. |
| `units` | empty | The cycles or blocks kept. |
| `cycleDurations` | empty | The length of every cycle closed (kept and dropped alike). |
| `discarded` | 0 | Units dropped. |
| `unitStart` | 0 | Where the open unit began. |
| `measuringStart` | none | When measuring began. |
| `timeToSetpoint` | none | When the oven first reached the setting. |
| `overshootC` | none | The first peak against the setting. |
| `preheatT`, `preheatC` | none | The preheat signal. |
| `noiseSigma` | 0 | The latest noise estimate. |
| `lastFlatCheck` | −∞ | When the no-cycle rule last ran. |

`now` is the t of the last accepted sample (0 with none). `cooling` is true when `startC > S`: the oven
starts above the setting and has to cool to it. `minUnits` is `o.minBlocks` when `kind` is Block, otherwise
`o.minCycles`.

### Each sample

`add(t, c)`, in this order:

1. If `phase` is Complete, stop.
2. Add (t, c) to the series. If it is rejected (t not after the last), stop.
3. If `startC` is none, `startC = c`.
4. Smoothing: update the window and compute `smooth` ([below](#smoothing)).
5. Noise: append c to the noise window, dropping the oldest beyond 120 values. If the series now holds a
   multiple of 30 samples, `noiseSigma = noiseSigma(noise window)`.
6. If `timeToSetpoint` is none and (`cooling` ? `smooth ≤ S` : `smooth ≥ S`), `timeToSetpoint = t`.
7. Feed (t, `smooth`) to the turning-point detector with hysteresis `h = max(o.minHysteresisC, 5 · noiseSigma)`.
   If it returns a turning point, handle it ([phases](#phases), [cycles](#cycles)).
8. If `phase` is Measuring and `kind` is Block: with `len = o.blockMinutes · 60`, while `phase` is still
   Measuring and `t ≥ unitStart + len`, close the block `[unitStart, unitStart + len]`, then add `len` to
   `unitStart`.
9. If `phase` isn't Complete and `t − lastFlatCheck ≥ 10`: `lastFlatCheck = t`, then apply the
   [no-cycle rule](#blocks-and-the-no-cycle-rule).
10. If `phase` isn't Complete and `t ≥ o.maxPointMinutes · 60`, complete as TimedOut.

### Smoothing

A moving average over the last `o.smoothingSeconds` (5 s), so that the turning points follow the oven, not
the sensor's noise.

The window is a queue of samples with a running sum. On each accepted sample: enqueue it and add c to the
sum; then, while the oldest sample's `T < t − o.smoothingSeconds`, dequeue it and subtract its C from the sum.
`smooth = sum / count`. So the window is the samples with `T ≥ t − 5`, the current one included.

**The running sum must be kept exactly so** (add on enqueue, subtract on dequeue, in that order, never
recomputed). Its rounding error decides comparisons such as `smooth ≥ S` when a smoothed value equals the
setpoint in exact arithmetic. Recomputing the sum from the window instead moves `timeToSetpointMinutes` in the
test data by a second, and changes which cycles are found in others. The smoothed temperature used elsewhere
(the preheat signal's) is the same `sum / count`.

### Noise and hysteresis

A turning point has to be a reversal bigger than the sensor's noise. The hysteresis is
`max(o.minHysteresisC, 5 · noiseSigma)`, where `noiseSigma` is the [noise sigma](#statistics) of the last 120
raw readings, recomputed only when the number of accepted samples is a multiple of 30 (it is 0, and the
hysteresis `o.minHysteresisC`, for the first 29). The window holds raw readings from the start of the
setpoint, heating included; the second differences make a steady ramp count as no noise. Each sample uses
the hysteresis in force when it arrives.

### Turning points

`TurningPointDetector.cs`. It finds the peaks and troughs of the smoothed series. A peak is confirmed once the
temperature has fallen a hysteresis below the highest value since the last trough, and a trough the reverse,
so noise smaller than the hysteresis never makes one.

A turning point has a kind (Peak or Trough), `T` (when the extreme happened: the t of the smoothed sample that
was the extreme), `C` (the smoothed value there) and `confirmedAt` (the t of the sample that confirmed it).

State: `direction` (Unknown, Rising, Falling), the extreme (`extremeC`, `extremeT`), and, while Unknown, the
lowest and highest values seen. The first sample sets the lowest and highest to its value. Then, for each
(t, c, h), including the first:

- **Unknown:** update the lowest and highest with c. If `c ≥ lowest + h`: Rising, extreme = (c, t). Otherwise
  if `c ≤ highest − h`: Falling, extreme = (c, t). No turning point either way.
- **Rising:** if `c > extremeC`, extreme = (c, t), no turning point. Otherwise if `c ≤ extremeC − h`: a Peak at
  (extremeT, extremeC), confirmed at t; direction Falling; extreme = (c, t). Otherwise nothing.
- **Falling:** if `c < extremeC`, extreme = (c, t), no turning point. Otherwise if `c ≥ extremeC + h`: a Trough
  at (extremeT, extremeC), confirmed at t; direction Rising; extreme = (c, t). Otherwise nothing.

The comparisons are strict or not exactly as written.

### Phases

1. **Heating** (or cooling) until the first turning point. Its first peak is the overshoot.
2. **Settling.** The first cycle after the overshoot isn't used: the oven is still finding its rhythm.
3. **Measuring.** Each trough closes a cycle, and each cycle is one independent value.
4. **Complete.**

On every turning point `tp`, first append it to `turningPoints`. Then:

- If `phase` is Heating: `phase = Settling`; if `timeToSetpoint` is none, `timeToSetpoint = tp.T` (an oven
  that runs cold turns round before it reaches the setting); and if `tp` is a Peak and not `cooling`,
  `overshootC = range(tp.T − 30, tp.T + 30).max − S` on the raw series (`tp.T + 30` may be beyond the last
  sample; `valueAt` clamps). A first turning point that is a trough gives no overshoot.
- Then, only if `tp` is a Trough and `kind` isn't Block, go on to [cycles](#cycles).

The time to setpoint is whichever comes first: step 6 of [each sample](#each-sample), or the first turning
point.

### Cycles

A cycle runs trough to trough. Measuring starts at the first trough that isn't the first turning point, so
the heat-up and the first peak are never part of a unit.

For a Trough `tp` (with `kind` not Block):

- If `phase` is Measuring: close the cycle `[unitStart, tp.T]`, then `unitStart = tp.T`. `unitStart` moves even
  when the cycle is too short to keep (as implemented: the short piece is lost, not joined to the next).
- Otherwise, if `phase` is Settling and `turningPoints` holds at least 2: `phase = Measuring`,
  `kind = Cycle`, `measuringStart = tp.T`, `unitStart = tp.T`.

The change from Heating to Settling happens first, so a first turning point that is a trough moves to
Settling but doesn't start measuring (the count is 1).

### Blocks and the no-cycle rule

Some ovens hold temperature without a visible on/off cycle. With no turning point for a long time and a flat
trace, the steady state is cut into fixed blocks of `o.blockMinutes` (3 min) instead.

The rule runs at most every 10 s of t ([step 9](#each-sample)). With `last` the `confirmedAt` of the latest
turning point (0 if none), `quiet = t − last`, and `noCycle = o.noCycleMinutes · 60`:

- **Measuring cycles, but the cycling has stopped.** If `phase` is Measuring and `kind` is Cycle:
  `expected = expectedUnitSeconds() ?? o.defaultCycleMinutes · 60`. If `quiet ≥ max(3 · expected, noCycle)` and
  the trace is flat at t: add the number of units to `discarded`, clear `units`, and start blocks at t.
  Otherwise nothing. (There is no minimum-time condition here.)
- **Never cycled.** If `phase` is Heating or Settling, `quiet ≥ noCycle`, `t ≥ o.flatWindowMinutes · 60` and
  the trace is flat at t: start blocks at t.

**Flat at t:** `slope(t − o.flatWindowMinutes · 60, t)` exists and `|slope · 60| < o.flatSlopeCPerMinute`, on
the raw series.

**Start blocks at t:** `phase = Measuring`, `kind = Block`, `measuringStart` = t if it is none (it keeps the
cycles' start when there were cycles), `unitStart = t`. From then on troughs are ignored, blocks close in
[step 8](#each-sample), and `minUnits` is `o.minBlocks`. `cycleDurations` is kept but no longer used.

**expectedUnitSeconds():** `o.blockMinutes · 60` when `kind` is Block; else `median(cycleDurations)` if any;
else, with at least 2 turning points, `2 · median(gaps)`, the gaps being between consecutive turning points'
`T`; else none.

### Closing a unit

`close(kind, t₀, t₁)`:

1. If `t₁ − t₀ < 20` s, ignore it: a reversal that quick is a disturbance, not a cycle. Nothing else happens.
2. Otherwise add a unit: kind, start t₀, end t₁, `meanC = mean(t₀, t₁)` (time-weighted, raw series), and
   `minC`, `maxC` = `range(t₀, t₁)`. Its `mid = (t₀ + t₁) / 2`, `duration = t₁ − t₀`, `swing = maxC − minC`.
3. If it is a Cycle, append `t₁ − t₀` to `cycleDurations`.
4. Drop warm-up units ([below](#discarded-units-the-trend-test)).
5. Work out the [estimate](#the-estimate). If it has a half-width, `n ≥ minUnits` and
   `halfWidth ≤ o.precisionC`, complete as Converged.

### Discarded units: the trend test

While the walls and racks are still soaking up heat, the cycle means creep. Those cycles aren't the steady
state, so the oldest is dropped until the trend is too small to matter or isn't significant. The trend is
projected over at least `o.driftHorizonMinutes` (30 min), so that a slow heat-soak drift isn't hidden by a
short window.

While there are at least 3 units:

1. `fit(mid, meanC)` over the units. If it is none, or has no standard error, stop.
2. `change = |slope · max(lastMid − firstMid, o.driftHorizonMinutes · 60)|`.
3. `tStat = |slope| / se` when `se > 0`; otherwise +∞ if `slope ≠ 0`, else 0.
4. If `change ≤ o.precisionC` or `tStat ≤ 2`, stop. (The 2 is a fixed constant, not a t quantile.)
5. Remove the oldest unit, add 1 to `discarded`, and repeat.

### The estimate

With the units kept (no estimate without units), and `means` their `meanC` in order:

| Value | Rule |
| --- | --- |
| n | the number of units |
| mean | `mean(means)`: every unit counts the same, whatever its length (as implemented) |
| standard deviation | `sd(means)` (n − 1; 0 for one unit) |
| half-width | the [95 % half-width](#statistics) of `means`: none for n < 2 |
| drift | for n ≥ 3, `fit(mid, means).slope · 3600` °C per hour (none if the fit is none) |

The interval uses Student's t with n − 1 degrees of freedom, from the table. The unit means are treated as
independent values; that is why whole cycles are the unit, not single readings.

### The stopping rule

A setpoint completes when the first of these happens:

| Reason | When |
| --- | --- |
| `Converged` | A unit closes, and then there are at least `minUnits` units and the half-width is at most `o.precisionC` (no slack in the comparison). |
| `TimedOut` | A sample with `t ≥ o.maxPointMinutes · 60` arrives, after that sample's processing, if it is still not complete. |
| `Manual` | The user finished it early (N twice). Live, or a replayed `end:` (below). |
| `Skipped` | The user skipped it (S twice). |
| `Aborted` | The session stopped (Q twice, Ctrl+C, an error), or a replay found no end. |
| `None` | Never completed. Not written in practice. |

Completing sets `phase = Complete`, `reason`, and `completedAt = now`. Later samples are ignored.
`finish(reason)` completes with `reason` only if it isn't already complete.

What a replay does with each `end:` reason: `end:Converged` and `end:TimedOut` come back by themselves when the
replay sees the same readings with the same options, so the replay calls `finish(Manual)`. If the replay has
completed by itself, as it does with the same options, that does nothing. If it hasn't (for example with a
tighter `--precision` or more `--min-cycles`), the setpoint is reported as finished early by the user (as
implemented). `end:Manual`, `end:Skipped`, `end:Aborted` and `end:None` call `finish` with that reason. A replay
with looser options can converge sooner than the live run did; the readings after that are ignored.

### Preheat signal

When the oven beeps or shows it is preheated, the user presses P, and the report shows how true that signal
was. `markPreheatSignal()`: if the series is empty, nothing; otherwise `preheatT = now` and
`preheatC = sum / count` of the smoothing window. It works in any phase, Complete included, and a later mark
replaces an earlier one. A replay calls it when it reads the `preheat-signal` event, after the same readings
as live.

### Metrics

`metrics()` turns the state into a [PointMetrics](recording.md#pointmetrics). With `units` the units kept:

| Field | Rule |
| --- | --- |
| `setpointC` | S |
| `reason` | `reason` |
| `kind` | `kind`, or none |
| `units` | the number kept |
| `discardedUnits` | `discarded` |
| `meanC`, `halfWidthC`, `stdDevC`, `driftCPerHour` | the [estimate](#the-estimate) |
| `swingC` | mean of the units' swings (none without units) |
| `minC`, `maxC` | min of the units' `minC`, max of their `maxC` |
| `cycleMinutes` | mean of the units' durations / 60, only when `kind` is Cycle and there are units |
| `startC` | `startC` |
| `timeToSetpointMinutes` | `timeToSetpoint / 60` |
| `overshootC` | `overshootC` |
| `settleMinutes` | the first kept unit's start / 60; without units, `measuringStart / 60` (or none). After warm-up units are dropped, this is the first unit *kept*. |
| `measureMinutes` | (last unit's end − first unit's start) / 60; none without units |
| `totalMinutes` | `(completedAt ?? now) / 60` |
| `preheatSignalMinutes` | `preheatT / 60` |
| `preheatSignalC` | `preheatC` |
| `precisionTargetC` | `o.precisionC` |
| `longestGapSeconds` | the largest difference between consecutive accepted samples' t; none with fewer than 2 |
| `offsetC` | `meanC − setpointC` |
| `metPrecision` | `halfWidthC` exists and `halfWidthC ≤ precisionTargetC + 1e-9` |

`metPrecision` has a 1e-9 slack that the stopping rule doesn't (as implemented).

## Replay

`Replay.Analyze` in `Recording.cs`: the analysis over a saved `readings.csv`, as it ran live. Input: the CSV
and the options. Output: a list of results, each (point, metrics, afterReset, startedWarm), in the order they
close.

State: the open analyser and point (none), the open setpoint's `afterReset` and `startedWarm`, and the pending
marks `resetPending` and `warmPending` (all false).

To **close with reason r**: if an analyser is open, `finish(r)`, add (point, metrics, afterReset, startedWarm)
to the results, and clear the open analyser and point.

Skip the header line. For each later line, split on `,` into fields f[0], f[1], …:

1. If there are fewer than 9 fields, or f[1] isn't an integer, skip the line.
2. **Point 0.** If f[5] is `Event`: `resume` or `reset` sets `resetPending`; `started-warm` sets `warmPending`.
   Every point-0 line is then skipped.
3. Otherwise parse the mode f[2] (`Bake` or `Convection`, exact case), the setpoint f[3] (a double), and the
   elapsed f[4] (a double; 0 if empty or unparsable).
4. **An event** (f[5] is `Event`), by its name f[8]:
   - `start`: close with Aborted; remove from the results every result whose point has this index; set
     `afterReset = resetPending` and `startedWarm = warmPending`, then clear both pending marks; open a new
     analyser for the setpoint, with the point (index, mode, setpoint).
   - `restart`: open a new analyser and point, discarding the open one without a result. `afterReset` and
     `startedWarm` stay as they were.
   - `preheat-signal`: mark the preheat signal on the open analyser, if any.
   - `end:<reason>`, if an analyser is open: close with Manual if the reason is `Converged` or `TimedOut`,
     otherwise with the reason. An unknown reason is an error.
   - Anything else: nothing.
5. **A reading:** if no analyser is open, or the open point's index isn't f[1], or f[5] isn't `Ok`, skip it.
   Otherwise, if f[6] parses as a double, `add(elapsed, f[6])`.

At the end of the file, close with Aborted.

Numbers are parsed in the invariant culture: `.` for the decimal point, exponents accepted.

The replayed point has no `adjustmentC`. `analyze` then puts the plan's point in its place (below).

## Combining runs

`AnalyzeCommand.Combine` and `Execute`. `analyze` takes one or more runs. Several runs of the same setpoints at
different adjustments measure how far the adjustment moves the oven.

**Loading.** A run is a folder holding `readings.csv` and `session.json`, or a `readings.csv` path. Without a
`session.json` the session is a default (as implemented): `unit` Fahrenheit, `toleranceC` 15/1.8, the default
AnalysisOptions (precision 1.0 °C), an empty plan, `started` the CSV's creation time.

**The unit** for reading the options is the latest run's (by `started`).

**The options for a run:** its own `options`, with `precisionC` replaced by `--precision` (converted from the
unit) and `minCycles` by `--min-cycles`, when given.

**One run:**

- The session is the run's, with `adjustmentC = --adjustment ?? the run's adjustmentC` (the option wins).
- Replay it with its options. Each result's point is replaced by the plan's point with the same index, if
  there is one. So the point has the plan's full-precision setpoint and its `adjustmentC`, while the metrics
  keep the CSV's three-decimal setpoint.

**Several runs:**

- Order them by `started`.
- For each run, `adj = run.adjustmentC ?? --adjustment ?? 0`. Replay it with its own options. For each result,
  in order, the point becomes the replayed point (the CSV's setpoint, three decimals) with
  `index` = the number of results so far + 1, and `adjustmentC` = the `adjustmentC` of the run's plan point
  with the original index, if it has one, else `adj`.
- The session is the latest run's, with `adjustmentC = latest.adjustmentC ?? --adjustment ?? 0` (so it is
  never unknown, as implemented) and `plan` = the combined results' points.

**Then, either way,** the session's fields become:

| Field | Becomes |
| --- | --- |
| `options` | the options for the session, as above |
| `toleranceC` | `--tolerance` (converted), else the session's |
| `oven` | the profile named by `--oven`, else the session's |
| `adjust` | the [adjustment options](#adjustment-options) built from the session's `adjust` and that oven |
| `sensor` | the sensor [resolved](#sensor-tolerance) from `--probe`, `--probe-tolerance` and `--probe-max` over the session's |

The report, and `results-reanalysed.json` (one run) or `results-combined.json` (several), are written from this
session and these results. If `--save-gains` is given, or the session's `characterise.changedToC` is set, the
characterisation is written too.

## Offset and verdict

`SessionSummary.Judge`. The offset is `meanC − setpointC`: positive when the oven runs hot. The verdict compares
the offset and its 95 % half-width with the session's `toleranceC` (T):

| Condition, in order | Verdict |
| --- | --- |
| no `offsetC` (no mean) | `Inconclusive` |
| no half-width (one unit), and `|offset| ≤ T` | `Borderline` |
| no half-width, otherwise | `OutOfTolerance` |
| `|offset| + halfWidth ≤ T` | `Accurate` |
| `|offset| − halfWidth > T` | `OutOfTolerance` |
| otherwise | `Borderline` |

In words: accurate when the whole interval is within tolerance, out of tolerance when the whole interval is
outside it, borderline when it straddles the limit.

The verdict uses neither the completion reason (an Aborted setpoint with a mean is judged like any other) nor
the sensor's tolerance. The probe's own accuracy is reported beside the verdict, not added to it (as
implemented; the README says the same: "The thermocouple's own accuracy comes on top of the ±95 % figure").

The verdict isn't stored in `results.json`; it is worked out for the report, where the names are `Accurate`,
`Borderline`, `Out of tolerance` and `Inconclusive`.

The tolerance is `--tolerance`, or by default ±15 °F (8.333… °C) for a °F oven and ±8 °C for a °C oven.

## Sensor tolerance

`Sensor.ToleranceC(c)`: the probe's standard tolerance at temperature c, ± °C:

- `limitsAdd` true (the way RTD classes are written): `limitC + limitFraction · |c|`;
- `limitsAdd` false (thermocouples, most probes): `max(limitC, limitFraction · |c|)`.

`c` is in °C (as implemented: the percentage is of the temperature in °C, as in ASTM E230 and IEC 60751).

The sensors the app knows:

| Sensor | `limitC` | `limitFraction` | `limitsAdd` | `maxC` |
| --- | --- | --- | --- | --- |
| type K, J or N thermocouple (standard limits) | 2.2 | 0.0075 | false | — |
| type T thermocouple | 1.0 | 0.0075 | false | — |
| type E thermocouple | 1.7 | 0.005 | false | — |
| `--probe type-k` | 2.2 | 0.0075 | false | — |
| `--probe type-k-special` (`type K thermocouple (special limits)`) | 1.1 | 0.004 | false | — |
| `--probe 80bk-a` (`Fluke 80BK-A type K probe`) | 2.2 | 0.02 | false | 260 |
| PT*n* RTD, class AA | 0.1 | 0.0017 | true | — |
| PT*n* RTD, class A | 0.15 | 0.002 | true | — |
| PT*n* RTD, class B | 0.3 | 0.005 | true | — |

**Resolving the sensor**, when any of `--probe`, `--probe-tolerance` or `--probe-max` is given: start from the
named probe's sensor, or else the session's. A `--probe-tolerance` such as `2.2C,2%` (the greater of the two) or
`0.3C+0.5%` (the two added; without C or F, the degrees are in the oven's unit) replaces `limitC`,
`limitFraction` and `limitsAdd`, and, when no `--probe` is named, the name becomes `<name> (custom tolerance)`
(`probe (custom tolerance)` if there was no sensor). `--probe-max` sets `maxC`. With none of the three, the
session's sensor stands.

The tolerance is used only in the report's "How far to trust it", at the highest setpoint.

## Mode summaries and comparisons

`SessionSummary`. Used by the report's findings and its bake-and-convection table. *Usable* results are those
with a mean.

**By mode**, for each mode with usable results, Bake first, with its results sorted by setpoint:

| Value | Rule |
| --- | --- |
| points | the number of usable results in the mode |
| mean offset | mean of their offsets |
| max abs offset | max of |offset| |
| gain per degree | with at least 2 distinct setpoints, `fit(setpointC, meanC).slope`; else none |
| mean swing | mean of the non-null `swingC`, or none |
| mean cycle | mean of the non-null `cycleMinutes`, or none |

**Comparisons** of bake and convection: group the usable results by `round(point.setpointC, 2)`, in ascending
order. In each group take the *last* Bake result and the *last* Convection result; when there are both,
`Δ = convection meanC − bake meanC`, and the difference is **significant** when `|Δ| > sqrt(a² + b²)`, a and b
being their half-widths (+∞ when missing, so never significant then). This groups setpoints by two decimals,
while the recommended adjustment groups them within 0.05 °C (as implemented).

## The recommended adjustment

`AdjustmentAdvisor.cs`; the model is in [docs/oven-control-model.md](../docs/oven-control-model.md). In short,
a mode's error at setting s with the adjustment at k is `error₀(s) + g·k`, where g, the gain, is how far one
degree of adjustment moves the oven. The advisor finds the change to the adjustment, on the oven's steps and
within its range, that best meets the objective.

### Constants

| Name | Value | Meaning |
| --- | --- | --- |
| `WorthC` | 5/1.8 °C (5 °F) | An error smaller than this isn't worth correcting: about the probe's own accuracy. |
| `ShapeSpanC` | 50/1.8 °C (50 °F) | Setpoints closer than this can't show how the error changes with temperature. |
| `SameSetpointC` | 0.05 °C | Setpoints with the same key `round(setpointC / 0.05)` count as the same (runs in °F store 176.667 and 176.66666…). |

### Adjustment options

The session's `adjust`; or, if it has none, the defaults for its oven (the generic profile if none) and unit:

- the generic oven: step 1/1.8 °C and range 35/1.8 °C for a °F oven, step 1 °C and range 19 °C for a °C oven,
  sharing `true`, and `knownGains` from the profile's gains, if any;
- another oven: its `adjustStepC`, `adjustRangeC` and `sharedAdjustment`, with its gains as `knownGains`;
- either way objective `Balanced`, model `Shift`, and no favour.

`run` and `analyze` build them the same way (`SharedSettings.BuildOptions`): the saved options, or the oven's
defaults when none were saved. If options were saved and `--oven` is given, `knownGains` becomes the new oven's
(possibly none). Then `--adjust-step` and `--adjust-range` (converted from the unit) replace the step and range;
`--objective` sets the objective, and clears the favour unless `--favour` is also given; `--favour` (a
temperature, converted) sets `favourC`; `--adjust-model` sets the model; `--separate-adjustments` sets
`sharedAcrossModes` false.

### Inputs

Every result with an offset gives an adjust point: mode, `point.setpointC`, `offsetC`, `halfWidthC`, and the
adjustment `point.adjustmentC ?? session.adjustmentC ?? 0`, in results order. The current adjustment `current`
is `session.adjustmentC` (possibly none).

### Groups

With no points there is no advice. If `sharedAcrossModes`: one group of all the points, whose mode is that mode
when only one was measured, else none (bake and convection together). Otherwise one group per mode, Bake first.
Each group gives one advice.

### Gains

For each mode in the group (in order of first appearance), from that mode's points:

1. **Measured.** Group the points by setpoint key. In each group, for every pair i < j in order:
   `dk = adjⱼ − adjᵢ`; skip the pair if `|dk| < 2·stepC − 1e-9` (too small a change to measure anything);
   otherwise `num += (offsetⱼ − offsetᵢ)·dk`, `den += dk²` and `var += (hwᵢ² + hwⱼ²)·dk²`, taking a missing
   half-width as 1 °C (as implemented). If `den > 0`: gain `num / den`, source `Measured`, half-width
   `sqrt(var) / den`.
2. **Profile.** Otherwise, if `knownGains` has the mode: that gain, source `Profile`, no half-width.
3. **Assumed.** Otherwise 1.0, source `Assumed`, no half-width. With model `Gain`, if
   `fit(setpointC, setpointC + offsetC)` over the mode's points has `0.5 < slope < 1.5`, the slope instead.

The offsets used here are the raw ones, each at its point's own adjustment.

### The errors now

Every point's offset as it would be at the reference adjustment `ref = current ?? the group's last point's
adjustment`: `offset + g(mode)·(ref − adjustment)`. Group these by (mode, setpoint key). Each group gives one
point *now*: the mode, the mean setpoint, the mean offset, the half-width `sqrt(Σ hw²) / count` if every
half-width is present (else none), and adjustment `ref`. Sort by mode, Bake first, then by setpoint.

### The search

`lo = −rangeC − (current ?? 0)` and `hi = rangeC − (current ?? 0)`: the changes that keep the setting within the
range. With no current adjustment, the range is taken about 0 (as implemented).

All the following are over the points now, each with its mode's gain g.

1. If every point's gain is at most 0.05, the adjustment doesn't move the oven: the change is 0, not clamped.
2. **Favour**, when `favourC` is set (the objective is then ignored, as implemented). `ḡ` is the mean gain over
   the points (one per point, not per mode). The error at the favoured temperature is
   `intercept + slope · favourC` from `fit(setpointC, offset)` over the points (both modes in one line when
   they share an adjustment, as implemented), or the mean offset if the fit is none.
   `wanted = snap(−error / ḡ)`, with `snap(c) = roundHalfAwayFromZero(c / stepC) · stepC`. The change is
   `wanted` clamped to `[snapUp(lo), snapDown(hi)]`, with `snapUp(c) = ceil(c / stepC − 1e-9) · stepC` and
   `snapDown(c) = floor(c / stepC + 1e-9) · stepC`. It is clamped when `|change − wanted| > 1e-9`.
3. **Balanced or Minimax.** The cost of a change Δ: for Balanced, `Σ (offset + g·Δ)²`; for Minimax,
   `max |offset + g·Δ|`. Every step is tried outright (few enough, and exact for either objective):
   `bestIn(from, to)` tries `Δ = k · stepC` for each integer k from `ceil(from / stepC − 1e-9)` up to
   `floor(to / stepC + 1e-9)`, starting from best Δ = 0 at cost +∞. A Δ becomes the best if its cost is lower
   than the best cost by more than 1e-9, or within 1e-9 of it with a smaller |Δ| (on a tie, the smaller
   change). With `reach = 4·rangeC + max |offset| / max(0.05, min g)`, `anywhere = bestIn(−reach, reach)` and
   the change is `bestIn(lo, hi)`. It is clamped when `anywhere < lo − 1e-9` or `anywhere > hi + 1e-9`.

[docs/oven-control-model.md](../docs/oven-control-model.md) says the app compares the two neighbouring steps;
the code searches every step. For these convex costs the result is the same, ties apart.

### The advice

| Field | Rule |
| --- | --- |
| `changeC` | the change found |
| `currentC` | `current` |
| `newSettingC` | `current + change`, when `current` is known (so it is on the step grid only if `current` is) |
| `clamped` | from the search |
| `gain` | the mean of the group's mode gains (one per mode) |
| `gainMeasured` | every mode gain is `Measured` |
| `gainHalfWidthC` | when `gainMeasured`: with one mode, its half-width; with more, `sqrt(Σ hw²) / count` if all are present, else none. Otherwise none. |
| `options` | the adjustment options |
| `mode` | the group's mode, or none |
| `residuals` | for each point now: mode, `setpointC`, `nowC` = its offset, `afterC = offset + g·change`, `halfWidthC` |
| `dialCorrections` | none if every `|afterC| < WorthC`; otherwise, for each residual with `|afterC| ≥ WorthC`: mode, `wantC` = its setpoint, `setC = setpoint − afterC` (where it still runs hot by r, set r lower) |
| `addSetpointsC` | below |
| `tooFewSetpoints` | below |
| `curved` | below |
| `gainFromProfile` | not `gainMeasured`, and some mode gain is from the `Profile` |
| `gains` | each mode's gain: `gain`, `source`, `halfWidth` |
| `worstAfterC` | computed: the largest `|afterC|`, 0 with no residuals |

**Setpoints to add.** For each mode among the points now: its distinct setpoints, sorted, and
`span = last − first`. The reason is the first that applies:

- one setpoint: "With one setpoint per mode, the app can't tell an error that's the same at every setting from
  one that grows with temperature."
- `span < ShapeSpanC`: "The setpoints are too close together to show how the error changes with temperature."
- two setpoints, and `|offset of the mode's last point − offset of its first| ≥ WorthC` (sorted by setpoint):
  "The error changes between the two setpoints. A third, in between, would show whether it changes in a
  straight line."
- otherwise the mode needs nothing.

For a mode that needs setpoints: with two setpoints and `span ≥ ShapeSpanC`, add their midpoint; otherwise add
`first − ShapeSpanC` and `last + ShapeSpanC`. Then clamp each to [121, 260] °C (ordinary baking temperatures,
250–500 °F), drop exact duplicates, and sort. `tooFewSetpoints` is the reason of the last mode that needed
setpoints (as implemented: one mode's reason overwrites another's, while the setpoints of both are kept).

**Curved.** For each mode with at least 3 points now: `fit(setpoint, offset)`; if the fit exists and
`max over the points of (|offset − (intercept + slope·setpoint)| − (hw ?? 0))` is greater than
`max(1.0, WorthC / 2)` (1.389 °C), the error bends more than a straight line and the uncertainty explain:
curved.

## Characterisation

`Characterisation.cs`. What a characterise run, or `analyze` of runs at different adjustments, found about the
oven: each mode's gain and error shape, how repeatable the results are, and the best adjustment with the gains
measured.

**Modes.** Advise as above, with `sharedAcrossModes` forced false and `current = session.adjustmentC`. For each
advice (one per mode): the mode; its gain if the source is `Measured`, else none; with at least 2 residuals in
the mode, `slopePerDegree = fit(setpoint, nowC).slope`; `curved` from the advice; `setpoints` = the number of
residuals.

**Repeats.** The results with a mean, grouped by (mode, `round(point.setpointC / 0.05)`, `round(adj / 0.05)`)
with `adj = point.adjustmentC ?? session.adjustmentC ?? 0`, in order of first appearance. Each group of two or
more is a repeat: the mode, the first result's setpoint and adjustment, and the means in order;
`spreadC = max − min`, and `stdDevC` their sample standard deviation.

**Recommendation.** The first advice of the ordinary recommendation (with the session's sharing), or none.

**measuredAny:** some mode's gain is measured. The measured gains are what is saved to the oven's profile, with
the time (not for the generic profile, and not for a simulation).

`characterisation.json` is the record serialised with the same options as `results.json`: `modes` (`mode`,
`gain` {`gain`, `source`, `halfWidth`}, `slopePerDegree`, `curved`, `setpoints`), `repeats` (`mode`,
`setpointC`, `adjustmentC`, `meansC`, `spreadC`, `stdDevC`), `recommendation` (an advice, with the fields above)
and `measuredAny`. Absent values are left out, as elsewhere.

**Phase 2 of a live characterise run** (`RunCommand.PlanPhase2Async`). When phase 1 is done:
`from = session.adjustmentC ?? 0`; `wanted` = the first advice's change (0 if none); the direction is
`sign(wanted)` if `|wanted| ≥ stepC / 2`, else −1 if the mean offset is positive and +1 if not; the size is
`round(max(characterise.stepC, |wanted|) / stepC) · stepC`; `to = clamp(from + direction·size, −rangeC, rangeC)`,
or, if that moves it by less than `2·stepC`, the same the other way. Phase 2 measures, at adjustment `to` and with
the next indexes, the highest setpoint in each planned mode in turn, then the lowest in each (once if they are
the same). The session gets `adjustmentC = to`, `characterise.changedToC = to` and `changedAt`.

## The report

`Report.cs`. `report.md`, `report-reanalysed.md` and `report-combined.md` are the same report. It has to match
line by line ([testdata.md](testdata.md)), so its wording is given here exactly. `{…}` is a value and `[…]` an
optional part.

### Formatting

In the oven's unit: `toDisplay(c)` is `c·9/5 + 32` for °F, c for °C; `deltaToDisplay(d)` is `d·1.8` or d;
`fromDisplay` and `deltaFromDisplay` are the inverses (`(f − 32)·5/9` and `d / 1.8`). The symbol is `°F` or
`°C`. `F<n>` is fixed-point with n decimals in the invariant culture.

| Name | Text |
| --- | --- |
| `T(c, n=1)` | `{toDisplay(c) F<n>} {symbol}`, e.g. `352.4 °F` |
| `D(d, n=1)` | a sign, then `{|deltaToDisplay(d)| F<n>} {symbol}`. With v = `deltaToDisplay(d)`, the sign is `+` if `round(v, n) > 0`, `−` (U+2212) if `round(v, n) < 0`, and `±` otherwise (banker's rounding). e.g. `+2.4 °F`, `−1.4 °F`, `±0.0 °F` |
| `Span(d, n=1)` | `{|deltaToDisplay(d)| F<n>} {symbol}` |
| `Setpoint(c)` | `round(toDisplay(c), 1)` formatted `0.#` (no trailing `.0`), then ` {symbol}`, e.g. `350 °F`, `176.7 °C` |
| `Minutes(m)` | `{m F0} min`, or `—` (U+2014) if none |
| `Adjustment(d)` | `0 {symbol}` if `|deltaToDisplay(d)| < 0.5`, else `D(d, 0)` |
| mode | `Bake` or `Convection` |

A missing value in a table cell is `—`. A table is a header line `| a | b | … |`, then `|` followed by ` --- |`
for each column, then one line per row `| x | y | … |`, with any `|` in a cell written `\|`. Lines end with the
platform's newline.

### Layout

```
# Oven temperature accuracy

Run {started, local, yyyy-MM-dd HH:mm}[ in {n} sittings ({each sitting, local, yyyy-MM-dd HH:mm, joined ", "})] with {meter} on {location}. A setpoint is accurate when its mean, including the ±95 % interval, is within ±{Span(toleranceC)} of the setting.

[## Findings

- {finding}
…
]
## Each setpoint

{the setpoint table}

{the explanation: one or two lines, then one or two blank lines, as below}
[## Best adjustment …]
[## Preheat signal …]
[## Bake and convection …]
[## How long each setpoint needed …]
## How far to trust it

- {sensor accuracy}
[- {gap}, one per line]
[- {probe range}]
- The probe measures the air where you put it. Ovens are hotter near the elements and walls, so keep it in the centre for every run if you want to compare runs.
- If your oven converts convection temperatures down automatically (often by 25 °F / 15 °C), the convection setting entered here should be the one the oven actually targets.

Settings: precision ±{Span(precisionC)}, at least {minCycles} cycles, up to {maxPointMinutes F0} min per setpoint. Oven's thermostat adjustment during the run: {Adjustment(adjustmentC), or "not recorded"}. Raw data: `readings.csv`.
```

The sittings part appears only with more than one sitting. Each optional section ends with a blank line. The
local time is in the PC's time zone (as implemented; see [Porting notes](#porting-notes)).

The explanation is one line: "*Measured* is the time-weighted mean over whole thermostat cycles once the oven had
settled; *±95 %* is how sure that mean is. *Swing* is the average peak-to-peak of a cycle and *Range* the lowest
and highest readings while measuring. *To temp* is from pressing Start to first reaching the setting (or the
thermostat first turning round, for an oven that runs cold); *First peak* is the first high point against the
setting, which is the overshoot when it is positive."

If any result started warm, or came after a reset, a second line follows it, starting with a space (as
implemented: the C# appends it after the line has ended, so it is a line of its own rather than the end of the
first; Markdown renders them as one paragraph). It is " The setpoint marked *started warm* began with the oven
still warm, not from cold, so its *To temp* and *First peak* aren't from cold. Its mean is as good as any." if
any started warm, followed (on the same line) by " Setpoints marked *after a reset* were measured after a break
(the run was resumed, or the oven was left to cool first), so their *To temp* and *First peak* aren't comparable
with the others. Their mean is." if any came after a reset. Then one blank line. Without either, the explanation
is followed by two blank lines.

### The setpoint table

Columns: `Mode | Set | Measured | Offset | ±95 % | Verdict | Swing | Range | Cycle | To temp | First peak | Time | Used`.
One row per result, in results order.

| Column | Text |
| --- | --- |
| Mode | the mode |
| Set | `Setpoint(point.setpointC)`; then ` (adj {Adjustment(point.adjustmentC)})` if `point.adjustmentC` is set and differs from `session.adjustmentC ?? 0` (an exact comparison); then ` (after a reset)` if `afterReset`; then ` (started warm)` if `startedWarm` |
| Measured | `T(meanC)` |
| Offset | `D(offsetC)` |
| ±95 % | `Span(halfWidthC)` |
| Verdict | the verdict's name |
| Swing | `Span(swingC)` |
| Range | `{toDisplay(minC) F0}–{toDisplay(maxC) F0}` (an en dash, U+2013, no spaces) |
| Cycle | `{cycleMinutes F1} min`, only when `kind` is Cycle |
| To temp | `Minutes(timeToSetpointMinutes)` |
| First peak | `D(overshootC)` |
| Time | `Minutes(totalMinutes)` |
| Used | `—` with no units, else `{units} cycles` or `{units} blocks` |

### Findings

Present when there is at least one line. In order:

1. For each mode summary: "{mode}: runs {Span(meanOffset)} {hot|cold} on average over {points}
   setpoint[s] (worst {Span(maxAbsOffset)})." (`hot` when the mean offset ≥ 0; `setpoint` when points is 1).
   Then, if the gain per degree exists and points ≥ 2: of the mode's results with an offset, `lo` is the first
   with the lowest setpoint and `hi` the first with the highest, and `change = hi.offset − lo.offset`. If
   `|change| ≥ max(2·precisionC, 1.0)`: " The error {grows|shrinks} with temperature: {D(lo.offset)} at
   {Setpoint(lo.setpoint)}, {D(hi.offset)} at {Setpoint(hi.setpoint)}, so a single adjustment can't correct all
   of it." (`grows` when change > 0). Otherwise: " The error is about the same at every setpoint, so the oven's
   thermostat adjustment can correct it." Then, if the mean swing exists: " The thermostat swings
   {Span(meanSwing)} peak to peak", followed by ", one cycle every {meanCycle F1} min." when the mean cycle
   exists, else ".".
2. For each advice: "{headline} Details are under Best adjustment."
3. For each comparison: if significant, "At {Setpoint}, convection is {Span(Δ)} {hotter|cooler} than bake."
   (`hotter` when Δ > 0); otherwise "At {Setpoint}, bake and convection are the same within the measurement's
   uncertainty ({D(Δ)})." The setpoint is that of the group's first result.

### Best adjustment

If there is any advice: `## Best adjustment`, a blank line, then for each advice:

```
**{headline}**

- {detail}
…

{the residual table}

[What to set after adjusting, where the adjustment alone leaves the oven 5 °F (3 °C) or more out:

{the dial table}
]
```

and after the last, the line "[Reading the results and adjusting the oven](https://github.com/TGoodhew/OvenTracker/blob/main/docs/adjusting-the-oven.md) explains how to enter the adjustment." and a blank line.

**Scope:** the advice's mode, or "Bake and convection (one adjustment for both)" when it has none.

**Headline:** if `|deltaToDisplay(change)| < 0.5`: "{scope}: leave the thermostat adjustment as it is[
({Adjustment(current)})]; no step would make the oven more accurate." (the bracket when `current` is known).
Otherwise, when the new setting is known: "{scope}: set the oven's thermostat adjustment to
{Adjustment(newSetting)} (it's {Adjustment(current)} now)." Otherwise: "{scope}: change the oven's thermostat
adjustment by {D(change, 0)}, from whatever it is set to now."

**Details**, in order, each when it applies:

1. With a favour: "This makes the oven right at {Setpoint(favourC)}, the temperature you favour."; else with
   Minimax: "This keeps the worst of the setpoints measured as close to its setting as it can."; else "This
   makes the overall error across the setpoints measured as small as it can."
2. If `worstAfterC < WorthC`: "Afterwards every setpoint measured should be within {Span(worstAfter)} of its
   setting, which is about the probe's own accuracy."; else "Afterwards the worst setpoint is still
   {Span(worstAfter)} out, so the table below says what to set to make up for it."
3. If `gainMeasured`: "Measured from runs at different adjustments: each degree of adjustment moves this oven
   {gain F2} degrees", then " (±{gainHalfWidth F2})." when it has a half-width, else "."; else, with model `Gain`
   and `|gain − 1| > 1e-6`: "This assumes the adjustment is scaled by the oven's own error slope, so each degree
   moves it {gain F2} degrees. That hasn't been measured; see docs/oven-control-model.md."; else "This assumes
   the adjustment moves the oven by the same amount at every setting. That hasn't been measured for this oven:
   after adjusting, run the same setpoints again and analyze both runs together (see
   docs/oven-control-model.md)."
4. If `clamped`: "The oven's adjustment only goes to ±{Span(rangeC, 0)}, so this is its limit. An error this
   large is worth a service call: the oven's sensor may be faulty."
5. If `current` is unknown and `|deltaToDisplay(change)| ≥ 0.5`: "The run didn't record the oven's current
   adjustment. Give it with --adjustment and the app will say what to set it to."
6. If `curved`: "The error bends with temperature rather than following a straight line, so where it matters,
   use the table of what to set."
7. If `gainFromProfile`: "Using the gain measured on this oven before (its profile, from characterise): each
   degree of adjustment moves it {gain F2} degrees."
8. If `|deltaToDisplay(change)| ≥ 0.5` and the session's oven isn't generic and has `howToChange` steps: "To set
   it on the {oven name}: 1. {step} 2. {step} …" (numbered, joined by single spaces).
9. If `tooFewSetpoints`: "{reason} Worth adding: {the setpoints to add, rounded, joined ", "}." Rounded is
   `Setpoint(fromDisplay(round(toDisplay(c) / step) · step))`, with step 25 for °F and 10 for °C.

Each detail is written `- {detail}`.

**Residual table:** `Mode | Set | Now | After adjusting | ±95 % | Within tolerance?`; per residual: the mode,
`Setpoint`, `D(nowC)`, `D(afterC)`, `Span(halfWidthC)` or `—`, and `Yes` if `|after| + hw ≤ T`, `No` if
`|after| − hw > T`, else `Borderline`, with hw the half-width, or 0 if none. (Here a missing half-width counts
as 0, where the verdict counts it as infinite: as implemented.)

**Dial table:** `Mode | For | Set the oven to`; per correction: the mode, `Setpoint(wantC)`, and
`{round(toDisplay(setC)) formatted 0} {symbol}`.

### Preheat signal

When any result has `preheatSignalMinutes`: `## Preheat signal`, a blank line, and the table
`Mode | Set | Signalled after | Oven was at | Against setting`, one row per such result: the mode,
`Setpoint(point.setpointC)`, `Minutes(preheatSignalMinutes)`, `T(preheatSignalC)` and
`D(preheatSignalC − point.setpointC)` (`—` when there is no `preheatSignalC`). Then a blank line.

### Bake and convection

When there are comparisons: `## Bake and convection`, a blank line, and the table
`Set | Bake | Convection | Convection − Bake | Real difference? | Swing bake / conv | To temp bake / conv`, one row
per comparison: `Setpoint`, `T(bake mean)`, `T(convection mean)`, `D(Δ)`, `Yes` or `No (within uncertainty)`,
`{Span(bake swing) or —} / {Span(convection swing) or —}` and
`{Minutes(bake to temp)} / {Minutes(convection to temp)}`. Then a blank line.

### How long each setpoint needed

When any result is `Converged`. With `done` the converged results in order, `first` the first of them, and
`rest` the others' `totalMinutes`, the lines are:

- "The first setpoint took {Minutes(first total)} ({Minutes(first settle)} to settle, then
  {Minutes(first measure)} measuring)."
- with one in `rest`: "The next setpoint took {Minutes(it)}, since the oven was already hot."; with more: "Later
  setpoints took {Minutes(min)} to {Minutes(max)} (median {Minutes(median)}), since the oven was already hot."
- if any of `done` is Cycle: "Each result needed {n} full thermostat cycles to pin the mean to
  ±{Span(precisionC)}; the app counts these as it goes, so the time adapts to your oven.", with {n} the number of
  units when the fewest and most (over the Cycle results) are equal, else `{min}–{max}` (en dash).

Each is written `- {line}`, then a blank line, then the table `Mode | Set | Settling | Measuring | Total | Ended`
for every result: the mode, `Setpoint`, `Minutes(settleMinutes)`, `Minutes(measureMinutes)`,
`Minutes(totalMinutes)` and the reason: Converged "settled and measured", TimedOut "stopped at the
{maxPointMinutes F0} min limit", Manual "finished early by you", Aborted "interrupted", Skipped "skipped", None
empty. Then a blank line.

### How far to trust it

**Sensor accuracy**, at `top`, the highest `point.setpointC` of the results (200 °C with none). Without a sensor:
"The ±95 % column is the statistical uncertainty only. The probe and the meter each add their own error, given
in their data sheets. An offset smaller than these may be the probe, not the oven." With one: "The ±95 % column
is the statistical uncertainty only. The probe itself adds its own: {who} is good to {limit},
±{Span(ToleranceC(top))} at {Setpoint(top)}, and the meter's own accuracy (in its data sheet) adds to that. An
offset smaller than these may be the probe, not the oven." {who} is "a standard-grade {name}" when
`isGenericType`, else "the {name}". {limit} is "±{Span(limitC)} plus {limitFraction·100, formatted 0.###} % of
the temperature" when `limitsAdd`, else "the greater of ±{Span(limitC)} and ±{limitFraction·100, formatted
0.###} % of the temperature".

**Gaps**, for each result with `longestGapSeconds ≥ 60`: "{mode} {Setpoint} has a {Minutes(gap / 60)} gap in its
readings (the meter went quiet, perhaps its auto power-off). The cycle means across the gap are interpolated, so
treat it with a little more care."

**Probe range**, when the sensor has `maxC` and there are results: `top` is the highest of every result's `maxC`
and `setpointC + overshootC` (the metrics' values). Nothing if there are none or `top < maxC − 25/1.8`. If
`top > maxC`: "The {name} is rated to {T(maxC, 0)}, but the readings reached {T(top, 0)}: those above its rating
can't be trusted, and the probe may be damaged."; else "The {name} is rated to {T(maxC, 0)}; the readings reached
{T(top, 0)}, close to that."

### characterisation.md

```
# Characterisation: {oven name, or "the oven"}

Measured {started, local, yyyy-MM-dd} with {meter}[ and a {sensor name}].

## How far the adjustment moves the oven

| Mode | Gain | ±95 % | Meaning |
| --- | --- | --- | --- |
{per mode: "| {mode} | {gain F2} | {halfWidth F2, or —} | each degree of adjustment moves it {gain F2} degrees |", or without a measured gain "| {mode} | — | — | not measured at two adjustments |"}

A gain of 1.00 means the adjustment does exactly what it says. Under 1, the oven moves less than the setting; over 1, more.

## How the error changes with temperature

{per mode: "- {mode}: {shape}."}

[## Repeatability

| Mode | Set | Adjustment | Means | Spread |
| --- | --- | --- | --- | --- |
{per repeat: "| {mode} | {Setpoint} | {D(adjustment, 0)} | {T(mean), joined ", "} | {Span(spread)} |"}

The spread between repeats is the real limit on how finely this oven can be adjusted.

][## Best adjustment, with the measured gains

**{the recommendation's headline}**

]
```

{shape}: with fewer than 2 setpoints, "one setpoint, so its shape isn't known"; curved, "it bends with
temperature, more than a straight line explains"; with `|slopePerDegree| ≥ 0.02`, "its error changes by about
{D(deltaFromDisplay(slope·100), 0)} per 100 {symbol} of setting ({hotter|cooler} at higher settings), so one
adjustment can only centre it" (`hotter` when the slope > 0); otherwise "its error is about the same at every
setting, so one adjustment corrects it everywhere". The slope is degrees per degree, the same in either unit.

## Live only

These belong to a live run, not to the recorded results, and a port of `analyze` doesn't need them:

- **Time left** (`EstimateRemaining`), from the required count and the expected unit length.
- **The heading check** (`HeadingCheck`): for a step of 20 °F or more, within 6 minutes of the setpoint starting
  the oven should have moved towards the new setting by half the step (at most 25 °F, but beyond half the
  previous setpoint's swing plus 5 °F, and at most 80 % of the step). It writes `not-heading` and `heading`.
- **The cooling wait** (`ResetWaiter`, `CoolingModel`): a Newton's-law fit that predicts when the oven is cool
  enough for the next setpoint. It writes `reset-start` and `reset-end:`.

## Porting notes

Things that decide whether the numbers match:

- **The smoothing's running sum.** Keep it as [described](#smoothing). It is the one place where a
  mathematically equal formulation changes the results.
- **The t table**, not a quantile function.
- **Replay precision.** A replay sees `F1` seconds, `F2` °C and an `F3` setpoint. A live run's `results.json`
  was worked out at full precision (and its `metrics.setpointC` is the plan's), so it can differ slightly from
  `results-reanalysed.json` for the same run (as implemented). Two readings less than 0.05 s apart can share an
  `elapsed_s`, and the series then rejects the second.
- **Rounding.** C#'s `Math.Round(x)` and `Math.Round(x, n)` round a midpoint to even, as Python's `round(x)`
  does; Python's `round(x, n)` rounds the exact binary value, which can differ from C# at a decimal tie. `snap`
  in the favour rule rounds half away from zero. C#'s fixed-point formatting (`F1`, `F2`, `0.#`) rounds half away
  from zero, while Python's `format` rounds the exact binary value half to even, so they can differ for a value
  exactly at a tie, such as 2.5 with `F0`.
- **Order.** Groups come in order of first appearance, then are sorted where this spec says. Modes sort Bake
  before Convection. `results` from one run are in the order the setpoints closed, not sorted.
- **The report's local times.** `started` and `sittings` are shown in the PC's time zone, not in the offset they
  were saved with (as implemented). The test data's reports were made in US Pacific time, so a port comparing
  reports must run in that time zone or convert the same way. The "Run" line's `yyyy-MM-dd HH:mm` is formatted
  with the PC's culture (as implemented: a culture whose time separator isn't `:` changes it); the sittings use
  the invariant culture.
