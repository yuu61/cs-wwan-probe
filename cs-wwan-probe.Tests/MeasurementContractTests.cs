using System.Text.Json;
using WwanProbe.Application;
using WwanProbe.Domain;
using WwanProbe.Infrastructure;
using WwanProbe.Infrastructure.Modem;
using WwanProbe.Presentation;

namespace WwanProbe.Tests
{
    // Shared measurement and adapter contracts.
    public class MeasurementContractTests
    {
        // Counter failures, invalid samples and actual zero must remain distinguishable.
        [Fact]
        public void UnavailableInvalidEmptyAndZeroTrafficReadings()
        {
            TrafficReading failed;
            using (PdhTrafficCounter missing = new("wwan-probe test adapter that does not exist")) failed = missing.Read();
            Assert.True(failed.RxKB is null && failed.TxKB is null && failed.BwMbps is null && failed.Error is not null, "Failed counters became zero.");

            List<CounterSample> samples =
            [
                new CounterSample(@"\Network Interface(test)\Current Bandwidth", 1000000, 0),
                new CounterSample(@"\Network Interface(test)\Bytes Received/sec", 0, 0),
                new CounterSample(@"\Network Interface(test)\Bytes Sent/sec", 0, 1),
            ];
            TrafficReading traffic = TrafficReading.FromSamples(samples);
            Assert.True(traffic.RxKB == 0 && traffic.TxKB == 0 && traffic.BwMbps == 1 && traffic.Error is null, "Valid zero was treated as missing.");
            samples[2] = samples[2] with { Status = 0xC0000BC6 };
            traffic = TrafficReading.FromSamples(samples);
            Assert.True(traffic.RxKB == 0 && traffic.TxKB is null && traffic.Error is not null, "Invalid counter status was accepted.");
            traffic = TrafficReading.FromSamples([]);
            Assert.True(traffic.RxKB is null && traffic.Error is not null, "An empty counter response became zero.");
        }

        // The counter keeps its query between reads: a failed read closes it and the next read opens it
        // again (with the same result), and after Dispose a read fails without opening one. The instance
        // name matches no adapter, so this runs on any machine: the query opens and the collection fails.
        [Fact]
        public void TrafficCounterReopensAfterAFailureButNotAfterDispose()
        {
            using PdhTrafficCounter counter = new("wwan-probe test adapter that does not exist");
            TrafficReading first = counter.Read();
            Assert.True(first.Error is not null && first.RxKB is null, "A missing adapter was read.");
            Assert.Contains("(collect performance counters)", first.Error, StringComparison.Ordinal);
            Assert.Equal(first, counter.Read());
            Assert.Equal(2, counter.Opens);
            counter.Dispose();
            TrafficReading closed = counter.Read();
            Assert.True(closed.Error is not null && closed.Error != first.Error && closed.RxKB is null && closed.BwMbps is null,
                "A read after Dispose opened the counters again.");
            Assert.Equal(2, counter.Opens);
        }

        // The reason text of each PDH status in a failed reading (shown on screen and in the CSV Error column).
        [Fact]
        public void PdhStatusReasons()
        {
            Assert.Equal("The counter returned no data; the adapter instance may not exist.", PdhTrafficCounter.Describe(0x800007D5));
            Assert.Equal("The specified instance is not present.", PdhTrafficCounter.Describe(0x800007D1));
            Assert.Equal("The specified object is not found on the system.", PdhTrafficCounter.Describe(0xC0000BB8));
            Assert.Equal("The specified counter could not be found.", PdhTrafficCounter.Describe(0xC0000BB9));
            Assert.Equal("The data is not valid.", PdhTrafficCounter.Describe(0xC0000BC6));
            Assert.Equal("PDH error 0x800007D0.", PdhTrafficCounter.Describe(0x800007D0));
        }

