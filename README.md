# OvenTracker

[![CI](https://github.com/TGoodhew/OvenTracker/actions/workflows/ci.yml/badge.svg)](https://github.com/TGoodhew/OvenTracker/actions/workflows/ci.yml)

Checks how accurate an oven's temperature setting really is, in **bake** and **convection**, using a
Keysight (Agilent) **U1253B** multimeter with a type K thermocouple, read over Bluetooth through a
**U1177A** IR-to-Bluetooth adapter. A .NET 11 console app for **Windows and Linux**, with a live
[Spectre.Console](https://spectreconsole.net) display.

For each setpoint it works out by itself when the oven has settled and how long it has to keep
measuring. It then reports the true average temperature, how far that is from the setting, how much the
thermostat swings, and how bake and convection differ.

![The live display while a setpoint is measured: the oven temperature cycling around the setting, the mean so far and its uncertainty, and the cycles counted](docs/images/dashboard.png)

*The live display at bake 350 °F, four cycles in. (These screenshots are from the built-in simulator, `--simulate`.)*

## The best path to great results

For an oven that's right, with numbers you can trust, do these in order. [The best path to great
results](docs/best-results.md) explains each step.

1. **Get the meter ready:** auto power-off **off** (the app shows how), a good battery, and
   `OvenTracker meters --probe` finds it.
2. **Name your probe** so the report knows its accuracy and range: `--probe 80bk-a`, or
   `--probe-tolerance 2.2C,2%` from any probe's datasheet.
3. **Name your oven** so the app knows its adjustment, convection conversion and cycle: `--oven ge-phs925`,
   or [describe yours](docs/adding-an-oven.md) in a small JSON file. `OvenTracker ovens` lists them.
4. **Prepare the oven:** turn its convection conversion off, find out its current adjustment (the app asks),
   and start from cold if you can.
5. **Place the probe** in the centre of the oven on the middle shelf, hanging free, the same place every time.
6. **Characterise it:** each setpoint, then a change of adjustment, then the highest and lowest again. You can
   stop with **F** and carry on later with `run --resume`.

   ```powershell
   OvenTracker characterise --oven ge-phs925 --probe 80bk-a --unit F --modes bake,convection --setpoints 325,375,425
   ```

7. **Read `characterisation.md` and `report.md`:** how far the adjustment moves your oven, whether the error
   changes with temperature, and the **best adjustment** to enter.
8. **Set it** on the oven, with the steps the report gives for your oven.
9. **Check it** with a short run at the temperature you bake at most:
   `OvenTracker run --unit F --modes bake --setpoints 350 --adjustment <new>`. The offset should be within the
   probe's accuracy of zero.

## What you need

- U1253B with a type K thermocouple in the temperature input, dial on temperature (°C or °F; the app
  reads whichever the meter shows). Tell the app which probe it is, so the report gives its real accuracy
  and range: `--probe 80bk-a` for a Fluke 80BK-A (±2.2 °C or 2 %, rated to 260 °C / 500 °F), `--probe
  type-k-special` for a special-limits thermocouple, or `--probe-tolerance 2.2C,2% --probe-max 260C` from
  any probe's datasheet. Without it, the report assumes a standard-grade type K (±2.2 °C or 0.75 %).
  `analyze --probe …` applies it to runs already made. The probe's insulation must be rated for your highest
  setpoint, and the app warns if a setpoint could take the probe past its rating.
- **Your oven, if the app knows it:** `--oven ge-phs925` for the GE Profile PHS925ST1SS. An oven profile gives
  the app the adjustment's steps and range, how to read and change it, any convection conversion, and how
  slowly the thermostat cycles. Without one, the app assumes a typical oven. Describe your own oven in a small
  JSON file ([Describing your oven](docs/adding-an-oven.md)). `OvenTracker ovens` lists them.
- **The meter's auto power-off turned off.** The U1253B switches itself off after 10 minutes by default,
  and reading it over Bluetooth doesn't keep it on, so it would switch off in the middle of every setpoint.
  To turn it off: hold the button that steps through the functions at the dial's position (button 4 in the
  guide's keypad table) for more than a second to enter Setup, go to **APO** with the arrow buttons, press
  **Hz**, set every digit to zero (OFF), press **Hz** to save, and hold the button again to leave Setup
  (User's and Service Guide, pp. 110 and 126–127). The app reminds you before a run until you say it's off.
- U1177A clipped to the meter's IR port, switched on and paired with the computer:
  - **Windows:** pair it in Bluetooth settings. Pairing adds two "Standard Serial over Bluetooth link" COM
    ports. The app uses the **outgoing** one (the one with the adapter's address) and finds it by itself.
  - **Linux:** pair it and bind it to `/dev/rfcomm0` (see [Linux](#linux)). The app finds RFCOMM devices by
    itself.
- OvenTracker from the [Releases](https://github.com/TGoodhew/OvenTracker/releases) page. The
  *self-contained* packages need nothing else; the others need the
  [.NET 11 runtime](https://dotnet.microsoft.com/download).

## Linux

OvenTracker runs on Linux on x64 and 64-bit ARM (a Raspberry Pi 4 or 5 makes a handy oven-side logger).
Unpack the package and run it from a terminal:

```sh
mkdir ~/oventracker && tar -xzf OvenTracker-*-linux-x64-self-contained.tar.gz -C ~/oventracker
~/oventracker/OvenTracker
```

The U1177A is a Bluetooth serial (SPP) device. Pair it once, then bind it to a serial device:

```sh
bluetoothctl                      # then: scan on, pair <address>, trust <address>, quit
sudo rfcomm bind 0 <address>      # makes /dev/rfcomm0 (rfcomm is in the bluez package; some
                                  # distributions put it in bluez-deprecated-tools)
sudo usermod -aG dialout $USER    # serial devices belong to the dialout group; log in again after
```

The binding doesn't survive a restart, so run `rfcomm bind` again after one. A U1173 IR-USB cable shows up
as `/dev/ttyUSB0` instead; give it with `--resource /dev/ttyUSB0`. `OvenTracker meters` lists the serial
devices it can see.

An EEVblog 121GW connects by Bluetooth LE through BlueZ instead, with no binding needed; see
[The EEVblog 121GW](#the-eevblog-121gw).

On Linux, runs are saved to `~/Documents/OvenTracker` (or `~/OvenTracker` if there is no `~/Documents`).
The app remembers the last meter in `~/.config/OvenTracker`.

## Use

On Linux, the program is `./OvenTracker`, and the resource is a device such as `/dev/rfcomm0`.


```powershell
OvenTracker.exe                       # asks for the unit, modes and setpoints, then guides you
OvenTracker.exe run --unit F --modes bake,convection --setpoints 350,400,450
OvenTracker.exe meters --probe        # which meters can it see, and which answers?
OvenTracker.exe run --meter u1253b --resource COM5   # a particular meter, without searching
OvenTracker.exe analyze "$HOME\Documents\OvenTracker\2026-11-20 1400" --tolerance 10
OvenTracker.exe analyze "<earlier run>" "<later run>" --favour 350   # runs together; right at 350 °F
OvenTracker.exe run --simulate --speed 120   # try it all without the meter or the oven
```

1. Hang the thermocouple tip in the centre of the oven, clear of racks and walls, and run the lead out
   under the door seal. Check its insulation is rated for your highest setpoint.
2. For each setpoint the app stops and shows the setting in large letters (e.g. *BAKE 350 °F*). Set the oven
   and start it, then press **Enter**. Leave the door shut and watch.
3. When the oven says it has preheated, press **P**. The report shows how true that signal was.
4. When a setpoint is done, the meter beeps, the app shows the result, and it stops again with the next
   setting: *CHANGE THE OVEN TO CONVECTION 350 °F*. Until you press a key, it reminds you every minute (the
   meter beeps and the PC's bell rings; `--remind <minutes>`, or `--remind 0` for no repeats). It waits like
   this with a real meter even with `--yes`, since nothing else changes the oven. At the end, it asks you
   to turn the oven off the same way.

![The prompt between setpoints: the setpoint just finished, and the next setting in large letters](docs/images/change.png)

If the oven doesn't head for the new setting within 6 minutes, the app beeps and asks whether you changed
it. For example, the setting was raised from 350 to 400 °F, but the oven is still cycling around 350 °F. If
you hadn't, change it and press **R R** to restart that setpoint. A change of mode at the same temperature
can't be seen this way, so check the mode on the prompt.

![The start of a run: what to do before starting, and the plan of setpoints with the modes alternating](docs/images/start.png)

By default the modes alternate at each setpoint (bake 350, convection 350, bake 400, ...). The oven
then only ever heats up between setpoints, and bake and convection are compared under the same
conditions. `--grouped` does all of bake first.

Keys while tracking: **P** preheat signal · **B** battery swap · **N N** finish this setpoint now · **R R**
restart it (if the door was opened) · **S S** skip · **Z** chart view · **Q Q** quit and write the report.
Ctrl+C also stops cleanly and writes the report.

If the meter stops answering for 30 s (most often its auto power-off), the live display says so in red,
with how to wake it ("press any button on the meter, or turn the dial"), and the PC beeps. Tracking
carries on as soon as readings come back. The gap is recorded in `readings.csv`, and the report notes any
setpoint with a gap of a minute or more. A gap of more than two minutes breaks the thermostat cycle it falls
in: that cycle is left out, and the others are kept.

### Battery swaps

A meter running on a battery (the U1253B, the 121GW) gives false readings once its battery is low. The
U1253B's guide says to change it as soon as the low-battery sign shows. A characterise session can outlast a
battery, so the app watches for it:
- **When the battery runs low** (below 20 %, checked every minute), or a reading jumps faster than an oven can
  change (10 °C or 18 °F in a second, as a failing meter's do), the app **pauses the setpoint**. It beeps and
  says what to do.
- **Swap or charge the battery:** switch the meter off, fit a fresh battery (or a charged one), and switch it
  back on at temperature.
- **Tracking carries on by itself** once the meter has been off and is back with sensible readings, or when you
  press **Enter**. It's the **same setpoint**, with its cycles so far kept, so there's no reheat or fresh start:
  only the cycle the swap broke is left out.
- **B** pauses for a swap whenever you choose, before the battery gets low. While paused, **C** carries on
  without a swap.
- While paused, readings are recorded in `readings.csv` outside the setpoint, so re-analysing gives exactly what
  the live run did.

Before a run, the app mentions a battery below 60 %: a long session will probably need a swap. The 34401A runs
off the mains, so none of this applies to it. A reading that jumps impossibly still pauses it, with advice to
check the probe.

### Logging a run

A session takes hours, so you may want it in the background or logged, e.g. over SSH on a Raspberry Pi:

```sh
./OvenTracker run --unit F --modes bake,convection --setpoints 350,400 | tee oven.log
```

When the output isn't a terminal (a file, a pipe, `nohup`), the app writes a plain progress line each minute
instead of the live display: elapsed time, temperature, phase, mean so far and time left. Alerts and notices
get lines of their own. It can't ask questions on a screen, so give `--unit`, `--modes` and `--setpoints`. If
the keyboard is still connected (as with `| tee`), the prompt between setpoints still waits for **Enter**.
If anything goes wrong mid-run, the setpoints already measured are still written to `report.md` and
`results.json`, and the app exits with code 1.

### Splitting a session (oven reset)

A full session takes hours, but it doesn't have to be done in one go. Stop after any setpoint (*Finish the
session*, or **Q Q**). The report covers what's done, and the app prints the command to carry on. Use the
oven, then leave the probe in it and resume:

```powershell
OvenTracker.exe run --resume "$HOME\Documents\OvenTracker\2026-11-20 1400"
```

It measures the setpoints that weren't finished, adds them to the same folder, and writes one report over
both sittings. A run that was cut off (closed, crashed, or the PC slept) can be resumed the same way.

If the oven is still hot, the app first waits for it to cool, so the next setpoint starts the way it would
have in one go: from just above the setpoint before it (or from cold, for the first). While it waits, it
shows the oven cooling and predicts when it will be ready. It learns how fast your oven cools as it goes,
and also predicts when the oven will be cold enough to start a whole session again. It notices if the door
is opened, or if the oven is turned back on, and beeps when it's ready. Press **C** to carry on early.

![The oven reset display: the oven cooling, the temperature it needs to reach for the next setpoint, and how long that and starting from cold will take](docs/images/reset.png)

Setpoints measured after a break are marked *after a reset* in the report. Their mean is as good as any
other, but their *To temp* and *First peak* aren't comparable with setpoints run straight on. The app knows
room temperature from the probe's first reading with the oven cold; if the oven is hot when you start, give
it with `--ambient 70`. Between setpoints, too, the app offers to wait whenever the oven is hotter than the
next setpoint should start from.

## How it decides how long to track

An oven's thermostat switches the element on and off, so the temperature cycles around a mean, typically
every few minutes. That mean, not any single reading, is what the setting should be. For each setpoint:

1. **Heating**, until the first turning point. The first peak is the overshoot.
2. **Settling.** The first cycle after the overshoot isn't used.
3. **Measuring.** Each full cycle (trough to trough) gives one time-weighted mean, an independent sample
   of where the thermostat really holds. While the cycle means still trend (the walls and racks are
   still soaking up heat), the oldest cycle is dropped. The trend test looks at least 30 minutes ahead,
   so a slow drift isn't missed.
4. **Done** once there are at least 4 cycles (`--min-cycles`) and the 95 % confidence interval of their
   mean is within ±2 °F / ±1 °C (`--precision`).

From the spread of the cycle means so far, it calculates how many cycles that precision needs. Times
your oven's own cycle length, that gives the time left on screen. A steady oven finishes in 4 cycles; a
noisy one takes more. An oven that holds temperature without a visible cycle is measured in 3-minute
blocks instead. Nothing runs past `--max-minutes` (120 by default), and the report says if a setpoint
stopped there.

Turning points are found with a hysteresis set from the measured noise (at least 1 °C), so sensor noise
never counts as a cycle.

## Output

At the end, the app shows the results for every setpoint and what it found:

![The results at the end of a run: the accuracy of each setpoint, how the oven got there, bake against convection, and the findings](docs/images/results.png)

Each run saves to `Documents\OvenTracker\<date time>\` (on Linux, `~/Documents/OvenTracker/<date time>/`),
or to `--out`:

- `report.md`: findings, a table per setpoint, the best thermostat adjustment (and what to set where it
  falls short), bake against convection, preheat signal accuracy, how long each setpoint needed, and how
  far to trust it.
- `results.json`: the same numbers, machine-readable (°C).
- `readings.csv`: every reading as it arrived, flushed line by line, with the events needed to replay
  the analysis. `analyze` re-runs it, for example with a different tolerance, and writes `report-reanalysed.md` and `results-reanalysed.json` (`-combined` for several runs) into the run's folder, or into `--out <folder>`.

A setpoint is judged **Accurate** when its mean and its ±95 % interval are within the tolerance (±15 °F /
±8 °C by default, `--tolerance`), **Out of tolerance** when they are clearly outside it, and
**Borderline** in between. The thermocouple's own accuracy comes on top of the ±95 % figure, and the
report spells that out.

[Reading the results and adjusting the oven](docs/adjusting-the-oven.md) explains each column, when an
offset is worth correcting, and how to enter the correction on a GE Profile PHS925ST1SS.

The **best adjustment** is the oven thermostat setting that fits all the setpoints measured best, on the
oven's own steps (1 °F, up to ±35 °F, by default). Tell the app the oven's current adjustment (`run` asks,
or `--adjustment`), and the report says what to set it to. When the error changes with temperature, no
single setting fixes it all, so the report also shows what's left at each setpoint and what to set to make
up for it. `--objective minimax` keeps the worst setpoint as close as it can, and `--favour 350` makes the
oven right at 350 °F.

How far the adjustment really moves the oven differs from oven to oven, and the app assumes 1:1 until it's
measured. `characterise` measures it, for any oven, in one (resumable) session:

```powershell
OvenTracker characterise --oven ge-phs925 --unit F --modes bake,convection --setpoints 325,375,425
```

It runs each setpoint, asks you to change the adjustment (using your oven profile's steps), and runs the
highest and lowest again. Then it writes `characterisation.md` with:
- each mode's gain;
- how the error changes with temperature;
- repeatability;
- the best adjustment.

It saves the gains to the oven's profile, so later recommendations use them. `analyze --save-gains` does the
same from runs you've already made at two adjustments. [How the oven's control responds to its
adjustment](docs/oven-control-model.md) explains the method.

## Other meters

Each kind of meter has a driver in `src/OvenTracker/Meter`. Four come with the app:

| `--meter` | What it is | `--resource` |
| --- | --- | --- |
| `u1253b` (the default) | A Keysight U1253B (or another U1250-series meter) through a U1177A Bluetooth adapter or a U1173 cable | A serial port: `COM5` on Windows, `/dev/rfcomm0` on Linux (9600 8N1, or give settings: `COM5:9600,N,8,1`). Left out, the app tries the port that worked last time, then each Bluetooth link. |
| `34401a` | An HP / Agilent / Keysight 34401A bench DMM reading a type K thermocouple as DC millivolts (see below) | `GPIB0::22::INSTR` (or `gpib:22`) through VISA, or a serial port for its RS-232 port: `COM3`, `/dev/ttyUSB0` (9600, even parity, 7 data bits, 2 stop bits from the factory, or give settings: `COM3:9600,N,8,2`). Required the first time, then remembered. |
| `121gw` | An EEVblog 121GW with a type K thermocouple, over Bluetooth LE (Windows and Linux) | `ble:C4:BE:84:12:34:56` or `ble:121GW`. Left out, the app tries the one that answered last time, then each 121GW the system knows (on Linux it scans if there's none). |
| `simulated` | A made-up oven and meter. `--simulate` is short for `--meter simulated`. | Not used. |

Without `--meter` the app uses the meter that answered last time, or the U1253B. `oventracker meters` lists what
each driver can see, and how each is connected. `--probe` asks each likely place what is there, and `--all`
asks every place. (`ports` and `--port` still work, as the names they had when there was only the U1253B.)

**Choosing the connection.** Some meters can be reached more than one way: a bench DMM may have GPIB and RS-232,
newer ones USB and LAN. `--resource` says both where and how, by its form:

| `--resource` | Connection |
| --- | --- |
| `COM3`, `/dev/ttyUSB0`, `/dev/rfcomm0` | A serial port, with the meter's usual settings. |
| `COM3:9600,E,7,2`, `/dev/ttyUSB0:9600,N,8,1,dtr` | A serial port with these settings: baud, parity (N, E, O, M, S), data bits, stop bits (1, 1.5, 2), and optionally a handshake (`rts`, `xon`, `dtr`). They must match the meter's own (often on its front panel). |
| `GPIB0::22::INSTR` (or `GPIB::22`, `gpib:22`) | GPIB address 22, through VISA. |
| `ASRL3::INSTR` | Serial port 3, through VISA. |
| `USB0::…::INSTR`, `TCPIP0::…::INSTR` | USB-TMC and LAN instruments, through VISA. |

Each driver says which it accepts (`OvenTracker run --help`, and its row above). The app remembers the resource,
settings and all, for next time.

### The 34401A with a type K thermocouple

The 34401A has no temperature function, so the app reads a type K thermocouple's voltage on the 100 mV DC range,
with the input at its high impedance (over 10 GΩ, so the thermocouple isn't loaded). It converts the voltage
with the NIST ITS-90 type K reference function. The probe is a generic type K, as for the U1253B: `--probe`
says which one (`--probe 80bk-a` for a Fluke 80BK-A), and its tolerance goes in the report. Plug the probe into
HI and LO Input with a type K to banana adapter, positive (yellow, chromel) to HI.

A thermocouple measures only the difference between its tip and its **cold junction**: where it meets the
copper of the meter's terminals, normally at room temperature. A meter with a thermocouple input measures that
junction itself; the 34401A can't, so give the room's temperature:

```powershell
OvenTracker.exe run --meter 34401a --resource GPIB0::22::INSTR --meter-option cj=22C --probe 80bk-a
OvenTracker.exe run --meter 34401a --resource COM3 --meter-option cj=72F
```

- **The cold junction:** `--meter-option cj=` takes a temperature with its unit, e.g. `22C` or `72F`. Every
  reading is off by as much as this is: a room 5 °C warmer than given reads 5 °C low. Without it the app assumes
  23 °C and says so when it connects. Measure the air next to the meter's terminals, and keep the adapter out of
  draughts and away from the oven. For the best accuracy, put the probe's cold end in iced water through a copper
  extension lead, and give `cj=0C`.
- **Integration time:** `--meter-option nplc=` takes 0.02, 0.2, 1, 10 (the default, about a third of a second
  a reading) or 100 (quieter, about 3 s a reading).
- **Recorded:** the run's `session.json` records the options it used (`meterOptions`), so a result says which
  cold junction it assumed.
- **Remembered:** the app remembers the address and the options, so after the first run `--meter 34401a` is
  enough.
- **GPIB** needs VISA: NI-VISA (with NI-488.2 for an NI adapter) or Keysight IO Libraries Suite, 64-bit. The app
  loads it only when GPIB is used, and says so if it isn't installed. The address is on the meter under
  I/O MENU, HP-IB ADDR (22 from the factory).
- **RS-232:** choose it on the meter under I/O MENU, INTERFACE. The meter only sends when it sees DTR, so use a
  null-modem cable that passes DTR/DSR (the 34398A is the one HP sold for it). The app holds DTR on.
- **No searching:** the app doesn't search the bus, since writing to unknown instruments isn't always harmless.
  `oventracker meters --probe --meter 34401a` asks each GPIB instrument VISA can see what it is (`*IDN?` only,
  nothing else is changed).
- **A bench meter:** it's mains powered, so there's no battery and no auto power-off. It beeps when a setpoint
  is done.

### The EEVblog 121GW

The 121GW streams its display over Bluetooth LE, about twice a second. The app takes its temperature mode,
in °C or °F as the meter shows it, with a type K thermocouple.

```powershell
OvenTracker.exe meters --meter 121gw          # the Bluetooth LE devices it knows; a 121GW is marked
OvenTracker.exe run --meter 121gw             # finds the 121GW by itself
```

- **Bluetooth on at the meter:** BT shows on its display. Holding the 1ms PEAK button turns it on or off.
- **On Windows, pair it first** in Windows Bluetooth settings (Add device, Bluetooth, then 121GW). The app
  uses Windows' Bluetooth LE (GATT) API, which works with paired devices.
- **On Linux** the app goes through BlueZ over D-Bus, so `bluetoothd` must be running
  (`sudo systemctl enable --now bluetooth`). No pairing is needed: the app scans for a few seconds when BlueZ
  doesn't know a 121GW yet. To skip the scan, make it known once: `bluetoothctl`, `scan on`, wait for 121GW,
  then `trust <address>`. If BlueZ refuses access, add yourself to the `bluetooth` group. WSL has no
  Bluetooth.
- **Only plain temperature counts:** with HOLD, REL or MIN/MAX on, the display isn't the live temperature, so
  the app waits as it does for the wrong dial position.
- **The unit flags:** firmware 1.21 and later tell the app whether the meter shows °C or °F. An older meter is
  taken as °C.
- **Auto power-off:** 30 minutes from the factory. The meter reports whether it's armed, so the app warns only
  when it is. Turn it off in the meter's setup (APO.oF).
- **Battery:** the meter reports only a low-battery flag, not a level, so the reset screen shows no battery
  percentage for it.

The protocol comes from EEVblog's packet format (V2) as written up by the dmm-tools project, and from QtDMM's
decoder. Both agree with packets captured from real meters. The driver hasn't been tried on a 121GW yet.
Report what you find.

**In the Python app** the 121GW goes through [bleak](https://github.com/hbldh/bleak), an optional extra
(`pip install "./python[ble]"`). bleak scans for the meter, so it needs no pairing on Windows either: the
meter just has to be on with Bluetooth on. `oventracker-py meters --meter 121gw` scans for 5 seconds and lists
what it finds, with any 121GW first. bleak works on Windows, Linux (through BlueZ) and macOS, though only the
first two have been exercised.

To add a meter, for example a bench DMM with a thermocouple input, implement `ITemperatureMeter` and
`IMeterDriver` and list the driver in `MeterDrivers.All`. Only four members are required; the rest (the
sensor, battery, beep, and the meter's auto power-off setting) are optional. A driver can also declare that
its meter switches itself off, with the steps to turn that off, and the app then warns before a run.
[Adding a meter](docs/adding-a-meter.md) explains every member, with a sketch of a SCPI bench-meter driver.
`U1253BDriver` (serial and Bluetooth), `Hp34401ADriver` (GPIB through VISA, RS-232, and its own
`--meter-option` settings), `Eevblog121GwDriver` (Bluetooth LE, a streamed protocol) and `SimulatedDriver`
are the worked examples.

## Protocol notes (U1253B)

The U1250 series talks at 9600 baud 8N1 through the U1177A: commands end in LF and replies in CR LF. The
app uses `*IDN?`, `CONF?` (temperature reads `"TEMP:K CEL"` or `"TEMP:K FAR"`), `FETC?` (±9.9E+37 means
an open thermocouple), `SYST:BATT?` and `SYST:BEEP`. It ignores the meter's own event lines (`*0`…`*7`
when the dial moves, `*L` for a button). It re-checks `CONF?` every few readings, so turning the dial
away from temperature is noticed and those readings aren't used. If the Bluetooth link drops, the app
reconnects and carries on.

## Protocol notes (121GW)

The meter streams 19-byte packets on the GATT characteristic `e7add780-b042-4876-aae1-112855353cc1` of the
service `0bd51666-e7cb-469b-8e4d-2742f1ba77cc`. The C# app subscribes with indications, or notifications if
the characteristic offers only those; the Python app leaves that to bleak, which on Windows uses notifications
when the characteristic has them. Either way the data is the same. It sends nothing to the meter.

Each packet is F2, then 17 bytes of display data, then an XOR checksum, so all 19 bytes XOR to zero. Byte 5
holds the main mode (5 is temperature) and the two high bits of the value. Byte 6 holds overload, sign, °C,
°F and the range. Bytes 7–8 hold the rest of the value, big-endian. Byte 15 holds the APO and low-battery
icons; bytes 16–17 hold REL, MIN/MAX and HOLD.

Packets aren't aligned to notifications, so the app treats them as one stream. It takes 19 bytes from each F2
and keeps them only if the checksum and the mode and range are valid. Each `Read` returns a packet newer than
the last, so a slow stream never gives the same reading twice.

## Protocol notes (34401A)

On GPIB, commands end in LF and replies end with EOI. On RS-232 the app first sends `SYST:REM`, as the manual
asks (User's Guide, p. 160). It then checks that `*IDN?` names a 34401A before changing anything. It sends
`*RST`, `*CLS`, `CONF:VOLT:DC 0.1` (the 100 mV range), `VOLT:DC:NPLC 10`, `INP:IMP:AUTO ON` (over 10 GΩ) and
`ZERO:AUTO ON`, and checks `SYST:ERR?`. Then it polls with `READ?`, in volts. +9.9E+37 is an overload, as an
open thermocouple's floating input usually gives; a voltage beyond type K's range is an error. It re-checks
`CONF?` every 30 readings, in case someone pressed Local and changed the function. At the end it gives the
front panel back.

## The Python package

`python/` holds a second implementation of the whole app in Python. It has the same commands (`run`,
`characterise`, `analyze`, `ovens`, `meters`), options, screens and files, drawn with
[Rich](https://github.com/Textualize/rich) instead of Spectre.Console. It has every meter the app has: the
U1253B, the 34401A (GPIB needs `pip install "./python[visa]"`), the 121GW (Bluetooth LE through
[bleak](https://github.com/hbldh/bleak): `pip install "./python[ble]"`) and the simulator. Install it with pip
on any system, with no .NET needed:

```sh
pip install ./python                     # or: pipx install ./python
oventracker-py run --simulate --speed 600
oventracker-py analyze ~/"Documents/OvenTracker/2026-11-20 1400" --tolerance 10
```

Use whichever suits you:
- `pip`/`pipx` and a Python that's already there (a Raspberry Pi, say);
- the analysis from your own scripts or notebooks (`import oventracker`);
- the method to read in Python.

A run recorded by either app reads the same in both, and they share the settings and oven profiles.
[python/README.md](python/README.md) has more.

The two implementations are kept aligned by a written specification and shared test data, not by reading
each other's code. The C# app is the reference:
- [spec/](spec/README.md) describes the recording formats (with JSON Schemas) and the analysis.
- `testdata/` holds recorded runs, real and simulated, with the results the analysis must give. Both
  implementations' tests replay every one, and CI also checks that the two outputs agree.
- Both carry the same version and are released together. [CHANGELOG.md](CHANGELOG.md) says which
  implementations have each change.

## Build

One codebase builds for every system. `build.ps1` needs PowerShell 7 and runs on Windows or Linux:

```powershell
./build.ps1 -SignScript C:\path\to\Sign-Files.ps1         # Windows: test, publish, sign, zip
./build.ps1 -SelfContained                                # this system, with the .NET runtime included
./build.ps1 -Runtime linux-x64,linux-arm64 -SkipTests     # Linux packages (build them on Linux)
```

`build.ps1` runs the tests, then publishes each runtime (by default the one you're on) into
`artifacts/publish/<runtime>`. It packs Windows into `artifacts/OvenTracker-<version>-win-x64.zip`, and
Linux into `.tar.gz`, which keeps the program's executable bit. For Windows, given a signing script
(`-SignScript`, or the `OVENTRACKER_SIGN_SCRIPT` environment variable), it Authenticode-signs OvenTracker's
own `OvenTracker.exe` and `OvenTracker.dll` with Azure Artifact Signing before packing, then fails unless
both are validly signed. Other publishers' DLLs are left alone. The signing account and tools stay outside
the repository. Without a script the build still works, unsigned, with a warning.

The code is the same on both systems. What differs is kept behind `OperatingSystem` checks: finding serial
ports (WMI on Windows; `/dev` and sysfs on Linux), how port names compare, and where runs and settings are
saved.

**CI** ([ci.yml](.github/workflows/ci.yml)) builds and tests on Windows and Linux for every push and pull
request. On Linux it also publishes the app and runs a whole simulated session in a terminal. It tests the
Python package on both systems, and checks that the C# and Python analyses agree on every case in
`testdata/`.
**Releases** ([release.yml](.github/workflows/release.yml)) build each package on its own system (Windows,
Linux x64 and Linux ARM64), sign the Windows programs, build the Python package, and publish them all with a `SHA256SUMS` file.
[docs/releasing.md](docs/releasing.md) explains how to make a release and how signing is set up.

## License

[MIT](LICENSE)
