using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using WwanProbe.Application;

namespace WwanProbe.Presentation
{
    internal readonly record struct ConsoleSize(int Width, int Height);

    // The console operations the TUI needs (SystemConsole on a real console, fakes in tests).
    internal interface IConsoleSurface
    {
        ConsoleSize Size { get; }
        void SetCursorRow(int row);
        void WriteLine(FrameLine line, int width, ConsoleColor? background);
        void Clear();
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
                    _ = sb.Append(segment.Text, 0, length);
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
                _ = sb.Append(text.PadRight(Math.Max(0, width)));
            }
            _ = sb.Append("\e[0m");
            return sb.ToString();
        }
    }

    // The real console. Keep drawing within both the visible window and the current buffer, leaving
    // the rightmost column unused so writing the footer cannot scroll the screen.
    internal sealed class SystemConsole : IConsoleSurface
    {
        public ConsoleSize Size => new(
            Math.Max(0, Math.Min(Console.WindowWidth, Console.BufferWidth) - 1),
            Math.Max(0, Math.Min(Console.WindowHeight, Console.BufferHeight)));

        public void SetCursorRow(int row) => Console.SetCursorPosition(0, row);

        public void WriteLine(FrameLine line, int width, ConsoleColor? background) => Console.Out.Write(ConsoleLine.Render(line, width, background));

        public void Clear() => Console.Clear();
    }

    internal sealed class ConsoleRenderer(IConsoleSurface console)
    {
        private readonly IConsoleSurface console = console;

        // Redraws the whole screen in place (no clear per frame to avoid flicker).
        // Returns false if a resize interrupts this frame; the UI retries on its next tick.
        public bool ShowFrame(Frame frame, int width, int height)
        {
            if (width < 1 || height < 1) return false;
            for (int row = 0; row < height; row++)
            {
                if (console.Size != new ConsoleSize(width, height)) return false;
                // The buffer can shrink between the size check and this cursor move.
                try { console.SetCursorRow(row); }
                catch (ArgumentOutOfRangeException) { return false; }

                if (row == height - 1) console.WriteLine(frame.Footer, width, ConsoleColor.DarkCyan);
                else if (row < frame.Body.Count) console.WriteLine(frame.Body[row], width, null);
                else console.WriteLine(new FrameLine(), width, null);
            }
            return true;
        }

        // Renders the current state; clears the screen once when the window is resized.
        public void ShowTuiScreen(MonitorSession session, MonitorView view, Func<MonitorSession, MonitorView, int, Frame>? build = null)
        {
            ConsoleSize size = console.Size;
            if (size.Width < 1 || size.Height < 1)
            {
                view.Dirty = true;
                return;
            }
            if (size.Width != view.LastWidth || size.Height != view.LastHeight)
            {
                console.Clear();
                view.LastWidth = size.Width;
                view.LastHeight = size.Height;
            }
            Frame frame = (build ?? MonitorFrame.Build)(session, view, size.Width);
            view.Dirty = !ShowFrame(frame, size.Width, size.Height);
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

    // Console modes for the TUI: VT processing (alternate screen, colors) on the output handle.
    internal static partial class ConsoleMode
    {
        private const int StdOutputHandle = -11;
        private const uint EnableVirtualTerminalProcessing = 0x0004;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr GetStdHandle(int handle);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetConsoleMode(IntPtr handle, out uint mode);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetConsoleMode(IntPtr handle, uint mode);

        // Returns the previous mode to restore, or null when the output is not a console.
        public static uint? EnableVirtualTerminal()
        {
            IntPtr handle = GetStdHandle(StdOutputHandle);
            if (!GetConsoleMode(handle, out uint mode)) return null;
            if ((mode & EnableVirtualTerminalProcessing) == 0) _ = SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
            return mode;
        }

        public static void Restore(uint? mode)
        {
            if (mode is uint m) _ = SetConsoleMode(GetStdHandle(StdOutputHandle), m);
        }
    }
}