        // Only device reads are simulated; the production adapter, snapshot, history, CSV and frame code run.
        private static FakeModem Fixture()
        {
            return new FakeModem
            {
                Cells = new CellsInfo
                {
                    ServingLte =
                    [
                        Fixtures.LteCell(100, "", 255, -11, 100, pci: 10, tac: 20, ta: 3),
                        Fixtures.LteCell(200, "44010", -90, -10, 1300, pci: 11, tac: 20, ta: 4),
                        Fixtures.LteCell(300, "44010", 255, 255, 6300),
                    ],
                },
            }.WithIntelStatus(At.Responses(
                ("AT+MTSM=1", At.Response("+MTSM: 40")),
                ("AT+XCESQ?", At.Response("+XCESQ: 0,99,99,255,255,19,58,20,255,255,255,255")),
                ("AT+XMCI=0", At.Response())));
        }

        private static readonly FakeTraffic FailedTraffic = new() { Reading = new TrafficReading(null, null, null, "counter unavailable") };

        [Fact]
        public void PrimaryIdentityMissingMeasurementsCarrierAlignmentCsvHistoryAndFrame()
        {
            FakeModem modem = Fixture();
            LteSnapshot snapshot = LteSnapshot.Take(modem, At.Intel, FailedTraffic, null, null);
            Assert.True(snapshot.Error is null && snapshot.TrafficError!.Contains("counter unavailable", StringComparison.Ordinal), "A traffic failure invalidated the whole observation.");
            Assert.True(snapshot.PrimaryCell!.CellId == 100 && snapshot.Serving.Count == 3 && snapshot.SecondaryCells.Count == 2, "Cell roles were changed by missing RSRP.");
            Assert.True(snapshot.PrimaryCell.Provider == "44010" && snapshot.PrimaryCell.Ta == 3 && snapshot.PrimaryCell.RsrqDb == -11, "Primary metadata was lost.");
            Assert.True(snapshot.PrimaryCell.RsrpDbm is null && snapshot.PrimaryCell.Quality is null, "Missing RSRP became a classified signal.");
            Assert.True(snapshot.TempC == 40 && snapshot.Rssnr == 10 && snapshot.Ca is null && snapshot.Neighbors!.Count == 0, "AT status was lost.");

            // The background sampler builds the same snapshot as the foreground call.
            MonitorSampler sampler = new(() => LteSnapshot.Take(modem, At.Intel, FailedTraffic, null, null));
            sampler.Start();
            Assert.True(SpinWait.SpinUntil(() => sampler.IsCompleted, 10000));
            LteSnapshot background = sampler.Receive();
            background.Timestamp = snapshot.Timestamp;
            Assert.Equal(JsonSerializer.Serialize(snapshot), JsonSerializer.Serialize(background));

            MonitorSession session = Fixtures.Session();
            session.AddSnapshot(snapshot);
            Assert.True(double.IsNaN(session.History["Rsrp"][0]) && session.History["Rsrq"][0] == -11, "History promoted an SCell or discarded the primary RSRQ.");
            Assert.True(double.IsNaN(session.History["RxKB"][0]) && session.History["TempC"][0] == 40, "History did not preserve missing traffic and valid temperature.");
            Assert.Equal(100, session.HandoverLog.Previous!.CellId);
            Dictionary<string, object?> row = SnapshotLog.ToRow(snapshot);
            Assert.True(Equals(row["CellID"], 100L) && row["RSRP_dBm"] is null && (string?)row["SCell_EARFCN"] == "1300+6300" && (string?)row["SCell_RSRP_dBm"] == "-90+",
                "CSV promoted or lost a carrier, or misaligned missing SCell fields.");
            MonitorView view = new() { Unicode = false };
            string text = Fixtures.Text(MonitorFrame.Build(session, view, 120));
            Assert.Contains("RSRP: n/a  RSRQ: -11 dB", text, StringComparison.Ordinal);
            Assert.Contains("CellID:100", text, StringComparison.Ordinal);
            Assert.Contains("BW: n/a   RX: n/a   TX: n/a", text, StringComparison.Ordinal);
            Assert.Contains("Traffic: counter unavailable", text, StringComparison.Ordinal);

            string csvPath = Fixtures.TempFile();
            try
            {
                SnapshotLog.Initialize(csvPath);
                SnapshotLog.Append(csvPath, snapshot);
                Dictionary<string, string> csv = Assert.Single(Fixtures.ReadCsv(csvPath));
                Assert.Equal(SnapshotLog.Columns, csv.Keys);
                Assert.True(csv["CellID"] == "100" && string.IsNullOrEmpty(csv["RSRP_dBm"]) && string.IsNullOrEmpty(csv["RX_KBps"]) && csv["Error"].Contains("Traffic: counter unavailable", StringComparison.Ordinal),
                    "CSV serialization lost missing values or diagnostics.");
                Assert.Equal("None", csv["Downgrade"]);
            }
            finally { File.Delete(csvPath); }

            // Keep modem-wide samples and visible gaps even when no LTE cell is reported.
            modem.Cells = new CellsInfo();
            LteSnapshot gap = LteSnapshot.Take(modem, At.Intel, FailedTraffic, null, null);
            session.AddSnapshot(gap);
            Assert.True(gap.PrimaryCell is null && gap.SecondaryCells.Count == 0 && session.History["Rsrp"].Count == 2, "A missing serving cell lost the time slot.");
            Assert.True(double.IsNaN(session.History["Rsrp"][1]) && session.History["TempC"][1] == 40 && session.HandoverLog.Previous is null,
                "Missing-cell handling lost temperature or kept the handover baseline.");
            Assert.Null(SignalStatistic.Compute(session.History["Rsrp"]));
            Assert.Equal("0 B/s", MonitorFrame.TrafficRate(0));
            Assert.Equal("n/a", MonitorFrame.TrafficRate(null));
        }

