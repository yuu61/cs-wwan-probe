using System.Globalization;
using System.Text.RegularExpressions;
using WwanProbe.Application;
using WwanProbe.Domain;
using static System.FormattableString;

namespace WwanProbe.Presentation
{
    // One screen line: Text/Color with optional colored Segments (Text is then their concatenation).
    // GrayLevel (0-255) draws the text in that RGB gray instead of Color.
    internal sealed record FrameLine
    {
        public FrameLine(string text = "", ConsoleColor color = ConsoleColor.Gray, byte? grayLevel = null)
        {
            Text = text;
            Color = color;
            GrayLevel = grayLevel;
        }

        public string Text { get; init; }
        public ConsoleColor Color { get; init; }
        public byte? GrayLevel { get; init; }
        public IReadOnlyList<FrameLine> Segments { get; init; } = [];

        public static FrameLine FromSegments(ConsoleColor color, IReadOnlyList<FrameLine> segments)
            => new(string.Concat(segments.Select(s => s.Text)), color) { Segments = segments };
    }

    // Body lines plus the footer, which is pinned to the bottom row.
    internal sealed record Frame(List<FrameLine> Body, FrameLine Footer);

    // View state owned by the monitor loop.
    // HandoverVisible / SatelliteVisible = the [h] / [s] view replaces the monitor body (at most one is set);
    // the offsets are the first shown row there.
    // Unicode = console accepts non-ASCII glyphs (TUI switches to UTF-8; plain output does not).
    // ChartRows = sparkline height in rows for Unicode charts (HistoryCharts.RowsMin..RowsMax).
    internal sealed class MonitorView
    {
        public bool Paused { get; set; }
        public bool Fetching { get; set; }
        public bool Done { get; set; }
        public bool Quit { get; set; }
        public bool Unicode { get; set; }
        public int LastWidth { get; set; }
        public int LastHeight { get; set; }
        public Dictionary<string, bool> ChartVisible { get; set; } = HistoryCharts.NewVisibility();
        public int ChartRows { get; set; } = HistoryCharts.RowsDefault;
        public bool HandoverVisible { get; set; }
        public int HandoverOffset { get; set; }
        public bool SatelliteVisible { get; set; }
        public int SatelliteOffset { get; set; }
        public bool RefreshRequested { get; set; }
        public bool ResetRequested { get; set; }
        public bool Dirty { get; set; }
    }

    // Builds one screen as lines. Pure with respect to the console: no cursor / write operations here.
    // Sections with their own state or tables are in HistoryChart.cs, HandoverSection.cs and GnssSection.cs.
    internal static partial class MonitorFrame
    {

        public static string SectionRule(string title, int width)
        {
            string head = $"-- {title} ";
            return head + new string('-', Math.Max(0, width - head.Length));
        }

        public static string Optional(object? value, string format)
            => value is null ? "n/a" : string.Format(CultureInfo.InvariantCulture, format, value);

        public static string TrafficRate(double? valueKB) => valueKB is double kb ? HistoryCharts.FormatSi(kb * 1024, "B/s") : "n/a";

        public static string CarrierAggregation(CarrierAggregation? ca)
        {
            if (ca is null) return "n/a";
            if (ca.Cells == 0) return "not on LTE";
            string bw = string.Join('+', ca.BandwidthsMHz.Select(b => b is double v ? v.ToString(CultureInfo.InvariantCulture) : "?"));
            string cells = ca.Cells == 1 ? "1 cell" : Invariant($"{ca.Cells} cells");
            return $"{cells} ({bw} MHz)";
        }

