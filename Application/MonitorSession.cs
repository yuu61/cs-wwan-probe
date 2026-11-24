using WwanProbe.Domain;
using WwanProbe.Infrastructure;
using WwanProbe.Infrastructure.Gnss;
using WwanProbe.Infrastructure.Modem;

namespace WwanProbe.Application
{
    // Interval = seconds between samples (0 = back to back), Count = samples (0 = infinite),
    // CsvPath = CSV log ("" = none), AtPort = "COMx" or "" (MBIM AT services).
    internal sealed class MonitorConfig
    {
        public int Interval { get; set; }
        public int Count { get; set; }
        public string CsvPath { get; set; } = "";
        public string AtPort { get; set; } = "";
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
        public SignalHistory History { get; } = new SignalHistory();
        public HandoverLog HandoverLog { get; set; } = new HandoverLog();
        public DowngradeLog DowngradeLog { get; set; } = new DowngradeLog();
        public int HistoryMax { get; set; } = 600;
        public int Iteration { get; set; }

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

        // Reads one sample from the devices; safe to run off the UI thread (it does not touch the session state).
        public LteSnapshot TakeSnapshot()
        {
            IModemDevice modem = Modem ?? throw new InvalidOperationException("No modem.");
            return LteSnapshot.Take(modem, Summary.At, Traffic ?? new PdhTrafficCounter(), GpsReceiver, NmeaReceiver);
        }

        // Takes one sample and updates the session (history, CSV).
        public void Sample() => AddSnapshot(TakeSnapshot());

        // Commits a completed sample on the UI thread, so rendering never sees partial history.
        public void AddSnapshot(LteSnapshot snapshot)
        {
            Iteration++;
            Snapshot = snapshot;
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
            finally { Pending = null; }
        }
    }
}
