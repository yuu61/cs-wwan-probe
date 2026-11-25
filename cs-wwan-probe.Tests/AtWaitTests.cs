using System.Diagnostics;
using System.Text.Json;
using WwanProbe.Application;
using WwanProbe.Infrastructure.Modem;

namespace WwanProbe.Tests
{
    // How long the per-sample AT session waits (AtSessionTimeout, SerialAt) and the network handoff of WinRtModem.
    public class AtWaitTests
    {
        // Modem whose AT channel is switched by the test: silent waits out the whole timeout and answers
        // nothing (a first command that times out, after which the rest are skipped), otherwise every command
        // answers OK at once; Throws fails before anything is sent (no session). Each timeout is recorded.
        private sealed class StuckModem : IModemDevice
        {
            public bool Silent { get; set; } = true;
            public bool Throws { get; set; }
            public List<int> Timeouts { get; } = [];

            public ModemDeviceInfo GetDeviceInfo() => new("Test", "", "", "", "", "", "On", "Lte");

            public ModemNetworkInfo GetNetwork() => new("", "44010", "Lte", "");

            public CellsInfo GetCellsInfo() => new() { ServingLte = [Fixtures.LteCell(100, "44010", -90, -10, 100, pci: 10)] };

            public IReadOnlyDictionary<string, string?> SendAt(AtChannel channel, IReadOnlyList<string> commands, int timeoutMs = 3000)
            {
                Timeouts.Add(timeoutMs);
                if (Throws) throw new InvalidOperationException("Intel AT Tunnel device service not available");
                Dictionary<string, string?> responses = new(StringComparer.OrdinalIgnoreCase);
                foreach (string c in commands) responses[c] = Silent ? null : At.Response();
                Stopwatch waited = Stopwatch.StartNew();
                while (Silent && waited.Elapsed < TimeSpan.FromMilliseconds(timeoutMs)) Thread.Sleep(1);
                return responses;
            }
        }

        // Timeouts the next sessions get when none of them answers.
        private static List<int> SilentSessions(AtSessionTimeout timeouts, int count)
        {
            List<int> used = [];
            for (int i = 0; i < count; i++)
            {
                int ms = timeouts.NextMs;
                used.Add(ms);
                timeouts.Record(ms, false, TimeSpan.FromMilliseconds(ms));
            }
            return used;
        }

        // Three silent sessions in a row shorten the wait to 1 s; then one session in ten waits 3 s again.
        [Fact]
        public void SilentSessionsShortenTheWaitAndEveryTenthWaitsInFull()
        {
            AtSessionTimeout timeouts = new();
            Assert.Equal(3000, timeouts.NextMs);
            List<int> used = SilentSessions(timeouts, 25);
            int[] expected =
            [
                3000, 3000, 3000,
                1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 3000,
                1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 3000,
                1000, 1000,
            ];
            Assert.Equal(expected, used);
            Assert.True(timeouts.Shortened);
        }

        // Any answer restores 3 s, and so does a session that ends sooner than its timeout without one (the
        // modem rejected the commands instead of staying silent); either also breaks a run of silent sessions.
        [Fact]
        public void AnAnswerOrAnEarlyEndRestoresTheFullWait()
        {
            AtSessionTimeout timeouts = new();
            _ = SilentSessions(timeouts, 4);
            timeouts.Record(1000, true, TimeSpan.FromMilliseconds(1000));
            Assert.True(!timeouts.Shortened && timeouts.NextMs == 3000, "An answer in a short session kept the short wait.");

            _ = SilentSessions(timeouts, 5);
            timeouts.Record(1000, false, TimeSpan.FromMilliseconds(999));
            Assert.True(!timeouts.Shortened && timeouts.NextMs == 3000, "A session that ended early counted as silent.");

            _ = SilentSessions(timeouts, 2);
            timeouts.Record(3000, false, TimeSpan.FromMilliseconds(50));
            Assert.Equal([3000, 3000, 3000, 1000], SilentSessions(timeouts, 4));

            // The full-length try during the short waits finds a modem that answers slowly.
            timeouts = new AtSessionTimeout();
            _ = SilentSessions(timeouts, 12);
            Assert.Equal(3000, timeouts.NextMs);
            timeouts.Record(3000, true, TimeSpan.FromMilliseconds(1500));
            Assert.Equal(3000, timeouts.NextMs);
        }

