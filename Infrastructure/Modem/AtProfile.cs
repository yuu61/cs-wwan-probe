using WwanProbe.Domain;

namespace WwanProbe.Infrastructure.Modem
{
    // Vendor AT command sets ("profiles") and their normalized results (pure, no I/O).
    // The profile is chosen at startup by sending each Probe command in order; the first answered with
    // OK wins (Intel first, so the tested L860-GL path is unchanged). Probes are test commands ("=?"),
    // which do not depend on the registration state. Only Intel was verified on hardware.
    //   Status = commands sent every sample (slowest / least important last: a timeout skips the rest)
    //   Config = commands sent once at startup for the RAT / band configuration
    //   Source = label of the cell list in 2G/3G downgrade reasons
    internal sealed record AtProfile(string Id, string Name, string Probe, string Source, IReadOnlyList<string> Status, IReadOnlyList<string> Config)
    {
        public const string IntelId = "Intel";
        public const string QuectelId = "Quectel";
        public const string FibocomGtId = "FibocomGt";

        public static readonly IReadOnlyList<AtProfile> All =
        [
            // XMCI=0 returns the stored measurements immediately; XMCI=1 waits for a fresh serving-cell
            // measurement and was seen to hang for >10 s on a weak cell.
            new AtProfile(IntelId, "Intel XMM (+X commands)", "AT+XMCI=?", "XMCI",
                ["AT+MTSM=1", "AT+XCESQ?", "AT+XLEC?", "AT+XMCI=0"],
                ["AT+XACT?"]),
            new AtProfile(QuectelId, "Quectel (+Q commands)", "AT+QENG=?", "QENG",
                ["AT+QTEMP", "AT+QCAINFO", "AT+QSINR", "AT+QENG=\"servingcell\"", "AT+QENG=\"neighbourcell\""],
                ["AT+QNWPREFCFG=\"mode_pref\"", "AT+QNWPREFCFG=\"gw_band\"", "AT+QNWPREFCFG=\"lte_band\"", "AT+QNWPREFCFG=\"nr5g_band\""]),
            new AtProfile(FibocomGtId, "Fibocom (+GT commands)", "AT+GTCAINFO=?", "GTCCINFO",
                ["AT+GTSENRDTEMP=1", "AT+GTCAINFO?", "AT+GTCCINFO?"],
                ["AT+GTACT?"]),
        ];

