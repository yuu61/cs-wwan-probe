using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;

namespace WwanProbe.Infrastructure.Modem
{
    // Crlf: request is "<cmd>\r\n" as ASCII, response is the raw AT response text.
    // Qdu:  request is UINT32 CommandType (0 = AT) + "<cmd>", response is UINT32 status (0 = OK) + text.
    internal enum AtFraming
    {
        Crlf,
        Qdu,
    }

    // Where AT commands go: a vendor MBIM device service or a serial (COM) port.
    //   IsQuery   = MBIM command type used by libmbim / mbimcli for the AT command (query, else set)
    //   ProfileId = AtProfile Id the service implies (it exists on one chipset family only), or null
    //   Unconfirmed = the service did not answer "AT" at startup (see AtChannelDetector.Find)
    internal sealed record AtChannel(string Name, Guid ServiceId, uint Cid, bool IsQuery, AtFraming Framing, string? ProfileId = null)
    {
        public string? Port { get; init; }
        public bool Unconfirmed { get; init; }

        // Vendor MBIM device services that carry AT commands, in probe order (libmbim data/mbim-service-*.json,
        // src/libmbim-glib/mbim-uuid.c and mbim-cid.h). The modem's COM ports may be held by a driver, so AT
        // commands go over MBIM when the firmware offers one of these. Only the L860-GL (Intel) was tested.
        public static readonly IReadOnlyList<AtChannel> Mbim =
        [
            new AtChannel("Intel AT Tunnel", new Guid("da138c64-6515-4893-92b2-a1e1ca7c81ca"), 1, false, AtFraming.Crlf, AtProfile.IntelId),
            new AtChannel("Fibocom AT", new Guid("ffffffff-abca-4b11-a4e2-f2fc87f94488"), 1, false, AtFraming.Crlf),
            new AtChannel("Compal AT", new Guid("a2a32a97-cab1-4f57-9ae1-451c74dda957"), 1, true, AtFraming.Crlf),
            // Quectel QDU is a firmware update service; only CID 8 (COMMAND) is ever sent.
            new AtChannel("Quectel QDU", new Guid("6427015f-579d-48f5-8c54-f43ed1e76f83"), 8, false, AtFraming.Qdu),
        ];

        // AT channel over a serial (COM) port, for modems whose driver exposes a free AT port.
        public static AtChannel Serial(string portName) => new($"Serial {portName}", Guid.Empty, 0, false, AtFraming.Crlf) { Port = portName };
    }

    // Sends AT commands in order over one session of a channel. Returns command -> raw response text
    // (including the final "OK"/"ERROR"). A command whose MBIM status is not success maps to null;
    // after a timeout the remaining commands are skipped (also null) so a stuck modem does not stall every call.
    internal interface IAtSender
    {
        IReadOnlyDictionary<string, string?> SendAt(AtChannel channel, IReadOnlyList<string> commands, int timeoutMs = 3000);
    }

    internal static partial class MbimAtFraming
    {
        [GeneratedRegex(@"^OK(\s*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex OkLine();

        public static byte[] ToRequest(AtFraming framing, string command)
        {
            if (framing == AtFraming.Qdu)
            {
                // mbimcli --quectel-set-command sends the command without a line terminator.
                return [.. BitConverter.GetBytes(0u), .. Encoding.ASCII.GetBytes(command)];
            }
            return Encoding.ASCII.GetBytes(command + "\r\n");
        }

        // Response bytes -> AT response text. For QDU the status word decides the final result code
        // when the text itself carries none (undocumented whether it does), so the parsers can rely on it.
        public static string? FromResponse(AtFraming framing, byte[]? bytes)
        {
            bytes ??= [];
            if (framing != AtFraming.Qdu) return Encoding.ASCII.GetString(bytes);
            if (bytes.Length < 4) return null;
            uint status = BitConverter.ToUInt32(bytes, 0);
            string text = Encoding.ASCII.GetString(bytes, 4, bytes.Length - 4).TrimEnd('\0');
            if (!FinalResult().IsMatch(text)) text += status == 0 ? "\r\nOK\r\n" : "\r\nERROR\r\n";
            else if (status != 0 && OkLine().IsMatch(text)) text = OkLine().Replace(text, "ERROR$1");
            return text;
        }

        [GeneratedRegex(@"^(OK|ERROR|\+CME ERROR:.*)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
        private static partial Regex FinalResult();
    }

    // Same contract as IAtSender over a COM port (opened for this call only).
    internal static partial class SerialAt
    {

        public static IReadOnlyDictionary<string, string?> Send(string portName, IReadOnlyList<string> commands, int timeoutMs)
        {
            Dictionary<string, string?> responses = new(StringComparer.OrdinalIgnoreCase);
            foreach (string cmd in commands) responses[cmd] = null;
            using SerialPort port = new(portName, 115200)
            {
                NewLine = "\r\n",
                ReadTimeout = 100,
                // USB AT ports may ignore input until DTR is asserted (fibocom-connect-fm350 sets it for the FM350 "MD AT" port).
                DtrEnable = true,
            };
            port.Open();
            port.DiscardInBuffer();
            foreach (string cmd in commands)
            {
                port.Write(cmd + "\r");
                StringBuilder text = new();
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                bool done = false;
                while (!done && DateTime.UtcNow < deadline)
                {
                    string line;
                    try { line = port.ReadLine(); }
                    catch (TimeoutException) { continue; }
                    _ = text.Append(line).Append("\r\n");
                    done = FinalResult().IsMatch(line);
                }
                if (!done) break;
                responses[cmd] = text.ToString();
            }
            return responses;
        }

        [GeneratedRegex(@"^(OK|ERROR|\+CME ERROR:.*|\+CMS ERROR:.*)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex FinalResult();
    }
}
