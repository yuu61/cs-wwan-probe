using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
    // localized Windows. One call blocks ~1 s: rate counters need two collections, and each read
    // collects twice one second apart, so a rate covers that second only.
    // The query and its counters are opened by the first read and kept for the next ones. A read that
    // fails (a collection, or a counter without a value) closes them and the next read opens them again,
    // so an adapter that disappears and comes back is found by name again. Reads run one at a time (one
    // sampler); Dispose may run during one (quit abandons a running sample) and closes the query once
    // that read is done. Nothing is opened again after Dispose.
    internal sealed partial class PdhTrafficCounter(string instanceName = "Generic Mobile Broadband Adapter") : ITrafficCounter, IDisposable
    {
        private const uint PdhFmtDouble = 0x00000200;
        private const uint PdhFmtNoCap100 = 0x00008000;

        private readonly string[] paths =
        [
            $@"\Network Interface({instanceName})\Current Bandwidth",
            $@"\Network Interface({instanceName})\Bytes Received/sec",
            $@"\Network Interface({instanceName})\Bytes Sent/sec",
        ];
        // Guards open / disposed only; never held during a PDH call.
        private readonly Lock gate = new();
        private OpenQuery? open;
        private bool disposed;

        // Queries opened and kept for reads (tests: a failed read closes its query, so the next one opens
        // another).
        internal int Opens { get; private set; }

        [StructLayout(LayoutKind.Explicit)]
        private struct PdhFmtCounterValue
        {
            [FieldOffset(0)] public uint CStatus;
            [FieldOffset(8)] public double DoubleValue;
        }

        // A PDH query handle; closing it also frees every counter added to it.
        private sealed class PdhQueryHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public PdhQueryHandle() : base(true) { }

            protected override bool ReleaseHandle() => PdhCloseQuery(handle) == 0;
        }

        // An open query and the handles of its counters (in the order of paths), which the query owns.
        private sealed class OpenQuery
        {
            public required PdhQueryHandle Query { get; init; }
            public required IntPtr[] Counters { get; init; }
        }

        [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial uint PdhOpenQuery(string? dataSource, IntPtr userData, out PdhQueryHandle query);
        [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial uint PdhAddEnglishCounter(PdhQueryHandle query, string path, IntPtr userData, out IntPtr counter);
        [LibraryImport("pdh.dll")]
        private static partial uint PdhCollectQueryData(PdhQueryHandle query);
        [LibraryImport("pdh.dll")]
        private static partial uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PdhFmtCounterValue value);
        [LibraryImport("pdh.dll")]
        private static partial uint PdhCloseQuery(IntPtr query);

        public TrafficReading Read()
        {
            try
            {
                TrafficReading reading = TrafficReading.FromSamples(Collect());
                if (reading.Error is not null) Close();
                return reading;
            }
            catch (Exception ex)
            {
                Close();
                return new TrafficReading(null, null, null, ex.Message);
            }
        }

        public void Dispose()
        {
            lock (gate) disposed = true;
            Close();
        }

        // The reason text for a PDH status (the Error of a failed reading).
        public static string Describe(uint status)
        {
            return status switch
            {
                0x800007D1 => "The specified instance is not present.",
                // PDH_NO_DATA: the first collection of a counter whose instance name matches no adapter.
                0x800007D5 => "The counter returned no data; the adapter instance may not exist.",
                0xC0000BB8 => "The specified object is not found on the system.",
                0xC0000BB9 => "The specified counter could not be found.",
                0xC0000BC6 => "The data is not valid.",
                _ => $"PDH error 0x{status:X8}.",
            };
        }

        private List<CounterSample> Collect()
        {
            OpenQuery q = Acquire();
            bool added = false;
            try
            {
                // Keeps the query (and so its counter handles) alive until this read is done, even when
                // Dispose closes it meanwhile; a query already closed throws ObjectDisposedException here.
                q.Query.DangerousAddRef(ref added);
                Check(PdhCollectQueryData(q.Query), "collect performance counters");
                Thread.Sleep(1000);
                Check(PdhCollectQueryData(q.Query), "collect performance counters");
                List<CounterSample> samples = [];
                for (int i = 0; i < paths.Length; i++)
                {
                    uint status = PdhGetFormattedCounterValue(q.Counters[i], PdhFmtDouble | PdhFmtNoCap100, IntPtr.Zero, out PdhFmtCounterValue value);
                    samples.Add(new CounterSample(paths[i], value.DoubleValue, status != 0 ? status : value.CStatus));
                }
                return samples;
            }
            finally
            {
                if (added) q.Query.DangerousRelease();
            }
        }

        // The open query, opened on first use (outside the lock: adding the first counter of a process
        // takes a few hundred ms, and Dispose must not wait for it).
        private OpenQuery Acquire()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (open is not null) return open;
            }
            OpenQuery opened = Open();
            lock (gate)
            {
                if (!disposed)
                {
                    open = opened;
                    Opens++;
                    return opened;
                }
            }
            opened.Query.Dispose();
            throw new ObjectDisposedException(nameof(PdhTrafficCounter));
        }

        private OpenQuery Open()
        {
            uint status = PdhOpenQuery(null, IntPtr.Zero, out PdhQueryHandle query);
            try
            {
                Check(status, "open a performance counter query");
                IntPtr[] counters = new IntPtr[paths.Length];
                for (int i = 0; i < paths.Length; i++) Check(PdhAddEnglishCounter(query, paths[i], IntPtr.Zero, out counters[i]), paths[i]);
                return new OpenQuery { Query = query, Counters = counters };
            }
            catch
            {
                query.Dispose();
                throw;
            }
        }

        private void Close()
        {
            OpenQuery? closing;
            lock (gate)
            {
                closing = open;
                open = null;
            }
            closing?.Query.Dispose();
        }

        private static void Check(uint status, string what)
        {
            if (status == 0) return;
            throw new InvalidOperationException($"{Describe(status)} ({what})");
        }
    }
}
