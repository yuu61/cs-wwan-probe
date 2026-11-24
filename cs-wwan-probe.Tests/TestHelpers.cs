using System.Text;
using WwanProbe.Application;
using WwanProbe.Domain;
using WwanProbe.Infrastructure;
using WwanProbe.Infrastructure.Modem;
using WwanProbe.Presentation;

namespace WwanProbe.Tests
{
    internal static class At
    {
        // AT response text from lines (CRLF, final OK).
        public static string Response(params string[] lines) => string.Join("\r\n", lines.Append("OK")) + "\r\n";

        public static Dictionary<string, string?> Responses(params (string Command, string? Response)[] pairs)
        {
            Dictionary<string, string?> d = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string c, string? r) in pairs) d[c] = r;
            return d;
        }

        public static AtProfile Profile(string id) => AtProfile.Find(id)!;

        public static ModemAt Intel => new(AtChannel.Mbim[0], Profile(AtProfile.IntelId), null, []);
    }

    // Simulated modem: WinRT values plus AT handlers per channel name (other channels throw like a
    // missing MBIM service). Every AT command sent is recorded in Sent.
    internal sealed class FakeModem : IModemDevice
    {
        public ModemDeviceInfo DeviceInfo { get; set; } = new ModemDeviceInfo("Test", "", "", "", "", "", "On", "Lte");
        public ModemNetworkInfo Network { get; set; } = new ModemNetworkInfo("", "44010", "Lte", "");
        public CellsInfo Cells { get; set; } = new CellsInfo();
        public Dictionary<string, Func<string, string?>> AtHandlers { get; } = [];
        public List<string> Sent { get; } = [];

        public ModemDeviceInfo GetDeviceInfo() => DeviceInfo;

        public ModemNetworkInfo GetNetwork() => Network;

        public CellsInfo GetCellsInfo() => Cells;

        public IReadOnlyDictionary<string, string?> SendAt(AtChannel channel, IReadOnlyList<string> commands, int timeoutMs = 3000)
        {
            if (!AtHandlers.TryGetValue(channel.Name, out Func<string, string?>? handler))
            {
                throw new InvalidOperationException($"{channel.Name} device service not available");
            }
            Dictionary<string, string?> r = new(StringComparer.OrdinalIgnoreCase);
            foreach (string c in commands)
            {
                Sent.Add(c);
                r[c] = handler(c);
            }
            return r;
        }

        // Intel AT Tunnel answering the per-sample status commands from a fixed table.
        public FakeModem WithIntelStatus(IReadOnlyDictionary<string, string?> responses)
        {
            AtHandlers[AtChannel.Mbim[0].Name] = c => responses.TryGetValue(c, out string? r) ? r : null;
            return this;
        }
    }

    internal sealed class FakeTraffic : ITrafficCounter
    {
        public TrafficReading Reading { get; set; } = new TrafficReading(0, 0, 0, null);

        public TrafficReading Read() => Reading;
    }

    internal static class Fixtures
    {
        public static LteCellInfo LteCell(long? cellId, string? provider, double? rsrp, double? rsrq, long? channel,
            long? pci = null, long? tac = null, int? ta = null) => new(cellId, provider, rsrp, rsrq, channel, pci, tac, ta);

        public static MonitorSession Session(int count = 0, int interval = 0, string csvPath = "")
        {
            return new MonitorSession
            {
                Config = new MonitorConfig { Count = count, Interval = interval, CsvPath = csvPath },
                Summary = new ModemSummary { Model = "Test" },
            };
        }

        public static string Text(Frame frame) => string.Join("\n", frame.Body.Select(l => l.Text));

        public static string TempFile() => Path.Combine(Path.GetTempPath(), $"wwan-probe-test-{Guid.NewGuid():N}.csv");

        // Minimal RFC 4180 reader: header -> value per row.
        public static List<Dictionary<string, string>> ReadCsv(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "The CSV must start with a UTF-8 BOM.");
            Assert.Equal(1, CountBom(bytes));
            string text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            List<List<string>> records = [];
            List<string> record = [];
            StringBuilder field = new();
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        _ = field.Append('"');
                        i++;
                    }
                    else if (c == '"')
                    {
                        quoted = false;
                    }
                    else
                    {
                        _ = field.Append(c);
                    }
                }
                else if (c == '"')
                {
                    quoted = true;
                }
                else if (c == ',')
                {
                    record.Add(field.ToString());
                    _ = field.Clear();
                }
                else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    record.Add(field.ToString());
                    _ = field.Clear();
                    records.Add(record);
                    record = [];
                    i++;
                }
                else
                {
                    _ = field.Append(c);
                }
            }
            List<string> header = records[0];
            return [.. records.Skip(1).Select(r => header.Select((h, i) => (h, v: r[i])).ToDictionary(x => x.h, x => x.v))];
        }

        private static int CountBom(byte[] bytes)
        {
            int count = 0;
            for (int i = 0; i + 2 < bytes.Length; i++)
            {
                if (bytes[i] == 0xEF && bytes[i + 1] == 0xBB && bytes[i + 2] == 0xBF) count++;
            }
            return count;
        }
    }

    // Simulated terminal for the TUI loop: queued keys and a callback per drawn frame.
    internal sealed class FakeTerminal : ITuiTerminal
    {
        public Queue<ConsoleKeyInfo> Keys { get; } = new Queue<ConsoleKeyInfo>();
        public Action<MonitorSession, MonitorView> OnFrame { get; set; } = (_, _) => { };
        public Action? OnRead { get; set; }

        public void Add(ConsoleKey key, char character = '\0') => Keys.Enqueue(new ConsoleKeyInfo(character, key, false, false, false));

        public ConsoleKeyInfo? ReadKey()
        {
            if (Keys.Count > 0) return Keys.Dequeue();
            OnRead?.Invoke();
            return null;
        }

        public bool IsWindowResized(MonitorView view) => false;

        public void Show(MonitorSession session, MonitorView view) => OnFrame(session, view);
    }

    internal static class Keys
    {
        public static Func<ConsoleKeyInfo?> From(Queue<ConsoleKeyInfo> queue) => () => queue.Count > 0 ? queue.Dequeue() : null;

        public static Queue<ConsoleKeyInfo> Of(params ConsoleKey[] keys) => new(keys.Select(k => new ConsoleKeyInfo('\0', k, false, false, false)));
    }

    internal static class Snapshots
    {
        public static LteSnapshot Handover(long? cellId = 100, string provider = "44010")
        {
            return new LteSnapshot
            {
                Timestamp = "2026-09-24 12:34:56",
                ProviderId = provider,
                Neighbors = [],
                PrimaryCell = new ServingCell { Provider = provider, CellId = cellId, Band = "B1", Earfcn = 100, Pci = 10, Tac = 20, RsrpDbm = -90 },
            };
        }
    }
}
