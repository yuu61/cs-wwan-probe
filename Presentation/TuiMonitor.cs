using System.Diagnostics;
using System.Text;
using WwanProbe.Application;

namespace WwanProbe.Presentation
{
    // What the TUI loop needs from the terminal (ConsoleTerminal on a real console, fakes in tests).
    internal interface ITuiTerminal
    {
        ConsoleKeyInfo? ReadKey();
        bool IsWindowResized(MonitorView view);
        void Show(MonitorSession session, MonitorView view);

        // The renderer is due to write every row (ConsoleRenderer.FullRedrawDue); the loop then draws a
        // frame even if nothing changed.
        bool FullRedrawDue { get; }

        // Blocks until input arrives, sampleDone (the running sample's completion, null when none runs)
        // is set or the timeout passes. Returns false on timeout.
        bool Wait(TimeSpan timeout, WaitHandle? sampleDone);
    }

    internal sealed class ConsoleTerminal : ITuiTerminal, IDisposable
    {
        private readonly ConsoleRenderer renderer = new(new SystemConsole());
        private readonly ConsoleInputWaitHandle input = new();

        // Console.KeyAvailable also reads and drops the events that are not keys, so the input handle
        // does not stay signaled after them.
        public ConsoleKeyInfo? ReadKey() => Console.KeyAvailable ? Console.ReadKey(true) : null;

        public bool IsWindowResized(MonitorView view) => renderer.IsWindowResized(view);

        public void Show(MonitorSession session, MonitorView view) => renderer.ShowTuiScreen(session, view);

        public bool FullRedrawDue => renderer.FullRedrawDue;

        public bool Wait(TimeSpan timeout, WaitHandle? sampleDone) => sampleDone is null
            ? input.WaitOne(timeout)
            : WaitHandle.WaitAny([input, sampleDone], timeout) != WaitHandle.WaitTimeout;

        public void Dispose() => input.Dispose();
    }

    // How long the TUI loop waits between passes. It sleeps until a key, the sample's completion or a
    // deadline instead of polling, but keeps the old 25 ms pace where it matters.
    internal static class LoopPace
    {
        // The shortest time from one pass to the next after a frame or a wake by input (key repeat,
        // focus or mouse events, a key release, an input handle that stays signaled), and the retry
        // pace for an unfinished frame or while following a resize.
        public static readonly TimeSpan MinPass = TimeSpan.FromMilliseconds(25);

        // The longest wait in any state: the size is checked at least this often (a resize that sends no
        // window event), and the schedule is checked again soon after the computer wakes from sleep.
        public static readonly TimeSpan Fallback = TimeSpan.FromMilliseconds(250);

        // After a resize or an unfinished frame, passes stay at MinPass this long, as a drag goes on.
        public static readonly TimeSpan FastFollow = TimeSpan.FromMilliseconds(500);

        // The wait measured from the start of a pass: MinPass while the frame is unfinished (dirty) or a
        // resize is being followed (fast), else Fallback; never past an automatic sample that is due
        // (untilSample from the start of the pass, rounded up to whole milliseconds, at least 1 ms).
        public static TimeSpan WaitTimeout(bool dirty, bool fast, TimeSpan? untilSample)
        {
            TimeSpan limit = dirty || fast ? MinPass : Fallback;
            if (untilSample is not TimeSpan due) return limit;
            TimeSpan rounded = due > TimeSpan.Zero ? RoundUp(due) : TimeSpan.FromMilliseconds(1);
            return rounded < limit ? rounded : limit;
        }

        // Whole milliseconds, rounded up: waits and sleeps drop the fraction and would end early.
        public static TimeSpan RoundUp(TimeSpan time) => TimeSpan.FromMilliseconds(Math.Ceiling(time.TotalMilliseconds));
    }

    // Interactive full-screen TUI loop.
    // Keys: q / Esc / Ctrl+C = quit, p = pause/resume, r = refresh now, R = reset statistics,
    //       1-6 = show/hide one history chart, g = show/hide all charts,
    //       h = handover history, s = satellite list (-Nmea),
    //       Up / Down = chart height, newer / older handovers or scroll satellites
    internal static class TuiMonitor
    {
        public static MonitorView NewView() => new() { Unicode = true, RefreshRequested = true, Dirty = true };

