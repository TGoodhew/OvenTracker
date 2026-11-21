namespace OvenTracker.Meter;

/// <summary>
/// A small physical model of an electric oven with an on/off thermostat, so the whole app can be
/// tried without the meter. Air, walls and the heating element are separate thermal masses, and
/// the thermostat's own sensor lags the air: together they give a realistic overshoot, a slow
/// heat-soak drift and a regular cycle. Each mode has its own deliberate error, so the report has
/// something to find.
/// </summary>
public sealed class SimulatedOven(int seed = 1)
{
    readonly Random rng = new(seed);

    public double AmbientC { get; init; } = 22;
    public double AirC { get; private set; } = 22;
    public double WallC { get; private set; } = 22;
    public double SensorC { get; private set; } = 22;
    public double Element { get; private set; }
    public bool HeaterOn { get; private set; }
    public OvenMode Mode { get; private set; } = OvenMode.Bake;
    public double? SetpointC { get; private set; }

    /// <summary>The thermostat adjustment (offset): it moves where the thermostat holds by this much at every setting.</summary>
    public double AdjustmentC { get; set; }

    /// <summary>An open door lets the hot air out many times faster.</summary>
    public bool DoorOpen { get; set; }

    /// <summary>The thermostat's error at a setpoint: bake runs hot, more so at the top; convection runs cool.</summary>
    public static double BiasC(OvenMode mode, double setpointC) =>
        mode == OvenMode.Bake ? 4.0 + 0.04 * (setpointC - 177) : -5.0 + 0.01 * (setpointC - 177);

    public void Set(OvenMode mode, double? setpointC)
    {
        Mode = mode;
        SetpointC = setpointC;
    }

    public void Step(double seconds)
    {
        var bake = Mode == OvenMode.Bake;
        double coupling = bake ? 0.03 : 0.06;  // the fan stirs the air into the walls and racks
        double hysteresis = bake ? 2.5 : 1.5;
        double sensorLag = bake ? 25 : 20;
        const double heaterRate = 1.6, airLoss = 0.0015, wallMass = 12, wallLoss = 0.0035, elementLag = 60;

        for (double done = 0; done < seconds; done += 0.5)
        {
            var dt = Math.Min(0.5, seconds - done);
            if (SetpointC is { } sp)
            {
                var target = sp + BiasC(Mode, sp) + AdjustmentC;
                if (SensorC < target - hysteresis) HeaterOn = true;
                else if (SensorC > target + hysteresis) HeaterOn = false;
            }
            else HeaterOn = false;

            Element += ((HeaterOn ? 1 : 0) - Element) * dt / elementLag;
            var toWall = coupling * (AirC - WallC);
            AirC += (heaterRate * Element - toWall - (DoorOpen ? 25 : 1) * airLoss * (AirC - AmbientC)) * dt;
            WallC += (toWall - wallLoss * (WallC - AmbientC)) / wallMass * dt;
            SensorC += (AirC - SensorC) * dt / sensorLag;
        }
    }

    /// <summary>What a thermocouple in the middle of the oven reads: the air, plus a little noise.</summary>
    public double ProbeC() => AirC + Gaussian() * 0.08;

    double Gaussian()
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}

/// <summary>A pretend thermocouple meter reading <see cref="SimulatedOven"/>, on a simulated clock.</summary>
public sealed class SimulatedMeter(SimulatedOven oven, double intervalSeconds) : ITemperatureMeter, IOvenSimulation
{
    DateTimeOffset clock = DateTimeOffset.UtcNow;
    DateTimeOffset awakeSince = DateTimeOffset.UtcNow;
    DateTimeOffset? offSince;

    /// <summary>Auto power-off, as --sim-apo sets it: off (null) by default.</summary>
    public TimeSpan? AutoPowerOffAfter { get; set; }

    /// <summary>How long the simulated meter stays off before "someone presses a button".</summary>
    public TimeSpan WakesAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long the battery lasts, as --sim-battery sets it: forever (null) by default.</summary>
    public TimeSpan? BatteryLastsFor { get; set; }

    DateTimeOffset batterySince = DateTimeOffset.UtcNow;
    readonly Random batteryNoise = new(7);

