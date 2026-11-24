using WwanProbe.Application;
using WwanProbe.Domain;
using WwanProbe.Presentation;

namespace WwanProbe.Tests
{
    // The TUI loop with a real background sampler and simulated console input/output.
    public class TuiResponsivenessTests
    {
        private static MonitorSampler Sampler(int delayMs) => new MonitorSampler(() =>
        {
            Thread.Sleep(delayMs);
            return new LteSnapshot
            {
                Timestamp = "test",
                PrimaryCell = new ServingCell { RsrpDbm = -90, RsrqDb = -10 },
                Rssnr = 10,
                RxKB = 1,
                TxKB = 2,
                TempC = 30,
            };
        });

        private static MonitorView View() => new MonitorView { RefreshRequested = true, Dirty = true };

        // Arrow repeats, visibility, pause and refresh must be processed before the first
        // slow sample finishes. Quit also must return from the UI loop while it is pending.
        [Fact]
        public void InputAndQuitDuringSlowSamplingWithCoalescedRedraw()
        {
            MonitorSampler sampler = Sampler(2000);
            MonitorSession session = Fixtures.Session();
            MonitorView view = View();
            FakeTerminal terminal = new FakeTerminal();
            int frames = 0;
            terminal.OnFrame = (s, v) =>
            {
                frames++;
                if (frames == 1)
                {
                    terminal.Add(ConsoleKey.UpArrow);
                    terminal.Add(ConsoleKey.UpArrow);
                    terminal.Add(ConsoleKey.DownArrow);
                    terminal.Add(ConsoleKey.D1, '1');
                    terminal.Add(ConsoleKey.P, 'p');
                    terminal.Add(ConsoleKey.R, 'r');
                }
                else
                {
                    Assert.True(v.ChartRows == 3 && !v.ChartVisible["1"], "Chart input was lost.");
                    Assert.True(v.Paused && v.Fetching, "Pause did not apply during sampling.");
                    Assert.True(s.Iteration == 0, "Input waited for sample completion.");
                    Assert.False(v.RefreshRequested, "Refresh queued a duplicate sample.");
                    terminal.Add(ConsoleKey.Q, 'q');
                }
            };
            TuiMonitor.Loop(session, view, sampler, terminal);
            Assert.Equal(2, frames);
            Assert.False(sampler.IsCompleted, "Quit waited for the sample.");
        }

        // A paused in-flight result is committed once; explicit refresh still works while
        // paused, and resume bypasses the otherwise long sampling interval.
        [Fact]
        public void PauseRefreshResumeHistoryCsvAndCount()
        {
            MonitorSampler sampler = Sampler(100);
            string csvPath = Fixtures.TempFile();
            try
            {
                MonitorSession session = Fixtures.Session(count: 3, interval: 60, csvPath: csvPath);
                SnapshotLog.Initialize(csvPath);
                MonitorView view = View();
                FakeTerminal terminal = new FakeTerminal();
                int stage = 0;
                terminal.OnFrame = (s, v) =>
                {
                    if (stage == 0)
                    {
                        terminal.Add(ConsoleKey.P, 'p');
                        stage = 1;
                    }
                    else if (stage == 1 && s.Iteration == 1)
                    {
                        Assert.True(v.Paused && !v.Fetching, "Pause started another sample.");
                        terminal.Add(ConsoleKey.R, 'r');
                        stage = 2;
                    }
                    else if (stage == 2 && s.Iteration == 2)
                    {
                        Assert.True(v.Paused && !v.Fetching, "Refresh resumed automatic sampling.");
                        terminal.Add(ConsoleKey.P, 'p');
                        stage = 3;
                    }
                };
                TuiMonitor.Loop(session, view, sampler, terminal);
                Assert.True(view.Done && session.Iteration == 3, "Count was not respected.");
                Assert.Equal(3, session.History["Rsrp"].Count);
                Assert.Null(sampler.Pending);
                List<Dictionary<string, string>> rows = Fixtures.ReadCsv(csvPath);
                Assert.True(rows.Count == 3 && rows[0]["RSRP_dBm"] == "-90", "CSV lost or corrupted samples.");
            }
            finally { File.Delete(csvPath); }
        }

        // Uppercase R resets the statistics at once, even during sampling, without a refresh;
        // the in-flight sample then starts the new history. Lowercase r refreshes and keeps them.
        [Fact]
        public void UppercaseRResetsDuringSamplingLowercaseOnlyRefreshes()
        {
            MonitorSampler sampler = Sampler(300);
            MonitorSession session = Fixtures.Session(interval: 60);
            session.Iteration = 2;
            foreach (List<double> series in session.History.Series)
            {
                series.Add(1);
                series.Add(2);
            }
            session.HandoverLog.Entries.Add(new HandoverEntry(5, "", new HandoverCell("1", 1, "B1", null, null, null, null), new HandoverCell("1", 2, "B1", null, null, null, null)));
            session.HandoverLog.Count = 5;
            session.DowngradeLog.AlertCount = 3;
            MonitorView view = View();
            view.HandoverOffset = 4;
            FakeTerminal terminal = new FakeTerminal();
            int stage = 0;
            terminal.OnFrame = (s, v) =>
            {
                if (stage == 0)
                {
                    terminal.Add(ConsoleKey.R, 'R');
                    stage = 1;
                }
                else if (stage == 1)
                {
                    Assert.True(s.History.Series.All(h => h.Count == 0) && s.HandoverLog.Count == 0 && s.HandoverLog.Entries.Count == 0, "Reset kept history.");
                    Assert.True(s.DowngradeLog.AlertCount == 0 && v.HandoverOffset == 0, "Reset kept the 2G/3G log or page.");
                    Assert.True(s.Iteration == 2 && v.Fetching && !v.RefreshRequested, "Reset changed progress or sampling.");
                    stage = 2;
                }
                else if (stage == 2 && s.Iteration == 3)
                {
                    Assert.True(s.History["Rsrp"].Count == 1 && s.History["TempC"].Count == 1, "In-flight sample was not kept after reset.");
                    terminal.Add(ConsoleKey.R, 'r');
                    stage = 3;
                }
                else if (stage == 3 && s.Iteration == 4)
                {
                    Assert.True(s.History["Rsrp"].Count == 2, "Lowercase r reset the statistics.");
                    terminal.Add(ConsoleKey.Q, 'q');
                }
            };
            TuiMonitor.Loop(session, view, sampler, terminal);
            Assert.True(stage == 3 && session.Iteration == 4, "Reset or refresh sequence did not complete.");
        }

        // Interval=0 must keep sampling, but never start concurrent requests.
        [Fact]
        public void BackToBackSamplingAndConcurrencyGuard()
        {
            MonitorSampler sampler = Sampler(50);
            MonitorSession session = Fixtures.Session(count: 2);
            TuiMonitor.Loop(session, View(), sampler, new FakeTerminal());
            Assert.True(session.Iteration == 2 && session.History["Rsrp"].Count == 2, "Interval=0 failed.");
            sampler.Start();
            Assert.Throws<InvalidOperationException>(() => sampler.Start());
        }

        // Worker errors must be surfaced, and the pending sample must be released.
        [Fact]
        public void WorkerErrorPropagationAndCleanup()
        {
            MonitorSampler sampler = new MonitorSampler(() => throw new InvalidOperationException("test failure"));
            sampler.Start();
            Assert.True(SpinWait.SpinUntil(() => sampler.IsCompleted, 5000));
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => sampler.Receive());
            Assert.Equal("test failure", ex.Message);
            Assert.Null(sampler.Pending);
            Assert.Throws<InvalidOperationException>(() => sampler.Receive());
        }
    }
}
