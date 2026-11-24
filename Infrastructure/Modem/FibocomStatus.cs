using System.Globalization;
using System.Text.RegularExpressions;
using WwanProbe.Domain;

namespace WwanProbe.Infrastructure.Modem
{
    // AT status response parsing for Fibocom "GT" command modems (FM350-GL, MediaTek T700) (pure, no I/O).
    // Reference: FM350 AT Commands User Manual V2.10 (see docs/modem-support.md). Number bases of
    // +GTCCINFO fields were taken from real FM350 output (TAC / cell ID hex, EARFCN / PCI decimal).
    // Not verified on hardware. Every parser returns null when the response is missing or "ERROR".
    public static class FibocomStatus
    {
        private static readonly Regex GtccinfoPrefix = new Regex(@"^\+GTCCINFO:\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex GtccinfoData = new Regex(@"^[12],\d+,", RegexOptions.CultureInvariant);
        private static readonly Regex GtcainfoLine = new Regex(@"^\s*(?:\+GTCAINFO:\s*)?(PCC|SCC\s*\d*)\s*:\s*(.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex NrBand = new Regex(@"^50(\d+)$", RegexOptions.CultureInvariant);

        // +GTCCINFO rsrp / rsrq index -> dBm / dB, or null. rsrp 0 means "below -140 dBm or not detectable"
        // and is treated as not measured; 255 = unknown for both.
        private static int? RsrpIndex(string? text)
        {
            long? v = AtResponse.ParseInt(text);
            if (v is not long n || n <= 0) return null;
            return SignalConversion.RsrpIndexToDbm(n);
        }

        private static double? RsrqIndex(string? text)
        {
            long? v = AtResponse.ParseInt(text);
            if (v is not long n || n < 0) return null;
            return SignalConversion.RsrqIndexToDb(n);
        }

        // AT+GTCCINFO? -> cells. Data lines follow "+GTCCINFO:" without a prefix (optionally after
        // "LTE service cell:" style headings):
        //   <IsServiceCell 1|2>,<rat 2=WCDMA 4=LTE 9=NR>,<mcc>,<mnc>,<tac|lac hex>,<cellid hex>,<arfcn>,<pci>,...
        //   LTE serving:  ...,<band>,<bandwidth RB>,<rssnr_value>,<rxlev>,<rsrp idx>,<rsrq idx>
        //   LTE neighbor: ...,<bandwidth RB>,<rxlev>,<rsrp idx>,<rsrq idx>
        // rssnr_value is -100..100 in 0.5 dB steps (255 = unknown); rsrp / rsrq are 3GPP TS 36.133 indices.
        // LTE serving cells also carry Rssnr (dB) and DlBandwidthMHz.
        public static List<AtCell>? ParseGtccinfo(string? response)
        {
            if (!AtResponse.IsOk(response) || !response!.Contains("+GTCCINFO:", StringComparison.OrdinalIgnoreCase)) return null;
            List<AtCell> cells = new List<AtCell>();
            foreach (string raw in Regex.Split(response, "\r?\n"))
            {
                string line = GtccinfoPrefix.Replace(raw, "").Trim();
                if (!GtccinfoData.IsMatch(line)) continue;
                string[] f = line.Split(',').Select(x => x.Trim()).ToArray();
                if (f.Length < 8) continue;
                bool serving = f[0] == "1";
                CellRole role = serving ? CellRole.Serving : CellRole.Neighbor;
                long? channel = AtResponse.ParseInt(f[6]);
                switch (f[1])
                {
                    case "2":
                        cells.Add(new AtCell { Rat = Rat.Umts, Role = role, Channel = channel });
                        break;
                    case "9":
                        cells.Add(new AtCell { Rat = Rat.Nr, Role = role, Channel = channel });
                        break;
                    case "4":
                        int i = serving ? 12 : 10; // rsrp index position
                        if (f.Length <= i + 1) break;
                        AtCell cell = new AtCell
                        {
                            Rat = Rat.Lte,
                            Role = role,
                            Channel = channel,
                            Earfcn = channel,
                            Pci = AtResponse.ParseInt(f[7]),
                            Tac = AtResponse.ParseInt(f[4], hex: true),
                            CellId = AtResponse.ParseInt(f[5], hex: true),
                            RsrpDbm = RsrpIndex(f[i]),
                            RsrqDb = RsrqIndex(f[i + 1]),
                        };
                        if (serving)
                        {
                            long? snr = AtResponse.ParseInt(f[10]);
                            cell = cell with
                            {
                                DlBandwidthMHz = AtResponse.RbToMHz(AtResponse.ParseInt(f[9])),
                                Rssnr = snr is long s && s >= -100 && s <= 100 ? s / 2.0 : null,
                            };
                        }
                        cells.Add(cell);
                        break;
                }
            }
            return cells;
        }

        // AT+GTCAINFO? -> Cells / BandwidthsMHz for LTE, or null without an LTE PCC line.
        //   PCC:<band>,<pci>,<earfcn>,<dl_bandwidth RB>,...
        //   SCC<n>:<scell_state>,<ul_configured>,<band>,<pci>,<earfcn>,<dl_bandwidth RB>,...
        // band 101..199 = 100 + E-UTRA band (NR carriers, band >= 501, are skipped).
        // The first carrier may follow "+GTCAINFO:" on the same line (as fibocom-connect-fm350 also accepts).
        public static CarrierAggregation? ParseGtcainfo(string? response)
        {
            if (!AtResponse.IsOk(response)) return null;
            bool pcc = false;
            List<double?> bandwidths = new List<double?>();
            foreach (string line in Regex.Split(response!, "\r?\n"))
            {
                Match m = GtcainfoLine.Match(line);
                if (!m.Success) continue;
                string[] f = m.Groups[2].Value.Split(',').Select(x => x.Trim()).ToArray();
                bool isPcc = string.Equals(m.Groups[1].Value, "PCC", StringComparison.OrdinalIgnoreCase);
                int o = isPcc ? 0 : 2; // offset of <band>
                if (f.Length <= o + 3) continue;
                long? band = AtResponse.ParseInt(f[o]);
                if (band is not long b || b <= 100 || b >= 200) continue;
                double? mhz = AtResponse.RbToMHz(AtResponse.ParseInt(f[o + 3]));
                if (isPcc)
                {
                    pcc = true;
                    bandwidths.Insert(0, mhz);
                }
                else
                {
                    bandwidths.Add(mhz);
                }
            }
            if (!pcc) return null;
            return new CarrierAggregation(bandwidths.Count, bandwidths);
        }

        // AT+GTSENRDTEMP=1 -> "+GTSENRDTEMP: <sensor_id>,<temperature>" (sensor 1 = soc_max).
        // The manual gives no unit; FM350 tools (fm350-util, fibocom-connect-fm350) divide by 1000 (milli-Celsius).
        public static double? ParseGtsenrdtemp(string? response)
        {
            List<string[]>? lines = AtResponse.GetLines(response, "GTSENRDTEMP");
            if (lines is null || lines.Count == 0 || lines[0].Length < 2) return null;
            long? v = AtResponse.ParseInt(lines[0][1]);
            if (v is not long n) return null;
            double c = Math.Round(n / 1000.0, 1);
            if (c < -40 || c > 125) return null;
            return c;
        }

        // AT+GTACT? -> "+GTACT: <rat>,<PreferredAct1>,<PreferredAct2>,<band>,..." -> RatConfig (see IntelStatus.ParseXact).
        //   rat: 1=UMTS 2=LTE 4=LTE/UMTS 10=Automatic (queried back as 20) 14=NR 16=NR/WCDMA 17=NR/LTE 20=NR/WCDMA/LTE
        //   PreferredAct: 2=WCDMA 3=LTE 6=NR
        //   band: 0 = automatic, 1..99 = UMTS band, 101..199 = 100 + LTE band, "50" + n = NR band n (501, 5010, 50512)
        public static RatConfig? ParseGtact(string? response)
        {
            string[]? f = AtResponse.GetField(response, "GTACT");
            if (f is null || f.Length < 1) return null;
            Dictionary<long, string> rats = new Dictionary<long, string>
            {
                [1] = "3G",
                [2] = "4G",
                [4] = "3G+4G",
                [10] = "3G+4G+5G (Auto)",
                [14] = "5G",
                [16] = "3G+5G",
                [17] = "4G+5G",
                [20] = "3G+4G+5G",
            };
            Dictionary<long, string[]> ratSets = new Dictionary<long, string[]>
            {
                [1] = new[] { Rat.Umts },
                [2] = new[] { Rat.Lte },
                [4] = new[] { Rat.Umts, Rat.Lte },
                [10] = new[] { Rat.Umts, Rat.Lte, Rat.Nr },
                [14] = new[] { Rat.Nr },
                [16] = new[] { Rat.Umts, Rat.Nr },
                [17] = new[] { Rat.Lte, Rat.Nr },
                [20] = new[] { Rat.Umts, Rat.Lte, Rat.Nr },
            };
            Dictionary<long, string> prefs = new Dictionary<long, string> { [2] = "3G", [3] = "4G", [6] = "5G" };
            long? rat = AtResponse.ParseInt(f[0]);
            if (rat is not long r || !rats.ContainsKey(r)) return null;
            long? pref = f.Length > 1 ? AtResponse.ParseInt(f[1]) : null;
            List<int> umts = new List<int>(), lte = new List<int>(), nr = new List<int>();
            foreach (string text in f.Skip(3))
            {
                Match m = NrBand.Match(text);
                if (m.Success)
                {
                    nr.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
                    continue;
                }
                long? n = AtResponse.ParseInt(text);
                if (n is not long b || b <= 0) continue;
                if (b > 100 && b < 200) lte.Add((int)(b - 100));
                else if (b < 100) umts.Add((int)b);
            }
            return new RatConfig
            {
                AllowedRats = ratSets[r],
                Allowed = rats[r],
                Preferred = pref is long p && prefs.TryGetValue(p, out string? text2) ? text2 : null,
                GsmBands = Array.Empty<int>(),
                UmtsBands = umts,
                LteBands = lte,
                NrBands = nr,
            };
        }
    }
}
