using WwanProbe.Application;
using WwanProbe.Presentation;

namespace WwanProbe.Tests
{
    // Resize handling of the renderer and the TUI loop.
    public class ConsoleRendererTests
    {
        private sealed class FakeConsole : IConsoleSurface
        {
            public ConsoleSize Current { get; set; } = new ConsoleSize(10, 4);
            public List<int> CursorRows { get; } = new List<int>();
            public List<(string Text, int Width, ConsoleColor? Background)> Lines { get; } = new List<(string, int, ConsoleColor?)>();
            public int FailRow { get; set; } = -1;
            public bool IoFailure { get; set; }
            public Action? OnWrite { get; set; }

            public ConsoleSize Size => Current;

            public void SetCursorRow(int row)
            {
                if (row == FailRow)
                {
                    if (IoFailure) throw new IOException("test console failure");
                    // The real exception a resize race raises.
                    Console.SetCursorPosition(0, -1);
                }
                CursorRows.Add(row);
            }

            public void WriteLine(FrameLine line, int width, ConsoleColor? background)
            {
                Lines.Add((line.Text, width, background));
                OnWrite?.Invoke();
            }

            public void Clear()
            {
            }
        }

        private static readonly Frame TestFrame = new Frame(new List<FrameLine> { new FrameLine("first"), new FrameLine("second") }, new FrameLine("footer"));

        [Fact]
        public void StableFrameClearsUnusedRowsAndDrawsTheFooter()
        {
            FakeConsole console = new FakeConsole();
            Assert.True(new ConsoleRenderer(console).ShowFrame(TestFrame, 10, 4));
            Assert.Equal(new[] { 0, 1, 2, 3 }, console.CursorRows);
            Assert.Equal(new[] { "first", "second", "", "footer" }, console.Lines.Select(l => l.Text));
            Assert.Equal(ConsoleColor.DarkCyan, console.Lines[3].Background);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ResizeInterruptsDrawingAndRecovers(bool width)
        {
            FakeConsole console = new FakeConsole();
            ConsoleRenderer renderer = new ConsoleRenderer(console);
            console.OnWrite = () => console.Current = width ? console.Current with { Width = 2 } : console.Current with { Height = 2 };
            Assert.False(renderer.ShowFrame(TestFrame, 10, 4));
            Assert.Equal(new[] { 0 }, console.CursorRows);
            console.OnWrite = null;
            Assert.True(renderer.ShowFrame(TestFrame, console.Current.Width, console.Current.Height));
            Assert.Equal("footer", console.Lines[^1].Text);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        public void CursorRacesAtBodyAndFooterAreHandled(int row)
        {
            FakeConsole console = new FakeConsole { FailRow = row };
            ConsoleRenderer renderer = new ConsoleRenderer(console);
            Assert.False(renderer.ShowFrame(TestFrame, 10, 4));
            Assert.Equal(row, console.Lines.Count);
            console.FailRow = -1;
            Assert.True(renderer.ShowFrame(TestFrame, 10, 4));
        }

        [Theory]
        [InlineData(0, 4)]
        [InlineData(-1, 4)]
        [InlineData(10, 0)]
        [InlineData(10, -1)]
        public void UnusableDimensionsSkipDrawing(int width, int height)
        {
            FakeConsole console = new FakeConsole();
            Assert.False(new ConsoleRenderer(console).ShowFrame(TestFrame, width, height));
            Assert.Empty(console.CursorRows);
        }

        [Fact]
        public void UnrelatedConsoleErrorsPropagate()
        {
            FakeConsole console = new FakeConsole { FailRow = 1, IoFailure = true };
            IOException ex = Assert.Throws<IOException>(() => new ConsoleRenderer(console).ShowFrame(TestFrame, 10, 4));
            Assert.Equal("test console failure", ex.Message);
        }

        [Fact]
        public void TuiRequestsAnotherFrameAfterResizeAndSkipsUnusableSizes()
        {
            FakeConsole console = new FakeConsole { FailRow = 1 };
            ConsoleRenderer renderer = new ConsoleRenderer(console);
            MonitorView view = new MonitorView { LastWidth = 10, LastHeight = 4 };
            renderer.ShowTuiScreen(new MonitorSession(), view, (_, _, _) => TestFrame);
            Assert.True(view.Dirty, "An interrupted frame was not marked for retry.");
            console.FailRow = -1;
            renderer.ShowTuiScreen(new MonitorSession(), view, (_, _, _) => TestFrame);
            Assert.False(view.Dirty, "A successful redraw kept requesting a retry.");
            console.Current = console.Current with { Width = 0 };
            renderer.ShowTuiScreen(new MonitorSession(), view, (_, _, _) => TestFrame);
            Assert.True(view.Dirty, "An unusable screen size was not marked for retry.");
            Assert.True(renderer.IsWindowResized(view));
        }

        // An interrupted frame must also be retried while paused and with unchanged size.
        [Fact]
        public void InterruptedFramesAreRetriedWhilePaused()
        {
            int frames = 0, ticks = 0;
            FakeTerminal terminal = new FakeTerminal
            {
                OnRead = () => ticks++,
                OnFrame = (_, v) =>
                {
                    frames++;
                    if (frames == 1) v.Dirty = true;
                    else v.Quit = true;
                },
            };
            MonitorView view = new MonitorView { Paused = true, Dirty = true };
            TuiMonitor.Loop(new MonitorSession(), view, new MonitorSampler(() => throw new InvalidOperationException("no sample expected")), terminal);
            Assert.Equal(2, frames);
            Assert.True(ticks <= 3, "The monitor loop lost the redraw request from an interrupted frame.");
        }
    }
}
