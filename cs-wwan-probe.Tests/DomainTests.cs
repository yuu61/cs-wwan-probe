using System.Diagnostics;
using System.Text;
using WwanProbe.Domain;
using WwanProbe.Infrastructure;
using WwanProbe.Infrastructure.Modem;

namespace WwanProbe.Tests
{
    // Adapters and domain rules. Fixtures quote the manuals / real output where
    // noted; "synthetic" ones are built from the documented layout.
    public class DomainTests
    {
        [Theory]
        [InlineData(0L, "B1/2100")]
        [InlineData(599L, "B1/2100")]
        [InlineData(1300L, "B3/1800")]
        [InlineData(1500L, "B3/1800")]
        [InlineData(4800L, "B11/1500")]
        [InlineData(5100L, "B12/700")]
        [InlineData(5230L, "B13/700")]
        [InlineData(5330L, "B14/700")]
        [InlineData(5500L, "B?/5500")]
        [InlineData(5900L, "B18/800")]
        [InlineData(6100L, "B19/800")]
        [InlineData(6500L, "B21/1500")]
        [InlineData(7800L, "B24/1600")]
        [InlineData(8300L, "B25/1900")]
        [InlineData(8800L, "B26/850")]
        [InlineData(9460L, "B28/700")]
        [InlineData(39650L, "B41/2500T")]
        [InlineData(42000L, "B42/3500T")]
        [InlineData(44000L, "B43/3700T")]
        [InlineData(66500L, "B66/AWS")]
        [InlineData(68700L, "B71/600")]
        [InlineData(70000L, "B?/70000")]
        [InlineData(-1L, "B?/-1")]
        public void EarfcnBandTable(long earfcn, string band) => Assert.Equal(band, LteBand.FromEarfcn(earfcn));

        [Fact]
        public void MissingEarfcnIsNotBand1() => Assert.Equal("B?", LteBand.FromEarfcn(null));

        [Fact]
        public void WinRtRsrpRsrqNormalization()
        {
            Assert.Equal(-100, SignalConversion.FromWinRtRsrp(-100));
            Assert.Equal(-140, SignalConversion.FromWinRtRsrp(-140));
            Assert.Equal(-80, SignalConversion.FromWinRtRsrp(61)); // L860-GL index 61 -> -80 dBm
            Assert.Null(SignalConversion.FromWinRtRsrp(null));
            Assert.Null(SignalConversion.FromWinRtRsrp(255));
            Assert.Null(SignalConversion.FromWinRtRsrp(uint.MaxValue));
            Assert.Equal(-12, SignalConversion.FromWinRtRsrq(-12));
            Assert.Equal(-10, SignalConversion.FromWinRtRsrq(20));
            Assert.Null(SignalConversion.FromWinRtRsrq(-30));
            Assert.Null(SignalConversion.FromWinRtRsrq(null));
        }

