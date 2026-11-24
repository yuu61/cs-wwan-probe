using System.Text.RegularExpressions;
using WwanProbe.Application;
using WwanProbe.Domain;
using WwanProbe.Infrastructure.Modem;
using WwanProbe.Presentation;

namespace WwanProbe.Tests
{
    // Handover history, reset and related frame sections.
    public class HandoverTests
    {
        private static void Add(HandoverLog log, LteSnapshot s) => log.Add(s.PrimaryCell, s.Timestamp, !string.IsNullOrEmpty(s.Error));

        [Fact]
        public void BaselineIdentityMetadataImmutableEventsReturnsAndRetention()
        {
            HandoverLog log = new HandoverLog(3);
            LteSnapshot sample = Snapshots.Handover();
            Add(log, sample);
            Assert.Equal(0, log.Count);
            sample.PrimaryCell = sample.PrimaryCell! with { Pci = 11, Tac = 21, RsrpDbm = -100 };
            Add(log, sample);
            Assert.Equal(0, log.Count);
            LteSnapshot next = Snapshots.Handover(200);
            Add(log, next);
            Assert.True(log.Count == 1 && log.Entries[0].From.CellId == 100 && log.Entries[0].To.CellId == 200, "Cell change was not recorded.");
            Assert.True(log.Entries[0].From.RsrpDbm == -100 && log.Entries[0].Timestamp == next.Timestamp, "Event must use the latest preceding measurement and detection time.");
            next.PrimaryCell = next.PrimaryCell! with { CellId = 999 };
            Assert.Equal(200, log.Entries[0].To.CellId);
            Add(log, Snapshots.Handover(200, "44020"));
            Add(log, Snapshots.Handover(100));
            Add(log, Snapshots.Handover(200));
            Assert.True(log.Count == 4 && log.Entries.Count == 3 && log.Entries[0].Number == 2, "PLMN changes, return transitions or retention failed.");
        }

        [Theory]
        [InlineData("error")]
        [InlineData("absent")]
        [InlineData("invalid")]
        [InlineData("missing-id")]
        [InlineData("missing-provider")]
        public void GapsResetTheBaseline(string kind)
        {
            HandoverLog log = new HandoverLog();
            Add(log, Snapshots.Handover());
            LteSnapshot gap = Snapshots.Handover(200);
            switch (kind)
            {
                case "error": gap.Error = "sample failed"; break;
                case "absent": gap.PrimaryCell = null; break;
                case "invalid": gap.PrimaryCell = gap.PrimaryCell! with { CellId = uint.MaxValue }; break;
                case "missing-id": gap.PrimaryCell = gap.PrimaryCell! with { CellId = null }; break;
                case "missing-provider": gap.PrimaryCell = gap.PrimaryCell! with { Provider = "" }; break;
            }
            Add(log, gap);
            Add(log, Snapshots.Handover(300));
            Assert.Equal(0, log.Count);
            Add(log, Snapshots.Handover(400));
            Assert.Equal(1, log.Count);
        }

        // The real snapshot builder: invalid RSRP must not promote a CA SCell into the primary-cell identity.
        [Fact]
        public void SnapshotIdentityIsIndependentOfRsrpAndSecondaryCells()
        {
            FakeModem modem = new FakeModem().WithIntelStatus(At.Responses(("AT+XMCI=0", At.Response())));
            FakeTraffic traffic = new FakeTraffic();
            LteCellInfo primary = Fixtures.LteCell(100, "44010", 255, 255, 100, pci: 10, tac: 20, ta: 0);
            LteCellInfo secondary = primary with { Rsrp = 50, CellId = 200 };
            modem.Cells = new CellsInfo { ServingLte = new[] { primary, secondary } };
            HandoverLog log = new HandoverLog();
            LteSnapshot snapshot = LteSnapshot.Take(modem, At.Intel, traffic, null, null);
            Assert.True(snapshot.Error is null && snapshot.PrimaryCell!.CellId == 100 && snapshot.PrimaryCell.RsrpDbm is null, "Unmeasured primary identity was lost.");
            Add(log, snapshot);
            modem.Cells = new CellsInfo { ServingLte = new[] { primary, secondary with { CellId = 300 } } };
            Add(log, LteSnapshot.Take(modem, At.Intel, traffic, null, null));
            modem.Cells = new CellsInfo { ServingLte = new[] { primary } };
            Add(log, LteSnapshot.Take(modem, At.Intel, traffic, null, null));
            Assert.Equal(0, log.Count);
            modem.Cells = new CellsInfo { ServingLte = new[] { primary with { CellId = 400 } } };
            Add(log, LteSnapshot.Take(modem, At.Intel, traffic, null, null));
            Assert.Equal(1, log.Count);
        }

