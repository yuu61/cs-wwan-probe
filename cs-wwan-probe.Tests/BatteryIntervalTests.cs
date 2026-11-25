using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using WwanProbe.Application;
using WwanProbe.Presentation;

namespace WwanProbe.Tests
{
    // -BatteryInterval: the power state is read as each sample completes; while Windows saves power the next
    // automatic sample waits at least BatteryInterval and the title shows it. Without the option nothing changes.
    public class BatteryIntervalTests
    {
        private const string TitleLeft = " wwan-probe LTE Signal Monitor";

        private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

        private static string Title(MonitorSession session, int width = 100) => MonitorFrame.Build(session, new MonitorView { Unicode = true }, width).Body[0].Text;

        private static MonitorSession PlainSession(int batteryInterval, FakePowerState power)
        {
            MonitorSession session = Fixtures.Session(count: 2);
            session.Modem = new FakeModem();
            session.Traffic = new FakeTraffic();
            session.Config.BatteryInterval = batteryInterval;
            session.Config.MinSampleSpacing = TimeSpan.Zero;
            session.Power = power;
            return session;
        }

        [Fact]
        public void EffectiveIntervalIsTheLongerOneOnlyWhileSavingWithTheOption()
        {
            Assert.Equal(0, new MonitorConfig().EffectiveInterval(true));
            Assert.Equal(3, new MonitorConfig { Interval = 3 }.EffectiveInterval(true));
            MonitorConfig battery = new() { BatteryInterval = 5 };
            Assert.Equal(0, battery.EffectiveInterval(false));
            Assert.Equal(5, battery.EffectiveInterval(true));
            Assert.Equal(7, new MonitorConfig { Interval = 2, BatteryInterval = 7 }.EffectiveInterval(true));
            Assert.Equal(10, new MonitorConfig { Interval = 10, BatteryInterval = 5 }.EffectiveInterval(true));
            Assert.Equal(2, new MonitorConfig { Interval = 2, BatteryInterval = 7 }.EffectiveInterval(false));
            // Counted from the completion like Interval, with the spacing from the start kept.
            Assert.Equal(Ms(6200), battery.NextSampleAt(TimeSpan.Zero, Ms(1200), saving: true));
            Assert.Equal(Ms(1200), battery.NextSampleAt(TimeSpan.Zero, Ms(1200), saving: false));
            Assert.Equal(Ms(1200), battery.NextSampleAt(TimeSpan.Zero, Ms(1200)));
            Assert.Equal(Ms(1000), battery.NextSampleAt(TimeSpan.Zero, Ms(50)));
            Assert.Equal(TimeSpan.FromSeconds(5), battery.PauseAfter(Ms(300), saving: true));
            Assert.Equal(Ms(700), battery.PauseAfter(Ms(300), saving: false));
        }

        // The TUI reads the power state once per committed sample, never on the passes in between. A sample
        // that completed while saving is followed BatteryInterval after its completion; the first one that
        // completed on AC power is followed at the normal pace again. The mark is shown only while saving.
        [Fact]
        public void TuiWaitsTheBatteryIntervalAfterASampleThatCompletedWhileSaving()
        {
            Stopwatch watch = Stopwatch.StartNew();
            ConcurrentQueue<TimeSpan> starts = new();
            MonitorSampler sampler = new(() =>
            {
                starts.Enqueue(watch.Elapsed);
                return new LteSnapshot { Timestamp = "test" };
            });
            MonitorSession session = Fixtures.Session(count: 4);
            session.Config.BatteryInterval = 1;
            session.Config.MinSampleSpacing = Ms(100);
            FakePowerState power = new(true, true, false);
            session.Power = power;
            List<string> titles = [];
            FakeTerminal terminal = new() { OnFrame = (s, v) => titles.Add(MonitorFrame.Build(s, v, 100).Body[0].Text) };
            TuiMonitor.Loop(session, new MonitorView { RefreshRequested = true, Dirty = true }, sampler, terminal);
            Assert.False(terminal.QuitByTime, "The samples did not finish.");
            Assert.Equal(4, session.Iteration);
            Assert.Equal(session.Iteration, power.Reads);
            List<TimeSpan> times = [.. starts];
            Assert.Equal(4, times.Count);
            Assert.True(times[1] - times[0] >= TimeSpan.FromSeconds(1), $"Second sample after {times[1] - times[0]}.");
            Assert.True(times[2] - times[1] >= TimeSpan.FromSeconds(1), $"Third sample after {times[2] - times[1]}.");
            Assert.True(times[3] - times[2] < Ms(800), $"Back on AC power, the fourth sample came {times[3] - times[2]} later.");
            Assert.DoesNotContain("SAVER", titles[0], StringComparison.Ordinal);
            Assert.Contains(TitleLeft + new string(' ', 42) + "#1/4  [SAVER 1s]  [RUNNING]", titles);
            Assert.Equal(TitleLeft + new string(' ', 57) + "#4/4  [DONE]", titles[^1]);
        }

