using System.Diagnostics;
using System.Globalization;

namespace WwanProbe.Infrastructure.Gnss
{
    // Elevated NMEA helper started by NmeaReceiver.Start (this executable with --nmea-helper); not for direct use.
    // Keeps a Windows location session (Geolocator) so the GNSS engine runs, enables the driver's
    // NMEA logging on the first GNSS device and publishes the satellites and fix state (never the
    // coordinates) to <StateDir>\state.json about once a second. Stops when <StateDir>\stop appears or the monitor
    // process ends, then restores NMEA logging to disabled and removes <StateDir>.
    // On a failure it keeps the state file with Status = Error for the monitor to show.
    public static class NmeaHelper
    {
        // Arguments: <StateDir> <ParentId> <ParentStartTicks>.
        public static int Run(IReadOnlyList<string> args)
        {
            if (args.Count != 3 ||
                !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parentId) ||
                !long.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parentStartTicks))
            {
                Console.Error.WriteLine($"Usage: {NmeaReceiver.HelperArgument} <StateDir> <ParentId> <ParentStartTicks>");
                return 2;
            }
            return Run(args[0], parentId, parentStartTicks);
        }

        private static int Run(string stateDir, int parentId, long parentStartTicks)
        {
            string statePath = Path.Combine(stateDir, "state.json");
            string stopPath = Path.Combine(stateDir, "stop");
            NmeaHelperState state = new NmeaHelperState();
            Process? parent = null;
            Mutex? nmeaLock = null;
            GnssDevice? device = null;
            GpsSource? gps = null;
            bool loggingEnabled = false;
            bool finished = false;
            try
            {
                state.UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                NmeaStateFile.Write(statePath, state);
                // Holding the process object also guards against the parent ID being reused.
                parent = Process.GetProcessById(parentId);
                if (parent.StartTime.ToUniversalTime().Ticks != parentStartTicks) throw new InvalidOperationException("The monitor process has already exited.");
                nmeaLock = GnssInterface.EnterNmeaLock();
                IReadOnlyList<string> interfaces = GnssInterface.Find();
                if (interfaces.Count == 0) throw new InvalidOperationException("No GNSS device interface found.");
                device = new GnssDevice(interfaces[0]);
                // Windows owns the fix session and assistance data; the native handle only listens.
                gps = GpsSource.Start();
                if (gps.Error is not null) throw new InvalidOperationException(gps.Error);
                loggingEnabled = true;
                device.SetNmeaLogging(true);
                state.Status = "Receiving";
                NmeaSatelliteState satellites = new NmeaSatelliteState();
                string buffer = "";
                Stopwatch published = Stopwatch.StartNew();
                bool dirty = true;
                while (!finished)
                {
                    // A short listen timeout keeps stop requests and a closed monitor noticed within ~1 s.
                    string? chunk = device.ReadNmea(1000);
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    if (chunk is not null)
                    {
                        NmeaSplit split = Nmea.Split(buffer + chunk);
                        buffer = split.Rest;
                        foreach (string sentence in split.Sentences) satellites.Add(sentence, now);
                        if (split.Sentences.Count > 0) state.NmeaUnixMs = now.ToUnixTimeMilliseconds();
                    }
                    if (dirty || published.ElapsedMilliseconds >= 1000)
                    {
                        NmeaReport report = satellites.Report(now);
                        state.Satellites = report.Satellites.ToList();
                        state.UsedCount = report.UsedCount;
                        state.Fix = report.Fix;
                        state.UpdatedUnixMs = now.ToUnixTimeMilliseconds();
                        // A monitor reading the file at this moment only delays the update.
                        dirty = !NmeaStateFile.Write(statePath, state);
                        published.Restart();
                    }
                    finished = File.Exists(stopPath) || parent.HasExited;
                }
            }
            catch (Exception ex)
            {
                state.Status = "Error";
                state.Error = GnssInterface.BaseMessage(ex);
            }
            finally
            {
                try
                {
                    if (loggingEnabled) device?.SetNmeaLogging(false);
                }
                catch (Exception ex)
                {
                    state.Status = "Error";
                    state.Error = string.Join("; ", new[] { state.Error, $"NMEA logging not restored: {GnssInterface.BaseMessage(ex)}" }
                        .Where(e => !string.IsNullOrEmpty(e)));
                }
                finally
                {
                    device?.Dispose();
                    gps?.Stop();
                    nmeaLock?.Dispose();
                    parent?.Dispose();
                    if (finished && state.Status != "Error")
                    {
                        try { Directory.Delete(stateDir, true); }
                        catch (Exception) { }
                    }
                    else
                    {
                        state.UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        NmeaStateFile.Write(statePath, state);
                    }
                }
            }
            return state.Status == "Error" ? 1 : 0;
        }
    }
}
