using WwanProbe.Application;
using WwanProbe.Infrastructure.Gnss;
using WwanProbe.Presentation;

namespace WwanProbe.Tests
{
    // GPS normalization, sampling, rendering and CSV contract.
    public class GpsTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-24T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        private static GpsCoordinate Coordinate()
        {
            return new GpsCoordinate
            {
                PositionSource = "Satellite",
                IsRemoteSource = false,
                Timestamp = Now,
                PositionSourceTimestamp = null,
                Latitude = 35.123456,
                Longitude = 139.123456,
                Altitude = 0,
                AltitudeReferenceSystem = "Ellipsoid",
                Accuracy = 4.5,
                AltitudeAccuracy = 8,
                Speed = 0,
                Heading = 0,
                Hdop = 1.2,
                Pdop = 1.9,
                Vdop = 1.5,
            };
        }

        internal static GpsObservation Fix() => GpsObservation.FromReading(new GpsReading("Ready", Coordinate(), null), Now);

        [Fact]
        public void SourceFreshnessPermissionNumericValidityAndZeroMissing()
        {
            GpsObservation fix = Fix();
            Assert.True(fix.Status == "Fix" && fix.Latitude == 35.123456 && fix.Longitude == 139.123456, "Satellite coordinates lost precision.");
            Assert.True(fix.AltitudeM == 0 && fix.SpeedMps == 0 && fix.HeadingDeg == 0 && fix.Hdop == 1.2, "Valid zero or HDOP was lost.");
            Assert.True(fix.Pdop == 1.9 && fix.Vdop == 1.5, "PDOP or VDOP was lost.");
            Assert.Equal("2026-09-24T12:00:00.0000000+00:00", fix.Timestamp);

            foreach (string source in new[] { "WiFi", "Cellular", "IPAddress", "Default", "Unknown", "Obfuscated" })
            {
                GpsObservation gps = GpsObservation.FromReading(new GpsReading("Ready", Coordinate() with { PositionSource = source }, null), Now);
                Assert.True(gps.Status == "NoFix" && gps.Latitude is null && gps.Longitude is null && gps.Timestamp is null, $"Non-GPS source {source} leaked a position.");
            }
            GpsObservation remote = GpsObservation.FromReading(new GpsReading("Ready", Coordinate() with { IsRemoteSource = true }, null), Now);
            Assert.True(remote.Source == "Remote" && remote.Latitude is null, "A remote GPS was labeled as local.");

            foreach (int seconds in new[] { -16, 6 })
            {
                GpsObservation gps = GpsObservation.FromReading(new GpsReading("Ready", Coordinate() with { PositionSourceTimestamp = Now.AddSeconds(seconds) }, null), Now);
                Assert.True(gps.Status == "Stale" && gps.Latitude is null, "Invalid source time was hidden by the recent delivery time.");
            }
            foreach (string state in new[] { "Disabled", "NotAvailable", "NoData", "Initializing", "NotInitialized" })
            {
                GpsObservation gps = GpsObservation.FromReading(new GpsReading(state, Coordinate(), null), Now);
                Assert.True(gps.Status != "Fix" && gps.Latitude is null, $"State {state} retained old coordinates.");
            }
            Assert.NotEqual("Fix", GpsObservation.FromReading(new GpsReading("Ready", Coordinate() with { Latitude = 91 }, null), Now).Status);
            GpsCoordinate degraded = Coordinate() with
            {
                Latitude = 0,
                Longitude = 0,
                Speed = double.NaN,
                Heading = double.PositiveInfinity,
                Accuracy = -1,
                AltitudeAccuracy = null,
                Hdop = null,
                Pdop = null,
                Vdop = null,
            };
            GpsObservation zero = GpsObservation.FromReading(new GpsReading("Ready", degraded, null), Now);
            Assert.True(zero.Status == "Fix" && zero.Latitude == 0 && zero.Longitude == 0, "Zero coordinates were treated as missing.");
            Assert.True(zero.SpeedMps is null && zero.HeadingDeg is null && zero.AccuracyM is null && zero.AltitudeM is null &&
                zero.Hdop is null && zero.Pdop is null && zero.Vdop is null, "Unavailable numeric values became zero.");
            using GpsSource notStarted = new(null, "access denied");
            GpsObservation failed = notStarted.Observe();
            Assert.True(failed.Status == "Unavailable" && failed.Error == "access denied", "Startup failure was not exposed.");
        }

        // Same receiver crosses the background sampler; a GPS failure cannot invalidate LTE.
        [Fact]
        public void FailureIsolationBackgroundSamplerFrameAndCsv()
        {
            FakeModem modem = new();
            FakeTraffic traffic = new();
            using GpsSource receiver = new(null, "GPS unavailable");
            LteSnapshot snapshot = LteSnapshot.Take(modem, null, traffic, receiver, null);
            Assert.True(snapshot.Error is null && snapshot.Gps!.Error == "GPS unavailable", "GPS failure invalidated LTE.");
            MonitorSampler sampler = new(() => LteSnapshot.Take(modem, null, traffic, receiver, null));
            sampler.Start();
            Assert.True(SpinWait.SpinUntil(() => sampler.IsCompleted, 10000));
            LteSnapshot background = sampler.Receive();
            Assert.True(background.Gps!.Error == snapshot.Gps.Error && background.Error is null, "Background GPS contract differs.");

            MonitorSession session = Fixtures.Session();
            session.Snapshot = snapshot;
            session.Iteration = 1;
            MonitorView view = new() { Unicode = false };
            Assert.Contains("GPS: Unavailable (GPS unavailable)", Fixtures.Text(MonitorFrame.Build(session, view, 100)), StringComparison.Ordinal);
            snapshot.Gps = Fix();
            string text = Fixtures.Text(MonitorFrame.Build(session, view, 100));
            Assert.Contains("Lat: 35.123456   Lon: 139.123456", text, StringComparison.Ordinal);
            Assert.Contains("Source: Satellite", text, StringComparison.Ordinal);
            Assert.Contains("HDOP: 1.2   PDOP: 1.9   VDOP: 1.5", text, StringComparison.Ordinal);

            string csvPath = Fixtures.TempFile();
            try
            {
                SnapshotLog.Initialize(csvPath);
                SnapshotLog.Append(csvPath, snapshot);
                snapshot.Gps = GpsObservation.FromReading(new GpsReading("NoData", null, null));
                SnapshotLog.Append(csvPath, snapshot);
                List<Dictionary<string, string>> rows = Fixtures.ReadCsv(csvPath);
                Assert.True(rows[0]["GPS_Status"] == "Fix" && rows[0]["GPS_Latitude"] == "35.123456" && rows[0]["GPS_Speed_mps"] == "0", "GPS CSV fix or units were lost.");
                Assert.True(rows[1]["GPS_Status"] == "NoFix" && string.IsNullOrEmpty(rows[1]["GPS_Latitude"]) && string.IsNullOrEmpty(rows[1]["GPS_Timestamp_UTC"]), "CSV retained stale coordinates.");
            }
            finally { File.Delete(csvPath); }
            snapshot.Gps = null;
            Assert.DoesNotContain("GPS / GNSS", Fixtures.Text(MonitorFrame.Build(session, view, 100)), StringComparison.Ordinal);
        }
    }
}
