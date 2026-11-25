using System.Runtime.InteropServices;
using System.Text;
using WwanProbe.Application;
using WwanProbe.Presentation;

namespace WwanProbe.Tests
{
    // Frame writes (changed rows only, one write per frame) and resize handling of the renderer and the TUI loop.
    public class ConsoleRendererTests
    {
        private static readonly Frame TestFrame = new([new FrameLine("first"), new FrameLine("second")], new FrameLine("footer"));
        private static readonly ConsoleSize Size = new(10, 4);

        // The rows a full redraw of the frame writes: body, blank rows, footer on the bottom row.
        private static string[] Rows(Frame frame, ConsoleSize size) => [.. Enumerable.Range(0, size.Height).Select(row => row == size.Height - 1
            ? ConsoleLine.Render(frame.Footer, size.Width, ConsoleColor.DarkCyan)
            : ConsoleLine.Render(row < frame.Body.Count ? frame.Body[row] : new FrameLine(), size.Width))];

        private static void AssertScreen(FakeConsole console, Frame frame, ConsoleSize size)
            => Assert.Equal(Rows(frame, size), Enumerable.Range(0, size.Height).Select(r => console.Screen.GetValueOrDefault(r)));

        private static Frame WithBody(params string[] lines) => new([.. lines.Select(l => new FrameLine(l))], new FrameLine("footer"));

        private static void Show(ConsoleRenderer renderer, MonitorView view, Frame frame)
            => renderer.ShowTuiScreen(new MonitorSession(), view, (_, _, _) => frame);

        private static ScreenBufferInfo Info(short bufferWidth, short bufferHeight, short left, short top, short right, short bottom)
            => new() { BufferWidth = bufferWidth, BufferHeight = bufferHeight, WindowLeft = left, WindowTop = top, WindowRight = right, WindowBottom = bottom };

        [Fact]
        public void StableFrameClearsUnusedRowsAndDrawsTheFooter()
        {
            FakeConsole console = new();
            Assert.True(new ConsoleRenderer(console).ShowFrame(TestFrame, Size));
            FrameWrite write = Assert.Single(console.Writes);
            Assert.False(write.Clear);
            Assert.Equal([0, 1, 2, 3], write.RowNumbers);
            Assert.Equal(
                [
                    ConsoleLine.Render(new FrameLine("first"), 10),
                    ConsoleLine.Render(new FrameLine("second"), 10),
                    ConsoleLine.Render(new FrameLine(), 10),
                    ConsoleLine.Render(new FrameLine("footer"), 10, ConsoleColor.DarkCyan),
                ],
                write.Rows.Select(r => r.Text));
        }

        // One size check before and one after the single write; an unchanged frame is not written.
        [Fact]
        public void FrameReadsTheSizeAroundItsOneWrite()
        {
            FakeConsole console = new();
            ConsoleRenderer renderer = new(console);
            MonitorView view = TuiMonitor.NewView();
            Show(renderer, view, TestFrame);
            Assert.Equal(2, console.SizeReads);
            _ = Assert.Single(console.Writes);
            Show(renderer, view, WithBody("first", "second"));
            Assert.Equal(3, console.SizeReads);
            _ = Assert.Single(console.Writes);
            Assert.False(view.Dirty, "A frame with nothing to write was marked for retry.");
        }

        [Fact]
        public void OnlyChangedRowsAreWritten()
        {
            FakeConsole console = new();
            ConsoleRenderer renderer = new(console);
            Assert.True(renderer.ShowFrame(WithBody("a", "b", "c"), Size));
            Frame changed = WithBody("a", "B", "c");
            Assert.True(renderer.ShowFrame(changed, Size));
            FrameWrite write = console.Writes[^1];
            Assert.False(write.Clear);
            Assert.Equal([1], write.RowNumbers);
            AssertScreen(console, changed, Size);
            // Rows a shorter body leaves are blanked; the unchanged footer is not written.
            Frame shorter = WithBody("a");
            Assert.True(renderer.ShowFrame(shorter, Size));
            Assert.Equal([1, 2], console.Writes[^1].RowNumbers);
            AssertScreen(console, shorter, Size);
        }

