using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WwanProbe.Application;
using WwanProbe.Infrastructure.Gnss;
using WwanProbe.Presentation;

namespace WwanProbe.Tests
{
    // NMEA satellite parsing, helper state, rendering and keys.
    // Sentences are real L860-GL output (2026-09-24, 2026-09-25, 2026-09-28) unless noted; coordinates are never involved.
    public class NmeaTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-24T12:00:00Z", CultureInfo.InvariantCulture);

        private static readonly string[] Capture =
        {
            "$GPGSV,3,1,12,02,87,163,17,08,60,031,32,01,59,199,,07,46,255,16,1*6B",
            "$GPGSV,3,2,12,30,29,298,,27,28,066,21,16,20,121,,10,16,053,19,1*6E",
            "$GPGSV,3,3,12,14,14,311,,03,02,172,,17,02,265,,23,00,029,,1*6E",
            "$GLGSV,2,1,06,74,66,141,,75,55,331,25,84,47,025,25,73,16,147,,1*70",
            "$GLGSV,2,2,06,86,15,228,,76,03,327,,1*78",
            "$GPGSA,A,3,02,08,07,27,10,,,,,,,,2.93,2.01,2.13*00",
            "$GNGSA,A,3,02,08,07,27,10,,,,,,,,2.93,2.01,2.13,1*03",
            "$GNGSA,A,3,75,84,,,,,,,,,,,2.93,2.01,2.13,2*07",
        };

        private static string Key(NmeaSatellite s) => $"{s.System}:{s.Id}";

        private static NmeaSatelliteState State(IEnumerable<string> sentences, DateTimeOffset now)
        {
            NmeaSatelliteState state = new NmeaSatelliteState();
            foreach (string s in sentences) state.Add(s, now);
            return state;
        }

        private static string Sentence(string body)
        {
            int checksum = 0;
            foreach (char c in body) checksum ^= c;
            return string.Format(CultureInfo.InvariantCulture, "${0}*{1:X2}", body, checksum);
        }

        private static string UsedKeys(NmeaReport report) => string.Join(',', report.Satellites.Where(s => s.Used).Select(Key));

        // Driver events cut sentences anywhere; corrupt sentences and noise are dropped.
        [Fact]
        public void StreamFramingAndChecksum()
        {
            string stream = string.Join("\r\n", Capture) + "\r\n" + "$GPGSV,1,1,00,1*00" + "\r\nnoise\r\n" + "$GPGGA,1234";
            List<string> sentences = new List<string>();
            string rest = "";
            for (int i = 0; i < stream.Length; i += 37)
            {
                NmeaSplit split = Nmea.Split(rest + stream.Substring(i, Math.Min(37, stream.Length - i)));
                sentences.AddRange(split.Sentences);
                rest = split.Rest;
            }
            Assert.Equal(Capture, sentences);
            Assert.Equal("$GPGGA,1234", rest);
            Assert.Equal("", Nmea.Split("$" + new string('A', 300)).Rest);
        }

