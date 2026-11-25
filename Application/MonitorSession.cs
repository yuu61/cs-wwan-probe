using WwanProbe.Domain;
using WwanProbe.Infrastructure;
using WwanProbe.Infrastructure.Gnss;
using WwanProbe.Infrastructure.Modem;

namespace WwanProbe.Application
{
    // Interval = seconds between samples (0 = back to back), Count = samples (0 = infinite),
    // CsvPath = CSV log ("" = none), AtPort = "COMx" or "" (MBIM AT services),
    // BatteryInterval = least seconds between samples while Windows saves power (0 = off),
    // MinSampleSpacing = least time from the start of one automatic sample to the start of the next.
    internal sealed class MonitorConfig
    {
        public int Interval { get; set; }
        public int BatteryInterval { get; set; }
        public int Count { get; set; }
        public string CsvPath { get; set; } = "";
        public string AtPort { get; set; } = "";
        // A sample normally takes about a second (the traffic counters measure over one); a sample that
        // fails at once (no network, airplane mode, a missing counter) would otherwise repeat back to back.
        public TimeSpan MinSampleSpacing { get; set; } = TimeSpan.FromSeconds(1);

        // The interval in force: with BatteryInterval set, at least that long while Windows saves power
        // (saving = the power state read when the last sample completed); otherwise Interval.
        public int EffectiveInterval(bool saving) => BatteryInterval > 0 && saving ? Math.Max(Interval, BatteryInterval) : Interval;

        // When the next automatic sample is due, on the caller's monotonic clock: the effective interval
        // after the last one completed, and not sooner than MinSampleSpacing after it started. A refresh (r),
        // resuming and the first sample do not wait for it.
        public TimeSpan NextSampleAt(TimeSpan started, TimeSpan completed, bool saving = false)
        {
            TimeSpan spaced = started + MinSampleSpacing;
            TimeSpan interval = completed + TimeSpan.FromSeconds(EffectiveInterval(saving));
            return spaced > interval ? spaced : interval;
        }

        // The same rule as a pause after a sample that started `elapsed` ago (never negative).
        public TimeSpan PauseAfter(TimeSpan elapsed, bool saving = false)
        {
            TimeSpan pause = NextSampleAt(TimeSpan.Zero, elapsed, saving) - elapsed;
            return pause > TimeSpan.Zero ? pause : TimeSpan.Zero;
        }
    }

    // Signal series, all the same length (NaN = missing sample):
    //   Rsrp (dBm), Rsrq (dB), Rssnr (dB), RxKB / TxKB (KB/s), TempC (C)
    internal sealed class SignalHistory
    {
        public static readonly IReadOnlyList<string> Names = ["Rsrp", "Rsrq", "Rssnr", "RxKB", "TxKB", "TempC"];

        private readonly Dictionary<string, List<double>> series = new(StringComparer.OrdinalIgnoreCase);

        public SignalHistory()
        {
            foreach (string name in Names) series[name] = [];
        }

        public List<double> this[string name] => series[name];

        public IEnumerable<List<double>> Series => Names.Select(n => series[n]);

        // Clear every series so they stay time-aligned.
        public void Clear()
        {
            foreach (List<double> s in series.Values) s.Clear();
        }
    }

    // 2G/3G findings seen so far, kept after they disappear so a brief downgrade is not missed on screen.
    internal sealed class DowngradeLog
    {
        public int AlertCount { get; set; }
        public int WarningCount { get; set; }
        public string? Last { get; set; }
        public DowngradeLevel? LastLevel { get; set; }
        public IReadOnlyList<string> LastReasons { get; set; } = [];

        public void Add(LteSnapshot snapshot)
        {
            DowngradeFinding? finding = snapshot.Downgrade;
            if (finding is null || finding.Level == DowngradeLevel.None) return;
            if (finding.Level == DowngradeLevel.Alert) AlertCount++;
            else WarningCount++;
            Last = snapshot.Timestamp;
            LastLevel = finding.Level;
            LastReasons = finding.Reasons;
        }
    }

    // Monitoring session use case (sample -> history -> CSV), UI independent.
    // Statistics (History, HandoverLog, DowngradeLog) can be cleared with ResetStatistics.
    internal sealed class MonitorSession
    {
        public IModemDevice? Modem { get; set; }
        public ITrafficCounter? Traffic { get; set; }
        public MonitorConfig Config { get; set; } = new MonitorConfig();
        public ModemSummary Summary { get; set; } = new ModemSummary();
        public LteSnapshot? Snapshot { get; set; }
        public GpsSource? GpsReceiver { get; set; }
        public NmeaReceiver? NmeaReceiver { get; set; }
        // Read once as each sample is committed, and only with Config.BatteryInterval (null = never read).
        public IPowerState? Power { get; set; }
        // Whether Windows was saving power when the last sample completed (always false without BatteryInterval).
        public bool PowerSaving { get; private set; }
        public SignalHistory History { get; } = new SignalHistory();
        public HandoverLog HandoverLog { get; set; } = new HandoverLog();
        public DowngradeLog DowngradeLog { get; set; } = new DowngradeLog();
        public int HistoryMax { get; set; } = 600;
        public int Iteration { get; set; }
        // The AT wait per sample, shortened while the AT channel stays silent (see AtSessionTimeout).
        public AtSessionTimeout AtTimeout { get; set; } = new AtSessionTimeout();

