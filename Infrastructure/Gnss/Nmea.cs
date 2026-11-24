using System.Globalization;
using System.Text.RegularExpressions;

namespace WwanProbe.Infrastructure.Gnss
{
    // NMEA 0183 satellites (GSV = satellites in view, GSA = used in the fix) and the receiver's fix
    // state (GSA, GGA, RMC). Pure parsing, also used by hardware-free tests. GGA and RMC also carry
    // the position: only the fields listed under NmeaFix are read, so reports never contain coordinates.

    // Id is the NMEA ID as reported (GLONASS 65-96 in GP/GN and GL sentences alike);
    // Signal is the NMEA 4.10 signal ID ("" before 4.10); missing values are null.
    public sealed record NmeaSatellite(string System, int Id, string Signal, int? ElevationDeg, int? AzimuthDeg, int? SnrDbHz, bool Used);

    // Dimension (GSA: NoFix | 2D | 3D); Quality (GGA fix quality); SatellitesUsed (GGA);
    // AltitudeMslM / GeoidSeparationM (GGA, metres); Valid (RMC status A); Mode (RMC mode).
    // Each value is null when its sentence has not been received recently.
    public sealed record NmeaFix(string? Dimension, string? Quality, int? SatellitesUsed, double? AltitudeMslM,
        double? GeoidSeparationM, bool? Valid, string? Mode);

    public sealed record NmeaReport(IReadOnlyList<NmeaSatellite> Satellites, int UsedCount, NmeaFix Fix);

    // Complete, checksum-valid sentences and the incomplete tail to prepend to the next chunk.
    public sealed record NmeaSplit(IReadOnlyList<string> Sentences, string Rest);

    public static class Nmea
    {
        // Display order of the constellations.
        public static readonly IReadOnlyList<string> Systems = new[] { "GPS", "QZSS", "SBAS", "GLONASS", "Galileo", "BeiDou", "NavIC", "Unknown" };

        // Sentences older than this are dropped from reports.
        public const int MaxAgeSeconds = 10;

        private static readonly Regex SentencePattern = new Regex(@"\$([^$*\r\n]+)\*([0-9A-Fa-f]{2})", RegexOptions.CultureInvariant);

        private static readonly Dictionary<string, string> TalkerSystems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GL"] = "GLONASS",
            ["GA"] = "Galileo",
            ["GB"] = "BeiDou",
            ["BD"] = "BeiDou",
            ["GQ"] = "QZSS",
            ["QZ"] = "QZSS",
            ["GI"] = "NavIC",
        };

        // Constellation of satellite id. GP/GN talkers mix systems, told apart by the NMEA ID ranges.
        public static string SatelliteSystem(string talker, int id)
        {
            if (TalkerSystems.TryGetValue(talker, out string? system)) return system;
            if (id >= 1 && id <= 32) return "GPS";
            if ((id >= 33 && id <= 64) || (id >= 120 && id <= 158)) return "SBAS";
            if (id >= 65 && id <= 96) return "GLONASS";
            if (id >= 193 && id <= 202) return "QZSS";
            return "Unknown";
        }

        // Splits a stream whose sentences can span driver events.
        public static NmeaSplit Split(string buffer)
        {
            List<string> sentences = new List<string>();
            int end = 0;
            foreach (Match match in SentencePattern.Matches(buffer))
            {
                end = match.Index + match.Length;
                int checksum = 0;
                foreach (char c in match.Groups[1].Value) checksum ^= c;
                if (checksum == Convert.ToInt32(match.Groups[2].Value, 16)) sentences.Add(match.Value);
            }
            string rest = buffer.Substring(end);
            int start = rest.LastIndexOf('$');
            // A tail without '$' is noise; an overlong one is corrupt (sentences are <= 82 chars).
            rest = start < 0 || rest.Length - start > 256 ? "" : rest.Substring(start);
            return new NmeaSplit(sentences, rest);
        }

