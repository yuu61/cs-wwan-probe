using System.Diagnostics;
using WwanProbe.Application;
using WwanProbe.Domain;
using WwanProbe.Presentation;

namespace WwanProbe.Tests
{
    // The TUI loop with a real background sampler and simulated console input/output.
    public class TuiResponsivenessTests
    {
        private static MonitorSampler Sampler(int delayMs)
        {
            return new MonitorSampler(() =>
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
        }

        private static MonitorView View() => new() { RefreshRequested = true, Dirty = true };

        private static MonitorSampler NoSampler() => new(() => throw new InvalidOperationException("no sample expected"));

        private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

        // Every pass that drew or was woken by input waits at least LoopPace.MinPass (less a margin for the
        // timer) after the previous wait returned: a pass starts only after that wait, and holds before its
        // own. A delay of the machine only lengthens the gap; a loop without the hold shows gaps near zero.
        private static void AssertHeld(FakeTerminal terminal)
            => Assert.All(Enumerable.Range(1, terminal.Waits.Count - 1), k =>
            {
                TimeSpan gap = terminal.Waits[k].At - terminal.WaitEnds[k - 1];
                Assert.True(gap >= LoopPace.MinPass - Ms(5), $"Pass {k} waited {gap} after the previous wait.");
            });

        // Arrow repeats, visibility, pause and refresh must be processed before the first
        // slow sample finishes. Quit also must return from the UI loop while it is pending.
        [Fact]
        public void InputAndQuitDuringSlowSamplingWithCoalescedRedraw()
        {
            MonitorSampler sampler = Sampler(2000);
            MonitorSession session = Fixtures.Session();
            MonitorView view = View();
            FakeTerminal terminal = new();
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
                FakeTerminal terminal = new();
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
            FakeTerminal terminal = new();
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
            Assert.Equal(3, stage);
            Assert.Equal(4, session.Iteration);
        }

        // Interval=0 must keep sampling (with the default spacing: these samples end at once), but never
        // start concurrent requests.
        [Fact]
        public void BackToBackSamplingAndConcurrencyGuard()
        {
            MonitorSampler sampler = Sampler(50);
            MonitorSession session = Fixtures.Session(count: 2);
            Stopwatch watch = Stopwatch.StartNew();
            TuiMonitor.Loop(session, View(), sampler, new FakeTerminal());
            Assert.True(session.Iteration == 2 && session.History["Rsrp"].Count == 2, "Interval=0 failed.");
            Assert.True(watch.Elapsed >= session.Config.MinSampleSpacing, "The second sample did not wait for the spacing.");
            sampler.Start();
            _ = Assert.Throws<InvalidOperationException>(sampler.Start);
        }

        // Every wait is bounded by the fallback, and it waits for the sample exactly while one runs.
        [Fact]
        public void LoopWaitsForTheSampleOrTheNextDeadline()
        {
            MonitorSampler sampler = Sampler(200);
            MonitorSession session = Fixtures.Session(count: 2, interval: 1);
            MonitorView view = View();
            TimeSpan? committed = null;
            FakeTerminal terminal = new()
            {
                OnWait = w =>
                {
                    Assert.Equal(view.Fetching, w.SampleDone);
                    Assert.InRange(w.Timeout, TimeSpan.Zero, LoopPace.Fallback);
                },
            };
            // The frame of the first finished sample comes after the loop took it, so the next sample is due
            // no later than this frame plus the interval.
            terminal.OnFrame = (s, v) =>
            {
                if (!v.Fetching && s.Iteration == 1) committed ??= terminal.Elapsed;
            };
            TuiMonitor.Loop(session, view, sampler, terminal);
            Assert.True(view.Done && !terminal.QuitByTime, "The samples did not finish.");
            Assert.Contains(terminal.Waits, w => w.SampleDone);
            Assert.Contains(terminal.Waits, w => !w.SampleDone);
            // Waiting for the interval ends at the due sample, not only at the fallback (this terminal ends
            // every wait within 25 ms, so the due time shows only in the timeouts the loop asks for): no wait
            // for the interval reaches past it (a margin for the time between the loop's clock and the wait).
            TimeSpan due = Assert.NotNull(committed) + TimeSpan.FromSeconds(session.Config.Interval);
            Assert.All(terminal.Waits.Where(w => !w.SampleDone), w => Assert.True(w.At + w.Timeout <= due + Ms(50), $"A wait at {w.At} for {w.Timeout} passes the due sample at {due}."));
        }

        [Fact]
        public void WaitTimeoutFollowsTheLoopState()
        {
            Assert.Equal(Ms(25), LoopPace.MinPass);
            Assert.Equal(Ms(250), LoopPace.Fallback);
            // An unfinished frame is retried, and a resize followed, at the short pace.
            Assert.Equal(LoopPace.MinPass, LoopPace.WaitTimeout(dirty: true, fast: false, untilSample: null));
            Assert.Equal(LoopPace.MinPass, LoopPace.WaitTimeout(dirty: false, fast: true, untilSample: null));
            // Paused or sampling (no automatic sample to wait for): the fallback, the longest wait in any state.
            Assert.Equal(LoopPace.Fallback, LoopPace.WaitTimeout(dirty: false, fast: false, untilSample: null));
            // Waiting for the interval: not past the fallback, nor past the due sample (rounded up, at least 1 ms).
            Assert.Equal(LoopPace.Fallback, LoopPace.WaitTimeout(dirty: false, fast: false, untilSample: TimeSpan.FromSeconds(86400)));
            Assert.Equal(Ms(11), LoopPace.WaitTimeout(dirty: false, fast: false, untilSample: Ms(10.2)));
            Assert.Equal(Ms(11), LoopPace.WaitTimeout(dirty: true, fast: false, untilSample: Ms(10.2)));
            Assert.Equal(Ms(1), LoopPace.WaitTimeout(dirty: false, fast: false, untilSample: Ms(0.3)));
            Assert.Equal(Ms(1), LoopPace.WaitTimeout(dirty: false, fast: false, untilSample: TimeSpan.Zero));
            Assert.Equal(Ms(1), LoopPace.WaitTimeout(dirty: false, fast: false, untilSample: Ms(-5)));
        }

        // Keys arriving faster than the loop (key repeat): frames stay at least LoopPace.MinPass apart.
        [Fact]
        public void KeyRepeatRedrawsAtMostEveryMinPass()
        {
            int frames = 0;
            FakeTerminal terminal = new();
            // Each frame finds the next key waiting; quit after a number of frames, not a time, so a slow
            // machine only takes longer.
            terminal.OnFrame = (_, _) =>
            {
                frames++;
                if (frames < 8) terminal.Add(ConsoleKey.G, 'g');
                else terminal.Add(ConsoleKey.Q, 'q');
            };
            TuiMonitor.Loop(new MonitorSession(), new MonitorView { Paused = true, Dirty = true }, NoSampler(), terminal);
            Assert.False(terminal.QuitByTime, $"Held keys drew {frames} frame(s).");
            AssertHeld(terminal);
        }

        // Input that wakes the loop without a key (focus, mouse, key releases; here an input handle that
        // stays signaled) cannot spin it.
        [Fact]
        public void InputWithoutKeysDoesNotSpinTheLoop()
        {
            FakeTerminal terminal = new() { SignaledInput = true };
            // Quit after a number of passes, not a time, so a slow machine only takes longer.
            terminal.OnWait = _ =>
            {
                if (terminal.Waits.Count == 8) terminal.Add(ConsoleKey.Q, 'q');
            };
            TuiMonitor.Loop(new MonitorSession(), new MonitorView { Paused = true, Dirty = true }, NoSampler(), terminal);
            Assert.False(terminal.QuitByTime, $"The loop made {terminal.Waits.Count} pass(es).");
            AssertHeld(terminal);
        }

        // After a resize the loop checks again at the short pace for a while (a drag goes on), then waits
        // up to the fallback again. A wait gets the time left of the pace its pass chose, so a slow pass
        // shortens it; the checks hold however slow the machine is:
        // - the short pace lasts LoopPace.FastFollow from the frame of the resize, so every wait that begins
        //   before frame + FastFollow is at most LoopPace.MinPass;
        // - a pass that chose the fallback started after the previous wait returned, so its wait ends at
        //   least LoopPace.Fallback after that: idle passes before the resize, and every pass that starts
        //   FastFollow after the wait of the resize pass (when the short pace has surely ended).
        [Fact]
        public void ResizeIsFollowedAtTheShortPaceForAWhile()
        {
            int width = 10;
            // The number of waits before the frame of the resize, and when that frame began.
            List<(int Waits, TimeSpan At)> resizes = [];
            // The window grows after three idle passes; the test ends after two passes that start once the
            // short pace has surely ended (counts, not times, so a slow machine only takes longer).
            FakeTerminal terminal = new() { Resized = v => v.LastWidth != width };
            terminal.OnRead = () => width = terminal.Waits.Count >= 3 ? 12 : 10;
            // The renderer takes the new size with the frame.
            terminal.OnFrame = (_, v) =>
            {
                if (v.LastWidth == width) return;
                resizes.Add((terminal.Waits.Count, terminal.Elapsed));
                v.LastWidth = width;
            };
            bool quitting = false;
            terminal.OnWait = _ =>
            {
                if (quitting || resizes.Count != 1 || terminal.Waits.Count <= resizes[0].Waits + 1) return;
                TimeSpan shortPaceOver = terminal.Waits[resizes[0].Waits].At + LoopPace.FastFollow;
                if (terminal.WaitEnds.Count(e => e >= shortPaceOver) < 2) return;
                quitting = true;
                terminal.Add(ConsoleKey.Q, 'q');
            };
            TuiMonitor.Loop(new MonitorSession(), new MonitorView { Paused = true, Dirty = true, LastWidth = 10, LastHeight = 4 }, NoSampler(), terminal);
            Assert.False(terminal.QuitByTime, "The short pace did not end.");
            (int resize, TimeSpan frameAt) = Assert.Single(resizes);
            Assert.Equal(3, resize);
            // The time from the previous wait's return to the end of wait k: at least the pace its pass chose.
            TimeSpan Span(int k) => terminal.Waits[k].At + terminal.Waits[k].Timeout - terminal.WaitEnds[k - 1];

            Assert.All(Enumerable.Range(1, resize - 1), k => Assert.True(Span(k) >= LoopPace.Fallback, $"Idle pass {k} before the resize waited only {Span(k)}."));
            List<TerminalWait> following = [.. terminal.Waits.Skip(resize).Where(w => w.At < frameAt + LoopPace.FastFollow)];
            Assert.NotEmpty(following);
            Assert.All(following, w => Assert.True(w.Timeout <= LoopPace.MinPass, $"A wait of {w.Timeout} while following the resize."));
            TimeSpan ended = terminal.Waits[resize].At + LoopPace.FastFollow;
            List<int> idle = [.. Enumerable.Range(resize + 1, terminal.Waits.Count - resize - 1).Where(k => terminal.WaitEnds[k - 1] >= ended)];
            Assert.True(idle.Count > 0, "The short pace did not end.");
            Assert.All(idle, k => Assert.True(Span(k) >= LoopPace.Fallback, $"Pass {k} after the short pace waited only {Span(k)}."));
        }

        // A paused screen with no key pressed is still drawn when the renderer is due to write every row
        // (here a terminal that is due 300 ms after each frame), and only then; every wait stays within the
        // fallback meanwhile, so the frame follows within it.
        [Fact]
        public void PausedScreenIsDrawnWhenAFullRedrawIsDue()
        {
            List<TimeSpan> frames = [];
            FakeTerminal terminal = new() { QuitAfter = TimeSpan.FromSeconds(5) };
            terminal.RedrawDue = () => terminal.Elapsed - frames[^1] >= Ms(300);
            terminal.OnFrame = (_, _) =>
            {
                frames.Add(terminal.Elapsed);
                if (frames.Count == 3) terminal.Add(ConsoleKey.Q, 'q');
            };
            TuiMonitor.Loop(new MonitorSession(), new MonitorView { Paused = true, Dirty = true }, NoSampler(), terminal);
            Assert.False(terminal.QuitByTime, $"The paused screen was drawn {frames.Count} time(s) in 5 s.");
            Assert.Equal(3, frames.Count);
            // Not on every pass: a frame only once the renderer is due.
            Assert.All(frames.Zip(frames.Skip(1)), p => Assert.True(p.Second - p.First >= Ms(300), $"Frames {p.Second - p.First} apart."));
            Assert.All(terminal.Waits, w => Assert.InRange(w.Timeout, TimeSpan.Zero, LoopPace.Fallback));
        }

        // Worker errors must be surfaced, and the pending sample must be released.
        [Fact]
        public void WorkerErrorPropagationAndCleanup()
        {
            MonitorSampler sampler = new(() => throw new InvalidOperationException("test failure"));
            sampler.Start();
            Assert.True(SpinWait.SpinUntil(() => sampler.IsCompleted, 5000));
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(sampler.Receive);
            Assert.Equal("test failure", ex.Message);
            Assert.Null(sampler.Pending);
            _ = Assert.Throws<InvalidOperationException>(sampler.Receive);
        }
    }
}