        // Intel XMM (L860-GL): real responses recorded in docs/neighbor-cells.md
        [Fact]
        public void IntelProfileWithL860Responses()
        {
            Dictionary<string, string?> intel = At.Responses(
                ("AT+MTSM=1", At.Response("+MTSM: 52")),
                ("AT+XCESQ?", At.Response("+XCESQ: 0,99,99,255,255,19,58,11,255,255,255,255")),
                ("AT+XLEC?", At.Response("+XLEC: 0,2,3,5,BAND_LTE_18,0,0,0,0")),
                ("AT+XMCI=0", At.Response(
                    "+XMCI: 4,440,50,\"0x8AA8\",\"0x0558E901\",\"0x019F\",\"0x0000170C\",\"0x00005D5C\",\"0xFFFFFFFF\",61,20,19,\"0x00000002\",\"0x00000000\"",
                    "+XMCI: 5,000,000,\"0xFFFE\",\"0xFFFFFFFF\",\"0x0184\",\"0x00009F8A\",\"0xFFFFFFFF\",\"0xFFFFFFFF\",31,15,255,\"0x7FFFFFFF\",\"0x00000000\"")));
            AtProfile profile = At.Profile(AtProfile.IntelId);
            AtStatus status = AtStatusParser.ParseStatus(profile, intel);
            Assert.Equal(52, status.TempC);
            Assert.Equal(5.5, status.Rssnr);
            Assert.Equal(new double?[] { 10, 20 }, status.Ca!.BandwidthsMHz);
            Assert.Equal(2, status.Cells!.Count);
            LteNeighbor neighbor = Assert.Single(status.Neighbors!);
            Assert.Equal(-110, neighbor.RsrpDbm);
            Assert.Equal(40842, neighbor.Earfcn);
            Assert.Equal("B41/2500T", neighbor.Band);
            AtCell serving = status.Cells[0];
            Assert.Equal(0x558E901, serving.CellId);
            Assert.Equal(0x8AA8, serving.Tac);
            Assert.Equal(-80, serving.RsrpDbm);

            intel["AT+XMCI=0"] = "ERROR\r\n";
            Assert.Null(AtStatusParser.ParseStatus(profile, intel).Neighbors);
            intel["AT+XMCI=0"] = At.Response();
            Assert.Empty(AtStatusParser.ParseStatus(profile, intel).Neighbors!);

            // Shortened real response (the recorded one lists more bands).
            RatConfig rat = AtStatusParser.ParseConfig(profile, At.Responses(("AT+XACT?", At.Response("+XACT: 4,2,1,1,2,4,5,8,101,103,118,141,171"))))!;
            Assert.Equal("3G+4G", rat.Allowed);
            Assert.Equal(new[] { "UMTS", "LTE" }, rat.AllowedRats);
            Assert.True(Downgrade.IsLegacyRatAllowed(rat.AllowedRats));
            Assert.Equal(new[] { 1, 3, 18, 41, 71 }, rat.LteBands);
            Assert.Equal("4G", rat.Preferred);
        }