        public static AtProfile? Find(string? id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    // Per-sample AT status:
    //   Cells     = common AT cells for the downgrade check (null = unavailable)
    //   Neighbors = LTE neighbors (null = unavailable)
    //   TempC (C), Rssnr (dB), Ca (null = unavailable)
    internal sealed record AtStatus(IReadOnlyList<AtCell>? Cells, IReadOnlyList<LteNeighbor>? Neighbors, double? TempC, double? Rssnr, CarrierAggregation? Ca);

    internal static class AtStatusParser
    {
        // LTE neighbor cells from common AT cells; null when the cell list is unavailable.
        // Neighbors without an RSRP measurement or EARFCN are skipped.
        public static List<LteNeighbor>? GetLteNeighbors(IEnumerable<AtCell>? cells)
        {
            if (cells is null) return null;
            List<LteNeighbor> neighbors = [];
            foreach (AtCell cell in cells)
            {
                if (!Rat.Is(cell.Rat, Rat.Lte) || cell.Role != CellRole.Neighbor) continue;
                if (cell.RsrpDbm is not int rsrp || cell.Earfcn is not long earfcn) continue;
                neighbors.Add(new LteNeighbor(rsrp, cell.RsrqDb, LteBand.FromEarfcn(earfcn), earfcn, cell.Pci));
            }
            return neighbors;
        }

        // Single-carrier CA value from an LTE serving cell when no CA command answered.
        public static CarrierAggregation? GetSingleCarrierCa(IReadOnlyList<AtCell>? cells)
        {
            // The serving LTE cell when there is one, else any serving cell (no LTE carrier).
            AtCell? serving = cells?.FirstOrDefault(c => Rat.Is(c.Rat, Rat.Lte) && c.Role == CellRole.Serving)
                ?? cells?.FirstOrDefault(c => c.Role == CellRole.Serving);
            return serving switch
            {
                null => null,
                _ when Rat.Is(serving.Rat, Rat.Lte) => new CarrierAggregation(1, [serving.DlBandwidthMHz]),
                _ => new CarrierAggregation(0, []),
            };
        }

        // Per-sample status from the responses (command -> response) of profile.Status.
        public static AtStatus ParseStatus(AtProfile profile, IReadOnlyDictionary<string, string?> responses)
        {
            switch (profile.Id)
            {
                case AtProfile.IntelId:
                    {
                        string? xmci = responses.Get("AT+XMCI=0");
                        List<AtCell>? cells = AtResponse.IsOk(xmci) ? IntelStatus.ParseXmci(xmci) : null;
                        return new AtStatus(cells, GetLteNeighbors(cells),
                            IntelStatus.ParseMtsm(responses.Get("AT+MTSM=1")),
                            IntelStatus.ParseXcesq(responses.Get("AT+XCESQ?")),
                            IntelStatus.ParseXlec(responses.Get("AT+XLEC?")));
                    }
                case AtProfile.QuectelId:
                    {
                        List<AtCell>? serving = QuectelStatus.ParseQengServing(responses.Get("AT+QENG=\"servingcell\""));
                        List<AtCell>? neighbours = QuectelStatus.ParseQengNeighbour(responses.Get("AT+QENG=\"neighbourcell\""));
                        List<AtCell>? cells = serving is null && neighbours is null ? null
                            : [.. serving ?? [], .. neighbours ?? []];
                        // QENG's <SINR> scale differs between module families, so SINR comes from QCAINFO / QSINR (dB).
                        QuectelCa? ca = QuectelStatus.ParseQcainfo(responses.Get("AT+QCAINFO"));
                        double? rssnr = ca?.PccRssnr ?? QuectelStatus.ParseQsinr(responses.Get("AT+QSINR"));
                        CarrierAggregation? carriers = ca is not null ? new CarrierAggregation(ca.Cells, ca.BandwidthsMHz) : GetSingleCarrierCa(serving);
                        return new AtStatus(cells, GetLteNeighbors(neighbours),
                            QuectelStatus.ParseQtemp(responses.Get("AT+QTEMP")), rssnr, carriers);
                    }
                case AtProfile.FibocomGtId:
                    {
                        List<AtCell>? cells = FibocomStatus.ParseGtccinfo(responses.Get("AT+GTCCINFO?"));
                        AtCell? lte = cells?.FirstOrDefault(c => Rat.Is(c.Rat, Rat.Lte) && c.Role == CellRole.Serving);
                        CarrierAggregation? ca = FibocomStatus.ParseGtcainfo(responses.Get("AT+GTCAINFO?")) ?? GetSingleCarrierCa(cells);
                        return new AtStatus(cells, GetLteNeighbors(cells),
                            FibocomStatus.ParseGtsenrdtemp(responses.Get("AT+GTSENRDTEMP=1")), lte?.Rssnr, ca);
                    }
                default:
                    return new AtStatus(null, null, null, null, null);
            }
        }

        // RAT / band configuration from the responses of profile.Config, or null.
        public static RatConfig? ParseConfig(AtProfile profile, IReadOnlyDictionary<string, string?> responses)
        {
            return profile.Id switch
            {
                AtProfile.IntelId => IntelStatus.ParseXact(responses.Get("AT+XACT?")),
                AtProfile.QuectelId => QuectelStatus.ParseQnwprefcfg(responses),
                AtProfile.FibocomGtId => FibocomStatus.ParseGtact(responses.Get("AT+GTACT?")),
                _ => null,
            };
        }
    }
}
