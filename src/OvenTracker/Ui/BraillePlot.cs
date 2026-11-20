using System.Globalization;
using System.Text;
using OvenTracker.Analysis;
using Spectre.Console;

namespace OvenTracker.Ui;

/// <summary>
/// A line chart drawn with Unicode braille characters, so each character cell is a 2 × 4 grid of
/// dots: temperature in blue, the setpoint dotted in yellow, the tolerance band faint.
/// </summary>
public static class BraillePlot
{
    // Dot bit for (x, y) within a cell, x 0..1, y 0..3 from the top.
    static readonly int[,] Bits = { { 0x01, 0x02, 0x04, 0x40 }, { 0x08, 0x10, 0x20, 0x80 } };
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Markup(IReadOnlyList<Sample> samples, double t0, double t1, double setpointC, double toleranceC,
        UnitFormatter f, int widthChars, int heightRows)
    {
        const int labelWidth = 7;
        var cols = Math.Max(10, widthChars - labelWidth - 1);
        var rows = Math.Max(3, heightRows);
        int pw = cols * 2, ph = rows * 4;

        double lo = setpointC - toleranceC, hi = setpointC + toleranceC;
        var visible = samples.Where(s => s.T >= t0 && s.T <= t1).ToList();
        foreach (var s in visible) { lo = Math.Min(lo, s.C); hi = Math.Max(hi, s.C); }
        var pad = Math.Max(1, (hi - lo) * 0.05);
        lo -= pad; hi += pad;
        if (t1 <= t0) t1 = t0 + 60;

        int Y(double c) => Math.Clamp((int)Math.Round((hi - c) / (hi - lo) * (ph - 1)), 0, ph - 1);

        var temp = new int[cols, rows];
        var set = new int[cols, rows];
        var band = new int[cols, rows];
        void Dot(int[,] layer, int x, int y) => layer[x / 2, y / 4] |= Bits[x % 2, y % 4];

        int sy = Y(setpointC), by0 = Y(setpointC + toleranceC), by1 = Y(setpointC - toleranceC);
        for (var x = 0; x < pw; x++)
        {
            if (x % 2 == 0) Dot(set, x, sy);
            if (x % 6 == 0) { Dot(band, x, by0); Dot(band, x, by1); }
        }

        // Per pixel column, draw from the lowest to the highest reading in it, joined to the column before.
        int? prevY = null;
        var i = 0;
        for (var x = 0; x < pw && i < visible.Count; x++)
        {
            var colEnd = t0 + (x + 1) * (t1 - t0) / pw;
            int? yMin = null, yMax = null;
            while (i < visible.Count && (visible[i].T <= colEnd || x == pw - 1))
            {
                var y = Y(visible[i].C);
                yMin = yMin is null ? y : Math.Min(yMin.Value, y);
                yMax = yMax is null ? y : Math.Max(yMax.Value, y);
                i++;
            }
            if (yMin is null) { continue; }
            var a = yMin.Value; var b = yMax!.Value;
            if (prevY is { } p) { a = Math.Min(a, p); b = Math.Max(b, p); }
            for (var y = a; y <= b; y++) Dot(temp, x, y);
            prevY = (yMin.Value + yMax.Value) / 2;
        }

        var sb = new StringBuilder();
        for (var r = 0; r < rows; r++)
        {
            var label = r == 0 || r == rows - 1 || r == rows / 2
                ? f.ToDisplay(hi - (r * 4 + 1.5) / (ph - 1) * (hi - lo)).ToString("F0", Inv).PadLeft(labelWidth - 1) + " "
                : new string(' ', labelWidth);
            sb.Append("[grey]").Append(label).Append("┤[/]");
            string? style = null;
            var run = new StringBuilder();
            for (var c = 0; c < cols; c++)
            {
                string? cellStyle; int bits;
                if (temp[c, r] != 0) { cellStyle = "deepskyblue1"; bits = temp[c, r] | set[c, r]; }
                else if (set[c, r] != 0) { cellStyle = "yellow"; bits = set[c, r]; }
                else if (band[c, r] != 0) { cellStyle = "grey30"; bits = band[c, r]; }
                else { cellStyle = null; bits = 0; }
                if (cellStyle != style) { Flush(sb, run, style); style = cellStyle; }
                run.Append(bits == 0 ? ' ' : (char)(0x2800 + bits));
            }
            Flush(sb, run, style);
            sb.Append('\n');
        }
        var left = $"{(t0 / 60).ToString("F0", Inv)} min";
        var right = $"{(t1 / 60).ToString("F0", Inv)} min";
        sb.Append("[grey]").Append(new string(' ', labelWidth)).Append('└').Append(new string('─', cols)).Append("[/]\n");
        sb.Append("[grey]").Append(new string(' ', labelWidth + 1)).Append(left)
          .Append(new string(' ', Math.Max(1, cols - left.Length - right.Length))).Append(right).Append("[/]");
        return sb.ToString();
    }

    static void Flush(StringBuilder sb, StringBuilder run, string? style)
    {
        if (run.Length == 0) return;
        var text = Spectre.Console.Markup.Escape(run.ToString());
        if (style is null) sb.Append(text);
        else sb.Append('[').Append(style).Append(']').Append(text).Append("[/]");
        run.Clear();
    }
}
