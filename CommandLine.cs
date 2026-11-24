using System.Globalization;
using System.Text.RegularExpressions;

namespace WwanProbe
{
    // Options as "-Name value" or "-Name:value" (PowerShell-style parameter binding): names are
    // case-insensitive and may be shortened to an unambiguous prefix, Interval / Count / CsvPath / AtPort
    // also bind by position, and an unknown or misspelled option is an error instead of being ignored.
    public sealed class CommandLine
    {
        private sealed record Parameter(string Name, bool IsSwitch, params string[] Aliases);

        private static readonly Parameter[] Parameters =
        {
            new Parameter("Interval", false),
            new Parameter("Count", false),
            new Parameter("CsvPath", false),
            new Parameter("AtPort", false),
            new Parameter("Gps", true),
            new Parameter("Nmea", true, "Nema"),
            new Parameter("Help", true, "?", "h"),
        };

        private static readonly string[] Positional = { "Interval", "Count", "CsvPath", "AtPort" };
        private static readonly Regex AtPortPattern = new Regex(@"^(COM\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public const string Usage =
            "Usage: cs-wwan-probe [-Interval <seconds>] [-Count <samples>] [-CsvPath <file>] [-AtPort COM<n>] [-Gps] [-Nmea]\n" +
            "  -Interval  seconds between samples, 0..86400 (0 = back to back, about 1 s each)\n" +
            "  -Count     number of samples, 0 = until stopped\n" +
            "  -CsvPath   append one CSV row per sample to this file\n" +
            "  -AtPort    send AT commands over this COM port instead of the MBIM AT services\n" +
            "  -Gps       show Windows satellite fixes (local GNSS only)\n" +
            "  -Nmea      also list satellites from the GNSS driver (-Nema works too; asks UAC for a helper)";

        public int Interval { get; private set; }
        public int Count { get; private set; }
        public string CsvPath { get; private set; } = "";
        public string AtPort { get; private set; } = "";
        public bool Gps { get; private set; }
        public bool Nmea { get; private set; }
        public bool Help { get; private set; }

        // Parsed options, or null with error set.
        public static CommandLine? Parse(IReadOnlyList<string> args, out string? error)
        {
            CommandLine options = new CommandLine();
            HashSet<string> bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int position = 0;
            error = null;
            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                bool named = (arg.Length > 1 && arg[0] == '-' && !IsNumber(arg)) || arg == "/?";
                if (!named)
                {
                    while (position < Positional.Length && bound.Contains(Positional[position])) position++;
                    if (position >= Positional.Length)
                    {
                        error = $"A positional parameter cannot be found that accepts argument '{arg}'.";
                        return null;
                    }
                    if (!options.Bind(Positional[position], arg, out error)) return null;
                    bound.Add(Positional[position]);
                    continue;
                }
                string name = arg.TrimStart('-', '/');
                string? value = null;
                int colon = name.IndexOf(':');
                if (colon >= 0)
                {
                    value = name.Substring(colon + 1);
                    name = name.Substring(0, colon);
                }
                Parameter? parameter = Resolve(name, out error);
                if (parameter is null) return null;
                if (!bound.Add(parameter.Name))
                {
                    error = $"Cannot bind parameter because parameter '{parameter.Name}' is specified more than once.";
                    return null;
                }
                if (parameter.IsSwitch)
                {
                    if (!options.BindSwitch(parameter.Name, value, out error)) return null;
                    continue;
                }
                if (value is null)
                {
                    if (i + 1 >= args.Count)
                    {
                        error = $"Missing an argument for parameter '{parameter.Name}'.";
                        return null;
                    }
                    value = args[++i];
                }
                if (!options.Bind(parameter.Name, value, out error)) return null;
            }
            return options;
        }

        private static bool IsNumber(string arg) => long.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

        private static Parameter? Resolve(string name, out string? error)
        {
            error = null;
            Parameter? exact = Parameters.FirstOrDefault(p => Names(p).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)));
            if (exact is not null) return exact;
            List<Parameter> matches = name.Length == 0 ? new List<Parameter>()
                : Parameters.Where(p => Names(p).Any(n => n.StartsWith(name, StringComparison.OrdinalIgnoreCase))).ToList();
            if (matches.Count == 1) return matches[0];
            error = matches.Count == 0
                ? $"A parameter cannot be found that matches parameter name '{name}'."
                : $"Parameter cannot be processed because the parameter name '{name}' is ambiguous. Possible matches include: {string.Join(" ", matches.Select(m => "-" + m.Name))}.";
            return null;
        }

        private static IEnumerable<string> Names(Parameter p) => new[] { p.Name }.Concat(p.Aliases);

        private bool BindSwitch(string name, string? value, out string? error)
        {
            error = null;
            bool on;
            switch (value?.Trim().TrimStart('$').ToUpperInvariant())
            {
                case null:
                case "TRUE":
                case "1":
                    on = true;
                    break;
                case "FALSE":
                case "0":
                    on = false;
                    break;
                default:
                    error = $"Cannot process argument transformation on parameter '{name}'. Cannot convert value \"{value}\" to type \"Switch\".";
                    return false;
            }
            switch (name)
            {
                case "Gps": Gps = on; break;
                case "Nmea": Nmea = on; break;
                case "Help": Help = on; break;
            }
            return true;
        }

        private bool Bind(string name, string value, out string? error)
        {
            error = null;
            switch (name)
            {
                case "Interval":
                    if (!Integer(name, value, 0, 86400, out int interval, out error)) return false;
                    Interval = interval;
                    return true;
                case "Count":
                    if (!Integer(name, value, 0, int.MaxValue, out int count, out error)) return false;
                    Count = count;
                    return true;
                case "CsvPath":
                    CsvPath = value;
                    return true;
                case "AtPort":
                    if (!AtPortPattern.IsMatch(value))
                    {
                        error = $"Cannot validate argument on parameter 'AtPort'. The argument \"{value}\" does not match the \"^(COM\\d+)?$\" pattern.";
                        return false;
                    }
                    AtPort = value;
                    return true;
            }
            error = $"A parameter cannot be found that matches parameter name '{name}'.";
            return false;
        }

        private static bool Integer(string name, string value, int min, int max, out int result, out string? error)
        {
            error = null;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            {
                error = $"Cannot process argument transformation on parameter '{name}'. Cannot convert value \"{value}\" to type \"System.Int32\".";
                return false;
            }
            if (result < min || result > max)
            {
                error = string.Format(CultureInfo.InvariantCulture,
                    "Cannot validate argument on parameter '{0}'. The {1} argument is outside the allowed range of {2} to {3}.", name, result, min, max);
                return false;
            }
            return true;
        }
    }
}