        // Quectel RM5xx: manual V1.2 examples (5.12, 5.20, 12.5, 5.25); QCAINFO has no example -> synthetic
        [Fact]
        public void QuectelProfileWithManualExamples()
        {
            Dictionary<string, string?> quectel = At.Responses(
                ("AT+QTEMP", At.Response("+QTEMP:\"aoss0-usr\",\"26\"", "+QTEMP:\"mdm-q6-usr\",\"27\"", "+QTEMP:\"xo-therm-usr\",\"24\"", "+QTEMP:\"sdx-case-therm-usr\",\"31\"")),
                ("AT+QCAINFO", At.Response(
                    "+QCAINFO: \"PCC\",1650,100,\"LTE BAND 3\",1,12,-100,-12,-68,8",
                    "+QCAINFO: \"SCC\",100,50,\"LTE BAND 1\",2,300,-95,-10,-70,5",
                    "+QCAINFO: \"SCC\",500,25,\"LTE BAND 1\",0,301,-,-,-,-")),
                ("AT+QSINR", At.Response("+QSINR: -3,-7,-1,-2,LTE")),
                ("AT+QENG=\"servingcell\"", At.Response("+QENG: \"servingcell\",\"NOCONN\",\"LTE\",\"FDD\",460,01,5F1EA15,12,1650,3,5,5,DE10,-100,-12,-68,11,0,-32768,27")),
                ("AT+QENG=\"neighbourcell\"", At.Response(
                    "+QENG: \"neighbourcell intra\",\"LTE\",38950,276,-3,-88,-65,0,37,7,16,6,44",
                    "+QENG: \"neighbourcell inter\",\"LTE\",39148,-,-,-,-,-,37,0,30,7,-,-,-,-",
                    "+QENG: \"neighbourcell inter\",\"LTE\",37900,-,-,-,-,-,0,0,30,6,-,-,-,-")));
            AtProfile profile = At.Profile(AtProfile.QuectelId);
            AtStatus status = AtStatusParser.ParseStatus(profile, quectel);
            Assert.Equal(31, status.TempC);
            Assert.Equal(8.0, status.Rssnr);
            Assert.Equal(2, status.Ca!.Cells);
            Assert.Equal(new double?[] { 20, 10 }, status.Ca.BandwidthsMHz);
            LteNeighbor neighbor = Assert.Single(status.Neighbors!);
            Assert.Equal(-88, neighbor.RsrpDbm);
            Assert.Equal(-3, neighbor.RsrqDb);
            Assert.Equal(276, neighbor.Pci);
            AtCell serving = status.Cells!.First(c => c.Role == CellRole.Serving);
            Assert.Equal(1650, serving.Earfcn);
            Assert.Equal(0x5F1EA15, serving.CellId);
            Assert.Equal(0xDE10, serving.Tac);
            Assert.Equal(-100, serving.RsrpDbm);

            quectel.Remove("AT+QCAINFO");
            status = AtStatusParser.ParseStatus(profile, quectel);
            Assert.Equal(-3.0, status.Rssnr);
            Assert.Equal(1, status.Ca!.Cells);
            Assert.Equal(new double?[] { 20 }, status.Ca.BandwidthsMHz);
            Assert.Equal(30, QuectelStatus.ParseQtemp(At.Response("+QTEMP: 30,28,27")));

            List<AtCell> endc = QuectelStatus.ParseQengServing(At.Response(
                "+QENG: \"servingcell\",\"NOCONN\"",
                "+QENG: \"LTE\",\"FDD\",460,01,5F1EA15,12,1650,3,5,5,DE10,-99,-12,-67,11,9,230,-",
                "+QENG:\"NR5G-NSA\",460,01,747,-71,13,-11,627264,78,12,1"))!;
            Assert.Equal("LTE,NR", string.Join(',', endc.Select(c => c.Rat)));
            Assert.Equal(1650, endc[0].Earfcn);
            Assert.Equal(-99, endc[0].RsrpDbm);
            Assert.Equal(627264, endc[1].Channel);
            List<AtCell> sa = QuectelStatus.ParseQengServing(At.Response("+QENG: \"servingcell\",\"NOCONN\",\"NR5G-SA\",\"TDD\", 460,01,9013B004,299,690E0F,633984,78,12,-107,-13,2,1,-"))!;
            Assert.Equal("NR", sa[0].Rat);
            Assert.Equal(633984, sa[0].Channel);
            // Synthetic, from the documented WCDMA layout.
            List<AtCell> wcdma = QuectelStatus.ParseQengServing(At.Response("+QENG: \"servingcell\",\"NOCONN\",\"WCDMA\",440,10,1234,ABCDEF,10700,300,0,-80,-5,-,-,-,-,-"))!;
            Assert.Equal("UMTS", wcdma[0].Rat);
            Assert.Equal(10700, wcdma[0].Channel);
            // Synthetic, WCDMA-mode LTE neighbor layout (RSRP before RSRQ).
            List<AtCell> nb = QuectelStatus.ParseQengNeighbour(At.Response("+QENG: \"neighbourcell\",\"LTE\",1650,12,-95,-11,20"))!;
            Assert.Equal(-95, nb[0].RsrpDbm);
            Assert.Equal(-11, nb[0].RsrqDb);

            Dictionary<string, string?> prefs = At.Responses(
                ("AT+QNWPREFCFG=\"mode_pref\"", At.Response("+QNWPREFCFG: \"mode_pref\",AUTO")),
                ("AT+QNWPREFCFG=\"gw_band\"", At.Response("+QNWPREFCFG: \"gw_band\",1:2:3:4:5:6:7:8:9:19")),
                ("AT+QNWPREFCFG=\"lte_band\"", At.Response("+QNWPREFCFG: \"lte_band\",1:3:8")),
                ("AT+QNWPREFCFG=\"nr5g_band\"", At.Response("+QNWPREFCFG: \"nr5g_band\",1:3:7:20:28:40:41:71:77:78:79")));
            RatConfig rat = AtStatusParser.ParseConfig(profile, prefs)!;
            Assert.Contains("3G", rat.Allowed);
            Assert.Equal(new[] { "UMTS", "LTE", "NR" }, rat.AllowedRats);
            Assert.True(Downgrade.IsLegacyRatAllowed(rat.AllowedRats));
            Assert.Equal(new[] { 1, 3, 8 }, rat.LteBands);
            Assert.Equal(11, rat.NrBands.Count);
            Assert.Equal(10, rat.UmtsBands.Count);
            prefs["AT+QNWPREFCFG=\"mode_pref\""] = At.Response("+QNWPREFCFG: \"mode_pref\",LTE:NR5G");
            Assert.Equal("4G+5G", AtStatusParser.ParseConfig(profile, prefs)!.Allowed);
            prefs["AT+QNWPREFCFG=\"mode_pref\""] = At.Response("+QNWPREFCFG: \"mode_pref\",GSM:LTE");
            prefs["AT+QNWPREFCFG=\"lte_band\""] = At.Response("+QNWPREFCFG: \"lte_band\",3");
            prefs["AT+QNWPREFCFG=\"nr5g_band\""] = "ERROR\r\n";
            rat = AtStatusParser.ParseConfig(profile, prefs)!;
            Assert.Equal("2G+4G", rat.Allowed);
            Assert.Equal(new[] { "GSM", "LTE" }, rat.AllowedRats);
            Assert.Equal(new[] { 3 }, rat.LteBands);
            Assert.Empty(rat.NrBands);
        }