        // Drain queued keys on every pass of the loop, including during sampling. Coalesce key repeats
        // into one redraw so holding an arrow does not build up a queue of full frames.
        public static void ReadInput(MonitorView view, Func<ConsoleKeyInfo?> readKey)
        {
            while (readKey() is ConsoleKeyInfo key)
            {
                bool isCtrlC = key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0;
                if (isCtrlC || key.Key == ConsoleKey.Q || key.Key == ConsoleKey.Escape)
                {
                    view.Quit = true;
                    return;
                }
                if (key.Key == ConsoleKey.P)
                {
                    view.Paused = !view.Paused;
                    view.Dirty = true;
                    if (!view.Paused && !view.Fetching) view.RefreshRequested = true;
                }
                // Uppercase R (by character, so Caps Lock counts) resets; lowercase r refreshes.
                if (key.KeyChar == 'R')
                {
                    view.ResetRequested = true;
                    continue;
                }
                // An in-flight sample already satisfies a refresh request.
                if (key.Key == ConsoleKey.R && !view.Fetching) view.RefreshRequested = true;
                if (key.Key == ConsoleKey.H)
                {
                    view.HandoverVisible = !view.HandoverVisible;
                    view.HandoverOffset = 0;
                    view.SatelliteVisible = false;
                    view.Dirty = true;
                    continue;
                }
                if (key.Key == ConsoleKey.S)
                {
                    view.SatelliteVisible = !view.SatelliteVisible;
                    view.SatelliteOffset = 0;
                    view.HandoverVisible = false;
                    view.Dirty = true;
                    continue;
                }
                if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
                {
                    int delta = key.Key == ConsoleKey.UpArrow ? -1 : 1;
                    if (view.SatelliteVisible)
                    {
                        view.SatelliteOffset = Math.Max(0, view.SatelliteOffset + delta);
                        view.Dirty = true;
                        continue;
                    }
                    if (view.HandoverVisible)
                    {
                        view.HandoverOffset = Math.Max(0, view.HandoverOffset + delta);
                        view.Dirty = true;
                        continue;
                    }
                    int rows = HistoryCharts.StepHeight(view.ChartRows, -delta);
                    if (rows != view.ChartRows)
                    {
                        view.ChartRows = rows;
                        view.Dirty = true;
                    }
                    continue;
                }
                string chartKey = key.Key == ConsoleKey.G ? "all" : key.KeyChar.ToString();
                if (HistoryCharts.Toggle(view.ChartVisible, chartKey)) view.Dirty = true;
            }
        }

        // Input and rendering run independently of the single background sample. Each pass reads the keys,
        // commits a finished sample, starts the next one when due, checks the size and draws; then it waits
        // for input, the sample or the next deadline (LoopPace).
        public static void Loop(MonitorSession session, MonitorView view, MonitorSampler sampler, ITuiTerminal terminal)
        {
            // Monotonic time since the loop started (QueryPerformanceCounter, which keeps counting through
            // sleep), so a change of the system clock does not move the schedule.
            long origin = Stopwatch.GetTimestamp();
            TimeSpan Now() => Stopwatch.GetElapsedTime(origin);
            TimeSpan nextSample = TimeSpan.Zero;
            TimeSpan sampleStarted = TimeSpan.Zero;
            TimeSpan fastUntil = TimeSpan.Zero;
            bool woken = false;
            while (!view.Quit)
            {
                TimeSpan passStart = Now();
                ReadInput(view, terminal.ReadKey);
                if (view.Quit) break;
                // A sample still in flight is committed afterwards and counts toward the new statistics.
                if (view.ResetRequested)
                {
                    session.ResetStatistics();
                    view.ResetRequested = false;
                    view.HandoverOffset = 0;
                    view.Dirty = true;
                }
                if (view.Fetching && sampler.IsCompleted)
                {
                    session.AddSnapshot(sampler.Receive());
                    view.Fetching = false;
                    view.Done = session.IsComplete;
                    view.Dirty = true;
                    nextSample = session.Config.NextSampleAt(sampleStarted, Now(), session.PowerSaving);
                }
                if (!view.Done && !view.Fetching && (view.RefreshRequested || (!view.Paused && Now() >= nextSample)))
                {
                    view.RefreshRequested = false;
                    sampleStarted = Now();
                    sampler.Start();
                    view.Fetching = true;
                    view.Dirty = true;
                }
                // A screen with nothing to change (paused, a long -Interval) is still drawn when the renderer
                // is due to write every row, within LoopPace.Fallback (no wait is longer); the renderer's own
                // clock decides, so frames of changed rows in between do not put it off.
                if (!view.Dirty && terminal.FullRedrawDue) view.Dirty = true;
                bool drawn = view.Dirty || terminal.IsWindowResized(view);
                if (drawn)
                {
                    (int Width, int Height) shownSize = (view.LastWidth, view.LastHeight);
                    view.Dirty = false;
                    terminal.Show(session, view);
                    // A new size (not the first one) or an unfinished frame: keep the short pace while a drag goes on.
                    bool resized = shownSize.Width > 0 && shownSize != (view.LastWidth, view.LastHeight);
                    if (resized || view.Dirty) fastUntil = Now() + LoopPace.FastFollow;
                }
                if (view.Done) break;
                TimeSpan? untilSample = view.Fetching || view.Paused ? null : nextSample - passStart;
                TimeSpan timeout = LoopPace.WaitTimeout(view.Dirty, passStart < fastUntil, untilSample);
                // A frame or a wake by input keeps the next pass MinPass after this one, so key repeat and
                // events without a key cannot run the loop faster. A wake by timeout (idle) is not held,
                // and neither is the first key after it.
                TimeSpan hold = passStart + LoopPace.MinPass - Now();
                if ((drawn || woken) && hold > TimeSpan.Zero) Thread.Sleep(LoopPace.RoundUp(hold));
                TimeSpan left = passStart + timeout - Now();
                woken = terminal.Wait(left > TimeSpan.Zero ? LoopPace.RoundUp(left) : TimeSpan.Zero, sampler.SampleDone);
            }
        }

