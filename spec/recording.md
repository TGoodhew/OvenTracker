# Recording formats

What a run writes to its folder, and what `analyze` writes. The C# app is the reference: `RunRecorder`,
`Replay` and `SessionInfo` in `src/OvenTracker/Session/Recording.cs`. [analysis.md](analysis.md) says what is
done with the readings.

## The files

| File | Written by | What it is |
| --- | --- | --- |
| `readings.csv` | `run`, `characterise` | Every reading as it arrived, and the events the analysis needs to be replayed. Flushed after each line. |
| `session.json` | `run`, `characterise` | How the run was set up: meter, unit, options, plan, oven, probe. Written when the run starts, and again when a characterise run adds its second phase or a run is resumed. [session.schema.json](session.schema.json). |
| `results.json` | `run`, `characterise` | `{ "session": …, "results": [ … ] }` at the end of the run. [results.schema.json](results.schema.json). |
| `report.md` | `run`, `characterise` | The report ([analysis.md, The report](analysis.md#the-report)). |
| `results-reanalysed.json`, `report-reanalysed.md` | `analyze` with one run | The same shapes as `results.json` and `report.md`, from replaying the recording. |
| `results-combined.json`, `report-combined.md` | `analyze` with several runs | The same, for the runs together. |
| `characterisation.json`, `characterisation.md` | a characterise run, or `analyze` of one, or `analyze --save-gains` | [analysis.md, Characterisation](analysis.md#characterisation). |

`analyze` writes into the latest run's folder (by `started`), or into `--out <folder>`.

## Units

Everything in the files is in °C, whatever the oven's unit: temperatures, setpoints, and differences
(offsets, swings, half-widths, tolerances, adjustment steps). A difference of 1 °F is 1/1.8 °C. A setpoint
entered in °F is converted with (F − 32) × 5/9 and stored at full precision, so 350 °F is
`176.66666666666666`. The oven's own unit (`unit` in `session.json`) matters only for display, and for
reading the command-line options, which are in the oven's unit.

Times are in seconds in `readings.csv`, and in minutes in the results (fields ending `Minutes`), except
`longestGapSeconds`.

## readings.csv

UTF-8 without a byte-order mark. Each line ends with the platform's newline (CRLF on Windows, LF elsewhere);
a reader must accept both. There is no quoting and no escaping: split each line on commas. The first line
is the header, exactly:

```
time_utc,point,mode,setpoint_c,elapsed_s,status,temp_c,raw,event
```

### Columns

| # | Column | Type | Format | Meaning |
| --- | --- | --- | --- | --- |
| 0 | `time_utc` | timestamp | .NET round-trip format `O` of a UTC time: `yyyy-MM-ddTHH:mm:ss.fffffffZ`, always seven fractional digits, e.g. `2026-11-20T19:18:08.6977161Z` | When the reading was taken, or when the event happened. See [Timestamps](#timestamps). |
| 1 | `point` | integer | invariant culture | The setpoint's `index` in the plan (from 1), or `0` for anything outside a setpoint. |
| 2 | `mode` | enum | `Bake` or `Convection` | The setpoint's mode. Empty when `point` is 0. |
| 3 | `setpoint_c` | number, °C | `F3` (three decimals), e.g. `176.667` | The setpoint. Empty when `point` is 0. |
| 4 | `elapsed_s` | number, s | `F1` (one decimal), e.g. `118.1` | Seconds since the setpoint's first reading (after a `start` or `restart`). Empty when `point` is 0. |
| 5 | `status` | enum | see below | The reading's status, or `Event`. |
| 6 | `temp_c` | number, °C | `F2` (two decimals), e.g. `180.87` | The reading in °C. Only when `status` is `Ok`; empty otherwise. |
| 7 | `raw` | text | see below | What the meter sent. Empty for events. |
| 8 | `event` | text | see below | The event's name. Empty for readings. |

Numbers use the invariant culture: a full stop for the decimal point, no thousands separator, a leading `-`
for negatives.

### Status

| Value | Meaning |
| --- | --- |
| `Ok` | A valid temperature. `temp_c` is set. |
| `Overload` | The meter read overload (`OL`): the probe is open or out of range. |
| `NotTemperature` | The meter isn't set to measure temperature (the dial or function is elsewhere). |
| `Error` | The meter's reply couldn't be understood. |
| `Event` | Not a reading: the row is an event (see below). |

Only `Ok` rows inside a setpoint are used by the analysis.

### raw

The meter's reply exactly as received, after the driver has trimmed the line ending, with two changes so it
can't break the CSV: every `,` becomes `;`, and every `"` is removed. Nothing else is escaped (as
implemented: a CR or LF in the middle of a reply would break the line; the drivers trim only the ends).
Examples: `+1.80873000E+02` (U1253B), `22.1` (the simulator, which writes its °C value with `F1`),
`TEMP:K CEL` (a U1253B reply that wasn't a number). For a 34401A that isn't set to measure resistance, it is
the meter's configuration description. A reader should treat it as opaque.

### Rows

| Kind | `point` | `mode`, `setpoint_c`, `elapsed_s` | `status` | `temp_c` | `raw` | `event` |
| --- | --- | --- | --- | --- | --- | --- |
| Reading during a setpoint | index | set | `Ok`, `Overload`, `NotTemperature` or `Error` | when `Ok` | set | empty |
| Reading outside a setpoint | `0` | empty | as above | when `Ok` | set | empty |
| Setpoint event | index | set | `Event` | empty | empty | name |
| Session event | `0` | empty | `Event` | empty | empty | name |

Readings outside a setpoint (point 0) are those before the first setpoint, between setpoints, while waiting
for the oven to cool, and after a setpoint completed but before the next began (the readings still queued
when it completed are written as point 0). The analysis ignores them.

Every reading during a setpoint is recorded, whatever its status. A setpoint event's `elapsed_s` is, unless
the table below says otherwise, the elapsed time of the last `Ok` reading the analysis accepted
(`SetpointAnalyzer.Now`, 0 before any), and its `time_utc` is the time of the last reading of any status.

### Events

Setpoint events (`point` ≥ 1):

| Event | When it is written | What replay does |
| --- | --- | --- |
| `start` | When the setpoint's first reading arrives, just before that reading's row. `time_utc` is that reading's time and `elapsed_s` is `0.0`. Not written again after a `restart`. | Closes any setpoint still open as `Aborted`. Removes any earlier result with the same index (a setpoint measured again in a later sitting replaces the earlier attempt). Takes the pending reset and started-warm marks (below) for this setpoint, and clears them. Starts a new analysis. |
| `restart` | The user pressed R twice: the setpoint's data is thrown away and it starts again. `elapsed_s` is the old attempt's last elapsed time. The next reading has `elapsed_s` 0 again. | Starts a new analysis for the same index, discarding the old one without a result. Keeps the setpoint's reset and started-warm marks. |
| `preheat-signal` | The user pressed P when the oven signalled it was preheated. | Marks the preheat signal on the open analysis (see [analysis.md, Preheat signal](analysis.md#preheat-signal)). |
| `not-heading` | The oven hasn't headed for the new setting within 6 minutes (the heading check). `time_utc` and `elapsed_s` are the reading's. | Nothing. |
| `heading` | After `not-heading`, the oven is heading for the setting after all. `time_utc` and `elapsed_s` are the reading's. | Nothing. |
| `meter-silent` | The meter has gone quiet (often its auto power-off). | Nothing. |
| `meter-back` | The meter is answering again. | Nothing. |
| `end:<reason>` | When the setpoint finishes, for any reason. `<reason>` is a completion reason: `Converged`, `TimedOut`, `Manual`, `Skipped` or `Aborted` (`None` is possible in principle). | If a setpoint is open: `end:Converged` and `end:TimedOut` finish it as `Manual` if the replayed analysis hasn't completed by itself; the others finish it with that reason, unless it has already completed. Then the result is added. See [analysis.md, Replay](analysis.md#replay). |

A setpoint whose run was killed (no `end:`) is closed as `Aborted` by the next `start`, or at the end of the
file.

Session events (`point` 0):

| Event | When it is written | What replay does |
| --- | --- | --- |
| `resume` | First, when a saved run is resumed: a new sitting begins. | Marks a reset pending: the next setpoint started follows a break. |
| `reset` | Just before a setpoint that follows a break begins: the first setpoint of a resumed sitting, or one after waiting for the oven to cool. | Marks a reset pending. |
| `started-warm` | Just before the first setpoint of a new (not resumed) run, when it was started without letting a warm oven cool: the oven was hotter than that setpoint's start target when the user was asked to set it. | Marks started-warm pending: the next setpoint started is marked `startedWarm`. |
| `reset-start` | A wait for the oven to cool began. | Nothing. |
| `reset-end:<outcome>` | The wait ended: `Ready` (it cooled enough), `CarryOn` (the user carried on before it did) or `Quit`. | Nothing. |
| `adjustment-changed:<°C>` | Characterise: phase 2 began at this adjustment, written with `F3`, e.g. `adjustment-changed:-5.556`. `session.json` is rewritten at the same time. | Nothing. |
| `prompt:<text>` | A prompt was shown, e.g. `prompt:SET THE OVEN TO BAKE 350 °F`. | Nothing. |
| `answered:<choice>:<n>s` | The prompt was answered after *n* whole seconds. `<choice>` is `Start`, `Skip`, `Finish` or `Wait` for the change prompt, `True` or `False` for the adjustment prompt, `True` for the turn-off prompt. | Nothing. |

A reader must ignore events it doesn't know. The `prompt:` text isn't sanitised (as implemented): a heading
such as `PRESS W TO LET IT COOL, OR SET IT TO` puts a comma in the event, so that row has ten fields. Replay
reads only the first nine.

### Timestamps

`time_utc` is for people and isn't used by the analysis, which uses `elapsed_s` alone. Readings carry the
meter reading's own time. In the simulator that is the simulated clock, which runs faster than real time
with `--speed`. Session events from `resume`, `reset`, `started-warm`, `adjustment-changed`, `prompt:` and
`answered:` carry the PC's clock instead (`reset-start` and `reset-end:` carry the last reading's time, or
the PC's clock before any). So `time_utc` need not increase down the file; in `sim-characterise` the
`adjustment-changed` row is two hours earlier than the rows around it.

### A resumed run

`run --resume <folder>` opens the same `readings.csv` and appends to it. The header is written only if the
file is missing or empty. The first row of the new sitting is the `resume` event; readings outside a setpoint
follow. Setpoints that finished as `Converged`, `TimedOut` or `Manual` in an earlier sitting are not measured
again. The others (`Aborted`, `Skipped`, or never started) are, each with a new `start`, and replay keeps the
last attempt at each index. `session.json` is rewritten with the new sitting added to `sittings`, and with
the adjustment, adjustment options, oven, ambient temperature and sensor as given for this sitting.

### Example

From `testdata/runs/real-2026-11-20-1118`, with lines left out at `…`:

```
time_utc,point,mode,setpoint_c,elapsed_s,status,temp_c,raw,event
2026-11-20T19:18:08.6977161Z,0,,,,Ok,180.87,+1.80873000E+02,
…
2026-11-20T19:18:10.2514534Z,1,Bake,176.667,0.0,Event,,,start
2026-11-20T19:18:10.2514534Z,1,Bake,176.667,0.0,Ok,180.81,+1.80807000E+02,
2026-11-20T19:18:12.4353591Z,1,Bake,176.667,2.2,Ok,180.78,+1.80778000E+02,
…
2026-11-20T19:18:24.5153522Z,1,Bake,176.667,14.3,Error,,TEMP:K CEL,
…
2026-11-20T19:20:08.4009343Z,1,Bake,176.667,118.1,Ok,180.26,+1.80262000E+02,
2026-11-20T19:20:08.4009343Z,1,Bake,176.667,118.1,Event,,,preheat-signal
…
2026-11-20T20:19:37.4838979Z,1,Bake,176.667,3687.2,Ok,189.15,+3.72461000E+02,
2026-11-20T20:19:37.4838979Z,1,Bake,176.667,3687.2,Event,,,end:Aborted
```

The last reading's `raw` is 372.461, in °F (the meter was showing °F by then), while `temp_c` is 189.15 °C:
`raw` is in whatever unit the meter shows, and `temp_c` is always °C.

## session.json

One JSON object, a `SessionInfo`. Written by `System.Text.Json` with `Json.Options`:

- property names in camelCase;
- enums as their names (`"Fahrenheit"`, `"Bake"`), not numbers;
- a property whose value is null is left out, never written as `null`;
- indented by two spaces;
- numbers as the shortest text that round-trips the double, so a whole number is written without a decimal
  point (`1`, not `1.0`);
- non-ASCII and HTML-sensitive characters escaped (`°` as `°`, `'` as `'`).

Only the structure and values matter ([testdata.md](testdata.md)); a reader must use a JSON parser. A reader
should ignore properties it doesn't know. Dictionary keys that are modes are written as the mode's name
(`"Bake"`), not camelCased.

Timestamps (`started`, `sittings`, `changedAt`, `measuredOn`) are a .NET `DateTimeOffset` in ISO 8601 with
the PC's local offset and up to seven fractional digits, trailing zeros dropped:
`2026-11-20T11:18:06.9757259-08:00`, `2026-01-10T10:00:00-08:00`.

### SessionInfo

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `appVersion` | string | yes | The app's version, e.g. `1.0.0`. |
| `started` | timestamp | yes | When the run (its first sitting) started. |
| `meter` | string | yes | The meter's make and model, for people, e.g. `Agilent Technologies U1253B`. |
| `location` | string | yes | Where the meter was, e.g. `COM5`, `GPIB0::22::INSTR`, `simulator`. |
| `unit` | `Fahrenheit` \| `Celsius` | yes | The oven's unit. For display and options only. |
| `toleranceC` | number, °C | yes | The tolerance a setpoint is judged against (±). Default 15 °F (8.333…) or 8 °C. |
| `options` | [AnalysisOptions](#analysisoptions) | yes | The analysis options. |
| `plan` | array of [TestPoint](#testpoint) | yes | The setpoints, in the order they are measured. |
| `driver` | string | no | The meter driver's name, e.g. `u1253b`, `simulated`. Missing in runs saved before drivers. |
| `identity` | [MeterIdentity](#meteridentity) | no | What the meter said it was. |
| `sensor` | [Sensor](#sensor) | no | The temperature sensor, when the meter said or `--probe` gave it. |
| `adjustmentC` | number, °C | no | The oven's thermostat adjustment during the run, if known. In a characterise run, the latest phase's adjustment (0 when it wasn't known). |
| `adjust` | [AdjustmentOptions](#adjustmentoptions) | no | How to work out the best adjustment. |
| `ambientC` | number, °C | no | Room temperature: given with `--ambient`, or the first reading when the oven was cold (between −10 and 45 °C). |
| `sittings` | array of timestamps | no | When each sitting started: the first, then each resume. |
| `oven` | [OvenProfile](#ovenprofile) | no | The oven's profile, as it was for this run. |
| `characterise` | [CharacteriseInfo](#characteriseinfo) | no | For a characterise run. |

### TestPoint

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `index` | integer | yes | 1, 2, 3… in plan order. The `point` column in `readings.csv`. |
| `mode` | `Bake` \| `Convection` | yes | The oven mode. |
| `setpointC` | number, °C | yes | The setting, at full precision. |
| `adjustmentC` | number, °C | no | The thermostat adjustment for this point, when it differs by design from the session's: a characterise run's two phases, or points from several runs analysed together. When absent, the session's `adjustmentC` applies. |

The plan's order: with `--grouped`, every setpoint in the first mode, then every setpoint in the next;
otherwise the modes alternate at each setpoint. Indexes are 1 to *n* in that order. A characterise run's
phase 2 appends points with the next indexes.

### AnalysisOptions

Every field is always written. A reader should take the default for any that is missing.

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `precisionC` | number, °C | 1.0 | Target half-width of the 95 % interval of the mean. `run` sets 2 °F (1.111…) or 1 °C unless `--precision`. |
| `minCycles` | integer | 4 | Fewest cycles a result is based on. |
| `minBlocks` | integer | 5 | Fewest blocks a result is based on. |
| `blockMinutes` | number, min | 3 | Block length. |
| `minHysteresisC` | number, °C | 1.0 | Smallest reversal counted as a turning point. |
| `smoothingSeconds` | number, s | 5 | Moving-average window. |
| `maxPointMinutes` | number, min | 120 | Longest at one setpoint. `run` sets `--max-minutes`, or the oven's `defaultMaxMinutes`. |
| `noCycleMinutes` | number, min | 15 | No turning point for this long, and flat: the oven holds without cycling. |
| `flatWindowMinutes` | number, min | 10 | The window for the flatness test. |
| `flatSlopeCPerMinute` | number, °C/min | 0.15 | Flat means a slope smaller than this. |
| `driftHorizonMinutes` | number, min | 30 | The shortest time a trend is projected over. |
| `defaultCycleMinutes` | number, min | 6 | A cycle length to assume before the oven's own is seen. |

### MeterIdentity

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `manufacturer` | string | yes | e.g. `Agilent Technologies`. |
| `model` | string | yes | e.g. `U1253B`. |
| `serial` | string | yes | Serial number, as the meter gave it. |
| `firmware` | string | yes | Firmware version, as the meter gave it. |

### Sensor

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `name` | string | yes | For people, e.g. `type K thermocouple`, `Fluke 80BK-A type K probe`, `PT100 RTD (class A)`. |
| `limitC` | number, °C | yes | The fixed part of the tolerance. |
| `limitFraction` | number | yes | The proportional part, as a fraction of the temperature in °C (0.0075 is 0.75 %). |
| `limitsAdd` | boolean | yes | True: the tolerance is `limitC + limitFraction·|T|` (RTD classes). False: the greater of the two (thermocouples). |
| `maxC` | number, °C | no | The highest temperature the probe is rated for. |
| `isGenericType` | boolean | yes | Computed: `name` starts with `type ` (a standard sensor type rather than a particular probe). Written, not read. |

### AdjustmentOptions

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `stepC` | number, °C | yes | The oven's adjustment step. Default 1 °F (0.5556) or 1 °C. |
| `rangeC` | number, °C | yes | How far the adjustment goes either way. Default 35 °F (19.444…) or 19 °C. |
| `objective` | `Balanced` \| `Minimax` | yes | What the recommendation minimises. Default `Balanced`. |
| `favourC` | number, °C | no | Make the oven right at this temperature instead. |
| `model` | `Shift` \| `Gain` | yes | How the adjustment is assumed to act when not measured. Default `Shift`. |
| `sharedAcrossModes` | boolean | yes | One adjustment for bake and convection. Default true. |
| `knownGains` | object: mode → number | no | Gains measured on this oven before (from its profile), e.g. `{ "Bake": 0.9 }`. |

### OvenProfile

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `key` | string | yes | What `--oven` takes, e.g. `ge-phs925`; `generic` for the generic profile. |
| `name` | string | yes | For people. |
| `adjustStepC` | number, °C | yes | The adjustment's step. |
| `adjustRangeC` | number, °C | yes | How far it goes either way. |
| `sharedAdjustment` | boolean | yes | One adjustment for bake and convection. |
| `howToRead` | array of strings | no | Steps to see the adjustment. |
| `howToChange` | array of strings | no | Steps to set it. |
| `convectionConversionC` | number, °C | no | How much the oven quietly lowers a convection setting. |
| `howToTurnOffConversion` | array of strings | no | Steps to turn that off. |
| `typicalCycleMinutes` | number, min | no | Its thermostat's cycle, roughly. |
| `gains` | object: mode → [MeasuredGain](#measuredgain) | no | Gains measured by characterise. |
| `notes` | array of strings | no | Anything else worth showing. |
| `source` | string | no | Where the facts come from. |
| `isGeneric` | boolean | yes | Computed: `key` is `generic`. Written, not read. |
| `defaultMaxMinutes` | number, min | yes | Computed: `max(120, 60 + 6·typicalCycleMinutes)`, or 120 without a cycle. Written, not read. |

### MeasuredGain

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `gain` | number | yes | Degrees the oven moves per degree of adjustment. |
| `halfWidth` | number | no | Its uncertainty, roughly 95 %. |
| `measuredOn` | timestamp | yes | When it was measured. |

### CharacteriseInfo

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `stepC` | number, °C | yes | The least change of adjustment to test (`--step`, default 10 °F or 10 °C). |
| `fromC` | number, °C | no | Phase 1's adjustment (0 when it wasn't known). |
| `changedToC` | number, °C | no | The adjustment phase 2 moved to. Absent until phase 2 begins. |
| `changedAt` | timestamp | no | When phase 2 began. |

## results.json

One JSON object, with the same serialisation rules as `session.json`:

| Field | Type | Meaning |
| --- | --- | --- |
| `session` | [SessionInfo](#sessioninfo) | The session as analysed. For `analyze`, with its options applied: the options as overridden by `--precision` and `--min-cycles`, the tolerance, oven, adjustment options and sensor as given. For several runs, the latest run's session with the combined plan ([analysis.md, Combining runs](analysis.md#combining-runs)). |
| `results` | array of [PointResult](#pointresult) | One per setpoint with a result. |

Both are always present. `results` may be empty.

The order of `results`: from `run`, by `point.index`. From `analyze` of one run, in the order the setpoints
closed in `readings.csv` (as implemented: not sorted, so a setpoint measured again in a later sitting comes
after those it followed). From `analyze` of several runs, by run (oldest first), then as above, with the
indexes renumbered 1, 2, 3….

A setpoint is in `results` once it has started (a `start` event); setpoints never started are not. A
setpoint skipped at the prompt never starts. In a live run that was resumed, setpoints that ended `Aborted`
or `Skipped` in an earlier sitting and weren't measured again are left out of `results.json` (as
implemented); replaying the recording keeps them.

### PointResult

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `point` | [TestPoint](#testpoint) | yes | The setpoint. From `run` and `analyze` of one run, the plan's point (full-precision `setpointC`, and the plan's `adjustmentC`). From `analyze` of several runs, the replayed point: `setpointC` as read from `readings.csv` (three decimals), the new index, and `adjustmentC` always set. |
| `metrics` | [PointMetrics](#pointmetrics) | yes | What was measured. |
| `afterReset` | boolean | yes | Measured after a break (a resumed sitting, or a wait for the oven to cool), so its time to temperature and first peak aren't comparable with the others. |
| `startedWarm` | boolean | yes | The first setpoint of the run, started with the oven still warm, so its time to temperature and first peak aren't from cold. |

### PointMetrics

Times in minutes are from the setpoint's first reading (after its last `restart`). See
[analysis.md, Metrics](analysis.md#metrics) for how each is worked out.

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `setpointC` | number, °C | yes | The setpoint the analysis used. From `analyze`, the value in `readings.csv` (three decimals, as implemented), so it can differ from `point.setpointC` in the fourth decimal. |
| `reason` | `None` \| `Converged` \| `TimedOut` \| `Manual` \| `Aborted` \| `Skipped` | yes | Why the setpoint ended. |
| `kind` | `Cycle` \| `Block` | no | What the steady state was cut into. Absent if measuring never began. |
| `units` | integer | yes | Cycles or blocks the result is based on. |
| `discardedUnits` | integer | yes | Cycles or blocks dropped: while the oven was still warming up, or when the cycling stopped and blocks took over. |
| `meanC` | number, °C | no | The mean of the unit means. Absent with no units. |
| `halfWidthC` | number, °C | no | The half-width of its 95 % interval. Absent with fewer than two units. |
| `stdDevC` | number, °C | no | The standard deviation of the unit means (n − 1; 0 with one unit). Absent with no units. |
| `driftCPerHour` | number, °C/h | no | The trend in the unit means. Absent with fewer than three units. |
| `swingC` | number, °C | no | The mean of the units' peak-to-peak. Absent with no units. |
| `minC` | number, °C | no | The lowest reading in the units. Absent with no units. |
| `maxC` | number, °C | no | The highest reading in the units. Absent with no units. |
| `cycleMinutes` | number, min | no | The mean cycle length. Only for `kind` `Cycle` with units. |
| `startC` | number, °C | no | The first reading. Absent with no readings. |
| `timeToSetpointMinutes` | number, min | no | When the oven first reached the setting, or first turned round. |
| `overshootC` | number, °C | no | The first peak against the setting. Only when the first turning point was a peak, heating up. |
| `settleMinutes` | number, min | no | When the first unit used began, or when measuring began if no unit closed. |
| `measureMinutes` | number, min | no | From the first unit's start to the last unit's end. Absent with no units. |
| `totalMinutes` | number, min | yes | When the setpoint completed (or its last reading). |
| `preheatSignalMinutes` | number, min | no | When the preheat signal was marked. |
| `preheatSignalC` | number, °C | no | The smoothed temperature then. |
| `precisionTargetC` | number, °C | yes | The precision target (`options.precisionC`). |
| `longestGapSeconds` | number, s | no | The longest time between two accepted readings. Absent with fewer than two. |
| `offsetC` | number, °C | no | Computed: `meanC − setpointC`. Present when `meanC` is. |
| `metPrecision` | boolean | yes | Computed: `halfWidthC` is present and at most `precisionTargetC + 1e-9`. |

`offsetC` and `metPrecision` are computed properties of the C# record that the serialiser writes. A
reader recomputes them; a writer writes them.
