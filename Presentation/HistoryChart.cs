using System.Globalization;
using WwanProbe.Application;
using WwanProbe.Domain;

namespace WwanProbe.Presentation
{
    // Step / MinSpan / Floor / Ceiling for Gauge.AutoScale, or Log + Floor for Gauge.LogScale.
    internal sealed record ChartScale(double Step, double MinSpan, double Floor, double Ceiling, bool Log = false);

    // Key = toggle key in the TUI, History = SignalHistory name. StepUnit = unit of a value difference
    // (dBm differences are dB). ValueFactor (optional) converts the history values into Unit; Si = format
    // values with SI prefixes (FormatSi). ScaleGroup (optional): shown charts with the same group share one
    // scale computed from all their visible samples (same Scale settings required).
    internal sealed record HistoryChart(string Key, string Label, string History, string Unit, string StepUnit, ConsoleColor Color,
        bool Visible, ChartScale Scale, double? ValueFactor = null, bool Si = false, string? ScaleGroup = null, double? RowsRatio = null);

    // History charts (definitions, visibility, height, scale and sparkline rows).
    internal static class HistoryCharts
    {
        // In display order.
        public static readonly IReadOnlyList<HistoryChart> All =
        [
            new HistoryChart("1", "RSRP", "Rsrp", "dBm", "dB", ConsoleColor.DarkGreen, true, new ChartScale(5, 10, -140, -44)),
            new HistoryChart("2", "RSRQ", "Rsrq", "dB", "dB", ConsoleColor.DarkYellow, true, new ChartScale(1, 4, -20, -3)),
            new HistoryChart("3", "SNR", "Rssnr", "dB", "dB", ConsoleColor.DarkCyan, true, new ChartScale(5, 10, -50, 50)),
            new HistoryChart("4", "RX", "RxKB", "B/s", "B/s", ConsoleColor.DarkMagenta, false, new ChartScale(0, 0, 100, 0, Log: true),
                ValueFactor: 1024, Si: true, ScaleGroup: "Throughput"),
            new HistoryChart("5", "TX", "TxKB", "B/s", "B/s", ConsoleColor.Magenta, false, new ChartScale(0, 0, 100, 0, Log: true),
                ValueFactor: 1024, Si: true, ScaleGroup: "Throughput"),
            new HistoryChart("6", "Temp", "TempC", "C", "C", ConsoleColor.DarkRed, false, new ChartScale(5, 10, -40, 125), RowsRatio: 0.5),
        ];

        // Unicode chart height in rows (each row adds 8 levels).
        public const int RowsDefault = 2;
        public const int RowsMin = 1;
        public const int RowsMax = 10;

        // Width of a chart row head (label + axis label).
        public const int HeadWidth = 12;

        // Rows for one chart: RowsRatio (default 1) of the shared chart height, rounded down, at least RowsMin.
        public static int ChartRows(HistoryChart chart, int rows) => Math.Max(RowsMin, (int)Math.Floor(rows * (chart.RowsRatio ?? 1)));

        // rows + delta clamped into [RowsMin, RowsMax].
        public static int StepHeight(int rows, int delta) => Math.Max(RowsMin, Math.Min(RowsMax, rows + delta));

        // Initial chart visibility: Key -> shown.
        public static Dictionary<string, bool> NewVisibility() => All.ToDictionary(c => c.Key, c => c.Visible, StringComparer.OrdinalIgnoreCase);

        // Toggles one chart (key = "1"..) or, with key = "all", hides every chart when any
        // is shown and shows every chart otherwise. Returns false for an unknown key.
        public static bool Toggle(Dictionary<string, bool> visible, string key)
        {
            if (key == "all")
            {
                bool show = !visible.ContainsValue(true);
                foreach (string k in visible.Keys.ToList()) visible[k] = show;
                return true;
            }
            if (!visible.TryGetValue(key, out bool value)) return false;
            visible[key] = visible[key] = !value;
            return true;
        }

        // Axis label that fits 5 columns: 10000 and above as "10k".
        public static string FormatAxis(double value)
        {
            return Math.Abs(value) >= 10000
                ? string.Format(CultureInfo.InvariantCulture, "{0:0}k", value / 1000)
                : string.Format(CultureInfo.InvariantCulture, "{0:0.##}", value);
        }

        // Value with an SI prefix (k/M/G, 1000 steps) and at most 3 significant digits:
        // 1234 "B/s" -> "1.23 kB/s". Without unit it fits 5 columns for axis labels: "1.23k".
        public static string FormatSi(double value, string unit = "")
        {
            string[] prefixes = ["", "k", "M", "G"];
            int i = 0;
            while (i < prefixes.Length - 1 && Math.Abs(value) >= 999.5 * Math.Pow(1000, i)) i++;
            double scaled = value / Math.Pow(1000, i);
            string format = Math.Abs(scaled) < 10 ? "0.##" : Math.Abs(scaled) < 100 ? "0.#" : "0";
            string number = scaled.ToString(format, CultureInfo.InvariantCulture);
            return !string.IsNullOrEmpty(unit) ? number + " " + prefixes[i] + unit : number + prefixes[i];
        }

        // Sparkline columns for console width.
        public static int SparkWidth(int width) => Math.Max(10, width - HeadWidth - 4);

        // History values converted into the chart's Unit (ValueFactor).
        public static double[] ToChartValues(HistoryChart chart, IEnumerable<double> values)
            => chart.ValueFactor is double factor ? [.. values.Select(v => v * factor)] : [.. values];

        // The samples a chart plots at console width: the most recent ones, in the chart's Unit.
        public static double[] Window(HistoryChart chart, IReadOnlyList<double> values, int width)
            => ToChartValues(chart, values.Skip(Math.Max(0, values.Count - SparkWidth(width))));

