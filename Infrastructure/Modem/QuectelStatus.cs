using WwanProbe.Domain;

namespace WwanProbe.Infrastructure.Modem
{
    // AT status response parsing for Quectel (Qualcomm based) modems (pure, no I/O).
    // Reference: Quectel RG50xQ&RM5xxQ Series AT Commands Manual V1.2 (see docs/modem-support.md).
    // Not verified on hardware. Every parser returns null when the response is missing, "ERROR",
    // or carries no usable value. Values "-" mean invalid. Cells use the common AT cell shape (AtProfile).
    internal sealed record QuectelCa(int Cells, IReadOnlyList<double?> BandwidthsMHz, double? PccRssnr);

    internal static partial class QuectelStatus
    {
        private static readonly string[] ServingRats = ["LTE", "WCDMA", "GSM", "NR5G-SA", "NR5G-NSA"];

        private static bool Eq(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static int? LteDbm(string? text)
        {
            long? v = AtResponse.ParseInt(text);
            return v is not long n || n < -140 || n > -44 ? null : (int)n;
        }

        private static double? LteDb(string? text)
        {
            long? v = AtResponse.ParseInt(text);
            return v is not long n || n < -20 || n > -3 ? null : (int)n;
        }

        // AT+QENG="servingcell" -> serving cells. Layouts (manual 5.20):
        //   "servingcell",<state>,"LTE",<is_tdd>,<MCC>,<MNC>,<cellID>,<PCID>,<earfcn>,<band>,<UL_bw>,<DL_bw>,<TAC>,<RSRP>,<RSRQ>,...
        //   EN-DC: "servingcell",<state> / "LTE",<is_tdd>,... (same LTE fields) / "NR5G-NSA",<MCC>,<MNC>,<PCID>,<RSRP>,<SINR>,<RSRQ>,<ARFCN>,...
        //   "servingcell",<state>,"NR5G-SA",<duplex>,<MCC>,<MNC>,<cellID>,<PCID>,<TAC>,<ARFCN>,...
        //   "servingcell",<state>,"WCDMA",<MCC>,<MNC>,<LAC>,<cellID>,<uarfcn>,...
        //   "servingcell",<state>,"GSM",<MCC>,<MNC>,<LAC>,<cellID>,<BSIC>,<arfcn>,... (EC2x manual; not in RM5xx)
        // Fields after RSRQ differ between module families (and the SINR formula conflicts), so they are not read.
        // LTE cells also carry DlBandwidthMHz (from the index 0..5) for the CA fallback.
        public static List<AtCell>? ParseQengServing(string? response)
        {
            List<string[]>? lines = AtResponse.GetLines(response, "QENG");
            if (lines is null) return null;
            List<AtCell> cells = [];
            foreach (string[] f in lines)
            {
                int r = Array.FindIndex(f, x => ServingRats.Any(s => Eq(x, s)));
                if (r < 0) continue;
                string? At(int i) => f.At(r + i);

                switch (f[r].ToUpperInvariant())
                {
                    case "LTE":
                        long? bw = AtResponse.ParseInt(At(9));
                        cells.Add(new AtCell
                        {
                            Rat = Rat.Lte,
                            Role = CellRole.Serving,
                            Channel = AtResponse.ParseInt(At(6)),
                            Earfcn = AtResponse.ParseInt(At(6)),
                            Pci = AtResponse.ParseInt(At(5)),
                            CellId = AtResponse.ParseInt(At(4), hex: true),
                            Tac = AtResponse.ParseInt(At(10), hex: true),
                            RsrpDbm = LteDbm(At(11)),
                            RsrqDb = LteDb(At(12)),
                            DlBandwidthMHz = AtResponse.IndexToMHz(bw),
                        });
                        break;
                    case "WCDMA":
                        cells.Add(new AtCell { Rat = Rat.Umts, Role = CellRole.Serving, Channel = AtResponse.ParseInt(At(5)) });
                        break;
                    case "GSM":
                        cells.Add(new AtCell { Rat = Rat.Gsm, Role = CellRole.Serving, Channel = AtResponse.ParseInt(At(6)) });
                        break;
                    case "NR5G-SA":
                    case "NR5G-NSA":
                        cells.Add(new AtCell { Rat = Rat.Nr, Role = CellRole.Serving, Channel = AtResponse.ParseInt(At(7)) });
                        break;
                    default:
                        // Unknown RAT: not a cell this monitor can describe.
                        break;
                }
            }
            return cells;
        }

        // AT+QENG="neighbourcell" -> neighbor cells.
        //   "neighbourcell intra"|"neighbourcell inter","LTE",<earfcn>,<PCID>,<RSRQ>,<RSRP>,...   (LTE mode)
        //   "neighbourcell","LTE",<earfcn>,<PCID>,<RSRP>,<RSRQ>,...                              (WCDMA mode, RSRP first)
        //   "neighbourcell","WCDMA",<uarfcn>,...
        //   "neighbourcell","GSM",... (channel position not documented in the RM5xx manual -> null)
        // Inter-frequency entries without a measurement ("-") are kept for the RAT check but have no RSRP.
        public static List<AtCell>? ParseQengNeighbour(string? response)
        {
            List<string[]>? lines = AtResponse.GetLines(response, "QENG");
            if (lines is null) return null;
            List<AtCell> cells = [];
            foreach (string[] f in lines)
            {
                if (f.Length < 3 || !f[0].StartsWith("neighbourcell", StringComparison.OrdinalIgnoreCase)) continue;
                switch (f[1].ToUpperInvariant())
                {
                    case "LTE":
                        bool rsrpFirst = Eq(f[0], "neighbourcell");
                        cells.Add(new AtCell
                        {
                            Rat = Rat.Lte,
                            Role = CellRole.Neighbor,
                            Channel = AtResponse.ParseInt(f[2]),
                            Earfcn = AtResponse.ParseInt(f[2]),
                            Pci = AtResponse.ParseInt(f.At(3)),
                            RsrpDbm = LteDbm(f.At(rsrpFirst ? 4 : 5)),
                            RsrqDb = LteDb(f.At(rsrpFirst ? 5 : 4)),
                        });
                        break;
                    case "WCDMA":
                        cells.Add(new AtCell { Rat = Rat.Umts, Role = CellRole.Neighbor, Channel = AtResponse.ParseInt(f[2]) });
                        break;
                    case "GSM":
                        cells.Add(new AtCell { Rat = Rat.Gsm, Role = CellRole.Neighbor, Channel = null });
                        break;
                    default:
                        // Unknown RAT (e.g. NR neighbors): skipped.
                        break;
                }
            }
            return cells;
        }

        // AT+QCAINFO -> Cells / BandwidthsMHz / PccRssnr for the LTE carriers, or null without a PCC line.
        //   "PCC",<freq>,<bandwidth>,<band>,<pcell_state>,<PCID>,<RSRP>,<RSRQ>,<RSSI>,<RSSNR>
        //   "SCC",<freq>,<bandwidth>,<band>,<scell_state>,<PCID>,<RSRP>,<RSRQ>,<RSSI>,<RSSNR>
        // bandwidth is in resource blocks; scell_state 0 = deconfigured (not counted); RSSNR is in dB (-10..30).
        // Carriers whose <band> is not "LTE BAND n" (NR) are skipped.
        public static QuectelCa? ParseQcainfo(string? response)
        {
            List<string[]>? lines = AtResponse.GetLines(response, "QCAINFO");
            if (lines is null) return null;
            string[]? pcc = null;
            List<double?> bandwidths = [];
            foreach (string[] f in lines)
            {
                if (f.Length < 5 || !f[3].StartsWith("LTE BAND", StringComparison.OrdinalIgnoreCase)) continue;
                double? mhz = AtResponse.RbToMHz(AtResponse.ParseInt(f[2]));
                if (Eq(f[0], "PCC"))
                {
                    pcc = f;
                    bandwidths.Insert(0, mhz);
                }
                else if (Eq(f[0], "SCC") && AtResponse.ParseInt(f[4]) != 0)
                {
                    bandwidths.Add(mhz);
                }
            }
            if (pcc is null) return null;
            long? rssnr = pcc.Length >= 10 ? AtResponse.ParseInt(pcc[9]) : null;
            return new QuectelCa(bandwidths.Count, bandwidths, rssnr is long s && s >= -10 && s <= 30 ? s : null);
        }

        // AT+QSINR -> "+QSINR: <PRX>,<DRX>,<RX2>,<RX3>,<sysmode>" (dB). Returns the PRX path in LTE mode.
        public static double? ParseQsinr(string? response)
        {
            string[]? f = AtResponse.GetField(response, "QSINR");
            if (f is null || f.Length < 5 || !Eq(f[4].Trim('"'), "LTE")) return null;
            long? v = AtResponse.ParseInt(f[0]);
            return v is not long n || n < -20 || n > 30 ? null : n;
        }

        // AT+QTEMP -> hottest sensor in Celsius. Two layouts exist:
        //   RM5xx: one line per sensor, +QTEMP: "<sensor>","<temp>"
        //   EM12/EG25 family: +QTEMP: <pmic_temp>,<xo_temp>,<pa_temp>
        // Out-of-range readings (absent sensors) are ignored.
        public static int? ParseQtemp(string? response)
        {
            List<string[]>? lines = AtResponse.GetLines(response, "QTEMP");
            if (lines is null) return null;
            int? max = null;
            foreach (string text in lines.SelectMany(f => f))
            {
                long? v = AtResponse.ParseInt(text);
                if (v is not long n || n < -40 || n > 125) continue;
                if (max is null || n > max) max = (int)n;
            }
            return max;
        }

        // AT+QNWPREFCFG="mode_pref" / "gw_band" / "lte_band" / "nr5g_band" -> RatConfig (see IntelStatus.ParseXact).
        // mode_pref is "AUTO" (every RAT the module supports: WCDMA & LTE & NR) or RATs joined with ':'.
        // No preferred RAT is read (rat_acq_order is a priority list), so Preferred is null.
        public static RatConfig? ParseQnwprefcfg(IReadOnlyDictionary<string, string?> responses)
        {
            string? Value(string name)
            {
                foreach (string? r in responses.Values)
                {
                    foreach (string[] f in AtResponse.GetLines(r, "QNWPREFCFG") ?? [])
                    {
                        if (f.Length >= 2 && Eq(f[0], name)) return f[1];
                    }
                }
                return null;
            }

            string? mode = Value("mode_pref");
            if (string.IsNullOrEmpty(mode)) return null;
            // GSM is not in the RM5xx manual; mapped so a module that lists it still gets the 2G warning.
            Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase)
            {
                ["GSM"] = "2G",
                ["WCDMA"] = "3G",
                ["LTE"] = "4G",
                ["NR5G"] = "5G",
            };
            Dictionary<string, string> ratNames = new(StringComparer.OrdinalIgnoreCase)
            {
                ["GSM"] = Rat.Gsm,
                ["WCDMA"] = Rat.Umts,
                ["LTE"] = Rat.Lte,
                ["NR5G"] = Rat.Nr,
            };
            bool auto = Eq(mode, "AUTO");
            string[] parts = mode.Split(':');
            IReadOnlyList<string> allowedRats = auto
                ? new[] { Rat.Umts, Rat.Lte, Rat.Nr }
                : [.. parts.Select(p => ratNames.TryGetValue(p, out string? n) ? n : p)];
            string allowed = auto
                ? "3G+4G+5G (AUTO)"
                : string.Join('+', parts.Select(p => names.TryGetValue(p, out string? n) ? n : p)
                    .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase));
            int[] Bands(string name)
            {
                return [.. (Value(name) ?? "").Split(':')
                    .Select(t => AtResponse.ParseInt(t)).Where(n => n is not null).Select(n => (int)n!.Value)];
            }
            return new RatConfig
            {
                AllowedRats = allowedRats,
                Allowed = allowed,
                Preferred = null,
                GsmBands = [],
                UmtsBands = Bands("gw_band"),
                LteBands = Bands("lte_band"),
                NrBands = Bands("nr5g_band"),
            };
        }

    }
}
