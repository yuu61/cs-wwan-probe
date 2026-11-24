using System.Globalization;
using WwanProbe.Infrastructure.Gnss;

namespace WwanProbe.Presentation
{
    // GPS / GNSS section on the main screen and the satellite list ([s] view).
    public static class GnssSection
    {
        private static readonly Dictionary<string, ConsoleColor> SystemColors = new Dictionary<string, ConsoleColor>(StringComparer.OrdinalIgnoreCase)
        {
            ["GPS"] = ConsoleColor.Green,
            ["QZSS"] = ConsoleColor.White,
            ["SBAS"] = ConsoleColor.Gray,
            ["GLONASS"] = ConsoleColor.Magenta,
            ["Galileo"] = ConsoleColor.Cyan,
            ["BeiDou"] = ConsoleColor.Yellow,
            ["NavIC"] = ConsoleColor.DarkMagenta,
            ["Unknown"] = ConsoleColor.DarkGray,
        };

        public static string FormatSatelliteSummary(NmeaObservation satellites)
        {
            switch (satellites.Status)
            {
                case "Starting": return "Starting (waiting for the elevated NMEA helper)";
                case "Stale": return "Stale (the NMEA helper stopped updating)";
                case "Unavailable": return $"Unavailable ({satellites.Error})";
            }
            if (!satellites.NmeaReceived) return "waiting for NMEA from the GNSS driver";
            string systems = string.Join(", ", satellites.Systems.Select(s => string.Format(CultureInfo.InvariantCulture, "{0} {1}", s.System, s.InView)));
            if (systems.Length > 0) systems = $" ({systems})";
            return string.Format(CultureInfo.InvariantCulture, "{0} in view{1}, {2} used", satellites.InView, systems, satellites.Used);
        }

        // The receiver's fix state from NMEA; values not received recently are n/a.
        public static void AddNmeaFixLines(List<FrameLine> lines, NmeaFix? fix)
        {
            if (fix is null) return;
            string status = fix.Valid is null ? "n/a" : fix.Valid.Value ? "valid" : "invalid";
            ConsoleColor color = fix.Valid == false || string.Equals(fix.Dimension, "NoFix", StringComparison.OrdinalIgnoreCase)
                ? ConsoleColor.DarkYellow : ConsoleColor.Gray;
            lines.Add(new FrameLine(string.Format(CultureInfo.InvariantCulture, " Fix (NMEA): {0}   Quality: {1}   Mode: {2} ({3})   Sats used: {4}",
                MonitorFrame.Optional(fix.Dimension, "{0}"), MonitorFrame.Optional(fix.Quality, "{0}"),
                MonitorFrame.Optional(fix.Mode, "{0}"), status, MonitorFrame.Optional(fix.SatellitesUsed, "{0}")), color));
            lines.Add(new FrameLine(string.Format(CultureInfo.InvariantCulture, " Altitude MSL: {0}   Geoid separation: {1}",
                MonitorFrame.Optional(fix.AltitudeMslM, "{0:0.0} m"), MonitorFrame.Optional(fix.GeoidSeparationM, "{0:0.0} m")), color));
        }

        public static void AddGps(List<FrameLine> lines, GpsObservation? gps, NmeaObservation? satellites, int width)
        {
            if (gps is null && satellites is null) return;
            CultureInfo inv = CultureInfo.InvariantCulture;
            lines.Add(new FrameLine(MonitorFrame.SectionRule("GPS / GNSS", width), ConsoleColor.DarkCyan));
            if (gps?.Status == "Fix")
            {
                lines.Add(new FrameLine(string.Format(inv, " Lat: {0:F6}   Lon: {1:F6}   Accuracy: {2}",
                    gps.Latitude, gps.Longitude, MonitorFrame.Optional(gps.AccuracyM, "{0:0.0} m")), ConsoleColor.Green));
                lines.Add(new FrameLine(string.Format(inv, " Alt: {0}   Speed: {1}   Heading: {2}",
                    MonitorFrame.Optional(gps.AltitudeM, "{0:0.0} m"), MonitorFrame.Optional(gps.SpeedMps, "{0:0.0} m/s"),
                    MonitorFrame.Optional(gps.HeadingDeg, "{0:0.0} deg"))));
                lines.Add(new FrameLine(string.Format(inv, " HDOP: {0}   PDOP: {1}   VDOP: {2}", MonitorFrame.Optional(gps.Hdop, "{0:0.0}"),
                    MonitorFrame.Optional(gps.Pdop, "{0:0.0}"), MonitorFrame.Optional(gps.Vdop, "{0:0.0}"))));
                lines.Add(new FrameLine($" Source: Satellite   Fix UTC: {gps.Timestamp}", ConsoleColor.DarkGray));
            }
            else if (gps is not null)
            {
                string? detail = gps.Status switch
                {
                    "Disabled" => gps.Error,
                    "Unavailable" => gps.Error,
                    "Stale" => "satellite report expired; waiting for a fresh fix",
                    _ => !string.IsNullOrEmpty(gps.Source) ? $"waiting for satellite fix; {gps.Source} position ignored" : "waiting for satellite fix",
                };
                lines.Add(new FrameLine($" GPS: {gps.Status} ({detail})", ConsoleColor.DarkYellow));
            }
            if (satellites is not null)
            {
                ConsoleColor color = satellites.Status == "Receiving" ? ConsoleColor.Gray : ConsoleColor.DarkYellow;
                lines.Add(new FrameLine($" Satellites: {FormatSatelliteSummary(satellites)}  [s]", color));
                if (satellites.Status == "Receiving") AddNmeaFixLines(lines, satellites.Fix);
            }
        }

