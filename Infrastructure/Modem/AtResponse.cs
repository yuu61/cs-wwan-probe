using System.Globalization;
using System.Text.RegularExpressions;

namespace WwanProbe.Infrastructure.Modem
{
    // AT response helpers shared by every vendor's parsers (pure, no I/O).
    // Every helper returns null when the response is missing, not OK ("ERROR"), or the value is invalid.
    public static class AtResponse
    {
        // LTE bandwidth in resource blocks (QCAINFO, +GTCCINFO, +GTCAINFO) -> MHz.
        public static readonly IReadOnlyDictionary<long, double> LteRbBandwidthMHz = new Dictionary<long, double>
        {
            [6] = 1.4,
            [15] = 3,
            [25] = 5,
            [50] = 10,
            [75] = 15,
            [100] = 20,
        };

        // LTE bandwidth index (QENG <DL_bandwidth>, +XLEC) -> MHz.
        public static readonly IReadOnlyList<double> LteIndexBandwidthMHz = new double[] { 1.4, 3, 5, 10, 15, 20 };

        private static readonly Regex OkLine = new Regex(@"^OK\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex HexPrefixed = new Regex("^0x([0-9A-Fa-f]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // True when the response has a final "OK" line.
        public static bool IsOk(string? response) => !string.IsNullOrEmpty(response) && OkLine.IsMatch(response);

        // Fields of the first "+<Name>: a,b,c" line, or null.
        public static string[]? GetField(string? response, string name)
        {
            if (!IsOk(response)) return null;
            Match m = Regex.Match(response!, $@"^\+{Regex.Escape(name)}:\s*(.+?)\s*$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!m.Success) return null;
            return m.Groups[1].Value.Split(',').Select(f => f.Trim()).ToArray();
        }

        // Fields of every "+<Name>: ..." line (quotes stripped), or null when the response is not OK.
        // Lines of other commands and unprefixed lines are skipped.
        public static List<string[]>? GetLines(string? response, string name)
        {
            if (!IsOk(response)) return null;
            List<string[]> lines = new List<string[]>();
            foreach (Match m in Regex.Matches(response!, $@"^\+{Regex.Escape(name)}:\s*(.*?)\s*$", RegexOptions.Multiline))
            {
                lines.Add(m.Groups[1].Value.Split(',').Select(f => f.Trim().Trim('"')).ToArray());
            }
            return lines;
        }

        // Integer from an AT field, or null ("-", empty, or not a number). Accepts a "0x" hex prefix.
        public static long? ParseInt(string? text, bool hex = false)
        {
            string t = (text ?? "").Trim().Trim('"');
            Match m = HexPrefixed.Match(t);
            if (m.Success)
            {
                t = m.Groups[1].Value;
                hex = true;
            }
            if (hex)
            {
                return long.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long h) ? h : null;
            }
            return long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : null;
        }

        // Element i of a field list, or null when out of range.
        public static string? At(this string[] fields, int index) =>
            index >= 0 && index < fields.Length ? fields[index] : null;

        // Response of one command from Invoke results, or null when it was not sent.
        public static string? Get(this IReadOnlyDictionary<string, string?> responses, string command) =>
            responses.TryGetValue(command, out string? r) ? r : null;

        // RB count -> MHz, or null.
        public static double? RbToMHz(long? rb) =>
            rb is long r && LteRbBandwidthMHz.TryGetValue(r, out double mhz) ? mhz : null;

        // Bandwidth index -> MHz, or null.
        public static double? IndexToMHz(long? index) =>
            index is long i && i >= 0 && i < LteIndexBandwidthMHz.Count ? LteIndexBandwidthMHz[(int)i] : null;
    }
}
