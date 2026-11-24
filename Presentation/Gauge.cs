using System.Text;

namespace WwanProbe.Presentation
{
    internal readonly record struct ScaleRange(double Min, double Max);

    // Gauges and colors for signal values.
    // Unicode glyphs are only used by the TUI, which switches the console to UTF-8.
    // Redirected (plain) output keeps ASCII: under the default console code page
    // (e.g. CP932) the block glyphs would turn into '?'.
    internal static class Gauge
    {
        private static readonly char[] AsciiGlyphs = ['_', '.', '-', '~', '=', '+', '*', '#'];

        // Visual bar: -140 to -44 dBm mapped to 0-20 chars (rounded half to even).
        public static string RsrpBar(double? dbm)
        {
            if (dbm is not double v) return "[        n/a         ]";
            int n = (int)Math.Max(0, Math.Min(20, Math.Round((v + 140) / 4.8)));
            return "[" + new string('#', n) + new string('-', 20 - n) + "]";
        }

        // Satellite carrier-to-noise: 0 to 50 dB-Hz mapped to 0-20 chars. No SNR = not tracked.
        public static string SnrBar(double? dbHz)
        {
            if (dbHz is not double v) return "[    not tracked     ]";
            int n = (int)Math.Max(0, Math.Min(20, Math.Floor(v / 2.5)));
            return "[" + new string('#', n) + new string('-', 20 - n) + "]";
        }

        // Auto scale for a sparkline: [min, max] of the valid (non-NaN) values, snapped outward
        // to multiples of step, widened (around the data center) to at least minSpan so a steady
        // signal does not magnify noise, then shifted/clamped into [floor, ceiling].
        // With no valid values the whole [floor, ceiling] range is returned.
        public static ScaleRange AutoScale(IEnumerable<double> values, double step, double minSpan, double floor, double ceiling)
        {
            List<double> valid = [.. values.Where(v => !double.IsNaN(v))];
            if (valid.Count == 0) return new ScaleRange(floor, ceiling);
            double min = valid.Min(), max = valid.Max();
            double lo = Math.Floor(min / step) * step;
            double hi = Math.Ceiling(max / step) * step;
            double need = Math.Ceiling(minSpan / step) * step;
            if (hi - lo < need)
            {
                lo = Math.Min(lo, Math.Floor((((min + max) / 2) - (need / 2)) / step) * step);
                hi = Math.Max(hi, lo + need);
            }
            if (lo < floor)
            {
                hi = Math.Min(ceiling, hi + (floor - lo));
                lo = floor;
            }
            if (hi > ceiling)
            {
                lo = Math.Max(floor, lo - (hi - ceiling));
                hi = ceiling;
            }
            return new ScaleRange(lo, hi);
        }

        // Log scale for non-negative series spanning decades (throughput): [Min, Max] are powers of
        // 10 around the positive valid values (at least one decade apart), with Min no lower than
        // floor (the value resolution). With no positive values the range is [floor, 10 x floor].
        // Values are plotted as log10 (ToLog); 0 and values below Min sit on the bottom level.
        public static ScaleRange LogScale(IEnumerable<double> values, double floor)
        {
            List<double> positive = [.. values.Where(v => !double.IsNaN(v) && v > 0)];
            if (positive.Count == 0) return new ScaleRange(floor, floor * 10);
            double lo = Math.Max(Math.Floor(Math.Log10(floor)), Math.Floor(Math.Log10(positive.Min())));
            double hi = Math.Max(lo + 1, Math.Ceiling(Math.Log10(positive.Max())));
            return new ScaleRange(Math.Pow(10, lo), Math.Pow(10, hi));
        }

        // log10 of each value, clamped to at least floor (> 0) so 0 stays finite; NaN stays NaN.
        public static double[] ToLog(IEnumerable<double> values, double floor)
            => [.. values.Select(v => double.IsNaN(v) ? v : Math.Log10(Math.Max(floor, v)))];

        // Sparklines map [min, max] linearly onto N levels (values outside are clamped).
        // Only the most recent width samples are shown, right-aligned. NaN (missing sample)
        // is drawn as a blank column so series sharing a time axis stay aligned.
        public static int SparkLevel(double value, double min, double max, int levels)
        {
            double level = Math.Floor((value - min) / (max - min) * levels);
            return (int)Math.Max(0, Math.Min(levels - 1, level));
        }

        // ASCII sparkline, 8 levels.
        public static string Sparkline(IReadOnlyList<double>? values, double min, double max, int width)
        {
            if (width <= 0) return "";
            values ??= [];
            int count = Math.Min(values.Count, width);
            StringBuilder sb = new();
            _ = sb.Append(' ', width - count);
            for (int i = values.Count - count; i < values.Count; i++)
            {
                if (double.IsNaN(values[i]))
                {
                    _ = sb.Append(' ');
                    continue;
                }
                _ = sb.Append(AsciiGlyphs[SparkLevel(values[i], min, max, AsciiGlyphs.Length)]);
            }
            return sb.ToString();
        }

        // Multi-row block sparkline, 8 x rows levels. Each column stacks U+2581..U+2588
        // (1/8..8/8) from the bottom row upward. Returns rows strings, top row first.
        public static string[] BlockSparkline(IReadOnlyList<double>? values, double min, double max, int width, int rows = 2)
        {
            rows = Math.Max(1, rows);
            StringBuilder[] builders = [.. Enumerable.Range(0, rows).Select(_ => new StringBuilder())]; // bottom row first
            if (width <= 0) return [.. builders.Select(_ => "")];
            values ??= [];
            int count = Math.Min(values.Count, width);
            foreach (StringBuilder b in builders) _ = b.Append(' ', width - count);
            for (int i = values.Count - count; i < values.Count; i++)
            {
                if (double.IsNaN(values[i]))
                {
                    foreach (StringBuilder b in builders) _ = b.Append(' ');
                    continue;
                }
                int eighths = SparkLevel(values[i], min, max, 8 * rows) + 1; // 1..8*rows
                for (int r = 0; r < rows; r++)
                {
                    int fill = Math.Max(0, Math.Min(8, eighths - (8 * r)));
                    _ = builders[r].Append(fill > 0 ? (char)(0x2580 + fill) : ' ');
                }
            }
            return [.. builders.Reverse().Select(b => b.ToString())];
        }

        public static ConsoleColor QualityColor(string? quality)
        {
            return quality switch
            {
                null or "" => ConsoleColor.DarkGray,
                "Excellent" => ConsoleColor.Green,
                "Good" => ConsoleColor.Cyan,
                "Fair" => ConsoleColor.Yellow,
                "Poor" => ConsoleColor.Red,
                _ => ConsoleColor.DarkRed,
            };
        }
    }
}