        // Fibocom FM350: the "1,4,..." / "1,9,..." +GTCCINFO lines are real FM350 output (OpenWrt forum);
        // the neighbor lines and the other commands are synthetic.
        [Fact]
        public void FibocomGtProfile()
        {
            Dictionary<string, string?> fibocom = At.Responses(
                ("AT+GTSENRDTEMP=1", At.Response("+GTSENRDTEMP: 1,45300")),
                ("AT+GTCAINFO?", At.Response("+GTCAINFO:", "PCC:103,358,1300,100,2,1,3,2,60", "SCC1:2,0,101,300,100,50,50,2,1,3,2,55")),
                ("AT+GTCCINFO?", At.Response(
                    "+GTCCINFO:",
                    "LTE service cell:",
                    "1,4,262,1,05D5,0019BF801,1300,358,103,100,13,60,60,22",
                    "1,9,,,FFFFFFF,00FFFFFFF,641760,940,5078,450,19,76,76,84",
                    "LTE neighbor cell:",
                    "2,4,262,1,05D5,0019BF802,1300,359,100,50,52,18",
                    "2,2,262,1,1234,00ABCDE,10700,300,0,0,0,40,90,45,20")));
            AtProfile profile = At.Profile(AtProfile.FibocomGtId);
            AtStatus status = AtStatusParser.ParseStatus(profile, fibocom);
            Assert.Equal(45.3, status.TempC);
            Assert.Equal(6.5, status.Rssnr);
            Assert.Equal(new double?[] { 20, 10 }, status.Ca!.BandwidthsMHz);
            AtCell serving = status.Cells!.First(c => c.Rat == "LTE" && c.Role == CellRole.Serving);
            Assert.Equal(-81, serving.RsrpDbm);
            Assert.Equal(-9, serving.RsrqDb);
            Assert.Equal(0x19BF801, serving.CellId);
            LteNeighbor neighbor = Assert.Single(status.Neighbors!);
            Assert.Equal(-89, neighbor.RsrpDbm);
            Assert.Equal("B3/1800", neighbor.Band);
            Assert.Single(status.Cells!, c => c.Rat == "NR");
            Assert.Single(status.Cells!, c => c.Rat == "UMTS");
            CarrierAggregation ca = FibocomStatus.ParseGtcainfo(At.Response("+GTCAINFO: PCC:103,358,1300,100,2,1,3,2,60", "SCC 1:2,0,101,300,100,50,50,2,1,3,2,55"))!;
            Assert.Equal(new double?[] { 20, 10 }, ca.BandwidthsMHz);
            fibocom.Remove("AT+GTCAINFO?");
            Assert.Equal(new double?[] { 20 }, AtStatusParser.ParseStatus(profile, fibocom).Ca!.BandwidthsMHz);

            RatConfig rat = AtStatusParser.ParseConfig(profile, At.Responses(("AT+GTACT?", At.Response("+GTACT: 17,6,,101,103,5078,50257,1"))))!;
            Assert.Equal("4G+5G", rat.Allowed);
            Assert.Equal(new[] { "LTE", "NR" }, rat.AllowedRats);
            Assert.False(Downgrade.IsLegacyRatAllowed(rat.AllowedRats));
            Assert.Equal("5G", rat.Preferred);
            Assert.Equal(new[] { 1, 3 }, rat.LteBands);
            Assert.Equal(new[] { 78, 257 }, rat.NrBands);
            Assert.Equal(new[] { 1 }, rat.UmtsBands);
            Assert.Contains("3G", FibocomStatus.ParseGtact(At.Response("+GTACT: 20,6,3,0"))!.Allowed);
        }