        [Fact]
        public void ChangedRowsLeaveTheScreenOfAFullRedraw()
        {
            string[] words = ["alpha", "beta", "gamma"];
            FakeConsole console = new();
            ConsoleRenderer renderer = new(console);
            MonitorView view = TuiMonitor.NewView();
            for (int i = 0; i < 30; i++)
            {
                List<FrameLine> body = [.. Enumerable.Range(0, i % 4).Select(n => new FrameLine(words[(i + n) % 3], n == 1 ? ConsoleColor.Red : ConsoleColor.Gray))];
                Frame frame = new(body, new FrameLine(words[i % 2] + " footer"));
                Show(renderer, view, frame);
                Assert.False(view.Dirty);
                AssertScreen(console, frame, Size);
            }
            Assert.All(console.Writes.Skip(1), w => Assert.False(w.Clear));
            Assert.Contains(console.Writes, w => w.Rows.Count < Size.Height);
        }

        // Every row is written again by time, not by the number of frames: an unchanged frame writes no row
        // before FullRedrawAge has passed since the last full redraw, however many frames come, and every
        // row from then on. A frame of changed rows does not restart the age.
        [Fact]
        public void EveryRowIsRewrittenPeriodically()
        {
            TimeSpan now = TimeSpan.FromSeconds(100);
            FakeConsole console = new();
            ConsoleRenderer renderer = new(console, () => now);
            Assert.True(renderer.ShowFrame(TestFrame, Size));
            TimeSpan full = now;
            for (int i = 1; i < 100; i++)
            {
                now = full + (ConsoleRenderer.FullRedrawAge * i / 100);
                Assert.False(renderer.FullRedrawDue);
                Assert.True(renderer.ShowFrame(TestFrame, Size));
            }
            now = full + ConsoleRenderer.FullRedrawAge - TimeSpan.FromTicks(1);
            Assert.False(renderer.FullRedrawDue);
            Assert.True(renderer.ShowFrame(TestFrame, Size));
            _ = Assert.Single(console.Writes);
            now = full + ConsoleRenderer.FullRedrawAge;
            // The loop draws a frame when the renderer is due (TuiMonitor.Loop), and that frame writes every row.
            Assert.True(renderer.FullRedrawDue);
            Assert.True(renderer.ShowFrame(TestFrame, Size));
            Assert.False(renderer.FullRedrawDue);
            Assert.Equal(2, console.Writes.Count);
            Assert.False(console.Writes[^1].Clear);
            Assert.Equal([0, 1, 2, 3], console.Writes[^1].RowNumbers);

            full = now;
            now = full + (ConsoleRenderer.FullRedrawAge / 2);
            Assert.True(renderer.ShowFrame(WithBody("first", "changed"), Size));
            Assert.Equal([1], console.Writes[^1].RowNumbers);
            now = full + ConsoleRenderer.FullRedrawAge - TimeSpan.FromTicks(1);
            Assert.False(renderer.FullRedrawDue);
            Assert.True(renderer.ShowFrame(WithBody("first", "changed"), Size));
            Assert.Equal(3, console.Writes.Count);
            now = full + ConsoleRenderer.FullRedrawAge;
            Assert.True(renderer.FullRedrawDue);
            Assert.True(renderer.ShowFrame(WithBody("first", "changed"), Size));
            Assert.Equal(4, console.Writes.Count);
            Assert.Equal([0, 1, 2, 3], console.Writes[^1].RowNumbers);
            AssertScreen(console, WithBody("first", "changed"), Size);
        }

        [Fact]
        public void ClearWritesEveryRow()
        {
            FakeConsole console = new();
            ConsoleRenderer renderer = new(console);
            Assert.True(renderer.ShowFrame(TestFrame, Size));
            Assert.True(renderer.ShowFrame(TestFrame, Size, clear: true));
            FrameWrite write = console.Writes[^1];
            Assert.True(write.Clear);
            Assert.Equal([0, 1, 2, 3], write.RowNumbers);
            AssertScreen(console, TestFrame, Size);
        }

        // The first TUI frame and the first frame after a resize clear the screen in the same write.
        [Fact]
        public void ResizeClearsAndWritesEveryRow()
        {
            FakeConsole console = new();
            ConsoleRenderer renderer = new(console);
            MonitorView view = TuiMonitor.NewView();
            Show(renderer, view, TestFrame);
            Assert.True(Assert.Single(console.Writes).Clear, "The first frame did not clear the screen.");
            console.Current = new ConsoleSize(12, 5);
            Assert.True(renderer.IsWindowResized(view));
            Show(renderer, view, TestFrame);
            FrameWrite write = console.Writes[^1];
            Assert.True(write.Clear);
            Assert.Equal([0, 1, 2, 3, 4], write.RowNumbers);
            Assert.Equal((12, 5), (view.LastWidth, view.LastHeight));
            AssertScreen(console, TestFrame, console.Current);
        }