        // Warning decisions survive changes to presentation labels in either direction.
        [Fact]
        public void WarningDecisionsAreIndependentOfDisplayText()
        {
            MonitorSession session = Fixtures.Session();
            session.AddSnapshot(LteSnapshot.Take(Fixture(), At.Intel, FailedTraffic, null, null));
            MonitorView view = new() { Unicode = false };
            RatConfig rat = new() { Allowed = "Automatic", AllowedRats = ["UMTS", "LTE"] };
            session.Summary = new ModemSummary { Model = "Test", RatConfig = rat, LegacyAllowed = Downgrade.IsLegacyRatAllowed(rat.AllowedRats) };
            Assert.True(session.Summary.LegacyAllowed);
            Assert.Contains("2G/3G enabled", Fixtures.Text(MonitorFrame.Build(session, view, 120)), StringComparison.Ordinal);
            rat = rat with { Allowed = "2G/3G disabled", AllowedRats = ["LTE", "NR"] };
            session.Summary = new ModemSummary { Model = "Test", RatConfig = rat, LegacyAllowed = Downgrade.IsLegacyRatAllowed(rat.AllowedRats) };
            Assert.False(session.Summary.LegacyAllowed);
            Assert.DoesNotContain("downgrade possible", Fixtures.Text(MonitorFrame.Build(session, view, 120)), StringComparison.Ordinal);
        }

        // The startup summary reads the RAT configuration once and derives the legacy capability from it.
        [Fact]
        public void SummaryReadsConfigurationOverAt()
        {
            FakeModem modem = new();
            modem.AtHandlers["Intel AT Tunnel"] = c => c switch
            {
                "AT" or "AT+XMCI=?" => At.Response(),
                "AT+XACT?" => At.Response("+XACT: 4,2,1,1,2,4,5,8,101,103,118,141,171"),
                _ => "ERROR\r\n",
            };
            ModemSummary summary = ModemSummary.Read(modem, "");
            Assert.True(summary.At!.Profile!.Id == "Intel" && summary.RatConfig!.Allowed == "3G+4G" && summary.LegacyAllowed && summary.Model == "Test");
        }
    }
}
