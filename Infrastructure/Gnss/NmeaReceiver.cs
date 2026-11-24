using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace WwanProbe.Infrastructure.Gnss
{
    // State file written by the helper (JSON, no coordinates):
    //   Status = Starting | Receiving | Error; Error; UpdatedUnixMs (heartbeat);
    //   NmeaUnixMs (last valid sentence, null = none yet); Satellites; UsedCount; Fix (see Nmea.cs).
    internal sealed class NmeaHelperState
    {
        public string Status { get; set; } = "Starting";
        public string? Error { get; set; }
        public long UpdatedUnixMs { get; set; }
        public long? NmeaUnixMs { get; set; }
        public List<NmeaSatellite> Satellites { get; set; } = [];
        public int UsedCount { get; set; }
        public NmeaFix? Fix { get; set; }
    }

    internal static class NmeaStateFile
    {
        // Writes the whole state to a temporary file and replaces the previous one, so a reader
        // never sees a partial file. The replace fails while the monitor is reading the file (even
        // with FileShare.Delete); then it returns false and the caller retries on its next cycle.
        public static bool Write(string path, NmeaHelperState state)
        {
            string temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(state));
                File.Move(temporary, path, true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // Latest state, or null when none has been published or it cannot be read yet.
        public static NmeaHelperState? Read(string path)
        {
            try { return JsonSerializer.Deserialize<NmeaHelperState>(File.ReadAllText(path)); }
            catch (Exception) { return null; }
        }
    }

    internal sealed record NmeaSystemCount(string System, int InView, int Used);

    // Helper state converted for display:
    //   Status: Starting | Receiving | Stale (heartbeat older than 10 s) | Unavailable (Error)
    //   Fix = the receiver's fix state, null unless Receiving;
    //   InView = distinct satellites, Used = distinct satellites in the fix,
    //   Systems = per-constellation counts in display order.
    internal sealed record NmeaObservation
    {
        public string Status { get; init; } = "Starting";
        public string? Error { get; init; }
        public string? UpdatedUtc { get; init; }
        public bool NmeaReceived { get; init; }
        public IReadOnlyList<NmeaSatellite> Satellites { get; init; } = [];
        public int InView { get; init; }
        public int Used { get; init; }
        public IReadOnlyList<NmeaSystemCount> Systems { get; init; } = [];
        public NmeaFix? Fix { get; init; }

        public static NmeaObservation FromState(NmeaHelperState? state, string? receiverError = null, bool helperExited = false,
            int? exitCode = null, DateTimeOffset? now = null)
        {
            NmeaObservation observation = new();
            if (!string.IsNullOrEmpty(receiverError)) return observation with { Status = "Unavailable", Error = receiverError };
            if (state is not null && state.Status == "Error") return observation with { Status = "Unavailable", Error = state.Error };
            if (helperExited)
            {
                string code = exitCode is int c ? c.ToString(CultureInfo.InvariantCulture) : "?";
                return observation with { Status = "Unavailable", Error = $"NMEA helper exited (code {code}) without a report" };
            }
            if (state is null) return observation;
            DateTimeOffset updated = DateTimeOffset.FromUnixTimeMilliseconds(state.UpdatedUnixMs);
            observation = observation with { UpdatedUtc = updated.ToString("o", CultureInfo.InvariantCulture) };
            if (((now ?? DateTimeOffset.UtcNow) - updated).TotalSeconds > 10) return observation with { Status = "Stale" };
            if (state.Status != "Receiving") return observation;
            List<NmeaSatellite> satellites = state.Satellites ?? [];
            List<NmeaSystemCount> systems = [];
            foreach (string system in Nmea.Systems)
            {
                List<NmeaSatellite> members = [.. satellites.Where(s => s.System == system)];
                if (members.Count == 0) continue;
                systems.Add(new NmeaSystemCount(system, members.Select(s => s.Id).Distinct().Count(),
                    members.Where(s => s.Used).Select(s => s.Id).Distinct().Count()));
            }
            return observation with
            {
                Status = "Receiving",
                NmeaReceived = state.NmeaUnixMs is not null,
                Satellites = satellites,
                Used = state.UsedCount,
                Fix = state.Fix,
                InView = satellites.Select(s => $"{s.System}:{s.Id}").Distinct().Count(),
                Systems = systems,
            };
        }
    }

    // Satellites from the GNSS driver's NMEA stream (-Nmea). The driver requires an administrator,
    // so a hidden helper process (this executable with --nmea-helper, NmeaHelper) runs elevated
    // (UAC when the monitor is not) and publishes a state file about once a second.
    // A non-elevated monitor cannot terminate the helper: it asks the helper to stop through a
    // stop file, and the helper also exits when the monitor process ends.
    internal sealed class NmeaReceiver(Process? process, string? stateDir, string? error)
    {
        public const string HelperArgument = "--nmea-helper";

        public Process? Process { get; } = process;
        public string? StateDir { get; } = stateDir;
        public string? Error { get; } = error;

        public static bool IsAdministrator()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        // Blocks until UAC is answered; a declined prompt leaves the monitor running with Error set.
        public static NmeaReceiver Start()
        {
            string? stateDir = null;
            try
            {
                stateDir = Path.Combine(Path.GetTempPath(), "wwan-nmea-" + Guid.NewGuid().ToString("N"));
                _ = Directory.CreateDirectory(stateDir);
                using Process self = Process.GetCurrentProcess();
                (string file, List<string> arguments) = SelfCommand();
                arguments.AddRange(
                [
                    HelperArgument, stateDir, self.Id.ToString(CultureInfo.InvariantCulture),
                    self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                ]);
                ProcessStartInfo start = new(file)
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    Arguments = JoinArguments(arguments),
                };
                if (!IsAdministrator()) start.Verb = "runas";
                Process process = Process.Start(start) ?? throw new InvalidOperationException("no process was started");
                return new NmeaReceiver(process, stateDir, null);
            }
            catch (Exception ex)
            {
                return new NmeaReceiver(null, stateDir, $"NMEA helper not started: {ex.Message}");
            }
        }

        // Asks the helper to stop (it restores the driver's NMEA logging) and waits at most
        // timeoutMs: the driver decides how long a pending NMEA read takes to cancel.
        public void Stop(int timeoutMs = 5000)
        {
            if (StateDir is null) return;
            bool exited = true;
            if (Process is not null)
            {
                try { File.Create(Path.Combine(StateDir, "stop")).Dispose(); }
                catch (Exception) { }
                // If the handle of an elevated helper cannot be queried, it still sees the stop file.
                try { exited = Process.HasExited || Process.WaitForExit(timeoutMs); }
                catch (Exception) { exited = false; }
            }
            // A helper still running removes the directory itself once it sees this process exit.
            if (exited)
            {
                try { Directory.Delete(StateDir, true); }
                catch (Exception) { }
            }
        }

        // Satellite failures must never fail the LTE sample that carries them.
        public NmeaObservation Observe()
        {
            if (!string.IsNullOrEmpty(Error)) return NmeaObservation.FromState(null, Error);
            try
            {
                if (Process is null || StateDir is null) throw new InvalidOperationException("no helper process");
                NmeaHelperState? state = NmeaStateFile.Read(Path.Combine(StateDir, "state.json"));
                bool exited = Process.HasExited;
                int? exitCode = null;
                if (exited)
                {
                    try { exitCode = Process.ExitCode; }
                    catch (Exception) { exitCode = null; }
                }
                return NmeaObservation.FromState(state, null, exited, exitCode);
            }
            catch (Exception ex)
            {
                return NmeaObservation.FromState(null, $"NMEA helper status unavailable: {ex.Message}");
            }
        }

        // This executable (or "dotnet <dll>" when started through the muxer).
        private static (string File, List<string> Arguments) SelfCommand()
        {
            string path = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unknown.");
            if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                string dll = Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("The entry assembly is unknown.");
                return (path, new List<string> { dll });
            }
            return (path, new List<string>());
        }

        // Windows command-line quoting (CommandLineToArgvW rules).
        private static string JoinArguments(IEnumerable<string> arguments)
        {
            StringBuilder line = new();
            foreach (string argument in arguments)
            {
                if (line.Length > 0) _ = line.Append(' ');
                if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
                {
                    _ = line.Append(argument);
                    continue;
                }
                _ = line.Append('"');
                int backslashes = 0;
                foreach (char c in argument)
                {
                    if (c == '\\')
                    {
                        backslashes++;
                        continue;
                    }
                    _ = line.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
                    backslashes = 0;
                    _ = line.Append(c);
                }
                _ = line.Append('\\', backslashes * 2);
                _ = line.Append('"');
            }
            return line.ToString();
        }
    }

    // GNSS interface lookup and the single-listener lock (a named mutex any NMEA tool should take).
    internal static class GnssInterface
    {
        public const string ClassGuid = "{3336e5e4-018a-4669-84c5-bd05f3bd368b}";

        // GNSS device interface IDs (paths).
        public static IReadOnlyList<string> Find()
        {
            Windows.Devices.Enumeration.DeviceInformationCollection devices = TaskWait.Await(
                Windows.Devices.Enumeration.DeviceInformation.FindAllAsync($"System.Devices.InterfaceClassGuid:=\"{ClassGuid}\""));
            return [.. devices.Select(d => d.Id)];
        }

        // Only one NMEA listener may run: each one resets the driver logging to disabled on exit,
        // which would silently stop another's stream. Returns the lock to Dispose, or throws.
        // Other tools that switch NMEA logging should take the same named mutex.
        public static Mutex EnterNmeaLock()
        {
            Mutex mutex = new(true, @"Global\WwanProbeGnssNmea", out bool createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                throw new InvalidOperationException("Another process is already listening to the GNSS NMEA stream.");
            }
            return mutex;
        }

        internal static string BaseMessage(Exception ex) => ex.GetBaseException().Message;
    }
}
