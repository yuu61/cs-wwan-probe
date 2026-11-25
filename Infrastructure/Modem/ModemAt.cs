using System.Diagnostics;

namespace WwanProbe.Infrastructure.Modem
{
    // AT channel and command set found once at startup (a failed detection needs a restart).
    // Channel / Profile are null when unavailable, Error is a short reason for the screen and
    // Tried lists "<channel>: <reason>" per MBIM service that did not answer OK.
    internal sealed record ModemAt(AtChannel? Channel, AtProfile? Profile, string? Error, IReadOnlyList<string> Tried)
    {
        public static readonly ModemAt NotInitialized = new(null, null, null, []);

        // atPort ("COM7") uses that serial port instead of the MBIM services.
        public static ModemAt Initialize(IAtSender sender, string? atPort)
        {
            AtChannel? channel = null;
            AtProfile? profile = null;
            List<string> tried = [];
            try
            {
                if (!string.IsNullOrEmpty(atPort))
                {
                    AtChannel serial = AtChannel.Serial(atPort);
                    if (!AtResponse.IsOk(AtChannelDetector.Probe(sender, serial, "AT")))
                    {
                        throw new InvalidOperationException($"no AT response on {atPort}");
                    }
                    channel = serial;
                }
                else
                {
                    channel = AtChannelDetector.Find(sender, tried) ?? throw new InvalidOperationException($"no AT channel ({tried.Count} MBIM services tried)");
                }
                // A channel that did not answer "AT" (AtChannelDetector.Find fallback) would not answer the
                // probes either, so it goes straight to the command set its service implies.
                if (!channel.Unconfirmed)
                {
                    profile = AtProfile.All.FirstOrDefault(p => AtResponse.IsOk(AtChannelDetector.Probe(sender, channel, p.Probe)));
                }
                // A service that exists on one chipset only (the Intel AT Tunnel) keeps its tested command set
                // even when the probe was lost, as before profiles existed (each sample tolerates failed commands).
                profile ??= AtProfile.Find(channel.ProfileId);
                string? error = profile is null ? $"unsupported AT command set on {channel.Name}" : null;
                return new ModemAt(channel, profile, error, tried);
            }
            catch (Exception ex)
            {
                return new ModemAt(channel, profile, ex.Message, tried);
            }
        }

        // Values WinRT does not provide, read over the AT channel in one session. timeouts gives the wait per
        // command and learns from the session; without it the IAtSender default applies.
        public AtStatus ReadStatus(IAtSender sender, AtSessionTimeout? timeouts = null)
        {
            if (Channel is null || Profile is null) throw new InvalidOperationException(Error ?? "AT not initialized");
            if (timeouts is null) return AtStatusParser.ParseStatus(Profile, sender.SendAt(Channel, Profile.Status));
            int timeoutMs = timeouts.NextMs;
            long started = Stopwatch.GetTimestamp();
            IReadOnlyDictionary<string, string?> responses = sender.SendAt(Channel, Profile.Status, timeoutMs);
            timeouts.Record(timeoutMs, responses.Values.Any(r => r is not null), Stopwatch.GetElapsedTime(started));
            return AtStatusParser.ParseStatus(Profile, responses);
        }

        // RAT / band configuration (does not change while running), or null when unavailable.
        public Domain.RatConfig? ReadConfig(IAtSender sender)
        {
            if (Channel is null || Profile is null) return null;
            try { return AtStatusParser.ParseConfig(Profile, sender.SendAt(Channel, Profile.Config)); }
            catch (Exception) { return null; }
        }
    }

    // The per-command timeout of the per-sample AT session (ModemAt.ReadStatus only; startup detection and
    // the configuration read keep theirs). A session is silent when no command answered (gave a response,
    // not null) and the whole session, getting the service and opening and closing the session included,
    // lasted at least its timeout. Typically the first command timed out, so the rest were skipped; refusals
    // (a status other than success) that add up to the timeout count too. After silentBeforeShort silent
    // sessions in a row the timeout drops from NormalMs to ShortMs, so a channel that never answers holds
    // each sample for 1 s instead of 3 s. One session in fullEvery still waits NormalMs, so a modem that
    // answers slowly is found again, and any answer (or a session that ends sooner without one) restores
    // NormalMs. A session that throws before it is sent changes nothing. Used by one sample at a time.
    internal sealed class AtSessionTimeout(int normalMs = 3000, int shortMs = 1000, int silentBeforeShort = 3, int fullEvery = 10)
    {
        private int silent;         // silent sessions in a row, counted up to silentBeforeShort
        private int shortSessions;  // ShortMs sessions since the last NormalMs one

        public int NormalMs { get; } = normalMs;
        public int ShortMs { get; } = shortMs;
        public bool Shortened => silent >= silentBeforeShort;

        // The timeout for the next session.
        public int NextMs => Shortened && shortSessions < fullEvery - 1 ? ShortMs : NormalMs;

        // A session sent with timeoutMs that took elapsed; answered = at least one command got a response.
        public void Record(int timeoutMs, bool answered, TimeSpan elapsed)
        {
            if (answered || elapsed < TimeSpan.FromMilliseconds(timeoutMs))
            {
                silent = 0;
                shortSessions = 0;
                return;
            }
            if (silent < silentBeforeShort) silent++;
            shortSessions = timeoutMs < NormalMs ? shortSessions + 1 : 0;
        }
    }

    internal static class AtChannelDetector
    {
        // Sends one command for startup probing and returns its response (null = no answer). A timed-out
        // command is sent once more: on the L860-GL the AT Tunnel was seen to drop a response right after
        // the process starts using it (no retry is needed once running).
        public static string? Probe(IAtSender sender, AtChannel channel, string command, int timeoutMs = 1500)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string? r = sender.SendAt(channel, [command], timeoutMs).Get(command);
                if (r is not null) return r;
            }
            return null;
        }

        // Finds the first MBIM AT channel that answers "AT" with OK, or null. tried receives one
        // "<name>: <reason>" line per service that did not answer OK (for diagnostics).
        // When none answers, a service with a ProfileId whose session opened is still returned (as a copy
        // with Unconfirmed = true): it only exists on that chipset, and its lost startup answers must not
        // disable AT for the whole session (each sample tolerates failed commands).
        public static AtChannel? Find(IAtSender sender, List<string>? tried, int timeoutMs = 1500)
        {
            AtChannel? fallback = null;
            foreach (AtChannel channel in AtChannel.Mbim)
            {
                string reason;
                try
                {
                    string? response = Probe(sender, channel, "AT", timeoutMs);
                    if (AtResponse.IsOk(response)) return channel;
                    reason = response is null ? "no response" : "no OK";
                    if (fallback is null && channel.ProfileId is not null) fallback = channel with { Unconfirmed = true };
                }
                catch (Exception ex)
                {
                    reason = ex.Message;
                }
                tried?.Add($"{channel.Name}: {reason}");
            }
            return fallback;
        }
    }
}