        [Fact]
        public void GsvCyclesGsaUnionAgingAndConstellations()
        {
            NmeaSatelliteState state = State(Capture, Now);
            NmeaReport report = state.Report(Now);
            Assert.True(report.Satellites.Count == 18 && report.UsedCount == 7, "GPGSA and GNGSA were double counted or satellites were lost.");
            NmeaSatellite gps2 = report.Satellites.Single(s => s.System == "GPS" && s.Id == 2);
            Assert.True(gps2.ElevationDeg == 87 && gps2.AzimuthDeg == 163 && gps2.SnrDbHz == 17 && gps2.Used && gps2.Signal == "1", "GSV fields were misread.");
            List<NmeaSatellite> glonass = report.Satellites.Where(s => s.System == "GLONASS").ToList();
            Assert.True(glonass.Count == 6 && string.Join(',', glonass.Where(s => s.Used).Select(s => s.Id)) == "75,84", "GLONASS usage did not match GNGSA system 2.");
            Assert.Null(glonass.Single(s => s.Id == 74).SnrDbHz);
            List<string> order = report.Satellites.Select(Key).ToList();
            Assert.Equal("GPS:8,GLONASS:75,GLONASS:84,GPS:27,GPS:10,GPS:2,GPS:7,GLONASS:74,GPS:1", string.Join(',', order.Take(9)));
            Assert.Equal("GPS:3,GPS:17,GPS:23", string.Join(',', order.TakeLast(3)));

            // A used satellite precedes a stronger unused one; equal SNR and elevation fall back to constellation order.
            NmeaSatelliteState sortState = State(new[]
            {
                "$GPGSV,1,1,03,05,10,100,40,06,20,200,30,07,30,300,,1*00",
                "$GLGSV,1,1,01,70,10,100,40,1*00",
                "$GPGSA,A,3,06" + new string(',', 11) + ",2.0,1.0,1.0,1*00",
            }, Now);
            Assert.Equal("GPS:6,GPS:5,GLONASS:70,GPS:7", string.Join(',', sortState.Report(Now).Satellites.Select(Key)));

            // An incomplete or out-of-order cycle never replaces the last complete one.
            state.Add("$GPGSV,3,1,01,05,10,100,30,1*00", Now);
            state.Add("$GPGSV,3,3,01,06,10,100,30,1*00", Now);
            Assert.Equal(12, state.Report(Now).Satellites.Count(s => s.System == "GPS"));
            // Losing the fix clears usage; stale constellations disappear.
            state.Add("$GNGSA,A,1,,,,,,,,,,,,,,,,1*1D", Now);
            state.Add("$GNGSA,A,1,,,,,,,,,,,,,,,,2*1E", Now);
            state.Add("$GPGSA,A,1,,,,,,,,,,,,,,,*1E", Now);
            Assert.Equal(0, state.Report(Now).UsedCount);
            state.Add("$GPGSV,1,1,00,1*64", Now.AddSeconds(8));
            NmeaReport later = state.Report(Now.AddSeconds(11));
            Assert.True(later.Satellites.Count == 0 && later.UsedCount == 0, "Expired or empty cycles kept satellites.");
            Assert.Equal("SBAS", Nmea.SatelliteSystem("GP", 40));
            Assert.Equal("QZSS", Nmea.SatelliteSystem("GP", 194));
            Assert.Equal("Galileo", Nmea.SatelliteSystem("GA", 5));
        }