        [Fact]
        public void DowngradeDetectionWithNormalizedRegistrationAndVendorCells()
        {
            AtCell[] cells =
            {
                new AtCell { Rat = Rat.Nr, Role = CellRole.Serving, Channel = 627264 },
                new AtCell { Rat = Rat.Umts, Role = CellRole.Neighbor, Channel = 10700 },
            };
            DowngradeFinding finding = Downgrade.Evaluate(new[] { "LTE" }, 0, cells, "QENG");
            Assert.Equal(DowngradeLevel.Warning, finding.Level);
            Assert.Equal("UMTS neighbor ch:10700 (QENG)", string.Join(';', finding.Reasons));
            List<AtCell> wcdma = QuectelStatus.ParseQengServing(At.Response("+QENG: \"servingcell\",\"NOCONN\",\"WCDMA\",440,10,1234,ABCDEF,10700,300,0,-80,-5,-,-,-,-,-"))!;
            Assert.Equal(DowngradeLevel.Alert, Downgrade.Evaluate(new[] { "LTE" }, 0, wcdma, "QENG").Level);
            Assert.Equal(new[] { "UMTS" }, ModemObservation.RatsFromDataClass("Umts, Hsdpa, Hsupa"));
            Assert.Equal(new[] { "LTE", "NR" }, ModemObservation.RatsFromDataClass("Lte, NewRadioNonStandalone"));
            Assert.Equal(DowngradeLevel.Alert, Downgrade.Evaluate(new[] { "UMTS" }, 0).Level);
            Assert.Equal(DowngradeLevel.None, Downgrade.Evaluate(new[] { "UMTS", "LTE" }, 0).Level);
            Assert.Equal("1 2G/3G serving cell(s) (WinRT)", Downgrade.Evaluate(new[] { "LTE" }, 1).Reasons.Single());
        }

        // libmbim: Intel/Fibocom/Compal raw "<cmd>\r\n"; Quectel QDU UINT32 type + command.
        [Fact]
        public void MbimAtFramingRoundTrip()
        {
            Assert.Equal(new byte[] { 65, 84, 13, 10 }, MbimAtFraming.ToRequest(AtFraming.Crlf, "AT"));
            Assert.Equal(new byte[] { 0, 0, 0, 0, 65, 84 }, MbimAtFraming.ToRequest(AtFraming.Qdu, "AT"));
            byte[] ok = new byte[] { 0, 0, 0, 0 }.Concat(Encoding.ASCII.GetBytes("+QTEMP: 30,28,27\r\n")).Concat(new byte[] { 0, 0 }).ToArray();
            Assert.Equal(30, QuectelStatus.ParseQtemp(MbimAtFraming.FromResponse(AtFraming.Qdu, ok)));
            byte[] fail = new byte[] { 1, 0, 0, 0 }.Concat(Encoding.ASCII.GetBytes("\r\nOK\r\n")).ToArray();
            Assert.False(AtResponse.IsOk(MbimAtFraming.FromResponse(AtFraming.Qdu, fail)));
            Assert.Equal("\r\nOK\r\n", MbimAtFraming.FromResponse(AtFraming.Crlf, Encoding.ASCII.GetBytes("\r\nOK\r\n")));
            Assert.Equal("", MbimAtFraming.FromResponse(AtFraming.Crlf, Array.Empty<byte>()));
            Assert.Equal("", MbimAtFraming.FromResponse(AtFraming.Crlf, null));
        }

        // TaskWait.FirstResult (behind TaskWait.Await / WinRtModem.GetCellsInfo) with plain .NET tasks.
        // A TaskCompletionSource that is never completed is a query left unanswered.
        private static (string? Result, string? Error, int Starts, long Ms) WaitTest(Task<string>[] tasks, int timeoutMs, int resendAfterMs = int.MaxValue)
        {
            int starts = 0;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                string result = TaskWait.FirstResult(() => tasks[starts++], timeoutMs, resendAfterMs);
                return (result, null, starts, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                return (null, ex.Message, starts, sw.ElapsedMilliseconds);
            }
        }

        private static Task<string> Pending() => new TaskCompletionSource<string>().Task;