        // Count currently observed LTE cells, not historical samples or physical sites.
        // EARFCN + PCI identifies duplicate observations across serving/neighbor lists.
        public static FrameLine LteBandLine(RatConfig rat, LteSnapshot? snapshot)
        {
            Dictionary<string, int> counts = [];
            HashSet<string> seen = [];
            IEnumerable<(string? Band, long? Earfcn, long? Pci)> cells =
                (snapshot?.Serving ?? []).Where(c => c is not null).Select(c => ((string?)c.Band, c.Earfcn, c.Pci))
                .Concat((snapshot?.Neighbors ?? []).Where(n => n is not null).Select(n => ((string?)n.Band, (long?)n.Earfcn, n.Pci)));
            foreach ((string? bandText, long? earfcn, long? pci) in cells)
            {
                Match m = BandNumber().Match(bandText ?? "");
                if (!m.Success) continue;
                string band = m.Groups[1].Value;
                if (earfcn is not null && pci is not null)
                {
                    string key = Invariant($"{band}:{earfcn}:{pci}");
                    if (!seen.Add(key)) continue;
                }
                counts[band] = counts.GetValueOrDefault(band) + 1;
            }

            // Stretch the observed counts across the grayscale range so 1 vs 2 is visible.
            // Only displayed bands determine the maximum; zero keeps the existing default.
            int maxCount = 1;
            foreach (int band in rat.LteBands) maxCount = Math.Max(maxCount, counts.GetValueOrDefault(Invariant($"{band}")));
            List<FrameLine> segments = [new FrameLine(" LTE bands: ", ConsoleColor.DarkGray)];
            foreach (int band in rat.LteBands)
            {
                if (segments.Count > 1) segments.Add(new FrameLine(" ", ConsoleColor.DarkGray));
                int count = counts.GetValueOrDefault(Invariant($"{band}"));
                byte? grayLevel = null;
                if (count > 0) grayLevel = maxCount == 1 ? (byte)255 : (byte)Math.Round(144 + (111.0 * (count - 1) / (maxCount - 1)));
                segments.Add(new FrameLine(Invariant($"B{band}"), ConsoleColor.DarkGray, grayLevel));
            }
            if (rat.NrBands.Count > 0)
            {
                segments.Add(new FrameLine("   NR bands: " + string.Join(' ', rat.NrBands.Select(b => Invariant($"n{b}"))), ConsoleColor.DarkGray));
            }
            return FrameLine.FromSegments(ConsoleColor.DarkGray, segments);
        }

        // finding: current sample, log: the session's 2G/3G log.
        public static void AddDowngradeLine(List<FrameLine> lines, DowngradeFinding? finding, DowngradeLog? log)
        {
            if (finding?.Level == DowngradeLevel.Alert)
            {
                lines.Add(new FrameLine(" !! 2G/3G DOWNGRADE: " + string.Join("; ", finding.Reasons), ConsoleColor.Red));
            }
            else if (finding?.Level == DowngradeLevel.Warning)
            {
                lines.Add(new FrameLine(" !  2G/3G cells visible: " + string.Join("; ", finding.Reasons), ConsoleColor.Yellow));
            }
            if (log is not null && log.AlertCount + log.WarningCount > 0 && !(finding is not null && finding.Level != DowngradeLevel.None))
            {
                string text = string.Format(CultureInfo.InvariantCulture, " !  2G/3G seen earlier (alert {0} / warning {1} samples), last {2} [{3}]: {4}",
                    log.AlertCount, log.WarningCount, log.Last, log.LastLevel, string.Join("; ", log.LastReasons));
                lines.Add(new FrameLine(text, ConsoleColor.DarkYellow));
            }
        }