        // r samples at once while saving, however long the battery interval.
        [Fact]
        public void RefreshDoesNotWaitForTheBatteryInterval()
        {
            MonitorSampler sampler = new(() => new LteSnapshot { Timestamp = "test" });
            MonitorSession session = Fixtures.Session(count: 2);
            session.Config.BatteryInterval = 3600;
            session.Power = new FakePowerState(true);
            FakeTerminal terminal = new() { QuitAfter = TimeSpan.FromSeconds(10) };
            bool refreshed = false;
            terminal.OnFrame = (s, v) =>
            {
                if (refreshed || v.Fetching || s.Iteration != 1) return;
                Assert.True(s.PowerSaving);
                terminal.Add(ConsoleKey.R, 'r');
                refreshed = true;
            };
            TuiMonitor.Loop(session, new MonitorView { RefreshRequested = true, Dirty = true }, sampler, terminal);
            Assert.False(terminal.QuitByTime, "Refresh waited for the battery interval.");
            Assert.Equal(2, session.Iteration);
        }

        // Sequential output waits the same effective interval and shows the same mark.
        [Fact]
        public void PlainOutputWaitsTheBatteryIntervalWhileSaving()
        {
            FakePowerState power = new(true);
            MonitorSession session = PlainSession(1, power);
            using StringWriter output = new(CultureInfo.InvariantCulture);
            Stopwatch watch = Stopwatch.StartNew();
            PlainMonitor.Run(session, CancellationToken.None, output: output);
            Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1), $"Two samples took {watch.Elapsed}.");
            Assert.Equal(2, session.Iteration);
            Assert.Equal(2, power.Reads);
            Assert.Contains("[SAVER 1s]", output.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void PlainOutputIgnoresThePowerStateWithoutTheOption()
        {
            FakePowerState power = new(true);
            MonitorSession session = PlainSession(0, power);
            using StringWriter output = new(CultureInfo.InvariantCulture);
            PlainMonitor.Run(session, CancellationToken.None, output: output);
            Assert.Equal(2, session.Iteration);
            Assert.Equal(0, power.Reads);
            Assert.DoesNotContain("SAVER", output.ToString(), StringComparison.Ordinal);
        }

        // With the option, the title shows the interval in force while saving and drops the mark once a
        // sample completes on AC power.
        [Fact]
        public void TheTitleShowsTheIntervalInForceWhileSaving()
        {
            MonitorSession session = Fixtures.Session(count: 3, interval: 2);
            session.Config.BatteryInterval = 5;
            FakePowerState power = new(true, false, true);
            session.Power = power;
            session.AddSnapshot(Snapshots.Handover());
            Assert.True(session.PowerSaving);
            Assert.Equal(TitleLeft + new string(' ', 42) + "#1/3  [SAVER 5s]  [RUNNING]", Title(session));
            session.AddSnapshot(Snapshots.Handover());
            Assert.False(session.PowerSaving);
            Assert.Equal(TitleLeft + new string(' ', 54) + "#2/3  [RUNNING]", Title(session));
            // An Interval longer than BatteryInterval stays in force, and the mark shows it.
            session.Config.Interval = 10;
            session.AddSnapshot(Snapshots.Handover());
            Assert.Equal(TitleLeft + new string(' ', 41) + "#3/3  [SAVER 10s]  [RUNNING]", Title(session));
            Assert.Equal(3, power.Reads);
        }

        // Without -BatteryInterval the power state is never read: the screen (text and colors) is that of a
        // session without one, even while Windows saves power. The CSV rows are the same with the option too.
        [Fact]
        public void WithoutTheOptionTheScreenAndCsvAreUnchanged()
        {
            string[] paths = [Fixtures.TempFile(), Fixtures.TempFile(), Fixtures.TempFile()];
            try
            {
                MonitorSession plain = Fixtures.Session(count: 3, csvPath: paths[0]);
                MonitorSession unused = Fixtures.Session(count: 3, csvPath: paths[1]);
                FakePowerState unread = new(true);
                unused.Power = unread;
                MonitorSession withOption = Fixtures.Session(count: 3, csvPath: paths[2]);
                withOption.Config.BatteryInterval = 5;
                withOption.Power = new FakePowerState(true);
                foreach (MonitorSession session in new[] { plain, unused, withOption })
                {
                    SnapshotLog.Initialize(session.Config.CsvPath);
                    session.AddSnapshot(Snapshots.Handover());
                }
                Assert.Equal(0, unread.Reads);
                Assert.False(unused.PowerSaving);
                Assert.Equal(TitleLeft + new string(' ', 54) + "#1/3  [RUNNING]", Title(unused));
                MonitorView view = new() { Unicode = true };
                Frame expected = MonitorFrame.Build(plain, view, 100);
                Frame actual = MonitorFrame.Build(unused, view, 100);
                Assert.Equal(expected.Body.Select(l => (l.Text, l.Color, l.GrayLevel)), actual.Body.Select(l => (l.Text, l.Color, l.GrayLevel)));
                // The footer names each session's own CSV file.
                Assert.Equal(
                    (expected.Footer.Text.Replace(paths[0], "log.csv", StringComparison.Ordinal), expected.Footer.Color),
                    (actual.Footer.Text.Replace(paths[1], "log.csv", StringComparison.Ordinal), actual.Footer.Color));
                // Back to back as before: the next sample is due when this one (1.2 s) completed.
                Assert.Equal(Ms(1200), unused.Config.NextSampleAt(TimeSpan.Zero, Ms(1200), unused.PowerSaving));
                Assert.True(withOption.PowerSaving);
                byte[] csv = File.ReadAllBytes(paths[0]);
                Assert.Equal(csv, File.ReadAllBytes(paths[1]));
                Assert.Equal(csv, File.ReadAllBytes(paths[2]));
            }
            finally
            {
                foreach (string path in paths) File.Delete(path);
            }
        }
    }
}
