namespace WwanProbe.Domain
{
    // 2G/3G downgrade detection (pure, no I/O).
    // Jamming 4G/5G to push a device onto 2G/3G (where a fake base station is easier) is a known
    // attack. All inputs are plain values so the rules can be tested without a modem.
    // In Japan no carrier runs 2G, and all 3G networks were shut down by 2026-03-31
    // (au 2022-03, SoftBank 2024-07, docomo FOMA 2026-03), so any 2G/3G cell there is suspect.
    internal enum DowngradeLevel
    {
        None,
        Warning,
        Alert,
    }

    // Level = Alert (registered/serving on 2G/3G), Warning (2G/3G cells visible) or None;
    // Reasons = human readable evidence.
    internal sealed record DowngradeFinding(DowngradeLevel Level, IReadOnlyList<string> Reasons);

    internal static class Downgrade
    {
        // Capability rule over normalized RAT identities, independent of display labels.
        public static bool IsLegacyRatAllowed(IEnumerable<string>? allowedRats) => allowedRats is not null && allowedRats.Any(Rat.IsLegacy);

        // registeredRats:     normalized registered RAT identities (GSM/UMTS/CDMA/LTE/NR)
        // legacyServingCount: WinRT GSM/UMTS/CDMA serving cells
        // atCells:            common AT cells (e.g. from AT+XMCI), null when unavailable
        // atSource:           label of the AT cell list shown in the reasons (e.g. "XMCI")
        public static DowngradeFinding Evaluate(IReadOnlyList<string> registeredRats, int legacyServingCount,
            IReadOnlyList<AtCell>? atCells = null, string? atSource = "AT")
        {
            List<string> alert = [];
            List<string> warning = [];

            List<string> legacyClass = [.. registeredRats.Where(Rat.IsLegacy)];
            bool hasModern = registeredRats.Any(Rat.IsModern);
            if (legacyClass.Count > 0 && !hasModern) alert.Add($"registered on {string.Join('/', legacyClass)}");
            if (legacyServingCount > 0) alert.Add(FormattableString.Invariant($"{legacyServingCount} 2G/3G serving cell(s) (WinRT)"));
            // LTE / NR cells are not legacy (CDMA is not reported by any AT profile).
            foreach (AtCell cell in atCells ?? [])
            {
                if (cell is null || !(Rat.Is(cell.Rat, Rat.Gsm) || Rat.Is(cell.Rat, Rat.Umts))) continue;
                string channel = cell.Channel is long ch ? ch.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
                string text = $"{cell.Rat} {(cell.Role == CellRole.Serving ? "serving" : "neighbor")} ch:{channel} ({atSource})";
                if (cell.Role == CellRole.Serving) alert.Add(text);
                else warning.Add(text);
            }

            DowngradeLevel level = alert.Count > 0 ? DowngradeLevel.Alert
                : warning.Count > 0 ? DowngradeLevel.Warning : DowngradeLevel.None;
            return new DowngradeFinding(level, [.. alert, .. warning]);
        }
    }
}
