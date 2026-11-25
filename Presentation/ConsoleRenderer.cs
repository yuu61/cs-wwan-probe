using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WwanProbe.Application;

namespace WwanProbe.Presentation
{
    internal readonly record struct ConsoleSize(int Width, int Height);

    // One screen row to write: its 0-based row number and its text from ConsoleLine.Render.
    internal readonly record struct ConsoleRow(int Row, string Text);

    // The console operations the TUI needs (SystemConsole on a real console, fakes in tests).
    internal interface IConsoleSurface
    {
        ConsoleSize Size { get; }

        // Writes the rows, each from its first column, as one frame; clear = clear the screen first.
        void WriteFrame(bool clear, IReadOnlyList<ConsoleRow> rows);
    }

    // One frame line as console text: clipped / padded to width, colored with VT (SGR) sequences.
    internal static class ConsoleLine
    {
        // ConsoleColor -> SGR foreground code (background = +10).
        public static int Sgr(ConsoleColor color)
        {
            int c = (int)color;
            int ansi = ((c & 4) != 0 ? 1 : 0) | ((c & 2) != 0 ? 2 : 0) | ((c & 1) != 0 ? 4 : 0);
            return 30 + ansi + ((c & 8) != 0 ? 60 : 0);
        }

        // The control characters AppendPrintable writes as spaces: U+0000-U+001F and U+007F.
        private static readonly SearchValues<char> Controls = SearchValues.Create([.. Enumerable.Range(0, 0x20).Select(c => (char)c), '\u007f']);

        private static string Fg(ConsoleColor color) => $"\e[{Sgr(color)}m";

        public static string Render(FrameLine line, int width, ConsoleColor? background = null)
        {
            StringBuilder sb = new();
            _ = sb.Append(Fg(line.Color));
            if (background is ConsoleColor bg) _ = sb.Append(CultureInfo.InvariantCulture, $"\e[{Sgr(bg) + 10}m");
            if (line.Segments.Count > 0)
            {
                int remaining = Math.Max(0, width);
                foreach (FrameLine segment in line.Segments)
                {
                    if (remaining <= 0) break;
                    int length = Math.Min(segment.Text.Length, remaining);
                    _ = sb.Append(Fg(segment.Color));
                    if (segment.GrayLevel is byte level) _ = sb.Append(CultureInfo.InvariantCulture, $"\e[38;2;{level};{level};{level}m");
                    AppendPrintable(sb, segment.Text.AsSpan(0, length));
                    // End the RGB override before spaces, the NR suffix or the next line.
                    if (segment.GrayLevel is not null) _ = sb.Append("\e[39m");
                    remaining -= length;
                }
                _ = sb.Append(Fg(line.Color));
                _ = sb.Append(' ', remaining);
            }
            else
            {
                string text = line.Text.Length > width ? line.Text[..Math.Max(0, width)] : line.Text;
                AppendPrintable(sb, text.PadRight(Math.Max(0, width)));
            }
            _ = sb.Append("\e[0m");
            return sb.ToString();
        }

        // Appends the text with each control character (U+0000-U+001F, U+007F) as a space: a CR or LF
        // (exception messages from WinRT contain CR LF CR LF) would move the cursor onto another row,
        // which redrawing only the changed rows would not repair. One space each keeps the length, so
        // the width is unchanged; text without control characters is appended as it is.
        private static void AppendPrintable(StringBuilder sb, ReadOnlySpan<char> text)
        {
            int start = sb.Length;
            _ = sb.Append(text);
            int i = text.IndexOfAny(Controls);
            while (i >= 0)
            {
                sb[start + i] = ' ';
                int next = text[(i + 1)..].IndexOfAny(Controls);
                i = next < 0 ? -1 : i + 1 + next;
            }
        }
    }