        // A modem following the MBIM spec reports dBm / dB, and may list neighbors in WinRT.
        [Fact]
        public void MbimSpecSignalUnitsAndWinRtNeighborFallback()
        {
            FakeModem modem = new FakeModem
            {
                Cells = new CellsInfo
                {
                    ServingLte = new[] { Fixtures.LteCell(500, "44020", -97, -11, 9460, pci: 5, tac: 30, ta: 0) },
                    NeighborLte = new[] { Fixtures.LteCell(4294967295, "44020", -105, -14, 1500, pci: 77, tac: 30) },
                },
            }.WithIntelStatus(At.Responses());
            FakeTraffic traffic = new FakeTraffic();
            LteSnapshot snapshot = LteSnapshot.Take(modem, At.Intel, traffic, null, null);
            Assert.True(snapshot.Serving.Count == 1 && snapshot.Serving[0].RsrpDbm == -97 && snapshot.Serving[0].RsrqDb == -11, "Spec dBm serving cell was dropped.");
            Assert.True(snapshot.Serving[0].Band == "B28/700" && snapshot.PrimaryCell!.RsrpDbm == -97, "Spec serving cell identity is wrong.");
            Assert.True(snapshot.Neighbors!.Count == 1 && snapshot.Neighbors[0].RsrpDbm == -105 && snapshot.Neighbors[0].Band == "B3/1800", "WinRT neighbor fallback failed.");
            modem.WithIntelStatus(At.Responses(("AT+XMCI=0", At.Response())));
            Assert.Empty(LteSnapshot.Take(modem, At.Intel, traffic, null, null).Neighbors!);
        }

        [Fact]
        public void SampleIntegrationCompactFrameDetailsKeysPagingAndEmptyHistory()
        {
            MonitorSession session = Fixtures.Session();
            for (int i = 0; i <= 12; i++) session.AddSnapshot(Snapshots.Handover(100 + i));
            Assert.True(session.Iteration == 13 && session.HandoverLog.Count == 12, "Sample commit did not update handover history.");
            MonitorView view = new MonitorView { Unicode = false, LastHeight = 24 };
            string text = Fixtures.Text(MonitorFrame.Build(session, view, 120));
            Assert.Contains("Handover history (12)", text);
            Assert.Contains("Switched to CellID:112 B1 PCI:10", text);
            Assert.DoesNotMatch(@"CellID:109\b", text);
            Assert.DoesNotMatch("From:|To:|->", text);

            TuiMonitor.ReadInput(view, Keys.From(Keys.Of(ConsoleKey.H)));
            Frame frame = MonitorFrame.Build(session, view, 120);
            text = Fixtures.Text(frame);
            Assert.True(view.HandoverVisible);
            Assert.Contains("#12 2026-09-24 12:34:56 Switched to CellID:112", text);
            Assert.Contains("B1 PCI:10 PLMN:44010 EARFCN:100 TAC:20 RSRP:-90dBm", text);
            Assert.DoesNotMatch("From:|To:|->", text);
            Assert.True(frame.Body.Count <= 23, "History page does not fit the terminal.");
            TuiMonitor.ReadInput(view, Keys.From(Keys.Of(ConsoleKey.DownArrow)));
            Assert.True(view.HandoverOffset == 1 && view.ChartRows == 2, "History scrolling changed chart height.");
            view.HandoverOffset = 10000;
            Assert.Contains("#1 2026-09-24", Fixtures.Text(MonitorFrame.Build(session, view, 120)));
            TuiMonitor.ReadInput(view, Keys.From(Keys.Of(ConsoleKey.H)));
            Assert.False(view.HandoverVisible);
            view.HandoverVisible = true;
            session.HandoverLog = new HandoverLog();
            Assert.Contains("no LTE cell changes", Fixtures.Text(MonitorFrame.Build(session, view, 80)));
        }

