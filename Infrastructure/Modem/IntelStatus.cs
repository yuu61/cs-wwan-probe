using System.Globalization;
using System.Text.RegularExpressions;
using WwanProbe.Domain;

namespace WwanProbe.Infrastructure.Modem
{
    // AT status response parsing for Intel XMM modems (Fibocom L860-GL etc.) (pure, no I/O).
    // Reference: FIBOCOM L860 AT Commands User Manual V3.2.3 (see docs/neighbor-cells.md).
    // Every parser returns null when the response is missing, "ERROR", or the value is invalid.
    public static class IntelStatus
    {
        private const uint XmciInvalid = uint.MaxValue;
        private static readonly Regex XmciLine = new Regex(@"^\+XMCI:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex XmciHexText = new Regex("^0x([0-9A-Fa-f]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static bool TryInt(string? text, out int value) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        // AT+MTSM=1 -> "+MTSM: <Temp>" (Celsius, -40..125)
        public static int? ParseMtsm(string? response)
        {
            string[]? f = AtResponse.GetField(response, "MTSM");
            if (f is null) return null;
            if (!TryInt(f[0], out int temp) || temp < -40 || temp > 125) return null;
            return temp;
        }

        // AT+XCESQ? -> "+XCESQ: <n>,<rxlev>,<ber>,<rscp>,<ecno>,<rsrq>,<rsrp>,<rssnr>,..."
        // rssnr range is -100..100 (255 = unknown); the manual does not state its unit.
        // Assumed 0.5 dB steps (as ModemManager's XMM plugin does), so the result is in dB.
        public static double? ParseXcesq(string? response)
        {
            string[]? f = AtResponse.GetField(response, "XCESQ");
            if (f is null || f.Length < 8) return null;
            if (!TryInt(f[7], out int rssnr) || rssnr < -100 || rssnr > 100) return null;
            return rssnr / 2.0;
        }

        // AT+XLEC? -> "+XLEC: <n>,<no_of_cells>,<bandwidth>[,<bandwidth>...][,<undocumented>...]"
        // no_of_cells: 0 = not on LTE, 1 = primary cell only, 2..5 = secondary cells added.
        // bandwidth:   0..5 = 1.4/3/5/10/15/20 MHz, 255 = invalid.
        public static CarrierAggregation? ParseXlec(string? response)
        {
            string[]? f = AtResponse.GetField(response, "XLEC");
            if (f is null || f.Length < 2) return null;
            if (!TryInt(f[1], out int cells) || cells < 0 || cells > 5) return null;
            List<double?> bandwidths = new List<double?>();
            for (int i = 0; i < cells && 2 + i < f.Length; i++)
            {
                bool valid = TryInt(f[2 + i], out int bw) && bw >= 0 && bw < AtResponse.LteIndexBandwidthMHz.Count;
                bandwidths.Add(valid ? AtResponse.LteIndexBandwidthMHz[bw] : null);
            }
            return new CarrierAggregation(cells, bandwidths);
        }

        // AT+XACT? -> "+XACT: <AcT>,<PreferredAcT>,<PreferredAcT2>,<band>,<band>,..." (not in the manual).
        // Layout as parsed by ModemManager (src/plugins/xmm/mm-modem-helpers-xmm.c):
        //   AcT / PreferredAcT: 0=2G 1=3G 2=4G 3=2G+3G 4=3G+4G 5=2G+4G 6=2G+3G+4G (third field ignored)
        //   band: <100 = UTRA band, 101..299 = 100 + E-UTRA band, >300 = GSM band in MHz
        public static RatConfig? ParseXact(string? response)
        {
            string[]? f = AtResponse.GetField(response, "XACT");
            if (f is null || f.Length < 4) return null;
            string[] modes = { "2G", "3G", "4G", "2G+3G", "3G+4G", "2G+4G", "2G+3G+4G" };
            string[][] modeRats =
            {
                new[] { Rat.Gsm }, new[] { Rat.Umts }, new[] { Rat.Lte }, new[] { Rat.Gsm, Rat.Umts },
                new[] { Rat.Umts, Rat.Lte }, new[] { Rat.Gsm, Rat.Lte }, new[] { Rat.Gsm, Rat.Umts, Rat.Lte },
            };
            if (!TryInt(f[0], out int act) || act < 0 || act >= modes.Length) return null;
            string? preferred = TryInt(f[1], out int p) && p >= 0 && p < modes.Length ? modes[p] : null;
            List<int> gsm = new List<int>(), umts = new List<int>(), lte = new List<int>();
            foreach (string v in f.Skip(3))
            {
                if (!TryInt(v, out int n)) continue;
                if (n > 300) gsm.Add(n);
                else if (n > 100) lte.Add(n - 100);
                else if (n > 0) umts.Add(n);
            }
            return new RatConfig
            {
                AllowedRats = modeRats[act],
                Allowed = modes[act],   // e.g. "3G+4G"
                Preferred = preferred,
                GsmBands = gsm,         // MHz values (900, 1800, ...)
                UmtsBands = umts,       // UTRA band numbers
                LteBands = lte,         // E-UTRA band numbers
                NrBands = Array.Empty<int>(), // XMM 7560 is LTE only
            };
        }

        // AT+XMCI (Measured Cell Information). Fibocom L860-GL AT command manual 9.1.13:
        //   TYPE 0/1 (GSM serving/neighbor):  <TYPE>,<MCC>,<MNC>,<LAC>,<CI>,<BSIC>,<RXLEV>,<BER>,<ARFCN>,<TARELIABILITY>,<TA>
        //   TYPE 2/3 (UMTS serving/neighbor): <TYPE>,<MCC>,<MNC>,<LAC>,<CI>,<PSC>,<DLUARFCN>,<ULUARFCN>,<PATHLOSS>,<RSSI>,<RSCP>,<ECNO>
        //   TYPE 4/5 (LTE serving/neighbor):  <TYPE>,<MCC>,<MNC>,<TAC>,<CI>,<PCI>,<DLEARFCN>,<ULEARFCN>,<PATHLOSS>,<RSRP>,<RSRQ>,<RSSNR>,<TA>,<CQI>
        //   (TYPE 6..9 = 1xRTT / EvDO, not supported.) Hex fields are quoted "0x...".
        //   LTE RSRP/RSRQ are 3GPP TS 36.133 indices (same scale as the WinRT cell info).
        // Returns the cells in the common AT cell shape (AtProfile).
        public static List<AtCell> ParseXmci(string? response)
        {
            List<AtCell> cells = new List<AtCell>();
            foreach (string line in Regex.Split(response ?? "", "\r?\n"))
            {
                Match m = XmciLine.Match(line);
                if (!m.Success) continue;
                string[] f = m.Groups[1].Value.Split(',');
                if (!TryInt(f[0], out int type) || type < 0 || type > 5) continue;
                string rat = new[] { Rat.Gsm, Rat.Umts, Rat.Lte }[type / 2];
                CellRole role = type % 2 == 0 ? CellRole.Serving : CellRole.Neighbor;
                int channelIndex = rat == Rat.Gsm ? 8 : 6;
                long? channel = f.Length > channelIndex ? ParseXmciNumber(f[channelIndex]) : null;
                if (rat != Rat.Lte)
                {
                    cells.Add(new AtCell { Rat = rat, Role = role, Channel = channel });
                    continue;
                }
                if (f.Length < 14) continue;
                int rsrpIdx = int.Parse(f[9].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
                int rsrqIdx = int.Parse(f[10].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
                cells.Add(new AtCell
                {
                    Rat = rat,
                    Role = role,
                    Channel = channel,
                    Tac = ParseXmciHex(f[3]),
                    CellId = ParseXmciHex(f[4]),
                    Pci = ParseXmciHex(f[5]),
                    Earfcn = ParseXmciHex(f[6]),
                    RsrpIdx = rsrpIdx,
                    RsrqIdx = rsrqIdx,
                    RsrpDbm = SignalConversion.RsrpIndexToDbm(rsrpIdx),
                    RsrqDb = SignalConversion.RsrqIndexToDb(rsrqIdx),
                });
            }
            return cells;
        }

        private static long? ParseXmciHex(string text)
        {
            Match m = XmciHexText.Match(text.Trim('"'));
            if (!m.Success) return null;
            uint value = Convert.ToUInt32(m.Groups[1].Value, 16);
            if (value == XmciInvalid) return null;
            return value;
        }

        private static long? ParseXmciNumber(string text)
        {
            string t = text.Trim('"');
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return ParseXmciHex(t);
            return TryInt(t, out int n) ? n : null;
        }
    }
}
