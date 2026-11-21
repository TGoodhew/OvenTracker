namespace OvenTracker.Meter;

/// <summary>
/// A type K thermocouple's voltage and temperature: the NIST ITS-90 reference function (NIST Monograph 175), with
/// the reference junction at 0 °C. A meter that reads the voltage (a DMM on its millivolt range) needs the cold
/// junction's own temperature too, where the thermocouple meets the copper of the meter's terminals: the
/// thermocouple measures only the difference, so <see cref="TemperatureFor"/> adds the cold junction's voltage back.
/// </summary>
public static class TypeK
{
    /// <summary>−270 to 0 °C.</summary>
    static readonly double[] Below0 =
    [
        0, 0.394501280250E-01, 0.236223735980E-04, -0.328589067840E-06, -0.499048287770E-08, -0.675090591730E-10,
        -0.574103274280E-12, -0.310888728940E-14, -0.104516093650E-16, -0.198892668780E-19, -0.163226974860E-22,
    ];

    /// <summary>0 to 1372 °C, with an exponential term.</summary>
    static readonly double[] Above0 =
    [
        -0.176004136860E-01, 0.389212049750E-01, 0.185587700320E-04, -0.994575928740E-07, 0.318409457190E-09,
        -0.560728448890E-12, 0.560750590590E-15, -0.320207200030E-18, 0.971511471520E-22, -0.121047212750E-25,
    ];

    const double A0 = 0.118597600000E+00, A1 = -0.118343200000E-03, A2 = 0.126968600000E+03;

    public const double MinC = -270, MaxC = 1372;

    /// <summary>The voltage, in mV, of a type K thermocouple at a temperature (°C) with its reference junction at 0 °C.</summary>
    public static double MillivoltsAt(double c)
    {
        var coeffs = c < 0 ? Below0 : Above0;
        double sum = 0, power = 1;
        foreach (var k in coeffs)
        {
            sum += k * power;
            power *= c;
        }
        if (c >= 0) sum += A0 * Math.Exp(A1 * (c - A2) * (c - A2));
        return sum;
    }

    /// <summary>d(mV)/d(°C), for Newton's method.</summary>
    static double Slope(double c)
    {
        var coeffs = c < 0 ? Below0 : Above0;
        double sum = 0, power = 1;
        for (var i = 1; i < coeffs.Length; i++)
        {
            sum += i * coeffs[i] * power;
            power *= c;
        }
        if (c >= 0) sum += A0 * Math.Exp(A1 * (c - A2) * (c - A2)) * 2 * A1 * (c - A2);
        return sum;
    }

    /// <summary>
    /// The temperature (°C) at the measuring junction, from the voltage across the thermocouple (mV) and the cold
    /// junction's temperature (°C). Null if the voltage is beyond type K's range.
    /// </summary>
    public static double? TemperatureFor(double millivolts, double coldJunctionC)
    {
        var e = millivolts + MillivoltsAt(coldJunctionC);
        if (e < MillivoltsAt(MinC) || e > MillivoltsAt(MaxC)) return null;
        // Newton's method on the reference function itself, so the two directions agree exactly; 41 µV/°C to start.
        var t = e / 0.041;
        for (var i = 0; i < 50; i++)
        {
            var step = (MillivoltsAt(t) - e) / Slope(t);
            t = Math.Clamp(t - step, MinC, MaxC);
            if (Math.Abs(step) < 1e-9) break;
        }
        return t;
    }
}