        // Device header for a non-Intel modem: AT line, RAT without a preferred RAT, NR bands, AUTO warning.
        [Fact]
        public void DeviceHeaderForOtherVendorsAndMissingAt()
        {
            MonitorSession session = Fixtures.Session();
            session.AddSnapshot(Snapshots.Handover());
            MonitorView view = new MonitorView { Unicode = false, LastHeight = 24 };
            session.Summary = new ModemSummary
            {
                LegacyAllowed = true,
                Model = "RM520N-GL",
                Firmware = "x",
                RadioState = "On",
                DataClass = "Lte",
                At = new ModemAt(AtChannel.Mbim[3], At.Profile(AtProfile.QuectelId), null, Array.Empty<string>()),
                RatConfig = new RatConfig { Allowed = "3G+4G+5G (AUTO)", UmtsBands = new[] { 1, 8 }, LteBands = new[] { 1, 3 }, NrBands = new[] { 78 } },
            };
            string text = Fixtures.Text(MonitorFrame.Build(session, view, 80));
            Assert.Contains("AT: Quectel QDU / Quectel (+Q commands)", text);
            Assert.Contains("RAT: 3G+4G+5G (AUTO)   2G/3G bands: B1 B8   [2G/3G enabled", text);
            Assert.DoesNotContain("prefer", text);
            Assert.Contains("LTE bands: B1 B3   NR bands: n78", text);
            session.Summary = session.Summary with { At = new ModemAt(null, null, "no AT channel (4 MBIM services tried)", Array.Empty<string>()), RatConfig = null };
            Frame frame = MonitorFrame.Build(session, view, 80);
            Assert.Contains("AT: unavailable (no AT channel (4 MBIM services tried))", Fixtures.Text(frame));
            int headerEnd = frame.Body.FindIndex(l => l.Text.StartsWith("-- Network ", StringComparison.Ordinal));
            Assert.All(frame.Body.Take(headerEnd), l => Assert.True(l.Text.Length <= 80, "Header lines must fit 80 columns."));
        }

        // RX / TX share one log scale while both are shown; a chart shown alone keeps its own.
        [Fact]
        public void RxTxSharedScale()
        {
            MonitorSession session = Fixtures.Session();
            session.AddSnapshot(Snapshots.Handover());
            session.History.Clear();
            for (int i = 0; i < 10; i++)
            {
                session.History["Rsrp"].Add(-90);
                session.History["Rsrq"].Add(-10);
                session.History["Rssnr"].Add(10);
                session.History["TempC"].Add(40);
                session.History["RxKB"].Add(20 + 6 * i);
                session.History["TxKB"].Add(0.2);
            }
            MonitorView view = new MonitorView { Unicode = false, LastHeight = 24 };
            string Scales() => string.Join(',', MonitorFrame.Build(session, view, 120).Body
                .Select(l => Regex.Match(l.Text, @"log scale (\S+\.\.\S+) B/s")).Where(m => m.Success).Select(m => m.Groups[1].Value));
            view.ChartVisible["4"] = true;
            view.ChartVisible["5"] = true;
            Assert.Equal("100..100k,100..100k", Scales());
            view.ChartVisible["5"] = false;
            Assert.Equal("10k..100k", Scales());
            view.ChartVisible["4"] = false;
            view.ChartVisible["5"] = true;
            Assert.Equal("100..1k", Scales());
            view.ChartVisible["4"] = true;
            view.Unicode = true;
            string text = Fixtures.Text(MonitorFrame.Build(session, view, 120));
            Assert.Matches(@" RX +100k \|", text);
            Assert.Matches(@" TX +100k \|", text);
            Assert.Equal(2, Regex.Matches(text, @"log, 5\.3 levels/decade").Count);
        }

        // Reset clears the statistics but keeps progress, the latest sample and the handover baseline.
        [Fact]
        public void ResetKeepsProgressLatestSampleAndBaseline()
        {
            MonitorSession session = Fixtures.Session();
            foreach (long cellId in new long[] { 100, 200 })
            {
                LteSnapshot sample = Snapshots.Handover(cellId);
                sample.Serving = new[] { new ServingCell { RsrpDbm = -90, RsrqDb = -10, Quality = "Good" } };
                if (cellId == 100) sample.Downgrade = new DowngradeFinding(DowngradeLevel.Alert, new[] { "test downgrade" });
                session.AddSnapshot(sample);
            }
            MonitorView view = new MonitorView { Unicode = false, LastHeight = 24 };
            string text = Fixtures.Text(MonitorFrame.Build(session, view, 120));
            Assert.True(session.HandoverLog.Count == 1 && session.History["Rsrp"].Count == 2 && text.Contains("seen earlier") && text.Contains("History (primary cell)"),
                "Reset test setup failed.");
            session.ResetStatistics();
            Assert.True(session.Iteration == 2 && session.Snapshot!.PrimaryCell!.CellId == 200, "Reset changed progress or the latest sample.");
            Assert.True(session.History.Series.All(s => s.Count == 0) && session.HandoverLog.Entries.Count == 0, "Reset kept history.");
            text = Fixtures.Text(MonitorFrame.Build(session, view, 120));
            Assert.Contains("Handover history (0)", text);
            Assert.DoesNotContain("seen earlier", text);
            Assert.DoesNotContain("History (primary cell)", text);
            session.AddSnapshot(Snapshots.Handover(300));
            HandoverEntry entry = session.HandoverLog.Entries[0];
            Assert.True(session.HandoverLog.Count == 1 && entry.Number == 1 && entry.From.CellId == 200 && entry.To.CellId == 300, "Handover across the reset was lost or misnumbered.");
        }
    }
}