        [Fact]
        public void WinRtWaitWithOneResend()
        {
            (string? Result, string? Error, int Starts, long Ms) r = WaitTest(new[] { Task.FromResult("first") }, 1000, 100);
            Assert.True(r.Result == "first" && r.Starts == 1, "A completed query is returned without a resend");
            r = WaitTest(new[] { Pending(), Task.FromResult("resent") }, 5000, 200);
            Assert.True(r.Result == "resent" && r.Starts == 2 && r.Ms >= 190 && r.Ms < 2000, $"An unanswered query is sent once more after ResendAfterMs ({r.Ms}ms)");
            r = WaitTest(new[] { Task.Delay(400, TestContext.Current.CancellationToken).ContinueWith(_ => "slow", TestContext.Current.CancellationToken), Pending() }, 5000, 100);
            Assert.True(r.Error is null && r.Result == "slow" && r.Starts == 2 && r.Ms >= 350 && r.Ms < 2000, $"A slow first answer after the resend is still taken ({r.Ms}ms)");
            r = WaitTest(new[] { Pending(), Pending() }, 400, 100);
            Assert.True(r.Error == "WinRT async operation timed out (400ms)" && r.Starts == 2 && r.Ms < 1500, "Two unanswered queries time out after TimeoutMs in total");
            r = WaitTest(new[] { Pending(), Pending() }, 200);
            Assert.True(r.Error!.Contains("timed out") && r.Starts == 1, "Without ResendAfterMs nothing is resent (Await)");
            TaskCompletionSource<string> failed = new TaskCompletionSource<string>();
            failed.SetException(new InvalidOperationException("modem error"));
            r = WaitTest(new[] { failed.Task, Pending() }, 1000, 100);
            Assert.True(r.Error == "modem error" && r.Starts == 1, "A failed query is rethrown, not resent");
        }

        // Startup detection (ModemAt.Initialize / AtChannelDetector.Find) with a simulated modem.
        [Fact]
        public void AtChannelAndCommandSetDetection()
        {
            FakeModem modem = new FakeModem();
            modem.AtHandlers["Intel AT Tunnel"] = _ => null;
            ModemAt at = ModemAt.Initialize(modem, "");
            Assert.True(at.Channel!.Name == "Intel AT Tunnel" && at.Channel.Unconfirmed && at.Profile!.Id == "Intel" && at.Error is null,
                "Intel AT Tunnel with lost answers must keep the Intel set");
            Assert.Equal(2, modem.Sent.Count); // only the two AT tries, no profile probes
            Assert.False(AtChannel.Mbim[0].Unconfirmed, "The channel table must not be modified");

            modem = new FakeModem();
            modem.AtHandlers["Intel AT Tunnel"] = _ => "";
            Assert.Equal("Intel", ModemAt.Initialize(modem, "").Profile!.Id);

            modem = new FakeModem();
            modem.AtHandlers["Intel AT Tunnel"] = c => c == "AT" ? At.Response() : null;
            at = ModemAt.Initialize(modem, "");
            Assert.True(!at.Channel!.Unconfirmed && at.Profile!.Id == "Intel", "Intel AT Tunnel with a lost probe keeps the Intel set");

            modem = new FakeModem();
            modem.AtHandlers["Fibocom AT"] = c => c is "AT" or "AT+GTCAINFO=?" ? At.Response() : "ERROR\r\n";
            at = ModemAt.Initialize(modem, "");
            Assert.True(at.Channel!.Name == "Fibocom AT" && at.Profile!.Id == "FibocomGt", "Missing Intel service falls through to Fibocom GT");
            Assert.Single(at.Tried);

            modem = new FakeModem();
            modem.AtHandlers["Fibocom AT"] = _ => null;
            at = ModemAt.Initialize(modem, "");
            Assert.True(at.Channel is null && at.Error == "no AT channel (4 MBIM services tried)", "A silent service without a Profile is rejected");

            modem = new FakeModem();
            modem.AtHandlers["Serial COM7"] = c => c is "AT" or "AT+QENG=?" ? At.Response() : "ERROR\r\n";
            at = ModemAt.Initialize(modem, "COM7");
            Assert.True(at.Channel!.Port == "COM7" && at.Profile!.Id == "Quectel", "AtPort uses the serial channel");
            modem.AtHandlers["Serial COM7"] = _ => null;
            Assert.Equal("no AT response on COM7", ModemAt.Initialize(modem, "COM7").Error);
        }
    }
}