        // A frame written for a size that changed during the write stays unfinished; the next pass sees
        // the resize, clears and writes every row.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ResizeInterruptsDrawingAndRecovers(bool width)
        {
            FakeConsole console = new();
            ConsoleRenderer renderer = new(console);
            MonitorView view = TuiMonitor.NewView();
            Show(renderer, view, TestFrame);
            console.OnWrite = () => console.Current = width ? console.Current with { Width = 2 } : console.Current with { Height = 2 };
            Show(renderer, view, WithBody("changed"));
            Assert.True(view.Dirty, "A frame written for the old size was not marked for retry.");
            Assert.True(renderer.IsWindowResized(view));
            console.OnWrite = null;
            Show(renderer, view, WithBody("changed"));
            Assert.False(view.Dirty);
            FrameWrite write = console.Writes[^1];
            Assert.True(write.Clear, "The redraw after the resize did not clear the screen.");
            Assert.Equal(Enumerable.Range(0, console.Current.Height), write.RowNumbers);
            AssertScreen(console, WithBody("changed"), console.Current);
        }

        // The size changed during the write and is back by the next pass: no resize to see, but the
        // screen is unknown, so every row is written.
        [Fact]
        public void UnfinishedFrameIsRewrittenInFull()
        {
            FakeConsole console = new();
            ConsoleRenderer renderer = new(console);
            MonitorView view = TuiMonitor.NewView();
            Show(renderer, view, TestFrame);
            console.OnWrite = () => console.Current = Size with { Height = 2 };
            Show(renderer, view, WithBody("first", "changed"));
            Assert.True(view.Dirty);
            console.OnWrite = null;
            console.Current = Size;
            Assert.False(renderer.IsWindowResized(view));
            Show(renderer, view, WithBody("first", "changed"));
            Assert.False(view.Dirty);
            FrameWrite write = console.Writes[^1];
            Assert.False(write.Clear);
            Assert.Equal([0, 1, 2, 3], write.RowNumbers);
        }

        [Fact]
        public void UnusableSizeForgetsTheScreen()
        {
            FakeConsole console = new();
            ConsoleRenderer renderer = new(console);
            MonitorView view = TuiMonitor.NewView();
            Show(renderer, view, TestFrame);
            console.Current = Size with { Height = 0 };
            Show(renderer, view, TestFrame);
            Assert.True(view.Dirty);
            _ = Assert.Single(console.Writes);
            // Back to the size of the last clear: no clear, but every row.
            console.Current = Size;
            Show(renderer, view, TestFrame);
            Assert.False(view.Dirty);
            FrameWrite write = console.Writes[^1];
            Assert.False(write.Clear);
            Assert.Equal([0, 1, 2, 3], write.RowNumbers);
        }

        [Theory]
        [InlineData(0, 4)]
        [InlineData(-1, 4)]
        [InlineData(10, 0)]
        [InlineData(10, -1)]
        public void UnusableDimensionsSkipDrawing(int width, int height)
        {
            FakeConsole console = new();
            Assert.False(new ConsoleRenderer(console).ShowFrame(TestFrame, new ConsoleSize(width, height)));
            Assert.Empty(console.Writes);
        }

        [Fact]
        public void UnrelatedConsoleErrorsPropagate()
        {
            FakeConsole console = new();
            ConsoleRenderer renderer = new(console);
            Assert.True(renderer.ShowFrame(TestFrame, Size));
            console.IoFailure = true;
            IOException ex = Assert.Throws<IOException>(() => renderer.ShowFrame(WithBody("changed"), Size));
            Assert.Equal("test console failure", ex.Message);
            // The failed write may have left anything on the screen: the next frame writes every row.
            console.IoFailure = false;
            Assert.True(renderer.ShowFrame(WithBody("changed"), Size));
            Assert.Equal([0, 1, 2, 3], console.Writes[^1].RowNumbers);
        }