        public static Frame Build(MonitorSession session, MonitorView view, int width)
        {
            MonitorConfig config = session.Config;
            ModemSummary summary = session.Summary;
            LteSnapshot? snapshot = session.Snapshot;

            List<FrameLine> lines = [];
            string rule = new('=', Math.Max(0, width));

            // Title bar
            string status = view.Done ? "DONE" : view.Paused ? "PAUSED" : view.Fetching ? "UPDATING" : "RUNNING";
            string countStr = config.Count == 0 ? "" : Invariant($"/{config.Count}");
            string right = Invariant($"#{session.Iteration}{countStr}  [{status}]");
            string left = " wwan-probe LTE Signal Monitor";
            int pad = Math.Max(1, width - left.Length - right.Length - 1);
            lines.Add(new FrameLine(left + new string(' ', pad) + right, ConsoleColor.Cyan));
            lines.Add(new FrameLine(rule, ConsoleColor.Cyan));

            // 2G/3G downgrade warnings go first so they are never scrolled away
            AddDowngradeLine(lines, snapshot?.Downgrade, session.DowngradeLog);

            if (view.HandoverVisible)
            {
                HandoverSection.Add(lines, session.HandoverLog, view, width);
                return new Frame(lines, new FrameLine(" [h] Monitor  [Up/Down] Newer/Older  [q] Quit  [p] Pause  [r] Refresh  [R] Reset stats", ConsoleColor.Black));
            }
            bool nmeaEnabled = session.NmeaReceiver is not null;
            if (view.SatelliteVisible)
            {
                GnssSection.AddSatellites(lines, snapshot?.Satellites, nmeaEnabled, view, width);
                return new Frame(lines, new FrameLine(" [s] Monitor  [Up/Down] Scroll  [q] Quit  [p] Pause  [r] Refresh  [R] Reset stats", ConsoleColor.Black));
            }

            // Device
            lines.Add(new FrameLine($" Model: {summary.Model}   FW: {summary.Firmware}"));
            lines.Add(new FrameLine($" IMEI:  {summary.Imei}   ICCID: {summary.SimIccId}   SPN: {summary.SimSpn}"));
            lines.Add(new FrameLine($" Radio: {summary.RadioState}   DataClass: {summary.DataClass}"));
            Infrastructure.Modem.ModemAt? at = summary.At;
            if (at?.Profile is not null)
            {
                lines.Add(new FrameLine($" AT: {at.Channel?.Name} / {at.Profile.Name}", ConsoleColor.DarkGray));
            }
            else
            {
                string reason = !string.IsNullOrEmpty(at?.Error) ? at.Error : "not initialized";
                lines.Add(new FrameLine($" AT: unavailable ({reason})", ConsoleColor.DarkGray));
            }
            RatConfig? rat = summary.RatConfig;
            if (rat is null)
            {
                lines.Add(new FrameLine(" RAT: n/a   LTE bands: n/a", ConsoleColor.DarkGray));
            }
            else
            {
                IEnumerable<string> legacyBands = rat.GsmBands.Select(b => Invariant($"{b}")).Concat(rat.UmtsBands.Select(b => Invariant($"B{b}")));
                string ratText = $" RAT: {rat.Allowed}";
                if (!string.IsNullOrEmpty(rat.Preferred)) ratText += $" (prefer {rat.Preferred})";
                if (summary.LegacyAllowed) ratText += $"   2G/3G bands: {string.Join(' ', legacyBands)}   [2G/3G enabled: downgrade possible]";
                lines.Add(new FrameLine(ratText, summary.LegacyAllowed ? ConsoleColor.DarkYellow : ConsoleColor.DarkGray));
                lines.Add(LteBandLine(rat, snapshot));
            }

            // Network
            lines.Add(new FrameLine(SectionRule("Network", width), ConsoleColor.DarkCyan));
            if (snapshot is null) lines.Add(new FrameLine(" Waiting for first sample...", ConsoleColor.DarkGray));
            else AddNetwork(lines, session, snapshot, view, width);

            // Footer is returned separately so it can be pinned to the bottom row.
            string csvStr = !string.IsNullOrEmpty(config.CsvPath) ? $"  CSV: {config.CsvPath}" : "";
            string satelliteKey = nmeaEnabled ? "[s] Satellites  " : "";
            FrameLine footer = new(
                Invariant($" [q] Quit  [p] Pause  [r] Refresh  [R] Reset  [h] Handovers  {satelliteKey}[1-6] Chart  [g] All charts  [Up/Down] Chart rows   Interval: {config.Interval}s{csvStr}"),
                ConsoleColor.Black);
            return new Frame(lines, footer);
        }

