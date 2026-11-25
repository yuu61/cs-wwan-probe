using System.Diagnostics;
using System.Globalization;
using WwanProbe.Application;
using WwanProbe.Presentation;

namespace WwanProbe.Tests
{
    // The least time between automatic samples (MonitorConfig.MinSampleSpacing): a sample that ends at once
    // (a failed read) is not repeated back to back, in the TUI or in the sequential output.
    public class SampleSpacingTests
    {
        private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

        private static MonitorSession PlainSession(int interval, int count = 0)
        {
            MonitorSession session = Fixtures.Session(count: count, interval: interval);
            session.Modem = new FakeModem();
            session.Traffic = new FakeTraffic();
            return session;
        }

        [Fact]
        public void NextSampleKeepsTheIntervalAndSpacesFastSamples()
        {
            MonitorConfig backToBack = new();
            Assert.Equal(TimeSpan.FromSeconds(1), backToBack.MinSampleSpacing);
            // A normal sample (about 1 s) is followed at once: completion + Interval, not completion + 1 s.
            Assert.Equal(Ms(1100), backToBack.NextSampleAt(TimeSpan.Zero, Ms(1100)));
            // A sample that failed at once: 1 s after it started.
            Assert.Equal(Ms(1000), backToBack.NextSampleAt(TimeSpan.Zero, Ms(50)));
            Assert.Equal(Ms(6000), backToBack.NextSampleAt(Ms(5000), Ms(5020)));
            // An interval of a second or more is unchanged.
            MonitorConfig everyFive = new() { Interval = 5 };
            Assert.Equal(Ms(6200), everyFive.NextSampleAt(TimeSpan.Zero, Ms(1200)));
            Assert.Equal(Ms(5050), everyFive.NextSampleAt(TimeSpan.Zero, Ms(50)));
            MonitorConfig unspaced = new() { MinSampleSpacing = TimeSpan.Zero };
            Assert.Equal(Ms(50), unspaced.NextSampleAt(TimeSpan.Zero, Ms(50)));
        }

        [Fact]
        public void PlainPauseIsNeverNegative()
        {
            MonitorConfig backToBack = new();
            Assert.Equal(Ms(700), backToBack.PauseAfter(Ms(300)));
            Assert.Equal(TimeSpan.Zero, backToBack.PauseAfter(Ms(1300)));
            Assert.Equal(TimeSpan.Zero, backToBack.PauseAfter(TimeSpan.FromHours(1)));
            MonitorConfig everyTwo = new() { Interval = 2 };
            Assert.Equal(TimeSpan.FromSeconds(2), everyTwo.PauseAfter(Ms(300)));
            Assert.Equal(TimeSpan.FromSeconds(2), everyTwo.PauseAfter(Ms(5000)));
        }

        // Failing samples that end at once start MinSampleSpacing apart, not on every pass of the loop.
        // The start times are taken on the loop thread, as the loop counts the spacing: a sample starts and
        // is drawn in the same pass (fetching, with a new iteration), however late the worker runs it.
        [Fact]
        public void FastSamplesAreSpacedInTheTui()
        {
            Stopwatch watch = Stopwatch.StartNew();
            MonitorSampler sampler = new(() => new LteSnapshot { Timestamp = "test", Error = "no network" });
            MonitorSession session = Fixtures.Session();
            session.Config.MinSampleSpacing = Ms(200);
            List<TimeSpan> times = [];
            int startedAt = -1;
            FakeTerminal terminal = new()
            {
                QuitAfter = Ms(1100),
                OnFrame = (s, v) =>
                {
                    if (!v.Fetching || s.Iteration == startedAt) return;
                    startedAt = s.Iteration;
                    times.Add(watch.Elapsed);
                },
            };
            TuiMonitor.Loop(session, new MonitorView { RefreshRequested = true, Dirty = true }, sampler, terminal);
            Assert.InRange(times.Count, 1, 7);
            Assert.All(times.Zip(times.Skip(1)), p => Assert.True(p.Second - p.First >= Ms(150), $"Samples started {p.Second - p.First} apart."));
        }

        // A refresh (r) and resuming start a sample at once, however long the spacing.
        [Fact]
        public void RefreshAndResumeDoNotWaitForTheSpacing()
        {
            MonitorSampler sampler = new(() => new LteSnapshot { Timestamp = "test" });
            MonitorSession session = Fixtures.Session();
            session.Config.MinSampleSpacing = TimeSpan.FromHours(1);
            FakeTerminal terminal = new() { QuitAfter = TimeSpan.FromSeconds(10) };
            int stage = 0;
            terminal.OnFrame = (s, v) =>
            {
                if (v.Fetching) return;
                if (stage == 0 && s.Iteration == 1)
                {
                    terminal.Add(ConsoleKey.R, 'r');
                    stage = 1;
                }
                else if (stage == 1 && s.Iteration == 2)
                {
                    terminal.Add(ConsoleKey.P, 'p');
                    stage = 2;
                }
                else if (stage == 2 && v.Paused)
                {
                    terminal.Add(ConsoleKey.P, 'p');
                    stage = 3;
                }
                else if (stage == 3 && s.Iteration == 3)
                {
                    terminal.Add(ConsoleKey.Q, 'q');
                }
            };
            TuiMonitor.Loop(session, new MonitorView { RefreshRequested = true, Dirty = true }, sampler, terminal);
            Assert.False(terminal.QuitByTime, $"Refresh or resume waited for the spacing (stage {stage}).");
            Assert.Equal(3, session.Iteration);
        }

        // Sequential output with the default spacing: a sample that ends at once is followed by the rest
        // of the spacing, not by the next sample at once. Two samples take at least the spacing (less a
        // margin for the timer); a slow machine only makes them take longer.
        [Fact]
        public void PlainOutputWaitsTheSpacingAfterAFastSample()
        {
            MonitorSession session = PlainSession(interval: 0, count: 2);
            using StringWriter output = new(CultureInfo.InvariantCulture);
            Stopwatch watch = Stopwatch.StartNew();
            PlainMonitor.Run(session, CancellationToken.None, output: output);
            TimeSpan elapsed = watch.Elapsed;
            Assert.Equal(2, session.Iteration);
            Assert.True(elapsed >= session.Config.MinSampleSpacing - Ms(50), $"Two fast samples took {elapsed}.");
            Assert.NotEmpty(output.ToString());
        }

        // Cancelling (Ctrl+C) ends the wait for the next sample at once.
        [Fact]
        public void PlainOutputStopsWaitingWhenCancelled()
        {
            MonitorSession session = PlainSession(interval: 60);
            using CancellationTokenSource cancel = new(Ms(300));
            using StringWriter output = new(CultureInfo.InvariantCulture);
            Stopwatch watch = Stopwatch.StartNew();
            PlainMonitor.Run(session, cancel.Token, output: output);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Cancelling did not end the wait.");
            Assert.Equal(1, session.Iteration);
        }
    }
}