        [Fact]
        public void TuiRequestsAnotherFrameAfterResizeAndSkipsUnusableSizes()
        {
            FakeConsole console = new();
            ConsoleRenderer renderer = new(console);
            MonitorView view = new() { LastWidth = 10, LastHeight = 4 };
            console.OnWrite = () => console.Current = console.Current with { Height = 3 };
            Show(renderer, view, TestFrame);
            Assert.True(view.Dirty, "An interrupted frame was not marked for retry.");
            console.OnWrite = null;
            Show(renderer, view, TestFrame);
            Assert.False(view.Dirty, "A successful redraw kept requesting a retry.");
            console.Current = console.Current with { Width = 0 };
            Show(renderer, view, TestFrame);
            Assert.True(view.Dirty, "An unusable screen size was not marked for retry.");
            Assert.True(renderer.IsWindowResized(view));
        }

        // An interrupted frame must also be retried while paused and with unchanged size.
        [Fact]
        public void InterruptedFramesAreRetriedWhilePaused()
        {
            int frames = 0, ticks = 0;
            FakeTerminal terminal = new()
            {
                OnRead = () => ticks++,
                OnFrame = (_, v) =>
                {
                    frames++;
                    if (frames == 1) v.Dirty = true;
                    else v.Quit = true;
                },
            };
            MonitorView view = new() { Paused = true, Dirty = true };
            TuiMonitor.Loop(new MonitorSession(), view, new MonitorSampler(() => throw new InvalidOperationException("no sample expected")), terminal);
            Assert.Equal(2, frames);
            Assert.True(ticks <= 3, "The monitor loop lost the redraw request from an interrupted frame.");
            // The retry comes at the short pace, not after the paused loop's fallback wait.
            Assert.True(terminal.Waits[0].Timeout <= LoopPace.MinPass, "The interrupted frame waited too long for its retry.");
        }

        [Fact]
        public void FrameIsOneSynchronizedWriteOfCursorMovesAndRows()
        {
            ConsoleRow[] rows = [new(0, "ab"), new(3, "cd")];
            Assert.Equal(["\e[?2026h\e[1;1H\e[2Kab\e[4;1H\e[2Kcd\e[?2026l"], VtFrame.Compose(false, rows));
            Assert.Equal(["\e[?2026h\e[H\e[2J\e[1;1H\e[2Kab\e[4;1H\e[2Kcd\e[?2026l"], VtFrame.Compose(true, rows));
            Assert.Equal("\e[12;1H", VtFrame.CursorToRow(11));
        }

        [Fact]
        public void LargeFramesAreSplitBetweenRows()
        {
            // Block glyphs are 3 UTF-8 bytes each: the limit counts bytes, not characters.
            ConsoleRow[] rows = [.. Enumerable.Range(0, 6).Select(r => new ConsoleRow(r, new string('█', 10)))];
            string whole = Assert.Single(VtFrame.Compose(true, rows));
            List<string> writes = VtFrame.Compose(true, rows, maxBytes: 80);
            Assert.Equal(4, writes.Count);
            Assert.Equal(whole, string.Concat(writes));
            Assert.StartsWith(VtFrame.BeginSynchronized + VtFrame.ClearScreen + VtFrame.CursorToRow(0), writes[0], StringComparison.Ordinal);
            Assert.EndsWith(VtFrame.EndSynchronized, writes[^1], StringComparison.Ordinal);
            Assert.All(writes, w => Assert.InRange(Encoding.UTF8.GetByteCount(w), 1, 80));
            // The erase before each row counts: one byte less and no two rows fit in a write.
            List<string> tight = VtFrame.Compose(true, rows, maxBytes: 79);
            Assert.Equal(6, tight.Count);
            Assert.All(tight, w => Assert.InRange(Encoding.UTF8.GetByteCount(w), 1, 79));
            Assert.All(writes.Skip(1), w => Assert.Contains(rows, r => w.StartsWith(VtFrame.CursorToRow(r.Row) + VtFrame.EraseRow + r.Text, StringComparison.Ordinal)));
            // A row larger than the limit is a write of its own.
            string wide = new('x', 200);
            List<string> parts = VtFrame.Compose(false, [new(0, "a"), new(1, wide), new(2, "b")], maxBytes: 64);
            Assert.Equal([VtFrame.BeginSynchronized + "\e[1;1H\e[2Ka", "\e[2;1H\e[2K" + wide, "\e[3;1H\e[2Kb" + VtFrame.EndSynchronized], parts);
        }