        // ReadStatus passes the shortened timeout to the AT channel and measures each session; startup and
        // callers without the policy keep the IAtSender default. A session that throws leaves the policy as it
        // was and still reaches AtError. The snapshot of a silent session is the same at either timeout.
        [Fact]
        public void SamplesShortenTheWaitOfASilentChannel()
        {
            StuckModem modem = new();
            MonitorSession session = new()
            {
                Modem = modem,
                Traffic = new FakeTraffic(),
                Summary = new ModemSummary { Model = "Test", At = At.Intel },
                AtTimeout = new AtSessionTimeout(normalMs: 40, shortMs: 15),
            };
            LteSnapshot full = session.TakeSnapshot();
            _ = session.TakeSnapshot();
            modem.Throws = true;
            LteSnapshot failed = session.TakeSnapshot();
            Assert.Equal("Intel AT Tunnel device service not available", failed.AtError);
            modem.Throws = false;
            _ = session.TakeSnapshot();
            LteSnapshot shortened = session.TakeSnapshot();
            Assert.Equal([40, 40, 40, 40, 15], modem.Timeouts);
            Assert.True(full.AtError is null && full.TempC is null && full.Neighbors is null && full.PrimaryCell!.CellId == 100, "A silent AT channel changed the sample.");
            shortened.Timestamp = full.Timestamp;
            Assert.Equal(JsonSerializer.Serialize(full), JsonSerializer.Serialize(shortened));

            modem.Silent = false;
            _ = session.TakeSnapshot();
            _ = session.TakeSnapshot();
            Assert.Equal([15, 40], modem.Timeouts[^2..]);

            _ = At.Intel.ReadStatus(modem);
            Assert.Equal(3000, modem.Timeouts[^1]);
            ModemAt startup = ModemAt.Initialize(modem, "");
            Assert.True(startup.Profile!.Id == "Intel" && modem.Timeouts[^1] == 1500, "Startup detection used the per-sample timeout.");
        }

        // The serial read waits for the time left until the deadline in one call: never 0 (no wait) or
        // -1 (forever), rounded up to whole milliseconds.
        [Fact]
        public void SerialReadTimeoutCoversTheTimeLeft()
        {
            Assert.Equal(3000, SerialAt.ReadTimeoutMs(TimeSpan.FromMilliseconds(3000)));
            Assert.Equal(2000, SerialAt.ReadTimeoutMs(TimeSpan.FromMilliseconds(1999.2)));
            Assert.Equal(1, SerialAt.ReadTimeoutMs(TimeSpan.FromTicks(1)));
            Assert.Equal(1, SerialAt.ReadTimeoutMs(TimeSpan.Zero));
            Assert.Equal(1, SerialAt.ReadTimeoutMs(TimeSpan.FromMilliseconds(-5)));
            Assert.Equal(int.MaxValue, SerialAt.ReadTimeoutMs(TimeSpan.MaxValue));
        }

        // The network GetNetwork reads is taken once by the GetCellsInfo call after it, and nothing carries
        // over to a later call (which then reads the network again).
        [Fact]
        public void NetworkHandoffIsTakenOnce()
        {
            Handoff<string> network = new();
            Assert.Null(network.Take());
            network.Leave("old");
            network.Leave("current");
            Assert.Equal("current", network.Take());
            Assert.Null(network.Take());
            network.Leave("failed read");
            network.Leave(null);
            Assert.Null(network.Take());
        }
    }
}
