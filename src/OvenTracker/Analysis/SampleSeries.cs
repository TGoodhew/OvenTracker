namespace OvenTracker.Analysis;

/// <summary>One temperature reading: seconds since the test point started, and °C.</summary>
public readonly record struct Sample(double T, double C);

/// <summary>A time-ordered series of readings with time-weighted statistics over any interval.</summary>
public sealed class SampleSeries
{
    readonly List<Sample> items = [];

    public int Count => items.Count;
    public IReadOnlyList<Sample> Items => items;
    public Sample this[int i] => items[i];
    public Sample Last => items[^1];

    public bool Add(Sample s)
    {
        if (items.Count > 0 && s.T <= items[^1].T) return false;
        items.Add(s);
        return true;
    }

    /// <summary>Index of the first sample at or after <paramref name="t"/>.</summary>
    public int LowerBound(double t)
    {
        int lo = 0, hi = items.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (items[mid].T < t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Linearly interpolated value at <paramref name="t"/>, clamped to the ends.</summary>
    public double ValueAt(double t)
    {
        if (items.Count == 0) throw new InvalidOperationException("No samples.");
        var i = LowerBound(t);
        if (i == 0) return items[0].C;
        if (i >= items.Count) return items[^1].C;
        var a = items[i - 1];
        var b = items[i];
        return a.C + (b.C - a.C) * (t - a.T) / (b.T - a.T);
    }

    /// <summary>Time-weighted (trapezoidal) mean over [t0, t1].</summary>
    public double Mean(double t0, double t1)
    {
        if (t1 <= t0) return ValueAt(t0);
        double area = 0, prevT = t0, prevV = ValueAt(t0);
        for (var i = LowerBound(t0); i < items.Count && items[i].T < t1; i++)
        {
            if (items[i].T <= t0) continue;
            area += (items[i].T - prevT) * (items[i].C + prevV) / 2;
            prevT = items[i].T;
            prevV = items[i].C;
        }
        var endV = ValueAt(t1);
        area += (t1 - prevT) * (endV + prevV) / 2;
        return area / (t1 - t0);
    }

    public (double Min, double Max) Range(double t0, double t1)
    {
        double min = ValueAt(t0), max = min;
        var end = ValueAt(t1);
        min = Math.Min(min, end);
        max = Math.Max(max, end);
        for (var i = LowerBound(t0); i < items.Count && items[i].T <= t1; i++)
        {
            min = Math.Min(min, items[i].C);
            max = Math.Max(max, items[i].C);
        }
        return (min, max);
    }

    /// <summary>Least-squares slope in °C per second over [t0, t1], or null with too few samples.</summary>
    public double? Slope(double t0, double t1)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        for (var i = LowerBound(t0); i < items.Count && items[i].T <= t1; i++)
        {
            xs.Add(items[i].T);
            ys.Add(items[i].C);
        }
        return xs.Count < 3 ? null : Stats.Fit(xs, ys)?.Slope;
    }
}
