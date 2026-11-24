using System.Runtime.InteropServices;

namespace WwanProbe.Infrastructure
{
    // Network interface traffic. Missing values stay null (a failed counter is not 0) and Error tells why.
    internal sealed record TrafficReading(double? BwMbps, double? RxKB, double? TxKB, string? Error)
    {
        // PDH_CSTATUS_VALID_DATA (0) and PDH_CSTATUS_NEW_DATA (1) are both usable.
        public static TrafficReading FromSamples(IEnumerable<CounterSample> samples)
        {
            double? bw = null, rx = null, tx = null;
            foreach (CounterSample s in samples)
            {
                if ((s.Status != 0 && s.Status != 1) || double.IsNaN(s.CookedValue) || double.IsInfinity(s.CookedValue)) continue;
                if (s.Path.EndsWith(@"\current bandwidth", StringComparison.OrdinalIgnoreCase)) bw = Math.Round(s.CookedValue / 1e6, 1);
                else if (s.Path.EndsWith(@"\bytes received/sec", StringComparison.OrdinalIgnoreCase)) rx = Math.Round(s.CookedValue / 1024, 1);
                else if (s.Path.EndsWith(@"\bytes sent/sec", StringComparison.OrdinalIgnoreCase)) tx = Math.Round(s.CookedValue / 1024, 1);
            }
            string? error = bw is null || rx is null || tx is null ? "One or more traffic counters are unavailable" : null;
            return new TrafficReading(bw, rx, tx, error);
        }
    }

    internal sealed record CounterSample(string Path, double CookedValue, uint Status);

    internal interface ITrafficCounter
    {
        TrafficReading Read();
    }

    // Performance counters of the mobile broadband adapter through PDH. English counter names work on
    // localized Windows. One call blocks ~1 s: rate counters need two collections.
    internal sealed partial class PdhTrafficCounter(string instanceName = "Generic Mobile Broadband Adapter") : ITrafficCounter
    {
        private const uint PdhFmtDouble = 0x00000200;
        private const uint PdhFmtNoCap100 = 0x00008000;

        private readonly string instanceName = instanceName;

        [StructLayout(LayoutKind.Explicit)]
        private struct PdhFmtCounterValue
        {
            [FieldOffset(0)] public uint CStatus;
            [FieldOffset(8)] public double DoubleValue;
        }

        [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);
        [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
        [LibraryImport("pdh.dll")]
        private static partial uint PdhCollectQueryData(IntPtr query);
        [LibraryImport("pdh.dll")]
        private static partial uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PdhFmtCounterValue value);
        [LibraryImport("pdh.dll")]
        private static partial uint PdhCloseQuery(IntPtr query);

        public TrafficReading Read()
        {
            try
            {
                return TrafficReading.FromSamples(Collect());
            }
            catch (Exception ex)
            {
                return new TrafficReading(null, null, null, ex.Message);
            }
        }

        private List<CounterSample> Collect()
        {
            string[] paths =
            [
                $@"\Network Interface({instanceName})\Current Bandwidth",
                $@"\Network Interface({instanceName})\Bytes Received/sec",
                $@"\Network Interface({instanceName})\Bytes Sent/sec",
            ];
            Check(PdhOpenQuery(null, IntPtr.Zero, out IntPtr query), "open a performance counter query");
            try
            {
                IntPtr[] counters = new IntPtr[paths.Length];
                for (int i = 0; i < paths.Length; i++) Check(PdhAddEnglishCounter(query, paths[i], IntPtr.Zero, out counters[i]), paths[i]);
                Check(PdhCollectQueryData(query), "collect performance counters");
                Thread.Sleep(1000);
                Check(PdhCollectQueryData(query), "collect performance counters");
                List<CounterSample> samples = [];
                for (int i = 0; i < paths.Length; i++)
                {
                    uint status = PdhGetFormattedCounterValue(counters[i], PdhFmtDouble | PdhFmtNoCap100, IntPtr.Zero, out PdhFmtCounterValue value);
                    samples.Add(new CounterSample(paths[i], value.DoubleValue, status != 0 ? status : value.CStatus));
                }
                return samples;
            }
            finally
            {
                _ = PdhCloseQuery(query);
            }
        }

        private static void Check(uint status, string what)
        {
            if (status == 0) return;
            string reason = status switch
            {
                0x800007D1 => "The specified instance is not present.",
                0xC0000BB8 => "The specified object is not found on the system.",
                0xC0000BB9 => "The specified counter could not be found.",
                0xC0000BC6 => "The data is not valid.",
                _ => $"PDH error 0x{status:X8}.",
            };
            throw new InvalidOperationException($"{reason} ({what})");
        }
    }
}