        internal static int? Integer(string? text, int min, int max)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)) return null;
            if (value < min || value > max) return null;
            return value;
        }

        internal static double? Decimal(string? text)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return null;
            return double.IsFinite(value) ? value : null;
        }
    }

    // Parser state: GSV cycles per talker and signal (Views / Pending), GSA per talker and system ID
    // (Used) and the latest GSA / GGA / RMC fix values (Fix).
    public sealed class NmeaSatelliteState
    {
        private sealed record View(DateTimeOffset Received, IReadOnlyList<NmeaSatellite> Satellites);

        private sealed class Pending
        {
            public required int Total { get; init; }
            public int Next { get; set; } = 1;
            public List<NmeaSatellite> Satellites { get; } = new List<NmeaSatellite>();
        }

        private sealed record UsedEntry(DateTimeOffset Received, IReadOnlyList<string> Keys);

        private sealed record FixValues
        {
            public DateTimeOffset Received { get; init; }
            public string Talker { get; init; } = "";
            public string? Dimension { get; init; }
            public string? Quality { get; init; }
            public int? SatellitesUsed { get; init; }
            public double? AltitudeMslM { get; init; }
            public double? GeoidSeparationM { get; init; }
            public bool? Valid { get; init; }
            public string? Mode { get; init; }
        }

        // GSA system ID (NMEA 4.10) -> talker of that constellation.
        private static readonly Dictionary<string, string> SystemTalkers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["1"] = "GP",
            ["2"] = "GL",
            ["3"] = "GA",
            ["4"] = "GB",
            ["5"] = "GQ",
            ["6"] = "GI",
        };

        // The L860-GL swaps GSA system IDs 4 and 5 (QZSS 195 under 4, BeiDou 21 under 5; seen 2026-09-28),
        // so a used satellite not in view under its nominal system is matched to the other of this pair.
        private static readonly Dictionary<string, string> SwappedSystems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BeiDou"] = "QZSS",
            ["QZSS"] = "BeiDou",
        };

        // GGA fix quality and GSA fix type codes.
        private static readonly Dictionary<string, string> FixQualities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["0"] = "Invalid",
            ["1"] = "GPS",
            ["2"] = "DGPS",
            ["3"] = "PPS",
            ["4"] = "RTK fixed",
            ["5"] = "RTK float",
            ["6"] = "Estimated",
            ["7"] = "Manual",
            ["8"] = "Simulation",
        };

        private static readonly Dictionary<string, string> FixDimensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["1"] = "NoFix",
            ["2"] = "2D",
            ["3"] = "3D",
        };

        // RMC mode indicator (NMEA 2.3+; F / P / R from 4.0).
        private static readonly Dictionary<string, string> FixModes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = "Autonomous",
            ["D"] = "Differential",
            ["E"] = "Estimated",
            ["F"] = "RTK float",
            ["M"] = "Manual",
            ["N"] = "Not valid",
            ["P"] = "Precise",
            ["R"] = "RTK fixed",
            ["S"] = "Simulator",
        };

        private static readonly Regex SentenceType = new Regex("^(?<talker>[A-Z]{2})(?<type>GSV|GSA|GGA|RMC)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly Dictionary<string, View> views = new Dictionary<string, View>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, UsedEntry> used = new Dictionary<string, UsedEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FixValues> fix = new Dictionary<string, FixValues>(StringComparer.OrdinalIgnoreCase);

        // Label for an NMEA code; an unlisted code is kept visible instead of being dropped.
        private static string? CodeLabel(Dictionary<string, string> labels, string? code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            return labels.TryGetValue(code, out string? label) ? label : $"Unknown ({code})";
        }

        private static bool Is(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // Stores the fix values of one sentence type. GN sentences describe the combined
        // multi-constellation solution, so a GP duplicate does not replace a recent GN one.
        private void SetFixSource(string source, string talker, DateTimeOffset now, FixValues values)
        {
            if (fix.TryGetValue(source, out FixValues? previous) && Is(previous.Talker, "GN") && !Is(talker, "GN") &&
                (now - previous.Received).TotalSeconds <= Nmea.MaxAgeSeconds) return;
            fix[source] = values with { Received = now, Talker = talker };
        }

        // Applies one checksum-valid sentence received at now. A GSV cycle (message 1..N) replaces
        // the previous one only when complete and in order. GGA and RMC contribute only the fix
        // fields (never the position); other sentence types are ignored.
        public void Add(string sentence, DateTimeOffset now)
        {
            string[] fields = sentence.Substring(1, sentence.LastIndexOf('*') - 1).Split(',');
            Match m = SentenceType.Match(fields[0]);
            if (!m.Success) return;
            string talker = m.Groups["talker"].Value;
            string type = m.Groups["type"].Value.ToUpperInvariant();
            string? F(int i) => i >= 0 && i < fields.Length ? fields[i] : null;
            if (type == "GGA")
            {
                // 6 quality, 7 satellites used, 9-10 altitude above mean sea level, 11-12 geoid separation.
                if (fields.Length < 13) return;
                SetFixSource("GGA", talker, now, new FixValues
                {
                    Quality = CodeLabel(FixQualities, fields[6]),
                    SatellitesUsed = Nmea.Integer(fields[7], 0, 99),
                    AltitudeMslM = Is(fields[10], "M") ? Nmea.Decimal(fields[9]) : null,
                    GeoidSeparationM = Is(fields[12], "M") ? Nmea.Decimal(fields[11]) : null,
                });
                return;
            }
            if (type == "RMC")
            {
                // 2 status (A = valid, V = warning), 12 mode indicator (NMEA 2.3+).
                if (fields.Length < 3) return;
                SetFixSource("RMC", talker, now, new FixValues
                {
                    Valid = Is(fields[2], "A") ? true : Is(fields[2], "V") ? false : null,
                    Mode = fields.Length > 12 ? CodeLabel(FixModes, fields[12]) : null,
                });
                return;
            }
            if (type == "GSA")
            {
                if (fields.Length < 18) return;
                SetFixSource("GSA", talker, now, new FixValues { Dimension = CodeLabel(FixDimensions, fields[2]) });
                string systemId = fields.Length > 18 ? fields[18] : "";
                string systemTalker = SystemTalkers.TryGetValue(systemId, out string? t) ? t : talker;
                List<string> keys = fields.Skip(3).Take(12).Select(f => Nmea.Integer(f, 1, 999))
                    .Where(id => id is not null)
                    .Select(id => $"{Nmea.SatelliteSystem(systemTalker, id!.Value)}:{id.Value.ToString(CultureInfo.InvariantCulture)}")
                    .ToList();
                used[$"{talker}:{systemId}"] = new UsedEntry(now, keys);
                return;
            }
            int dataCount = fields.Length - 4;
            int? total = Nmea.Integer(F(1), 1, 99);
            int? number = Nmea.Integer(F(2), 1, 99);
            if (dataCount < 0 || (dataCount % 4) > 1 || total is null || number is null || number > total) return;
            string signal = dataCount % 4 == 1 ? fields[^1] : "";
            string key = $"{talker}:{signal}";
            if (number == 1) pending[key] = new Pending { Total = total.Value };
            if (!pending.TryGetValue(key, out Pending? cycle) || cycle.Total != total || cycle.Next != number)
            {
                pending.Remove(key);
                return;
            }
            for (int i = 4; i + 3 < fields.Length; i += 4)
            {
                int? id = Nmea.Integer(fields[i], 1, 999);
                if (id is null) continue;
                cycle.Satellites.Add(new NmeaSatellite(Nmea.SatelliteSystem(talker, id.Value), id.Value, signal,
                    Nmea.Integer(fields[i + 1], 0, 90), Nmea.Integer(fields[i + 2], 0, 359), Nmea.Integer(fields[i + 3], 0, 99), false));
            }
            cycle.Next++;
            if (number == total)
            {
                views[key] = new View(now, cycle.Satellites.ToArray());
                pending.Remove(key);
            }
        }

        // Key ("System:Id") of a GSA satellite: the nominal one, or its swapped pair when only that is in view.
        private static string ResolveUsedKey(string key, HashSet<string> inView)
        {
            if (inView.Contains(key)) return key;
            string[] parts = key.Split(':', 2);
            if (parts.Length == 2 && SwappedSystems.TryGetValue(parts[0], out string? other))
            {
                string swapped = $"{other}:{parts[1]}";
                if (inView.Contains(swapped)) return swapped;
            }
            return key;
        }

        private static void Expire<T>(Dictionary<string, T> table, Func<T, DateTimeOffset> received, DateTimeOffset now, int maxAgeSeconds)
        {
            foreach (string key in table.Keys.ToList())
            {
                if ((now - received(table[key])).TotalSeconds > maxAgeSeconds) table.Remove(key);
            }
        }

        // Current satellites (one row per satellite and signal) and the number of distinct satellites
        // used in the fix. Cycles not refreshed for maxAgeSeconds are dropped, so a constellation
        // that stops reporting disappears. GPGSA and GNGSA listing the same satellite count once.
        // GSA satellites are matched to the views with ResolveUsedKey (BeiDou / QZSS swap).
        // Fix values age out the same way.
        public NmeaReport Report(DateTimeOffset now, int maxAgeSeconds = Nmea.MaxAgeSeconds)
        {
            Expire(views, v => v.Received, now, maxAgeSeconds);
            Expire(used, u => u.Received, now, maxAgeSeconds);
            Expire(fix, f => f.Received, now, maxAgeSeconds);
            HashSet<string> inView = new HashSet<string>();
            foreach (View view in views.Values)
            {
                foreach (NmeaSatellite s in view.Satellites) inView.Add(SatelliteKey(s));
            }
            HashSet<string> usedKeys = new HashSet<string>();
            foreach (UsedEntry entry in used.Values)
            {
                foreach (string key in entry.Keys) usedKeys.Add(ResolveUsedKey(key, inView));
            }
            // Used in the fix first, then strongest, then highest; a missing SNR or elevation sorts last.
            // Remaining ties: constellation order, ID, signal.
            List<NmeaSatellite> satellites = views.Values.SelectMany(v => v.Satellites)
                .Select(s => s with { Used = usedKeys.Contains(SatelliteKey(s)) })
                .OrderByDescending(s => s.Used)
                .ThenByDescending(s => s.SnrDbHz ?? -1)
                .ThenByDescending(s => s.ElevationDeg ?? -1)
                .ThenBy(s => SystemOrder(s.System))
                .ThenBy(s => s.Id)
                .ThenBy(s => s.Signal, StringComparer.OrdinalIgnoreCase)
                .ToList();
            fix.TryGetValue("GSA", out FixValues? gsa);
            fix.TryGetValue("GGA", out FixValues? gga);
            fix.TryGetValue("RMC", out FixValues? rmc);
            NmeaFix fixState = new NmeaFix(gsa?.Dimension, gga?.Quality, gga?.SatellitesUsed, gga?.AltitudeMslM,
                gga?.GeoidSeparationM, rmc?.Valid, rmc?.Mode);
            return new NmeaReport(satellites, usedKeys.Count, fixState);
        }

        private static string SatelliteKey(NmeaSatellite s) => $"{s.System}:{s.Id.ToString(CultureInfo.InvariantCulture)}";

        private static int SystemOrder(string system)
        {
            for (int i = 0; i < Nmea.Systems.Count; i++)
            {
                if (Nmea.Systems[i] == system) return i;
            }
            return -1;
        }
    }
}
