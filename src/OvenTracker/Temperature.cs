using System.Globalization;

namespace OvenTracker;

public enum TempUnit { Fahrenheit, Celsius }

public enum OvenMode { Bake, Convection }

public static class Temp
{
    public static double CToF(double c) => c * 9.0 / 5.0 + 32.0;
    public static double FToC(double f) => (f - 32.0) * 5.0 / 9.0;

    public static TempUnit? ParseUnit(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "F" or "°F" or "FAHRENHEIT" => TempUnit.Fahrenheit,
        "C" or "°C" or "CELSIUS" => TempUnit.Celsius,
        _ => null,
    };

    public static string ModeName(OvenMode mode) => mode == OvenMode.Bake ? "Bake" : "Convection";
}

/// <summary>Converts and formats temperatures held in °C for display in the oven's own unit.</summary>
public sealed class UnitFormatter(TempUnit unit)
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public TempUnit Unit => unit;
    public string Symbol => unit == TempUnit.Fahrenheit ? "°F" : "°C";

    public double ToDisplay(double c) => unit == TempUnit.Fahrenheit ? Temp.CToF(c) : c;
    public double FromDisplay(double d) => unit == TempUnit.Fahrenheit ? Temp.FToC(d) : d;
    public double DeltaToDisplay(double dc) => unit == TempUnit.Fahrenheit ? dc * 1.8 : dc;
    public double DeltaFromDisplay(double dd) => unit == TempUnit.Fahrenheit ? dd / 1.8 : dd;

    /// <summary>An absolute temperature, e.g. "352.4 °F".</summary>
    public string T(double c, int decimals = 1) => $"{ToDisplay(c).ToString("F" + decimals, Inv)} {Symbol}";

    /// <summary>A signed difference, e.g. "+2.4 °F".</summary>
    public string D(double dc, int decimals = 1)
    {
        var v = DeltaToDisplay(dc);
        var s = Math.Abs(v).ToString("F" + decimals, Inv);
        var sign = Math.Round(v, decimals) > 0 ? "+" : Math.Round(v, decimals) < 0 ? "−" : "±";
        return $"{sign}{s} {Symbol}";
    }

    /// <summary>An unsigned span (swing, uncertainty), e.g. "4.2 °F".</summary>
    public string Span(double dc, int decimals = 1) => $"{Math.Abs(DeltaToDisplay(dc)).ToString("F" + decimals, Inv)} {Symbol}";

    /// <summary>A setpoint, shown as the user entered it, e.g. "350 °F".</summary>
    public string Setpoint(double c) => $"{Math.Round(ToDisplay(c), 1).ToString("0.#", Inv)} {Symbol}";
}