        // Scale range of values (in Unit) with the chart's Scale settings.
        public static ScaleRange Scale(HistoryChart chart, IEnumerable<double> values)
        {
            return chart.Scale.Log ? Gauge.LogScale(values, chart.Scale.Floor)
                : Gauge.AutoScale(values, chart.Scale.Step, chart.Scale.MinSpan, chart.Scale.Floor, chart.Scale.Ceiling);
        }

        // Appends one history chart (sparkline rows + stats line) to lines.
        // The scale is range (shared by a ScaleGroup) or, without it, computed from the visible
        // (most recent) samples only; its bounds are shown as the axis labels. Stats cover the whole history.
        public static void AddChart(List<FrameLine> lines, HistoryChart chart, IReadOnlyList<double> values, int width, bool unicode,
            int rows = RowsDefault, ScaleRange? range = null)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            int sparkWidth = SparkWidth(width);
            double[] visible = Window(chart, values, width);
            ScaleRange r = range ?? Scale(chart, visible);
            string maxText = chart.Si ? FormatSi(r.Max) : FormatAxis(r.Max);
            string minText = chart.Si ? FormatSi(r.Min) : FormatAxis(r.Min);
            // Log charts plot log10(v); the axis labels keep the original values.
            double[] plotValues = visible;
            double plotMin = r.Min, plotMax = r.Max;
            if (chart.Scale.Log)
            {
                plotValues = Gauge.ToLog(visible, r.Min);
                plotMin = Math.Log10(r.Min);
                plotMax = Math.Log10(r.Max);
            }
            string scaleText;
            if (unicode)
            {
                string[] sparkRows = Gauge.BlockSparkline(plotValues, plotMin, plotMax, sparkWidth, rows);
                int last = sparkRows.Length - 1;
                for (int row = 0; row <= last; row++)
                {
                    // Axis labels: max on the top row, min on the bottom row (a single row shows only the label).
                    string axis = last == 0 ? "" : row == 0 ? maxText : row == last ? minText : "";
                    string label = row == 0 ? chart.Label : "";
                    lines.Add(new FrameLine(string.Format(inv, " {0,-5}{1,5} |{2}|", label, axis, sparkRows[row]), chart.Color));
                }
                scaleText = chart.Scale.Log
                    ? string.Format(inv, "log, {0:0.#} levels/decade", 8 * sparkRows.Length / (plotMax - plotMin))
                    : string.Format(inv, "{0:0.##} {1}/level", (r.Max - r.Min) / (8 * sparkRows.Length), chart.StepUnit);
                if (last == 0) scaleText = string.Format(inv, "scale {0}..{1} {2}, {3}", minText, maxText, chart.Unit, scaleText);
            }
            else
            {
                lines.Add(new FrameLine(string.Format(inv, " {0,-10} |{1}|", chart.Label, Gauge.Sparkline(plotValues, plotMin, plotMax, sparkWidth)), chart.Color));
                scaleText = string.Format(inv, "{0} {1}..{2} {3}: _ . - ~ = + * #", chart.Scale.Log ? "log scale" : "scale", minText, maxText, chart.Unit);
            }
            SignalStatistic? stat = SignalStatistic.Compute(ToChartValues(chart, values));
            string text = stat is null ? "(no valid samples)"
                : chart.Si
                    ? string.Format(inv, "min {0} / avg {1} / max {2}  (n={3}, {4})", FormatSi(stat.Min, chart.Unit), FormatSi(stat.Avg, chart.Unit),
                        FormatSi(stat.Max, chart.Unit), stat.Count, scaleText)
                    : string.Format(inv, "min {0} / avg {1} / max {2} {3}  (n={4}, {5})", stat.Min, stat.Avg, stat.Max, chart.Unit, stat.Count, scaleText);
            lines.Add(new FrameLine(new string(' ', HeadWidth + 1) + text, ConsoleColor.DarkGray));
        }

        // History section: the visible charts on one time axis; hidden ones are listed in the title.
        // Shown charts with the same ScaleGroup share one scale; a chart shown alone keeps its own.
        public static void AddSection(List<FrameLine> lines, MonitorSession session, MonitorView view, int width)
        {
            Dictionary<string, bool> visibility = view.ChartVisible;
            int chartRows = view.ChartRows > 0 ? view.ChartRows : RowsDefault;
            List<HistoryChart> shown = [.. All.Where(c => visibility.TryGetValue(c.Key, out bool v) && v)];
            List<HistoryChart> hidden = [.. All.Where(c => !(visibility.TryGetValue(c.Key, out bool v) && v))];
            string title = "History (primary cell)";
            if (hidden.Count > 0) title += "  hidden: " + string.Join(", ", hidden.Select(c => $"{c.Key} {c.Label}"));
            lines.Add(new FrameLine(MonitorFrame.SectionRule(title, width), ConsoleColor.DarkCyan));
            Dictionary<string, ScaleRange> groupRanges = [];
            foreach (IGrouping<string, HistoryChart> group in shown.Where(c => c.ScaleGroup is not null).GroupBy(c => c.ScaleGroup!).Where(g => g.Count() > 1))
            {
                List<double> samples = [];
                foreach (HistoryChart chart in group) samples.AddRange(Window(chart, session.History[chart.History], width));
                groupRanges[group.Key] = Scale(group.First(), samples);
            }
            foreach (HistoryChart chart in shown)
            {
                ScaleRange? range = chart.ScaleGroup is not null && groupRanges.TryGetValue(chart.ScaleGroup, out ScaleRange g) ? g : null;
                AddChart(lines, chart, session.History[chart.History], width, view.Unicode, ChartRows(chart, chartRows), range);
            }
        }
    }
}