        // L860-GL GSA with system IDs 4 and 5 swapped (real output, 2026-09-28): QZSS 195 is listed under
        // BeiDou (4) and BeiDou 21 under QZSS (5). The BeiDou GSV is a minimal single cycle containing 21.
        [Fact]
        public void GsaSystemIds4And5SwappedByTheReceiver()
        {
            string[] swapCapture =
            {
                "$GPGSV,3,1,11,20,75,295,24,05,64,004,24,21,48,070,16,15,40,258,,1*6B",
                "$GPGSV,3,2,11,11,38,151,25,30,36,063,17,29,26,270,15,18,17,319,14,1*66",
                "$GPGSV,3,3,11,24,10,196,,07,09,038,17,06,05,146,,1*5E",
                "$GLGSV,3,1,09,85,56,307,,70,51,296,,69,44,020,27,79,15,092,18,1*77",
                "$GLGSV,3,2,09,83,13,166,,78,10,042,,68,05,052,,71,05,252,,1*7A",
                "$GLGSV,3,3,09,80,01,140,,1*4D",
                "$GQGSV,2,1,05,196,85,208,,195,73,164,29,199,30,190,,194,09,182,,1*6F",
                "$GQGSV,2,2,05,200,07,237,,1*63",
                "$GBGSV,1,1,02,21,52,034,34,04,44,139,33,1*00",
                "$GPGSA,A,3,20,05,11,30,07,,,,,,,,3.31,2.19,2.48*04",
                "$GNGSA,A,3,20,05,11,30,07,,,,,,,,3.31,2.19,2.48,1*07",
                "$GNGSA,A,3,69,,,,,,,,,,,,3.31,2.19,2.48,2*08",
                "$GNGSA,A,3,195,,,,,,,,,,,,3.31,2.19,2.48,4*3C",
                "$GNGSA,A,3,21,,,,,,,,,,,,3.31,2.19,2.48,5*03",
            };
            NmeaSatelliteState state = State(swapCapture, Now);
            NmeaReport report = state.Report(Now);
            List<string> used = report.Satellites.Where(s => s.Used).Select(Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            Assert.Equal("BeiDou:21,GLONASS:69,GPS:11,GPS:20,GPS:30,GPS:5,GPS:7,QZSS:195", string.Join(',', used));
            Assert.Equal(used.Count, report.UsedCount);
            // QZSS 195 also listed by GPGSA (QZSS by its ID range) counts once.
            state.Add("$GPGSA,A,3,20,05,11,30,07,195,,,,,,,3.31,2.19,2.48*00", Now);
            Assert.Equal(8, state.Report(Now).UsedCount);

            // A low BeiDou ID under system 5 is BeiDou when that receiver numbers QZSS 193-202.
            NmeaSatelliteState low = State(new[]
            {
                "$GBGSV,1,1,01,07,13,181,21,1*00", "$GQGSV,1,1,01,194,09,182,20,1*00", "$GNGSA,A,3,07" + new string(',', 11) + ",2.0,1.0,1.0,5*00",
            }, Now);
            Assert.Equal("BeiDou:7", UsedKeys(low.Report(Now)));
            // NMEA 4.11 numbering (QZSS 1-10): the nominal system wins when both satellites are in view.
            NmeaSatelliteState strict = State(new[]
            {
                "$GBGSV,1,1,01,02,40,100,30,1*00", "$GQGSV,1,1,01,02,60,200,30,1*00", "$GNGSA,A,3,02" + new string(',', 11) + ",2.0,1.0,1.0,5*00",
            }, Now);
            Assert.Equal("QZSS:2", UsedKeys(strict.Report(Now)));
        }

        // Fix state from GGA / RMC / GSA: real L860-GL field values (2026-09-25) with dummy coordinates.
        public static NmeaFix GoodFix()
        {
            NmeaSatelliteState state = State(new[]
            {
                Sentence("GNGGA,205350.033,3500.0000,N,13900.0000,E,1,13,0.82,14.600,M,39.432,M,,"),
                Sentence("GNRMC,205349.083,A,3500.0000,N,13900.0000,E,26.136,300.9,240926,,,A,V"),
                Sentence("GNGSA,A,3,65,88,66,81,,,,,,,,,1.36,0.79,1.11,2"),
            }, Now);
            return state.Report(Now).Fix;
        }

        [Fact]
        public void FixStateGnPreferenceAgingAndNoCoordinates()
        {
            NmeaSatelliteState state = State(new[]
            {
                Sentence("GNGGA,205350.033,3500.0000,N,13900.0000,E,1,13,0.82,14.600,M,39.432,M,,"),
                Sentence("GNRMC,205349.083,A,3500.0000,N,13900.0000,E,26.136,300.9,240926,,,A,V"),
                Sentence("GNGSA,A,3,65,88,66,81,,,,,,,,,1.36,0.79,1.11,2"),
            }, Now);
            NmeaFix fix = state.Report(Now).Fix;
            Assert.True(fix.Dimension == "3D" && fix.Quality == "GPS" && fix.SatellitesUsed == 13, "GSA / GGA fix fields were misread.");
            Assert.True(fix.AltitudeMslM == 14.6 && fix.GeoidSeparationM == 39.432 && fix.Valid == true && fix.Mode == "Autonomous", "GGA altitude or RMC status was misread.");
            Assert.DoesNotMatch("3500|13900|205350", JsonSerializer.Serialize(state.Report(Now)));
            // A GP duplicate does not override the combined GN solution; unknown codes stay visible.
            state.Add(Sentence("GPGGA,205351.033,3500.0000,N,13900.0000,E,2,05,0.82,,M,,M,,"), Now);
            state.Add(Sentence("GNRMC,205352.083,V,,,,,,,240926,,,X,V"), Now);
            fix = state.Report(Now).Fix;
            Assert.True(fix.Quality == "GPS" && fix.SatellitesUsed == 13, "A GP sentence replaced the GN solution.");
            Assert.True(fix.Valid == false && fix.Mode == "Unknown (X)", "An invalid RMC or unknown mode was hidden.");
            state.Add(Sentence("GNGGA,205353.033,,,,,0,00,,,M,,M,,"), Now);
            fix = state.Report(Now).Fix;
            Assert.True(fix.Quality == "Invalid" && fix.SatellitesUsed == 0 && fix.AltitudeMslM is null, "A no-fix GGA kept the old altitude.");
            fix = state.Report(Now.AddSeconds(11)).Fix;
            Assert.True(fix.Dimension is null && fix.Quality is null && fix.Valid is null, "Expired fix values were kept.");
        }

        private static NmeaObservation PublishedObservation()
        {
            NmeaReport report = State(Capture, Now).Report(Now);
            string directory = Path.Combine(Path.GetTempPath(), $"wwan-nmea-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "state.json");
                Assert.Null(NmeaStateFile.Read(path));
                NmeaHelperState published = new NmeaHelperState
                {
                    Status = "Receiving",
                    UpdatedUnixMs = Now.ToUnixTimeMilliseconds(),
                    NmeaUnixMs = Now.ToUnixTimeMilliseconds(),
                    Satellites = report.Satellites.ToList(),
                    UsedCount = report.UsedCount,
                    Fix = GoodFix(),
                };
                Assert.True(NmeaStateFile.Write(path, published), "State was not written.");
                using (FileStream held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    Assert.False(NmeaStateFile.Write(path, published), "A replace during a read did not report a retry.");
                }
                Assert.True(NmeaStateFile.Write(path, published), "The retry after the read failed.");
                return NmeaObservation.FromState(NmeaStateFile.Read(path), now: Now.AddSeconds(2));
            }
            finally { Directory.Delete(directory, true); }
        }

        // Helper state file round trip. A replace during a read fails softly and succeeds on retry.
        [Fact]
        public void HelperStateFileObservationStatesAndFailureIsolation()
        {
            NmeaObservation observation = PublishedObservation();
            Assert.True(observation.Status == "Receiving" && observation.InView == 18 && observation.Used == 7, "State round trip lost counts.");
            Assert.Equal("GPS,GLONASS", string.Join(',', observation.Systems.Select(s => s.System)));
            Assert.True(observation.Systems[0].InView == 12 && observation.Systems[1].Used == 2, "Per-constellation counts are wrong.");
            Assert.True(observation.Fix!.Dimension == "3D" && observation.Fix.AltitudeMslM == 14.6 && observation.Fix.Valid == true, "The fix state did not survive the state file.");

            NmeaHelperState receiving = new NmeaHelperState { Status = "Receiving", UpdatedUnixMs = Now.ToUnixTimeMilliseconds() };
            Assert.Equal("Starting", NmeaObservation.FromState(null, now: Now).Status);
            Assert.Equal("Stale", NmeaObservation.FromState(receiving, now: Now.AddSeconds(11)).Status);
            Assert.False(NmeaObservation.FromState(receiving, now: Now).NmeaReceived);
            NmeaObservation failed = NmeaObservation.FromState(new NmeaHelperState { Status = "Error", Error = "No GNSS device interface found." }, helperExited: true, now: Now);
            Assert.True(failed.Status == "Unavailable" && failed.Error == "No GNSS device interface found.", "The helper error was not shown.");
            NmeaObservation crashed = NmeaObservation.FromState(receiving, helperExited: true, exitCode: 1, now: Now);
            Assert.True(crashed.Status == "Unavailable" && crashed.Error!.Contains("code 1"), "A crashed helper looked alive.");
            NmeaObservation declined = new NmeaReceiver(null, null, "NMEA helper not started: The operation was canceled by the user.").Observe();
            Assert.True(declined.Status == "Unavailable" && declined.Error!.Contains("canceled"), "A declined UAC prompt was not shown.");

            FakeModem modem = new FakeModem();
            FakeTraffic traffic = new FakeTraffic();
            LteSnapshot snapshot = LteSnapshot.Take(modem, null, traffic, null, new NmeaReceiver(null, null, "declined"));
            Assert.True(snapshot.Error is null && snapshot.Satellites!.Status == "Unavailable", "An NMEA failure invalidated LTE.");
            // A helper handle that cannot be queried (unstarted Process) must neither fail the LTE sample nor the shutdown.
            snapshot = LteSnapshot.Take(modem, null, traffic, null, new NmeaReceiver(new Process(), null, null));
            Assert.True(snapshot.Error is null && snapshot.Satellites!.Status == "Unavailable", "A broken receiver failed the LTE sample.");
            Assert.Null(LteSnapshot.Take(modem, null, traffic, null, null).Satellites);
            string directory = Path.Combine(Path.GetTempPath(), $"wwan-nmea-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                new NmeaReceiver(new Process(), directory, null).Stop(10);
                Assert.True(File.Exists(Path.Combine(directory, "stop")), "The stop request was not left for the helper.");
            }
            finally { Directory.Delete(directory, true); }
        }

        // Main screen summary, the [s] view and its footer.
        [Fact]
        public void SatelliteSummaryColoredListScrollingAndOptIn()
        {
            MonitorSession session = Fixtures.Session();
            session.NmeaReceiver = new NmeaReceiver(null, null, null);
            LteSnapshot snapshot = LteSnapshot.Take(new FakeModem(), null, new FakeTraffic(), null, null);
            snapshot.Satellites = PublishedObservation();
            session.Snapshot = snapshot;
            session.Iteration = 1;
            MonitorView view = new MonitorView { Unicode = false, LastHeight = 30 };
            Frame frame = MonitorFrame.Build(session, view, 100);
            string text = Fixtures.Text(frame);
            Assert.Contains("Satellites: 18 in view (GPS 12, GLONASS 6), 7 used  [s]", text);
            Assert.Contains("[s] Satellites", frame.Footer.Text);
            Assert.Contains("Fix (NMEA): 3D   Quality: GPS   Mode: Autonomous (valid)   Sats used: 13", text);
            Assert.Contains("Altitude MSL: 14.6 m   Geoid separation: 39.4 m", text);

            view.SatelliteVisible = true;
            frame = MonitorFrame.Build(session, view, 100);
            List<FrameLine> rows = frame.Body.Where(l => Regex.IsMatch(l.Text, "^ (GPS|GLONASS) ")).ToList();
            Assert.True(rows.Count == 18 && frame.Footer.Text.Contains("[s] Monitor"), "The satellite list is incomplete.");
            Assert.True(rows.Count(r => r.Color == ConsoleColor.Green) == 12 && rows.Count(r => r.Color == ConsoleColor.Magenta) == 6, "Constellations are not colored.");
            Assert.Matches("^ GPS +8 +1 +60 +31 +32 +yes ", rows[0].Text);
            Assert.Matches(@"^ GPS +1 +1 +59 +199 +n/a +\[ +not tracked +\]", rows.Single(r => Regex.IsMatch(r.Text, "^ GPS +1 ")).Text);
            Assert.Contains(" yes ", rows.Single(r => Regex.IsMatch(r.Text, "^ GPS +2 ")).Text);
            view.LastHeight = 12;
            view.SatelliteOffset = 99;
            frame = MonitorFrame.Build(session, view, 100);
            Assert.True(view.SatelliteOffset == 16 && frame.Body.Count <= 12 && Fixtures.Text(frame).Contains("Rows 17-18 / 18"), "Satellite scrolling does not fit the screen.");
            session.NmeaReceiver = null;
            snapshot.Satellites = null;
            Assert.Contains("start cs-wwan-probe with -Nmea", Fixtures.Text(MonitorFrame.Build(session, view, 100)));
            view.SatelliteVisible = false;
            frame = MonitorFrame.Build(session, view, 100);
            Assert.DoesNotContain("Satellites:", Fixtures.Text(frame));
            Assert.DoesNotContain("[s]", frame.Footer.Text);
        }

        [Fact]
        public void STogglesTheSatelliteListExclusiveWithH()
        {
            MonitorView view = new MonitorView { HandoverVisible = true, HandoverOffset = 2 };
            TuiMonitor.ReadInput(view, Keys.From(Keys.Of(ConsoleKey.S, ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.UpArrow)));
            Assert.True(view.SatelliteVisible && !view.HandoverVisible && view.SatelliteOffset == 1 && view.ChartRows == 2, "s did not open a scrollable list.");
            TuiMonitor.ReadInput(view, Keys.From(Keys.Of(ConsoleKey.H)));
            Assert.True(view.HandoverVisible && !view.SatelliteVisible, "h and s views overlapped.");
            TuiMonitor.ReadInput(view, Keys.From(Keys.Of(ConsoleKey.S, ConsoleKey.S)));
            Assert.True(!view.SatelliteVisible && !view.HandoverVisible && view.SatelliteOffset == 0, "s did not return to the monitor.");
        }
    }
}
