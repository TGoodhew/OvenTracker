# The best path to great results

This is the recommended way to go from "I think my oven runs hot" to an oven that's right, with numbers you can
trust. Follow the steps in order. Each one removes a source of error that would otherwise end up in the
results.

| Step | What | Why |
| --- | --- | --- |
| 1 | [Get the meter ready](#1-get-the-meter-ready) | A meter that switches itself off, or a flat battery, leaves gaps. |
| 2 | [Tell the app about your probe](#2-tell-the-app-about-your-probe) | The probe's own accuracy sets how small an error can be trusted. |
| 3 | [Tell the app about your oven](#3-tell-the-app-about-your-oven) | Its adjustment's steps, how to change it, and how long its cycle is. |
| 4 | [Prepare the oven](#4-prepare-the-oven) | Turn off convection conversion, and start from cold. |
| 5 | [Place the probe](#5-place-the-probe) | Where it hangs decides what it measures. |
| 6 | [Characterise the oven](#6-characterise-the-oven) | Measures its error and how its adjustment really moves it. |
| 7 | [Read the results](#7-read-the-results) | What to change, and whether it's worth changing. |
| 8 | [Set the adjustment](#8-set-the-adjustment) | On the oven, the way its manual says. |
| 9 | [Check it](#9-check-it) | A short run confirms the oven is right now. |
| 10 | [Now and then](#10-now-and-then) | A repeat shows how repeatable your oven is. |

The examples are for a GE Profile PHS925ST1SS with a Fluke 80BK-A probe, in °F. Put in your own oven,
probe and unit.

## 1. Get the meter ready

- **Turn the meter's auto power-off off.** A setpoint takes far longer than the 10 minutes after which the
  U1253B switches itself off, and reading it remotely doesn't keep it on. The app shows the steps before
  each run until you say it's done. For the U1253B:
  1. Hold button 4 for a second to enter Setup.
  2. Go to **APO** and press **Hz**.
  3. Set every digit to zero, then press **Hz**.
  4. Hold button 4 again to leave Setup.
- **Check the battery.** The app shows it before the run and on every screen. Start above 50 %: a
  characterisation can take most of a day.
- **Connect it.** On Windows, pair the U1177A; on Linux, pair it and `rfcomm bind` it (see the README). Then
  check the app finds it:

  ```powershell
  OvenTracker meters --probe
  ```

## 2. Tell the app about your probe

The report's *How far to trust it* section, and the threshold for an error worth correcting, both come from the
probe's accuracy. Without being told, the app assumes a standard-grade type K thermocouple (±2.2 °C or 0.75 %).
Many probes are looser. A Fluke 80BK-A is ±2.2 °C or **2 %**, so ±8 °F at 450 °F, and it's rated only to
260 °C (500 °F).

- **A known probe:** `--probe 80bk-a` (`type-k` and `type-k-special` are also built in).
- **Any other:** from its datasheet, e.g. `--probe-tolerance 2.2C,2% --probe-max 260C`.

Check the probe's insulation is rated for your highest setpoint. The app warns before a run if a setpoint's
first peak and swing could take it past its rating.

## 3. Tell the app about your oven

```powershell
OvenTracker ovens                       # the ovens the app knows
OvenTracker ovens --show ge-phs925      # everything about one
```

- **If your oven is listed,** use `--oven <key>`. The app then knows:
  - its adjustment's steps and range, and how to read and change it;
  - its convection conversion, and how to turn it off;
  - how slowly its thermostat cycles, which sets how long it allows per setpoint (180 minutes for the
    PHS925, whose cycle is 20 minutes).
- **If it isn't,** describe it in a small JSON file from its use and care guide ([Describing your
  oven](adding-an-oven.md)). It takes five minutes, and makes every later step use your oven's own numbers
  and instructions.

The app remembers the oven, so you only need to give it once.

## 4. Prepare the oven

- **Turn off automatic convection conversion** (*Auto Recipe Conversion*, *Convect Conversion*). With it on,
  the oven quietly sets convection about 25 °F lower than you asked, and every convection result looks 25 °F
  cold. The oven's profile gives the steps, and the app repeats them before the run. On the PHS925: hold BAKE
  and BROIL HI/LO until **SF**, touch CONVECTION COOK until **CON OFF**, then touch START.
- **Find out the adjustment set now** (the PHS925: hold BROIL HI/LO and BAKE until **SF**, touch BAKE, read
  the signed number, touch START). The app asks for it at the start; 0 if it has never been changed. With it,
  the report says exactly what to set, not just how much to change it by.
- **Start from cold**, ideally. The first setpoint's time to temperature and first peak are only
  like-for-like from cold. If the oven is warm, the app says so and offers to wait (**W**), predicting how
  long the oven will take to cool.

## 5. Place the probe

- Hang the tip in the **centre of the oven, on the middle shelf**, clear of the racks and walls, with the lead
  clipped to a rack so the tip hangs free in the air. Near the elements and walls, the air is a lot hotter or
  cooler than in the middle.
- Run the lead out under the door seal; the meter stays outside.
- Put it in the **same place every time**, so runs can be compared.

## 6. Characterise the oven

```powershell
OvenTracker characterise --oven ge-phs925 --probe 80bk-a --unit F --modes bake,convection --setpoints 325,375,425
```

- **Choose setpoints** across the range you bake at: three, at least 50 °F apart, show whether the error is
  the same everywhere or grows with temperature.
- **Phase 1** measures every setpoint in each mode at the current adjustment. The app tells you when to set
  the oven, and shows the setting in large letters. Set it, start it, press **Enter**. When the oven beeps to
  say it's preheated, press **P** (optional): the report shows how true that beep was.
- **Between setpoints** it waits for you, beeping every minute until you press a key. **Leave the door
  shut.** If it does get opened, press **R** twice to restart that setpoint.
- **When phase 1 is done**, it asks you to change the oven's adjustment by a clear amount (at least 10 °F,
  towards correct), with your oven's steps. Do it, press **Enter**, and phase 2 measures the highest and
  lowest setpoints again.
- **It takes a while.** Each setpoint takes as long as the oven needs to settle and complete four thermostat
  cycles: about 1½–2 hours for a slow oven like the PHS925, so the best part of a day for the whole
  characterisation.
- **You don't have to do it in one go.** Press **F** at any prompt, use the oven, and carry on later:

  ```powershell
  OvenTracker run --resume "Documents\OvenTracker\<the run's folder>"
  ```

  If the oven is still hot when you resume, the app waits for it to cool to the right starting point first,
  and predicts how long that will take.
- The app looks after itself on the way:
  - if the meter goes quiet (e.g. its auto power-off), it says so in red and carries on when it's back;
  - if the oven isn't heading for a new setting within 6 minutes, it asks whether you changed it.

## 7. Read the results

At the end, the run's folder has `characterisation.md` and `report.md`:

- **Gains.** How far one degree of adjustment really moves the oven, in each mode. 1.00 means the adjustment
  does what it says. The app saves the gains to your oven's profile, and uses them from then on.
- **Shape.** Whether the error is the same at every setting (one adjustment fixes it everywhere) or grows with
  temperature (one adjustment can only centre it).
- **Best adjustment.** The setting to enter, e.g. *"set the oven's thermostat adjustment to −8 °F (it's 0 °F
  now)"*, and the error left at each setpoint after it.
- **What to set instead,** where the adjustment alone leaves a setting 5 °F or more out: *"for 425 °F, set
  418 °F"*.
- **How far to trust it.** Your probe's accuracy at your highest setpoint. An error smaller than that isn't
  worth correcting: the probe can't tell it from no error.

[Reading the results and adjusting the oven](adjusting-the-oven.md) explains every column. If you mostly bake
at one temperature, `--favour 350` on `analyze` makes the oven right there instead:

```powershell
OvenTracker analyze "Documents\OvenTracker\<the run's folder>" --favour 350
```

## 8. Set the adjustment

Enter the setting the report gives, using the steps it prints for your oven. On the PHS925:
1. Hold BROIL HI/LO and BAKE until **SF**.
2. Touch BAKE.
3. Touch BAKE again until the sign is right.
4. Type the number.
5. **Check the sign**, then touch START.

## 9. Check it

A single setpoint, at the temperature you bake at most, with the new adjustment:

```powershell
OvenTracker run --oven ge-phs925 --probe 80bk-a --unit F --modes bake --setpoints 350 --adjustment -8
```

The offset should now be within your probe's accuracy of zero, and the verdict *Accurate*. If it isn't, analyse
this run together with the characterisation; the measured gains give a better setting:

```powershell
OvenTracker analyze "<characterisation folder>" "<this run's folder>"
```

## 10. Now and then

- **Repeat a setpoint** on another day, without changing anything, and analyse it together with the
  characterisation. The characterisation lists each repeat and how far apart the results were: that spread is
  the real limit on how finely your oven can be adjusted.
- **Check again after any repair** to the oven's sensor or control, or if your baking starts to look off.

## Things that spoil results

| Problem | What happens | What to do |
| --- | --- | --- |
| The door is opened during a setpoint | A dip, then a slow recovery: the mean is off | Press **R R** to restart that setpoint. |
| The probe touches a rack or wall | It reads the metal, not the air | Hang it free, in the centre. |
| The meter switches itself off | Gaps in the readings | Turn auto power-off off (step 1). |
| Convection conversion is on | Convection looks about 25 °F cold | Turn it off (step 4). |
| The oven was changed but the setting wasn't (or vice versa) | The wrong setpoint is measured | The app asks if the oven isn't heading for the new setting; restart with **R R**. |
| A warm start | The first setpoint's time to temperature isn't from cold | Press **W** and let it cool, or accept *started warm* (its mean is still good). |
| The wrong probe assumed | *How far to trust it* is too optimistic | Give `--probe`. |
| Logging to a file or over SSH | No live display | Fine: the app writes a progress line each minute (`| tee oven.log`). |