        private static void AddNetwork(List<FrameLine> lines, MonitorSession session, LteSnapshot snapshot, MonitorView view, int width)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            lines.Add(new FrameLine($" {snapshot.ProviderName} ({snapshot.ProviderId}) | {snapshot.DataClass} | APN: {snapshot.Apn}"));
            lines.Add(new FrameLine(string.Format(inv, " BW: {0}   RX: {1}   TX: {2}   Updated: {3}", Optional(snapshot.BwMbps, "{0} Mbps"),
                TrafficRate(snapshot.RxKB), TrafficRate(snapshot.TxKB), snapshot.Timestamp)));
            if (!string.IsNullOrEmpty(snapshot.TrafficError)) lines.Add(new FrameLine($" Traffic: {snapshot.TrafficError}", ConsoleColor.DarkYellow));
            lines.Add(new FrameLine(string.Format(inv, " Temp: {0}   RSSNR: {1}   CA: {2}",
                Optional(snapshot.TempC, "{0} C"), Optional(snapshot.Rssnr, "{0:0.0} dB"), CarrierAggregation(snapshot.Ca))));

            GnssSection.AddGps(lines, snapshot.Gps, snapshot.Satellites, width);

            // Serving cells
            lines.Add(new FrameLine(SectionRule("Serving Cell (LTE)", width), ConsoleColor.DarkCyan));
            if (snapshot.Serving.Count == 0) lines.Add(new FrameLine(" (no LTE serving cell)", ConsoleColor.DarkGray));
            foreach (ServingCell c in snapshot.Serving)
            {
                lines.Add(new FrameLine(string.Format(inv, " {0} RSRP: {1}  RSRQ: {2}  [{3}]", Gauge.RsrpBar(c.RsrpDbm),
                    Optional(c.RsrpDbm, "{0} dBm"), Optional(c.RsrqDb, "{0} dB"), Optional(c.Quality, "{0}")), Gauge.QualityColor(c.Quality)));
                lines.Add(new FrameLine(Invariant($" {c.Band} | EARFCN:{c.Earfcn} | PCI:{c.Pci} | CellID:{c.CellId} | TAC:{c.Tac} | TA:{c.Ta} | MNC:{c.Provider}")));
            }

            HandoverSection.Add(lines, session.HandoverLog, view, width);

            // History (primary serving cell and modem-wide values) on one time axis
            if (session.History["Rsrp"].Count > 0) HistoryCharts.AddSection(lines, session, view, width);

            // Neighbors (AT channel, e.g. AT+XMCI; WinRT when the modem reports them there)
            if (snapshot.Neighbors is null)
            {
                lines.Add(new FrameLine(SectionRule("Neighbors", width), ConsoleColor.DarkCyan));
                string reason = !string.IsNullOrEmpty(snapshot.AtError) ? snapshot.AtError : "neighbor cell query failed";
                lines.Add(new FrameLine($" (unavailable: {reason})", ConsoleColor.DarkGray));
            }
            else
            {
                lines.Add(new FrameLine(SectionRule(Invariant($"Neighbors ({snapshot.Neighbors.Count})"), width), ConsoleColor.DarkCyan));
                if (snapshot.Neighbors.Count == 0) lines.Add(new FrameLine(" (none)", ConsoleColor.DarkGray));
                foreach (LteNeighbor n in snapshot.Neighbors.OrderByDescending(n => n.RsrpDbm))
                {
                    lines.Add(new FrameLine(string.Format(inv, " {0} {1,4} dBm {2,5} dB  {3,-10} EARFCN:{4,-6} PCI:{5}",
                        Gauge.RsrpBar(n.RsrpDbm), n.RsrpDbm, n.RsrqDb, n.Band, n.Earfcn, n.Pci), ConsoleColor.Gray));
                }
            }

            // UMTS
            foreach (UmtsCell u in snapshot.Umts)
            {
                lines.Add(new FrameLine(Invariant($" [UMTS] CellId:{u.CellId} UARFCN:{u.Uarfcn} RSCP:{u.RscpDbm}dBm"), ConsoleColor.DarkYellow));
            }

            if (!string.IsNullOrEmpty(snapshot.Error)) lines.Add(new FrameLine($" Error: {snapshot.Error}", ConsoleColor.Red));
        }

        [GeneratedRegex(@"^B(\d+)(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex BandNumber();
    }
}
