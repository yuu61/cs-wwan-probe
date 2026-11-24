using System.Globalization;
using WwanProbe.Domain;

namespace WwanProbe.Infrastructure.Modem
{
    // Normalized observations from WinRT and AT for one sample.
    // AT values are null when unavailable (AtError holds the reason when the session failed);
    // Neighbors fall back to WinRT when the AT channel has no neighbor list (empty = none reported).
    internal sealed class ModemObservation
    {
        private static readonly Dictionary<string, string> DataClassRats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Gprs"] = Rat.Gsm,
            ["Edge"] = Rat.Gsm,
            ["Umts"] = Rat.Umts,
            ["Hsdpa"] = Rat.Umts,
            ["Hsupa"] = Rat.Umts,
            ["Cdma1xRtt"] = Rat.Cdma,
            ["Cdma1xEvdo"] = Rat.Cdma,
            ["Cdma1xEvdoRevA"] = Rat.Cdma,
            ["Cdma1xEvdv"] = Rat.Cdma,
            ["Cdma3xRtt"] = Rat.Cdma,
            ["Cdma1xEvdoRevB"] = Rat.Cdma,
            ["CdmaUmb"] = Rat.Cdma,
            ["Lte"] = Rat.Lte,
            ["NewRadioNonStandalone"] = Rat.Nr,
            ["NewRadioStandalone"] = Rat.Nr,
        };

        public string Timestamp { get; set; } = "";
        public string ProviderName { get; set; } = "";
        public string ProviderId { get; set; } = "";
        public string DataClass { get; set; } = "";
        public IReadOnlyList<string> RegisteredRats { get; set; } = [];
        public string Apn { get; set; } = "";
        public double? BwMbps { get; set; }
        public double? RxKB { get; set; }
        public double? TxKB { get; set; }
        public IReadOnlyList<ServingCell> Serving { get; set; } = [];
        public IReadOnlyList<UmtsCell> Umts { get; set; } = [];
        public IReadOnlyList<LteNeighbor>? Neighbors { get; set; }
        public double? TempC { get; set; }
        public double? Rssnr { get; set; }  // dB, assuming 0.5 dB steps on Intel (unit undocumented)
        public CarrierAggregation? Ca { get; set; }
        public string? AtError { get; set; }
        public string? TrafficError { get; set; }
        public int LegacyServingCount { get; set; }
        public IReadOnlyList<AtCell>? AtCells { get; set; }
        public string? AtSource { get; set; }
        public string? Error { get; set; }

        // Normalize WinRT DataClasses flags ("Lte, NewRadioNonStandalone") into RAT identities.
        public static IReadOnlyList<string> RatsFromDataClass(string? dataClass)
        {
            return [.. (dataClass ?? "").Split(',')
                .Select(n => DataClassRats.TryGetValue(n.Trim(), out string? rat) ? rat : null)
                .Where(r => r is not null).Select(r => r!).Distinct()];
        }

        // LTE neighbor cells reported by WinRT (MBIM_CID_BASE_STATIONS_INFO), or null when there are none.
        // Returning none is allowed by the spec (the L860-GL never reports any), so an empty list is
        // treated as "unavailable" and does not hide the AT error.
        public static List<LteNeighbor>? WinRtLteNeighbors(IEnumerable<LteCellInfo>? cells)
        {
            List<LteNeighbor> neighbors = [];
            foreach (LteCellInfo cell in cells ?? [])
            {
                if (cell is null) continue;
                int? rsrp = SignalConversion.FromWinRtRsrp(cell.Rsrp);
                if (rsrp is not int r || cell.Channel is not long earfcn) continue;
                neighbors.Add(new LteNeighbor(r, SignalConversion.FromWinRtRsrq(cell.Rsrq), LteBand.FromEarfcn(earfcn), earfcn, cell.Pci));
            }
            return neighbors.Count == 0 ? null : neighbors;
        }

        // at: the startup AT detection; null skips the AT values.
        public static ModemObservation Observe(IModemDevice modem, ModemAt? at, ITrafficCounter traffic)
        {
            ModemObservation o = new()
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                AtSource = at?.Profile?.Source,
            };
            try
            {
                ModemNetworkInfo network = modem.GetNetwork();
                o.ProviderName = network.ProviderName;
                o.ProviderId = network.ProviderId;
                o.DataClass = network.DataClass;
                o.RegisteredRats = RatsFromDataClass(o.DataClass);
                o.Apn = network.Apn;

                CellsInfo cells = modem.GetCellsInfo();

                TrafficReading t = traffic.Read();
                o.BwMbps = t.BwMbps;
                o.RxKB = t.RxKB;
                o.TxKB = t.TxKB;
                o.TrafficError = t.Error;

                o.Serving = [.. cells.ServingLte.Select(c => new ServingCell
                {
                    RsrpDbm = SignalConversion.FromWinRtRsrp(c.Rsrp),
                    RsrpRaw = c.Rsrp,
                    RsrqDb = SignalConversion.FromWinRtRsrq(c.Rsrq),
                    RsrqRaw = c.Rsrq,
                    Band = LteBand.FromEarfcn(c.Channel),
                    Earfcn = c.Channel,
                    Pci = c.Pci,
                    CellId = c.CellId,
                    Tac = c.Tac,
                    Ta = c.Ta,
                    Provider = string.IsNullOrEmpty(c.ProviderId) ? o.ProviderId : c.ProviderId,
                })];

                // WinRT lacks temperature, SINR and CA info (and neighbors on the L860-GL).
                try
                {
                    AtStatus status = (at ?? ModemAt.NotInitialized).ReadStatus(modem);
                    o.Neighbors = status.Neighbors;
                    o.TempC = status.TempC;
                    o.Rssnr = status.Rssnr;
                    o.Ca = status.Ca;
                    o.AtCells = status.Cells;
                }
                catch (Exception ex)
                {
                    o.AtError = ex.Message;
                }
                o.Neighbors ??= WinRtLteNeighbors(cells.NeighborLte);
                o.LegacyServingCount = cells.LegacyServingCount;
                o.Umts = cells.ServingUmts;
            }
            catch (Exception ex)
            {
                o.Error = ex.Message;
            }
            return o;
        }
    }
}