    // The VT text of one frame for a single console write.
    internal static class VtFrame
    {
        // Synchronized Output (DEC private mode 2026): the terminal shows the frame once it is complete.
        // Consoles without it ignore the mode.
        public const string BeginSynchronized = "\e[?2026h";
        public const string EndSynchronized = "\e[?2026l";
        public const string ClearScreen = "\e[H\e[2J";
        // Erase the whole row before writing it. Wrap is off, so a row wider on screen than its length
        // (wide glyphs) piles its overflow into the rightmost column, which rows never fill. The row is
        // erased before it is written, so each time a row is written again whatever an earlier row left
        // in that column is cleared, and a wide glyph of the row that ends in that column stays (an
        // erase after the row would remove that glyph whole).
        public const string EraseRow = "\e[2K";
        // Only an extreme window makes a frame this large; it is then written in parts.
        public const int MaxWriteBytes = 32 * 1024;

        public static string CursorToRow(int row) => string.Create(CultureInfo.InvariantCulture, $"\e[{row + 1};1H");

        // The frame as writes of at most maxBytes UTF-8 bytes each, split between rows (a row that does
        // not fit alone is a write of its own). Synchronized Output encloses the whole frame.
        public static List<string> Compose(bool clear, IReadOnlyList<ConsoleRow> rows, int maxBytes = MaxWriteBytes)
        {
            List<string> writes = [];
            StringBuilder text = new(BeginSynchronized);
            if (clear) _ = text.Append(ClearScreen);
            int bytes = text.Length;
            int rowsInWrite = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                string move = CursorToRow(rows[i].Row);
                int rowBytes = move.Length + EraseRow.Length + Encoding.UTF8.GetByteCount(rows[i].Text) + (i == rows.Count - 1 ? EndSynchronized.Length : 0);
                if (rowsInWrite > 0 && bytes + rowBytes > maxBytes)
                {
                    writes.Add(text.ToString());
                    _ = text.Clear();
                    bytes = 0;
                    rowsInWrite = 0;
                }
                _ = text.Append(move).Append(EraseRow).Append(rows[i].Text);
                bytes += rowBytes;
                rowsInWrite++;
            }
            writes.Add(text.Append(EndSynchronized).ToString());
            return writes;
        }
    }

    // The console output stream, opened once (each open sends a probe write). A frame goes here as one
    // write of UTF-8 bytes (the console code page the TUI sets) instead of through Console.Out, whose
    // 256-character buffer turns a frame into a WriteFile per 256 characters.
    internal static class ConsoleOutput
    {
        private static readonly Stream Output = Console.OpenStandardOutput();

        public static void Write(List<string> writes)
        {
            // Sequences given to Console.Out (alternate screen, modes) reach the console first.
            Console.Out.Flush();
            foreach (string text in writes)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(text.Length));
                try
                {
                    int length = Encoding.UTF8.GetBytes(text, buffer);
                    Output.Write(buffer, 0, length);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
    }

    // The real console. Keep drawing within both the visible window and the current buffer, leaving
    // the rightmost column unused so writing the footer cannot scroll the screen.
    internal sealed class SystemConsole : IConsoleSurface
    {
        public ConsoleSize Size => ConsoleWindow.DrawableSize();

        public void WriteFrame(bool clear, IReadOnlyList<ConsoleRow> rows) => ConsoleOutput.Write(VtFrame.Compose(clear, rows));
    }

    // clock: monotonic time for the age of the last full redraw (a Stopwatch by default, fakes in tests).
    internal sealed class ConsoleRenderer(IConsoleSurface console, Func<TimeSpan>? clock = null)
    {
        // The first frame this long after the last full redraw writes every row even if unchanged, so a
        // screen changed behind the renderer (a clear in the terminal, a resize missed between two size
        // checks) does not stay wrong. TuiMonitor.Loop draws a frame as soon as FullRedrawDue, also while
        // nothing changes (paused, a long -Interval), so every row is written again within FullRedrawAge
        // (plus at most LoopPace.Fallback) of the last full redraw.
        public static readonly TimeSpan FullRedrawAge = TimeSpan.FromSeconds(10);

        private readonly IConsoleSurface console = console;
        private readonly Func<TimeSpan> clock = clock ?? MonotonicClock();
        // The rows on the screen as last written; null when unknown (start, clear, an unusable size, a
        // failed or unfinished write), which makes the next frame write every row.
        private string[]? shown;
        // The clock at the start of the last frame that wrote every row.
        private TimeSpan lastFullRedraw;

        // FullRedrawAge has passed since the last frame that wrote every row: the next frame writes every row.
        public bool FullRedrawDue => clock() - lastFullRedraw >= FullRedrawAge;

        // Time since the renderer was made (QueryPerformanceCounter: a change of the system clock does not move it).
        private static Func<TimeSpan> MonotonicClock()
        {
            long origin = Stopwatch.GetTimestamp();
            return () => Stopwatch.GetElapsedTime(origin);
        }

        // Writes the rows that differ from the screen in one console write (none when nothing changed);
        // clear = clear the screen and write every row, as does the first frame FullRedrawAge after the
        // last full redraw. Returns false if the size is unusable or changed during the write; the UI
        // then draws again on its next pass, writing every row (after clearing the screen when the size
        // differs from the last one shown).
        public bool ShowFrame(Frame frame, ConsoleSize size, bool clear = false)
        {
            TimeSpan now = clock();
            if (size.Width < 1 || size.Height < 1)
            {
                shown = null;
                return false;
            }
            string[] rows = RenderRows(frame, size);
            bool full = clear || shown is null || shown.Length != rows.Length || now - lastFullRedraw >= FullRedrawAge;
            if (full) lastFullRedraw = now;
            string[]? previous = full ? null : shown;
            List<ConsoleRow> changed = [];
            for (int row = 0; row < rows.Length; row++)
            {
                if (previous is null || rows[row] != previous[row]) changed.Add(new ConsoleRow(row, rows[row]));
            }
            if (changed.Count == 0) return true;

            // Unknown until the write and the size check below succeed (an exception leaves it so).
            shown = null;
            console.WriteFrame(clear, changed);
            // Rows written for the old size may be clipped or misplaced: leave the frame unfinished.
            if (console.Size != size) return false;
            shown = rows;
            return true;
        }

        // Every screen row as console text: the body, blank rows, and the footer on the bottom row.
        private static string[] RenderRows(Frame frame, ConsoleSize size)
        {
            string[] rows = new string[size.Height];
            string blank = ConsoleLine.Render(new FrameLine(), size.Width);
            for (int row = 0; row < rows.Length - 1; row++) rows[row] = row < frame.Body.Count ? ConsoleLine.Render(frame.Body[row], size.Width) : blank;
            rows[^1] = ConsoleLine.Render(frame.Footer, size.Width, ConsoleColor.DarkCyan);
            return rows;
        }

        // Renders the current state; the first frame after a resize clears the screen.
        public void ShowTuiScreen(MonitorSession session, MonitorView view, Func<MonitorSession, MonitorView, int, Frame>? build = null)
        {
            ConsoleSize size = console.Size;
            if (size.Width < 1 || size.Height < 1)
            {
                // The screen is unknown when the size comes back.
                shown = null;
                view.Dirty = true;
                return;
            }
            bool clear = size.Width != view.LastWidth || size.Height != view.LastHeight;
            if (clear)
            {
                view.LastWidth = size.Width;
                view.LastHeight = size.Height;
            }
            Frame frame = (build ?? MonitorFrame.Build)(session, view, size.Width);
            view.Dirty = !ShowFrame(frame, size, clear);
        }

        public bool IsWindowResized(MonitorView view)
        {
            ConsoleSize size = console.Size;
            return size.Width != view.LastWidth || size.Height != view.LastHeight;
        }

        // Plain sequential output for redirected consoles (pipes, logs, CI).
        public static void ShowPlainFrame(Frame frame, TextWriter output)
        {
            foreach (FrameLine line in frame.Body) output.WriteLine(line.Text);
            output.WriteLine("");
        }
    }

    // Console modes for the TUI: VT processing (alternate screen, colors, no automatic wrap) on the output
    // handle, window events (resizes) on the input handle.
    internal static partial class ConsoleMode
    {
        private const int StdInputHandle = -10;
        private const int StdOutputHandle = -11;
        private const uint EnableVirtualTerminalProcessing = 0x0004;
        private const uint EnableWindowInput = 0x0008;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr GetStdHandle(int handle);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetConsoleMode(IntPtr handle, out uint mode);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetConsoleMode(IntPtr handle, uint mode);

        public static IntPtr OutputHandle => GetStdHandle(StdOutputHandle);

        // Returns the previous mode to restore, or null when the output is not a console.
        public static uint? EnableVirtualTerminal()
        {
            IntPtr handle = OutputHandle;
            if (!GetConsoleMode(handle, out uint mode)) return null;
            if ((mode & EnableVirtualTerminalProcessing) == 0) _ = SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
            return mode;
        }

        public static void Restore(uint? mode)
        {
            if (mode is uint m) _ = SetConsoleMode(OutputHandle, m);
        }

        public static IntPtr InputHandle => GetStdHandle(StdInputHandle);

        // Puts resize events into the input buffer, so they wake the TUI at once (Console.KeyAvailable
        // reads and drops them). Returns true only when this call set the bit.
        public static bool AddWindowInput()
        {
            IntPtr handle = InputHandle;
            return GetConsoleMode(handle, out uint mode) && (mode & EnableWindowInput) == 0 && SetConsoleMode(handle, mode | EnableWindowInput);
        }

        // Clears that bit alone: Console.TreatControlCAsInput changes another bit of the same mode, so
        // the two can be restored in any order.
        public static void RemoveWindowInput()
        {
            IntPtr handle = InputHandle;
            if (GetConsoleMode(handle, out uint mode)) _ = SetConsoleMode(handle, mode & ~EnableWindowInput);
        }
    }

    // The console input handle as a WaitHandle: signaled while the input buffer holds any event (keys, key
    // releases, focus, mouse, resizes). Not owned: disposing it does not close the standard input.
    internal sealed class ConsoleInputWaitHandle : WaitHandle
    {
        public ConsoleInputWaitHandle() => SafeWaitHandle = new SafeWaitHandle(ConsoleMode.InputHandle, ownsHandle: false);
    }

    // CONSOLE_SCREEN_BUFFER_INFO. Every field is declared because the call writes all 22 bytes.
    [StructLayout(LayoutKind.Sequential)]
    internal struct ScreenBufferInfo
    {
        public short BufferWidth, BufferHeight;
        public short CursorLeft, CursorTop;
        public ushort Attributes;
        public short WindowLeft, WindowTop, WindowRight, WindowBottom;
        public short MaximumWindowWidth, MaximumWindowHeight;
    }

    // The drawable console size from one GetConsoleScreenBufferInfo call (Console.WindowWidth,
    // BufferWidth, WindowHeight and BufferHeight make one call each).
    internal static partial class ConsoleWindow
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetConsoleScreenBufferInfo(IntPtr handle, out ScreenBufferInfo info);

        public static ConsoleSize DrawableSize() => GetConsoleScreenBufferInfo(ConsoleMode.OutputHandle, out ScreenBufferInfo info)
            ? DrawableSize(info)
            : throw new IOException(Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError()));

        // The visible window within the buffer, without the rightmost column: the same values as
        // Min(Console.WindowWidth, Console.BufferWidth) - 1 and Min(Console.WindowHeight, Console.BufferHeight).
        public static ConsoleSize DrawableSize(ScreenBufferInfo info) => new(
            Math.Max(0, Math.Min(info.WindowRight - info.WindowLeft + 1, info.BufferWidth) - 1),
            Math.Max(0, Math.Min(info.WindowBottom - info.WindowTop + 1, info.BufferHeight)));
    }
}
