namespace OvenTracker.Analysis;

public enum TurningKind { Peak, Trough }

/// <param name="T">When the extreme happened (seconds since the point started).</param>
/// <param name="C">The (smoothed) temperature at the extreme.</param>
/// <param name="ConfirmedAt">When the reversal was large enough to be sure of it.</param>
public sealed record TurningPoint(TurningKind Kind, double T, double C, double ConfirmedAt);

/// <summary>
/// Finds the peaks and troughs of a thermostat's on/off cycle. A peak is confirmed once the
/// temperature has fallen a hysteresis below the highest value since the last trough (and the
/// reverse for a trough), so sensor noise smaller than the hysteresis never makes a turning point.
/// </summary>
public sealed class TurningPointDetector
{
    enum Direction { Unknown, Rising, Falling }

    Direction direction = Direction.Unknown;
    double extremeC, extremeT;
    double unknownMin = double.PositiveInfinity, unknownMax = double.NegativeInfinity;
    bool any;

    public TurningPoint? Add(double t, double c, double hysteresis)
    {
        if (!any)
        {
            any = true;
            unknownMin = unknownMax = c;
        }

        switch (direction)
        {
            case Direction.Unknown:
                unknownMin = Math.Min(unknownMin, c);
                unknownMax = Math.Max(unknownMax, c);
                if (c >= unknownMin + hysteresis) { direction = Direction.Rising; extremeC = c; extremeT = t; }
                else if (c <= unknownMax - hysteresis) { direction = Direction.Falling; extremeC = c; extremeT = t; }
                return null;

            case Direction.Rising:
                if (c > extremeC) { extremeC = c; extremeT = t; return null; }
                if (c <= extremeC - hysteresis)
                {
                    var peak = new TurningPoint(TurningKind.Peak, extremeT, extremeC, t);
                    direction = Direction.Falling;
                    extremeC = c; extremeT = t;
                    return peak;
                }
                return null;

            default:
                if (c < extremeC) { extremeC = c; extremeT = t; return null; }
                if (c >= extremeC + hysteresis)
                {
                    var trough = new TurningPoint(TurningKind.Trough, extremeT, extremeC, t);
                    direction = Direction.Rising;
                    extremeC = c; extremeT = t;
                    return trough;
                }
                return null;
        }
    }
}