    /// <summary>The battery's charge: 81.4 % for ever, or running down over <see cref="BatteryLastsFor"/>.</summary>
    public double BatteryPercent => BatteryLastsFor is { } lasts
        ? Math.Max(0, 100 - 100 * (clock - batterySince).TotalSeconds / lasts.TotalSeconds)
        : 81.4;
    static readonly MeterConfig Config = new("type K, °C (simulated)", true, TempUnit.Celsius, Sensor.Thermocouple("K"));

    public SimulatedOven Oven => oven;
    public string Location => "simulator";

    public MeterIdentity Connect() => new("OvenTracker", "simulated meter", "SIM00001", "0.00");
    public void Disconnect() { }
    public MeterConfig ReadConfig() => Config;
    public MeterConfig LastConfig => Config;

    public MeterReading Read()
    {
        oven.Step(intervalSeconds);
        clock = clock.AddSeconds(intervalSeconds);
        if (offSince is null && AutoPowerOffAfter is { } apo && clock - awakeSince >= apo) offSince = clock;
        if (offSince is { } off)
        {
            if (clock - off < WakesAfter) throw new TimeoutException("The simulated meter has switched itself off.");
            offSince = null; // someone pressed a button
            awakeSince = clock;
        }
        var probe = oven.ProbeC();
        // A nearly flat battery gives false readings, as a real one does: jumping tens of degrees.
        if (BatteryLastsFor is not null && BatteryPercent < 10) probe += (batteryNoise.NextDouble() - 0.5) * 100;
        var v = Math.Round(probe, 1);
        return new MeterReading(clock, ReadingStatus.Ok, v, v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
    }

    public double? ReadBatteryPercent() => BatteryPercent;
    public AutoPowerOffSetting ReadAutoPowerOff() => AutoPowerOffAfter is { } t ? AutoPowerOffSetting.On(t) : AutoPowerOffSetting.Off;
    public void SetOven(OvenMode mode, double? setpointC) => oven.Set(mode, setpointC);
    public void SetAdjustment(double adjustmentC) => oven.AdjustmentC = adjustmentC;
    public void SetAutoPowerOff(TimeSpan? after) => AutoPowerOffAfter = after;

    public void SetBattery(TimeSpan? lastsFor)
    {
        BatteryLastsFor = lastsFor;
        batterySince = clock;
    }

    public void SwapBattery()
    {
        offSince = clock; // switched off for the swap, back on after WakesAfter
        batterySince = clock + WakesAfter;
    }

    public void PretendUsed(double setpointC)
    {
        oven.Set(OvenMode.Bake, setpointC);
        oven.Step(3600);
        oven.Set(OvenMode.Bake, null);
        oven.Step(120); // turned off a couple of minutes ago
    }
    public void Dispose() { }
}

/// <summary>A made-up oven and meter, so the whole app can be tried without either.</summary>
public sealed class SimulatedDriver : IMeterDriver
{
    public const string DriverName = "simulated";

    public string Name => DriverName;
    public bool HasBattery => true;
    public string Description => "a simulated oven and meter";
    public string ResourceHelp => "nothing (there is only one)";
    public bool IsSimulation => true;

    /// <summary>The simulated meter can switch itself off (--sim-apo), and says whether it will.</summary>
    public AutoPowerOff AutoPowerOff { get; } = new(TimeSpan.FromMinutes(10), ["Leave out --sim-apo."], HowToWake: "wait: the simulated meter wakes by itself after 2 minutes");
    public string NotFoundHelp => "";
    public string NotTemperatureHelp => "";

    public ProbeResult? Open(MeterRequest request)
    {
        var meter = new SimulatedMeter(new SimulatedOven(Environment.TickCount), request.IntervalSeconds);
        return new ProbeResult(meter.Location, meter.Connect(), null, meter);
    }

    public IReadOnlyList<MeterCandidate> Discover() => [new("simulator", "Simulated oven and meter", null, true, "Simulated")];

    public ProbeResult Probe(string resource) => new(resource, new SimulatedMeter(new SimulatedOven(), 1).Connect(), null);
}
