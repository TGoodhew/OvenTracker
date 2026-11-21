# Adding a meter

OvenTracker reads its temperatures through a **driver**, one for each kind of meter. Four come with the
app: the Keysight U1253B (`u1253b`), the HP 34401A with a PT100 (`34401a`), the EEVblog 121GW over Bluetooth
LE (`121gw`) and the simulator (`simulated`). This page explains how to add another,
for example a bench DMM with a thermocouple input, or a USB thermocouple logger.

A meter needs two classes, both in `src/OvenTracker/Meter`:

| Class | Implements | What it does |
| --- | --- | --- |
| The meter | `ITemperatureMeter` (`Meter.cs`) | Talks to one meter: connects, reads a temperature, and whatever else the meter can do. |
| The driver | `IMeterDriver` (`MeterDriver.cs`) | Knows that kind of meter: how to find one, how to open it, what to tell people, and facts about it such as auto power-off. |

Then the driver is listed in `MeterDrivers.All`, and `--meter <name>` selects it. `U1253BMeter` with
`U1253BDriver`, and `SimulatedMeter` with `SimulatedDriver`, are the worked examples.

## The meter: `ITemperatureMeter`

The app calls a meter from one thread at a time (the poller's), so it doesn't need to be thread-safe.
Only four members are required. Every other member has a default, and the app does without it.

### Required

| Member | What to do |
| --- | --- |
| `string Location` | Where the meter is, for people and the report: `COM5`, `/dev/rfcomm0`, `GPIB0::22::INSTR`. |
| `MeterIdentity Connect()` | Open the connection, check it's the right kind of meter, and return its make, model, serial number and firmware. Throw if it isn't there. The poller calls this again after the link drops, so it must work more than once. |
| `void Disconnect()` | Close the connection. It mustn't throw if the connection is already gone. |
| `MeterReading Read()` | Take one reading. See below. |

`Read` returns a `MeterReading` with the time, a status, the temperature **in °C** (convert if the meter
shows °F), and the raw text the meter sent, which goes into `readings.csv`:

| Status | When |
| --- | --- |
| `Ok` | A good temperature, in `Celsius`. |
| `Overload` | The meter reads OL: an open or broken probe. |
| `NotTemperature` | The meter isn't measuring temperature (e.g. its dial is on volts). Readings like this aren't used. |
| `Error` | A reply that makes no sense. |

If the meter doesn't answer at all, **throw `TimeoutException`**. The poller then keeps asking at the
normal rate (a meter that has switched itself off answers again the moment it's woken), and the live
display warns if it stays quiet for 30 s. Throw `IOException` if the link itself is broken; the poller
then disconnects and reconnects.

### Optional

| Member | Default | Implement it if the meter can… |
| --- | --- | --- |
| `MeterConfig? ReadConfig()` | `null` | …say what it's set to measure. Return a description for people, whether it's measuring temperature, the unit it shows, and the `Sensor` (e.g. `Sensor.Thermocouple("K")`). The report uses the sensor's standard tolerance in *How far to trust it*. |
| `MeterConfig? LastConfig` | `ReadConfig()` | …remember its last configuration, so the poller doesn't ask again after every reading. |
| `double? ReadBatteryPercent()` | `null` | …report its battery. The live display shows it, and warns below 20 %. |
| `void Beep()` | nothing | …beep. It's called when a setpoint is finished. |
| `AutoPowerOffSetting ReadAutoPowerOff()` | `Unknown` | …report whether its auto power-off is on (and after how long) or off. See [Auto power-off](#auto-power-off). |

## The driver: `IMeterDriver`

| Member | What to do |
| --- | --- |
| `string Name` | What `--meter` takes: lower case, no spaces, e.g. `34465a`. |
| `string Description` | For people: "Keysight 34465A over USB". |
| `string ResourceHelp` | What `--resource` means for this driver: "a VISA address, e.g. USB0::0x2A8D::0x0101::MY12345678::INSTR". |
| `string NotFoundHelp` | What to check when no meter answered, in plain text. |
| `string NotTemperatureHelp` | What to do when the meter isn't measuring temperature, e.g. "Select TEMP with the thermocouple plugged in." |
| `ProbeResult? Open(MeterRequest)` | Connect to the meter at `request.Resource`, or, when that's null, find one: try `request.Preferred` (where it was last time) first, then wherever this kind of meter is usually found. Return the connected meter, or null if none answered. Call `request.Progress` with each place you try. |
| `IReadOnlyList<MeterCandidate> Discover()` | The places a meter might be, cheaply, without sending them anything. `oventracker meters` lists them. Mark the likely ones `Usable`: `--probe` asks only those. |
| `ProbeResult Probe(string resource)` | Ask one place whether this kind of meter is there, then close it again. |
| `bool IsSimulation` | Default `false`. Only the simulator is a simulation. |
| `AutoPowerOff? AutoPowerOff` | Default `null`. See below. |
| `IReadOnlyList<MeterOption> Options` | Default none. The settings your driver takes through `--meter-option key=value`. See below. |
| `string? CheckOptions(options)` | Default: refuses any option. Say what's wrong with the options given, or return null. |

Text the driver returns is plain text, not Spectre markup: the app escapes it.

## Auto power-off

Many handheld meters switch themselves off after a few minutes. A setpoint takes far longer than that, so
a meter that does this will switch off in the middle of a measurement. Two pieces tell the app about it:

**1. The driver's metadata**, for every meter of this kind. Set `AutoPowerOff` to an `AutoPowerOff` record:

| Field | What it is |
| --- | --- |
| `DefaultTimeout` | How long the meter waits before switching off, as it comes from the factory. |
| `HowToTurnOff` | The steps to turn auto power-off off, one string per step. They're shown, numbered, before a run. |
| `Source` | Where the steps come from, e.g. the manual and page. Shown with them. |
| `RemoteReadsKeepItOn` | `true` if reading the meter remotely restarts its countdown, so it can't switch off mid-run. No warning is shown then. |
| `HowToWake` | What to do when it has switched off. The live display's alert uses it: "press any button on the meter". |

The U1253B's, from `U1253BDriver`:

```csharp
public AutoPowerOff AutoPowerOff { get; } = new(
    TimeSpan.FromMinutes(10),
    [
        "Hold the button that steps through the functions at the dial's current position (button 4 in the guide's keypad table) for more than a second, to enter Setup.",
        "Use the arrow buttons to go to APO, then press Hz to edit it.",
        "Set every digit to zero, which means OFF.",
        "Press Hz to save, then hold the same button for more than a second to leave Setup.",
    ],
    "Keysight U1253B User's and Service Guide, pp. 34, 110 and 126–127",
    HowToWake: "press any button on the meter, or turn the dial");
```

A meter that never switches itself off, such as a bench DMM, leaves `AutoPowerOff` null.

**2. The meter's own setting**, if it can report it. Implement `ReadAutoPowerOff()` to return
`AutoPowerOffSetting.Off`, `AutoPowerOffSetting.On(timeout)`, or `AutoPowerOffSetting.Unknown` (the
default).

Before a run, the app decides whether to warn (`AutoPowerOffAdvice.For`):

| Driver metadata | Meter reports | What the app does |
| --- | --- | --- |
| none | anything but On | Nothing. |
| any | **On** | A red warning: auto power-off is on, after how long, and the steps to turn it off. It offers to check again after you change it. |
| present | **Off** | Nothing (a grey "Auto power-off is off."). |
| present | **Unknown** | A yellow reminder that the meter switches off after its default timeout, with the steps. You can say you've turned it off, and it isn't shown again for that meter (by serial number, in the app's settings). |
| present, `RemoteReadsKeepItOn` | anything but On | Nothing. |

Whatever the warning said, if the meter goes quiet during a run, the live display says so within 30 s.
For a meter with auto power-off, it says how to wake it. The PC beeps, `meter-silent` and `meter-back`
events are recorded in `readings.csv`, tracking carries on when readings return, and the report notes any
setpoint whose readings have a gap of a minute or more.

## Connections

`--resource` names both where a meter is and how it's connected, so one option covers every transport.
`ResourceSpec.Parse` (`Meter/ResourceSpec.cs`) reads it the same way for every driver:

| Form | `Transport` | Notes |
| --- | --- | --- |
| `COM3`, `/dev/ttyUSB0` | `Serial` | `Settings` is null: use your meter's defaults (`spec.SettingsOr(...)`). |
| `COM3:9600,E,7,2,dtr` | `Serial` | `Settings` holds baud, parity, data bits, stop bits and handshake; `DtrDsr` is set for `dtr`. |
| `GPIB0::22::INSTR`, `GPIB::22`, `gpib:22` | `Gpib` | `Address` is the full VISA resource; `Visa` is true. |
| `ASRL3::INSTR` | `Serial` | Through VISA. |
| `USB0::…::INSTR`, `TCPIP0::…::INSTR` | `Usb`, `Lan` | Through VISA. |

In your driver:

- **Parse the resource** in `Open` and `Probe`. Refuse transports your meter doesn't have, with a message
  that says what it does take. `U1253BDriver.Check` refuses anything but a serial port.
- **Open a serial port** with `ResourceSpec.OpenSerial(port, settings, dtrDsr)`. It configures the port, and
  holds DTR for meters that use DTR/DSR, such as the 34401A.
- **Return the full resource**, settings included, as `ProbeResult.Resource` when they aren't your defaults, so
  the app remembers them. `U1253BMeter.Location` does this.
- **Say how each candidate is connected** in `Discover`, with `MeterCandidate.Transport` ("Serial",
  "Bluetooth", "GPIB", "USB", "LAN"). `oventracker meters` shows it.
- **Describe each transport** in `ResourceHelp`, e.g. "a GPIB address (GPIB0::22::INSTR), or a serial port
  with its settings (COM3:9600,E,7,2,dtr)".

### GPIB, USB and LAN through VISA

`Meter/Visa.cs` wraps the VISA C library (NI-VISA, Keysight IO Libraries, R&S VISA). It loads the library only
when it's used, so the app builds and runs without it:

- `Visa.IsAvailable` says whether the library loads, and `Visa.MissingHelp` says what to install.
- `Visa.Find("GPIB?*INSTR")` lists resources for `Discover`.
- `Visa.Open(resource, timeout)` opens one, for writes and reads.

`Meter/ScpiPort.cs` puts a line-at-a-time `IScpiPort` over both VISA and a serial port. A meter written
against `IScpiPort` works on either link, and tests drive it with a fake; see `Hp34401ATests`. The 34401A's
driver chooses the link from the resource with `Hp34401ADriver.PortFor`.

Don't search a GPIB bus by writing to every address: other instruments may not like it, and a bus extender
makes every address look occupied. Try `request.Resource` or `request.Preferred`. List the bus in `Discover`,
so `oventracker meters --probe` asks each instrument only when someone asks for that.

### Bluetooth LE

`Meter/Ble/` has an `IBleLink`: a subscription to one GATT characteristic, whose notifications arrive through
a callback.

- **Windows:** `WindowsGattLink` uses the Win32 Bluetooth GATT API, which works with paired devices, and
  keeps the app on plain .NET, which the WinRT API wouldn't allow. `BleLinks.List()` lists the paired devices.
- **Linux:** not yet (issue #34). `BleLinks.IsSupported` says so, and a driver should return
  `BleLinks.NotSupportedHelp` as its error.

A streaming meter (one that sends without being asked) keeps the latest packet. Its `Read` waits for a newer
one, so the app's polling never records the same packet twice. `Eevblog121GwMeter` is the example, and its
tests drive it with a fake link. Put packets back together across notifications (`Gw121Assembler`): a
notification can hold part of a packet, or parts of two.

## Driver settings: `--meter-option`

Settings that only your meter has go through `--meter-option key=value`. There's no new command-line option
per driver.

1. List each setting in `Options`, for the help text.
2. Check the given values in `CheckOptions`. The app calls it before connecting and stops with your message
   if it returns one. Keys arrive in lower case.
3. Read them from `MeterRequest.Options` in `Open`.

The app remembers each driver's options with its resource. The 34401A's `rtd` (PT100 class) and `nplc`
(integration time) are the example.

## Registering the driver

Add an instance to `MeterDrivers.All` in `MeterDriver.cs`:

```csharp
public static IReadOnlyList<IMeterDriver> All { get; } = [new U1253BDriver(), new Hp34401ADriver(), new SimulatedDriver(), new My34465ADriver()];
```

`--meter` takes its `Name`. Without `--meter`, the app uses the meter that answered last time, or the
U1253B. Each run records the driver's name, the meter's identity and the sensor in `session.json` and
`results.json`.

## A sketch: a SCPI bench DMM on a serial port

This sketch shows the shape of a driver for a bench meter that speaks SCPI on a serial port, measuring
thermocouple temperature. It isn't complete, and the commands depend on the meter, so check its
programming guide.

```csharp
public sealed class BenchDmmMeter(string port) : ITemperatureMeter
{
    SerialPort? sp;
    public string Location => port;

    public MeterIdentity Connect()
    {
        Disconnect();
        sp = new SerialPort(port, 9600) { NewLine = "\n", ReadTimeout = 3000 };
        sp.Open();
        var idn = Query("*IDN?").Split(',');                 // e.g. "Keysight Technologies,34465A,MY123,A.03"
        Write("CONF:TEMP TC,K");                             // thermocouple, type K
        Write("UNIT:TEMP C");                                // so readings arrive in °C
        return new MeterIdentity(idn[0], idn[1], idn[2], idn[3]);
    }

    public void Disconnect() { try { sp?.Close(); } catch { } sp?.Dispose(); sp = null; }

    public MeterReading Read()
    {
        var reply = Query("READ?");                          // throws TimeoutException if it doesn't answer
        var now = DateTimeOffset.UtcNow;
        if (!double.TryParse(reply, NumberStyles.Float, CultureInfo.InvariantCulture, out var c))
            return new MeterReading(now, ReadingStatus.Error, null, reply);
        if (Math.Abs(c) >= 9e37) return new MeterReading(now, ReadingStatus.Overload, null, reply); // SCPI overload
        return new MeterReading(now, ReadingStatus.Ok, c, reply);
    }

    public MeterConfig ReadConfig() => new("TEMP TC K, °C", true, TempUnit.Celsius, Sensor.Thermocouple("K"));
    public void Beep() => Write("SYST:BEEP");
    public void Dispose() => Disconnect();

    string Query(string cmd) { Write(cmd); return sp!.ReadLine().Trim(); }
    void Write(string cmd) => (sp ?? throw new MeterException("Not connected.")).WriteLine(cmd);
}

public sealed class BenchDmmDriver : IMeterDriver
{
    public string Name => "benchdmm";
    public string Description => "a SCPI bench DMM with a thermocouple input, on a serial port";
    public string ResourceHelp => "a serial port, e.g. COM3";
    public string NotFoundHelp => "Check the meter is on, its serial interface is enabled at 9600 baud, and give the port with --resource.";
    public string NotTemperatureHelp => "The meter isn't measuring temperature.";
    // A bench meter doesn't switch itself off, so no AutoPowerOff.

    public ProbeResult? Open(MeterRequest request)
    {
        // A bench meter isn't on an obvious port, so ask for one rather than writing to unknown devices.
        if ((request.Resource ?? request.Preferred) is not { } port) return null;
        request.Progress?.Invoke(port);
        var meter = new BenchDmmMeter(port);
        try { return new ProbeResult(port, meter.Connect(), null, meter); }
        catch (Exception ex) { meter.Dispose(); return new ProbeResult(port, null, ex.Message); }
    }

    public IReadOnlyList<MeterCandidate> Discover() =>
        PortFinder.List().Select(p => new MeterCandidate(p.Port, p.Description, null, Usable: false)).ToList();

    public ProbeResult Probe(string resource)
    {
        using var meter = new BenchDmmMeter(resource);
        try { return new ProbeResult(resource, meter.Connect(), null); }
        catch (Exception ex) { return new ProbeResult(resource, null, ex.Message); }
    }
}
```

`PortFinder` lists serial ports on Windows and Linux (with Bluetooth RFCOMM and USB product names), so a
serial-port driver can use it for `Discover`.

## Testing a driver

- **Without the meter:** a fake `ITemperatureMeter` (like `BareMeter` in `MeterDriverTests`) that reads a
  `SimulatedOven` can drive a `MeterPoller` and a `SetpointAnalyzer` through a whole setpoint. Throwing
  `TimeoutException` for a while tests the quiet-meter handling (see `AutoPowerOffTests`).
- **The protocol:** test parsing against replies captured from a real meter, as `ProtocolTests` does for
  the U1253B.
- **The metadata:** check `AutoPowerOffAdvice.For(driver.AutoPowerOff, …)` gives the warning you expect for
  On, Off and Unknown.
- **With the meter:** `oventracker meters --probe --meter <name>`, then a short run (`--setpoints 300`, one
  mode).

## Checklist

- [ ] `ITemperatureMeter`: `Location`, `Connect`, `Disconnect`, `Read` (°C, the right status,
      `TimeoutException` when it doesn't answer).
- [ ] Whatever else the meter can do: `ReadConfig` with the `Sensor`, battery, beep, `ReadAutoPowerOff`.
- [ ] `IMeterDriver`: `Name`, `Description`, `ResourceHelp`, `NotFoundHelp`, `NotTemperatureHelp`, `Open`,
      `Discover`, `Probe`.
- [ ] `AutoPowerOff` metadata if the meter switches itself off, with the steps and where they come from.
- [ ] Listed in `MeterDrivers.All`.
- [ ] Tests, and a row in the README's *Other meters* table.