        public static MonitorSession Initialize(IModemDevice modem, ITrafficCounter traffic, MonitorConfig config, int historyMax = 600)
        {
            if (!string.IsNullOrEmpty(config.CsvPath)) SnapshotLog.Initialize(config.CsvPath);
            return new MonitorSession
            {
                Modem = modem,
                Traffic = traffic,
                Config = config,
                Summary = ModemSummary.Read(modem, config.AtPort),
                HistoryMax = historyMax,
            };
        }

        public bool IsComplete => Config.Count > 0 && Iteration >= Config.Count;

        // Clears the history charts, handover history and 2G/3G log. Iteration (progress toward
        // Config.Count), the latest snapshot and the CSV log are kept. The handover baseline (the
        // current cell) is kept too, so a cell change across the reset is still detected.
        public void ResetStatistics()
        {
            History.Clear();
            HandoverLog.Clear();
            DowngradeLog = new DowngradeLog();
        }

        // Reads one sample from the devices; safe to run off the UI thread. It touches no session state
        // except AtTimeout, which only measurements use (one runs at a time).
        public LteSnapshot TakeSnapshot()
        {
            IModemDevice modem = Modem ?? throw new InvalidOperationException("No modem.");
            if (Traffic is not null) return LteSnapshot.Take(modem, Summary.At, Traffic, GpsReceiver, NmeaReceiver, AtTimeout);
            // Without a counter of its own, each sample opens and closes one.
            using PdhTrafficCounter traffic = new();
            return LteSnapshot.Take(modem, Summary.At, traffic, GpsReceiver, NmeaReceiver, AtTimeout);
        }

        // Takes one sample and updates the session (history, CSV).
        public void Sample() => AddSnapshot(TakeSnapshot());

        // Commits a completed sample on the UI thread, so rendering never sees partial history.
        public void AddSnapshot(LteSnapshot snapshot)
        {
            Iteration++;
            Snapshot = snapshot;
            // The power state for the wait before the next sample and the title; not read without BatteryInterval.
            PowerSaving = Config.BatteryInterval > 0 && Power is { IsSaving: true };
            AddSignalHistory(snapshot);
            DowngradeLog.Add(snapshot);
            HandoverLog.Add(snapshot.PrimaryCell, snapshot.Timestamp, !string.IsNullOrEmpty(snapshot.Error));
            if (!string.IsNullOrEmpty(Config.CsvPath)) SnapshotLog.Append(Config.CsvPath, snapshot);
        }

        // History tracks the explicitly selected primary serving cell only, so carrier aggregation
        // SCells do not get interleaved into one series. All series are appended together
        // (NaN when a value is unavailable) so gaps stay visible and modem-wide measurements
        // survive a missing LTE cell. Oldest samples beyond HistoryMax are dropped.
        private void AddSignalHistory(LteSnapshot snapshot)
        {
            ServingCell? cell = snapshot.PrimaryCell;
            Dictionary<string, double?> sample = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Rsrp"] = cell?.RsrpDbm,
                ["Rsrq"] = cell?.RsrqDb,
                ["Rssnr"] = snapshot.Rssnr,
                ["RxKB"] = snapshot.RxKB,
                ["TxKB"] = snapshot.TxKB,
                ["TempC"] = snapshot.TempC,
            };
            foreach (string name in SignalHistory.Names)
            {
                List<double> h = History[name];
                h.Add(sample[name] ?? double.NaN);
                while (h.Count > HistoryMax) h.RemoveAt(0);
            }
        }
    }

    // One reusable background worker for blocking modem/counter reads.
    // Only completed snapshots cross back to the UI; session/history/CSV stay on its thread.
    internal sealed class MonitorSampler(Func<LteSnapshot> sample)
    {
        private readonly Func<LteSnapshot> sample = sample;

        public Task<LteSnapshot>? Pending { get; private set; }

        public bool IsCompleted => Pending is { IsCompleted: true };

        // Set once the running sample has finished; null when none runs. The UI waits on it with the console input.
        public WaitHandle? SampleDone => Pending is { } task ? ((IAsyncResult)task).AsyncWaitHandle : null;

        // Starts a read-only measurement. A sample still running at quit is abandoned.
        public void Start()
        {
            if (Pending is not null) throw new InvalidOperationException("A sample is already in progress.");
            Pending = Task.Run(sample);
        }

        // The completed snapshot; a worker failure is rethrown. The pending sample is released either way.
        public LteSnapshot Receive()
        {
            Task<LteSnapshot>? pending = Pending;
            if (pending is null || !pending.IsCompleted) throw new InvalidOperationException("The sample is not complete.");
            try { return pending.GetAwaiter().GetResult(); }
            finally
            {
                Pending = null;
                // Closes the event behind SampleDone now instead of at finalization (one per sample).
                pending.Dispose();
            }
        }
    }
}