        // A rendered line without its SGR sequences.
        private static string Body(string rendered)
        {
            StringBuilder body = new();
            int i = 0;
            while (i < rendered.Length)
            {
                if (rendered[i] == '\e') i += rendered.AsSpan(i).IndexOf('m') + 1;
                else _ = body.Append(rendered[i++]);
            }
            return body.ToString();
        }

        // Control characters (CR LF CR LF in a WinRT exception message) are written as one space each, in
        // plain lines and in segments: they would move the cursor onto another row. The width is unchanged,
        // and a line without them is written as before.
        [Fact]
        public void ControlCharactersAreWrittenAsSpaces()
        {
            string plain = ConsoleLine.Render(new FrameLine("a\r\n\r\nb"), 10);
            Assert.DoesNotContain("\r", plain, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", plain, StringComparison.Ordinal);
            Assert.Equal("a    b    ", Body(plain));
            Assert.Equal(ConsoleLine.Render(new FrameLine("a    b"), 10), plain);

            FrameLine segments = FrameLine.FromSegments(ConsoleColor.Gray, [new FrameLine("a\r\n", ConsoleColor.Red), new FrameLine("\r\nb", ConsoleColor.Green, grayLevel: 128)]);
            string segmented = ConsoleLine.Render(segments, 10);
            Assert.DoesNotContain("\r", segmented, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", segmented, StringComparison.Ordinal);
            Assert.Equal("a    b    ", Body(segmented));
            Assert.Equal(ConsoleLine.Render(FrameLine.FromSegments(ConsoleColor.Gray, [new FrameLine("a  ", ConsoleColor.Red), new FrameLine("  b", ConsoleColor.Green, grayLevel: 128)]), 10), segmented);

            // The whole range: U+0000-U+001F and U+007F (an ESC from a message cannot start a sequence).
            Assert.Equal(ConsoleLine.Render(new FrameLine("  x   "), 6), ConsoleLine.Render(new FrameLine("\0\ex\t\u001f\u007f"), 6));
            Assert.Equal(ConsoleLine.Render(new FrameLine("a b"), 3), ConsoleLine.Render(new FrameLine("a\u007fb"), 3));
            Assert.Equal("\e[37mab ~\e[0m", ConsoleLine.Render(new FrameLine("ab ~"), 4));
        }

        [Fact]
        public void ControlCharactersDoNotReachTheConsole()
        {
            FakeConsole console = new();
            Assert.True(new ConsoleRenderer(console).ShowFrame(WithBody("first", "x\r\n\r\ny"), Size));
            FrameWrite write = Assert.Single(console.Writes);
            Assert.Equal([0, 1, 2, 3], write.RowNumbers);
            Assert.All(write.Rows, r =>
            {
                Assert.DoesNotContain("\r", r.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("\n", r.Text, StringComparison.Ordinal);
            });
            Assert.Equal(ConsoleLine.Render(new FrameLine("x    y"), 10), write.Rows[1].Text);
        }

        [Fact]
        public void ScreenBufferInfoGivesTheDrawableSize()
        {
            // GetConsoleScreenBufferInfo writes all 22 bytes of the structure.
            Assert.Equal(22, Marshal.SizeOf<ScreenBufferInfo>());
            // A new console: the window at the top of a long buffer; the rightmost column stays unused.
            Assert.Equal(new ConsoleSize(119, 30), ConsoleWindow.DrawableSize(Info(120, 9001, 0, 0, 119, 29)));
            // The alternate screen: the buffer is the window.
            Assert.Equal(new ConsoleSize(119, 30), ConsoleWindow.DrawableSize(Info(120, 30, 0, 0, 119, 29)));
            // Scrolled down the buffer.
            Assert.Equal(new ConsoleSize(79, 25), ConsoleWindow.DrawableSize(Info(80, 300, 0, 100, 79, 124)));
            // A buffer smaller than the window (while resizing) bounds both dimensions.
            Assert.Equal(new ConsoleSize(59, 20), ConsoleWindow.DrawableSize(Info(60, 20, 0, 0, 99, 29)));
            // Degenerate windows never give a negative size.
            Assert.Equal(new ConsoleSize(0, 1), ConsoleWindow.DrawableSize(Info(1, 1, 0, 0, 0, 0)));
            Assert.Equal(new ConsoleSize(0, 0), ConsoleWindow.DrawableSize(Info(0, 0, 0, 0, -1, -1)));
        }
    }
}
