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
    }

    internal sealed class ConsoleTerminal : ITuiTerminal
    {
        private readonly ConsoleRenderer renderer = new(new SystemConsole());

        public ConsoleKeyInfo? ReadKey() => Console.KeyAvailable ? Console.ReadKey(true) : null;

        public bool IsWindowResized(MonitorView view) => renderer.IsWindowResized(view);

        public void Show(MonitorSession session, MonitorView view) => renderer.ShowTuiScreen(session, view);
    }

    // Interactive full-screen TUI loop.
    // Keys: q / Esc / Ctrl+C = quit, p = pause/resume, r = refresh now, R = reset statistics,
    //       1-6 = show/hide one history chart, g = show/hide all charts,
    //       h = handover history, s = satellite list (-Nmea),
    //       Up / Down = chart height, newer / older handovers or scroll satellites
    internal static class TuiMonitor
    {
        public static MonitorView NewView() => new() { Unicode = true, RefreshRequested = true, Dirty = true };

        // Drain queued keys on every tick, including during sampling. Coalesce key repeats
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

        // Input and rendering run independently of the single background sample.
        public static void Loop(MonitorSession session, MonitorView view, MonitorSampler sampler, ITuiTerminal terminal)
        {
            DateTime nextSample = DateTime.MinValue;
            while (!view.Quit)
            {
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
                    nextSample = DateTime.Now.AddSeconds(session.Config.Interval);
                }
                if (!view.Done && !view.Fetching && (view.RefreshRequested || (!view.Paused && DateTime.Now >= nextSample)))
                {
                    view.RefreshRequested = false;
                    sampler.Start();
                    view.Fetching = true;
                    view.Dirty = true;
                }
                if (view.Dirty || terminal.IsWindowResized(view))
                {
                    view.Dirty = false;
                    terminal.Show(session, view);
                }
                if (view.Done) break;
                Thread.Sleep(25);
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
            try
            {
                MonitorSampler sampler = new(session.TakeSnapshot);
                // Block glyphs (U+2581..U+2588) become '?' under the default CP932 output encoding.
                Console.OutputEncoding = new UTF8Encoding(false);
                savedMode = ConsoleMode.EnableVirtualTerminal();
                // Draw on the alternate screen so quitting restores the shell's previous screen.
                Console.Out.Write("\e[?1049h");
                inAltScreen = true;
                Console.CursorVisible = false;
                Console.TreatControlCAsInput = true;
                Console.Clear();

                Loop(session, view, sampler, new ConsoleTerminal());
            }
            finally
            {
                Console.Out.Write("\e[0m");
                if (inAltScreen) Console.Out.Write("\e[?1049l");
                Console.TreatControlCAsInput = savedCtrlC;
                Console.CursorVisible = savedCursor;
                Console.OutputEncoding = savedEncoding;
                ConsoleMode.Restore(savedMode);
            }
        }
    }

    // Sequential (non-interactive) monitor loop for redirected stdin/stdout.
    internal static class PlainMonitor
    {
        public static void Run(MonitorSession session, CancellationToken cancel, int width = 100)
        {
            MonitorView view = new() { Unicode = false };
            while (!cancel.IsCancellationRequested)
            {
                session.Sample();
                ConsoleRenderer.ShowPlainFrame(MonitorFrame.Build(session, view, width), Console.Out);
                if (session.IsComplete) return;
                _ = cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(session.Config.Interval));
            }
        }
    }
}
