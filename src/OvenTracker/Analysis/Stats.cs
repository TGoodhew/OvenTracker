namespace OvenTracker.Analysis;

public sealed record LinearFit(double Slope, double Intercept, double? SlopeStdErr, int N);

public static class Stats
{
    // Two-sided 95 % critical values of Student's t for 1..30 degrees of freedom.
    static readonly double[] T975 =
    [
        12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
        2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
        2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042,
    ];

    public static double TCrit95(int df) =>
        df < 1 ? double.PositiveInfinity : df <= 30 ? T975[df - 1] : 1.96 + 2.4 / df;

    public static double Mean(IReadOnlyList<double> x)
    {
        if (x.Count == 0) throw new ArgumentException("No values.", nameof(x));
        double s = 0;
        foreach (var v in x) s += v;
        return s / x.Count;
    }

    /// <summary>Sample standard deviation (n − 1).</summary>
    public static double StdDev(IReadOnlyList<double> x)
    {
        if (x.Count < 2) return 0;
        var m = Mean(x);
        double ss = 0;
        foreach (var v in x) ss += (v - m) * (v - m);
        return Math.Sqrt(ss / (x.Count - 1));
    }

    /// <summary>Half-width of the 95 % confidence interval of the mean, or null with fewer than two values.</summary>
    public static double? HalfWidth95(IReadOnlyList<double> x) =>
        x.Count < 2 ? null : TCrit95(x.Count - 1) * StdDev(x) / Math.Sqrt(x.Count);

    /// <summary>
    /// The smallest number of independent values whose 95 % interval half-width would be at most
    /// <paramref name="target"/>, given a spread of <paramref name="sd"/>.
    /// </summary>
    public static int RequiredCount(double sd, double target, int min, int max)
    {
        if (sd <= 0) return min;
        for (var n = Math.Max(min, 2); n <= max; n++)
            if (TCrit95(n - 1) * sd / Math.Sqrt(n) <= target) return n;
        return max;
    }

    public static LinearFit? Fit(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var n = x.Count;
        if (n < 2 || y.Count != n) return null;
        double mx = Mean(x), my = Mean(y), sxx = 0, sxy = 0;
        for (var i = 0; i < n; i++)
        {
            sxx += (x[i] - mx) * (x[i] - mx);
            sxy += (x[i] - mx) * (y[i] - my);
        }
        if (sxx <= 0) return null;
        var slope = sxy / sxx;
        var intercept = my - slope * mx;
        double? se = null;
        if (n >= 3)
        {
            double sse = 0;
            for (var i = 0; i < n; i++)
            {
                var r = y[i] - (intercept + slope * x[i]);
                sse += r * r;
            }
            se = Math.Sqrt(sse / (n - 2) / sxx);
        }
        return new LinearFit(slope, intercept, se, n);
    }

    public static double Median(IEnumerable<double> values)
    {
        var a = values.ToArray();
        if (a.Length == 0) throw new ArgumentException("No values.", nameof(values));
        Array.Sort(a);
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    /// <summary>
    /// White-noise sigma of a series, from the median absolute second difference, so a steady
    /// ramp (an oven heating up) does not count as noise.
    /// </summary>
    public static double NoiseSigma(IReadOnlyList<double> x)
    {
        if (x.Count < 5) return 0;
        var d2 = new double[x.Count - 2];
        for (var i = 2; i < x.Count; i++) d2[i - 2] = Math.Abs(x[i] - 2 * x[i - 1] + x[i - 2]);
        // For white noise, var(second difference) = 6 sigma^2; MAD -> sigma via 1.4826.
        return 1.4826 * Median(d2) / Math.Sqrt(6);
    }
}