        public static void Run(MonitorSession session)
        {
            MonitorView view = NewView();
            bool savedCursor = Console.CursorVisible;
            bool savedCtrlC = Console.TreatControlCAsInput;
            Encoding savedEncoding = Console.OutputEncoding;
            uint? savedMode = null;
            bool inAltScreen = false;
            bool noWrap = false;
            bool windowInput = false;
            try
            {
                MonitorSampler sampler = new(session.TakeSnapshot);
                // Block glyphs (U+2581..U+2588) become '?' under the default CP932 output encoding.
                Console.OutputEncoding = new UTF8Encoding(false);
                savedMode = ConsoleMode.EnableVirtualTerminal();
                // Draw on the alternate screen so quitting restores the shell's previous screen.
                Console.Out.Write("\e[?1049h");
                inAltScreen = true;
                // No automatic wrap (DECAWM): a row wider than the window (a resize during the write, wide
                // characters) is clipped instead of spilling into the next row or scrolling the screen,
                // which redrawing only the changed rows would not repair.
                Console.Out.Write("\e[?7l");
                noWrap = true;
                Console.CursorVisible = false;
                Console.TreatControlCAsInput = true;
                // A resize wakes the loop at once; without it the loop sees it within LoopPace.Fallback.
                windowInput = ConsoleMode.AddWindowInput();

                // The first frame clears the screen (a new view has no size yet).
                using ConsoleTerminal terminal = new();
                Loop(session, view, sampler, terminal);
            }
            finally
            {
                Console.Out.Write("\e[0m");
                // The mode outlives the alternate screen (conhost) and the program (Windows Terminal).
                if (noWrap) Console.Out.Write("\e[?7h");
                if (inAltScreen) Console.Out.Write("\e[?1049l");
                if (windowInput) ConsoleMode.RemoveWindowInput();
                Console.TreatControlCAsInput = savedCtrlC;
                Console.CursorVisible = savedCursor;
                Console.OutputEncoding = savedEncoding;
                ConsoleMode.Restore(savedMode);
            }
        }
    }

    // Sequential (non-interactive) monitor loop for redirected stdin/stdout (output = Console.Out by default).
    internal static class PlainMonitor
    {
        public static void Run(MonitorSession session, CancellationToken cancel, int width = 100, TextWriter? output = null)
        {
            MonitorView view = new() { Unicode = false };
            while (!cancel.IsCancellationRequested)
            {
                long started = Stopwatch.GetTimestamp();
                session.Sample();
                ConsoleRenderer.ShowPlainFrame(MonitorFrame.Build(session, view, width), output ?? Console.Out);
                if (session.IsComplete) return;
                // The effective interval after the sample, and MinSampleSpacing from its start; cancelling ends
                // the wait at once.
                _ = cancel.WaitHandle.WaitOne(session.Config.PauseAfter(Stopwatch.GetElapsedTime(started), session.PowerSaving));
            }
        }
    }
}
