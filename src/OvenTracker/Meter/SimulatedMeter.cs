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
                var target = sp + BiasC(Mode, sp);
                if (SensorC < target - hysteresis) HeaterOn = true;
                else if (SensorC > target + hysteresis) HeaterOn = false;
            }
            else HeaterOn = false;

            Element += ((HeaterOn ? 1 : 0) - Element) * dt / elementLag;
            var toWall = coupling * (AirC - WallC);
            AirC += (heaterRate * Element - toWall - airLoss * (AirC - AmbientC)) * dt;
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

/// <summary>A pretend U1253B reading <see cref="SimulatedOven"/>, on a simulated clock.</summary>
public sealed class SimulatedMeter(SimulatedOven oven, double intervalSeconds) : ITemperatureMeter
{
    DateTimeOffset clock = DateTimeOffset.UtcNow;
    static readonly MeterConfig Config = U1253BProtocol.ParseConfig("\"TEMP:K CEL\"");

    public SimulatedOven Oven => oven;
    public string Location => "simulator";

    public MeterIdentity Connect() => new("Simulated", "U1253B", "SIM00001", "0.00");
    public void Disconnect() { }
    public MeterConfig ReadConfig() => Config;

    public MeterReading Read()
    {
        oven.Step(intervalSeconds);
        clock = clock.AddSeconds(intervalSeconds);
        var v = Math.Round(oven.ProbeC(), 1);
        return U1253BProtocol.ToReading(clock, v.ToString("+0.00000000E+00;-0.00000000E+00", System.Globalization.CultureInfo.InvariantCulture), Config);
    }

    public double? ReadBatteryPercent() => 81.4;
    public void Beep() { }
    public void Dispose() { }
}