        // Satellite list (the [s] view): one row per satellite and signal, colored by constellation.
        // satellites = null means -Nmea is off (enabled = false) or no sample has completed yet.
        public static void AddSatellites(List<FrameLine> lines, NmeaObservation? satellites, bool enabled, MonitorView view, int width)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            lines.Add(new FrameLine(MonitorFrame.SectionRule("Satellites (NMEA GSV/GSA) [s]", width), ConsoleColor.DarkCyan));
            if (!enabled)
            {
                lines.Add(new FrameLine(" (satellite list is off: start cs-wwan-probe with -Nmea)", ConsoleColor.DarkGray));
                return;
            }
            if (satellites is null)
            {
                lines.Add(new FrameLine(" Waiting for first sample...", ConsoleColor.DarkGray));
                return;
            }
            string summary = FormatSatelliteSummary(satellites);
            bool receiving = satellites.Status == "Receiving";
            if (!receiving || satellites.Satellites.Count == 0)
            {
                lines.Add(new FrameLine($" {summary}", receiving ? ConsoleColor.DarkGray : ConsoleColor.DarkYellow));
                if (receiving) AddNmeaFixLines(lines, satellites.Fix);
                return;
            }
            lines.Add(new FrameLine($" {summary}   Updated: {satellites.UpdatedUtc}", ConsoleColor.DarkGray));
            AddNmeaFixLines(lines, satellites.Fix);
            lines.Add(new FrameLine(" System     ID  Sig  Elev  Azim  SNR  Used  C/N0 (0-50 dB-Hz)", ConsoleColor.DarkGray));
            IReadOnlyList<NmeaSatellite> rows = satellites.Satellites;
            int height = view.LastHeight > 0 ? view.LastHeight : 30;
            // Title, rule, optional downgrade warning, section title, summary, two fix lines, header,
            // scroll line and footer.
            int pageSize = Math.Max(1, height - 10);
            int offset = Math.Clamp(view.SatelliteOffset, 0, Math.Max(0, rows.Count - pageSize));
            view.SatelliteOffset = offset;
            foreach (NmeaSatellite s in rows.Skip(offset).Take(pageSize))
            {
                string text = string.Format(inv, " {0,-8} {1,4} {2,4} {3,5} {4,5} {5,4}  {6,-4}  {7}", s.System, s.Id, s.Signal,
                    MonitorFrame.Optional(s.ElevationDeg, "{0}"), MonitorFrame.Optional(s.AzimuthDeg, "{0}"),
                    MonitorFrame.Optional(s.SnrDbHz, "{0}"), s.Used ? "yes" : "", Gauge.SnrBar(s.SnrDbHz));
                lines.Add(new FrameLine(text, SystemColors.TryGetValue(s.System, out ConsoleColor color) ? color : ConsoleColor.DarkGray));
            }
            if (rows.Count > pageSize)
            {
                lines.Add(new FrameLine(string.Format(inv, " Rows {0}-{1} / {2}  [Up/Down] scroll", offset + 1, Math.Min(rows.Count, offset + pageSize), rows.Count),
                    ConsoleColor.DarkGray));
            }
        }
    }
}
