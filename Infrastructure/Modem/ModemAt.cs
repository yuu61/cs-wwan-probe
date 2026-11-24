namespace WwanProbe.Infrastructure.Modem
{
    // AT channel and command set found once at startup (a failed detection needs a restart).
    // Channel / Profile are null when unavailable, Error is a short reason for the screen and
    // Tried lists "<channel>: <reason>" per MBIM service that did not answer OK.
    public sealed record ModemAt(AtChannel? Channel, AtProfile? Profile, string? Error, IReadOnlyList<string> Tried)
    {
        public static readonly ModemAt NotInitialized = new ModemAt(null, null, null, Array.Empty<string>());

        // atPort ("COM7") uses that serial port instead of the MBIM services.
        public static ModemAt Initialize(IAtSender sender, string? atPort)
        {
            AtChannel? channel = null;
            AtProfile? profile = null;
            List<string> tried = new List<string>();
            try
            {
                if (!string.IsNullOrEmpty(atPort))
                {
                    AtChannel serial = AtChannel.Serial(atPort);
                    if (!AtResponse.IsOk(AtChannelDetector.Probe(sender, serial, "AT"))) throw new InvalidOperationException($"no AT response on {atPort}");
                    channel = serial;
                }
                else
                {
                    channel = AtChannelDetector.Find(sender, tried);
                    if (channel is null) throw new InvalidOperationException($"no AT channel ({tried.Count} MBIM services tried)");
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

        // Values WinRT does not provide, read over the AT channel in one session.
        public AtStatus ReadStatus(IAtSender sender)
        {
            if (Channel is null || Profile is null) throw new InvalidOperationException(Error ?? "AT not initialized");
            return AtStatusParser.ParseStatus(Profile, sender.SendAt(Channel, Profile.Status));
        }

        // RAT / band configuration (does not change while running), or null when unavailable.
        public Domain.RatConfig? ReadConfig(IAtSender sender)
        {
            if (Channel is null || Profile is null) return null;
            try { return AtStatusParser.ParseConfig(Profile, sender.SendAt(Channel, Profile.Config)); }
            catch (Exception) { return null; }
        }
    }

    public static class AtChannelDetector
    {
        // Sends one command for startup probing and returns its response (null = no answer). A timed-out
        // command is sent once more: on the L860-GL the AT Tunnel was seen to drop a response right after
        // the process starts using it (no retry is needed once running).
        public static string? Probe(IAtSender sender, AtChannel channel, string command, int timeoutMs = 1500)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string? r = sender.SendAt(channel, new[] { command }, timeoutMs).Get(command);
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
