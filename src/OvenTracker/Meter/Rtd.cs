namespace OvenTracker.Meter;

/// <summary>A platinum RTD's tolerance class, IEC 60751.</summary>
public enum RtdClass { AA, A, B }

/// <summary>
/// A platinum resistance thermometer (PT100, PT1000) and its conversion between resistance and temperature:
/// the Callendar–Van Dusen equation with the IEC 60751 coefficients (α = 0.00385). Above 0 °C,
/// R = R0(1 + A·t + B·t²), which inverts exactly; below 0 °C a C·(t − 100)·t³ term is added and the
/// inverse is found by Newton's method.
/// </summary>
/// <param name="R0">The resistance at 0 °C: 100 Ω for a PT100, 1000 Ω for a PT1000.</param>
public sealed record Rtd(double R0, RtdClass Class)
{
    public const double A = 3.9083e-3;
    public const double B = -5.775e-7;
    public const double C = -4.183e-12;

    public static readonly Rtd Pt100A = new(100, RtdClass.A);

    /// <summary>The resistance at a temperature (°C).</summary>
    public double ResistanceAt(double c) =>
        c >= 0 ? R0 * (1 + A * c + B * c * c) : R0 * (1 + A * c + B * c * c + C * (c - 100) * c * c * c);

    /// <summary>The temperature (°C) for a resistance (Ω).</summary>
    public double TemperatureFor(double ohms)
    {
        var ratio = ohms / R0;
        if (ratio >= 1) return (-A + Math.Sqrt(A * A - 4 * B * (1 - ratio))) / (2 * B);
        // Below 0 °C: start from the quadratic, then refine with Newton's method on the full equation.
        var t = (-A + Math.Sqrt(Math.Max(0, A * A - 4 * B * (1 - ratio)))) / (2 * B);
        for (var i = 0; i < 20; i++)
        {
            var f = ResistanceAt(t) - ohms;
            var df = R0 * (A + 2 * B * t + C * (4 * t * t * t - 300 * t * t));
            var step = f / df;
            t -= step;
            if (Math.Abs(step) < 1e-9) break;
        }
        return t;
    }

    /// <summary>The sensor for the report: its tolerance class, ±(a + b·|t|) °C, IEC 60751.</summary>
    public Sensor Sensor => Class switch
    {
        RtdClass.AA => new($"PT{R0:0} RTD (class AA)", 0.1, 0.0017, LimitsAdd: true),
        RtdClass.A => new($"PT{R0:0} RTD (class A)", 0.15, 0.002, LimitsAdd: true),
        _ => new($"PT{R0:0} RTD (class B)", 0.3, 0.005, LimitsAdd: true),
    };

    /// <summary>Reads "pt100", "pt100-a", "pt100-b", "pt1000-aa" (class A when left off). Null if it isn't one.</summary>
    public static Rtd? Parse(string? text)
    {
        var s = text?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s)) return null;
        var parts = s.Split('-', 2);
        double? r0 = parts[0] switch { "pt100" => 100, "pt500" => 500, "pt1000" => 1000, _ => null };
        if (r0 is null) return null;
        RtdClass? cls = parts.Length == 1 ? RtdClass.A : parts[1] switch { "aa" => RtdClass.AA, "a" => RtdClass.A, "b" => RtdClass.B, _ => null };
        return cls is null ? null : new Rtd(r0.Value, cls.Value);
    }
}
