namespace WwanProbe.Domain
{
    // LTE cell identity, observation continuity and bounded transition history.
    internal sealed record HandoverCell(string Provider, long CellId, string Band, long? Earfcn, long? Pci, long? Tac, int? RsrpDbm);

    internal sealed record HandoverEntry(int Number, string Timestamp, HandoverCell From, HandoverCell To);

    // Bounded session-local cell-change history. Count includes entries already evicted.
    internal sealed class HandoverLog
    {
        public HandoverLog(int maxEntries = 100)
        {
            if (maxEntries is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maxEntries));
            MaxEntries = maxEntries;
        }

        public List<HandoverEntry> Entries { get; } = [];
        public HandoverCell? Previous { get; set; }
        public int Count { get; set; }
        public int MaxEntries { get; }

        // Compare PLMN + Cell ID of the unfiltered first LTE serving cell, matching the
        // primary-cell convention used by this monitor. Never promote an SCell because
        // the primary has no signal measurement. Missing/failed observations break the
        // baseline: a later reconnection cannot establish a directly observed handover.
        public void Add(ServingCell? cell, string timestamp, bool observationFailed = false)
        {
            if (observationFailed || cell is null || string.IsNullOrWhiteSpace(cell.Provider) ||
                cell.CellId is not long cellId || cellId < 0 || cellId > 0x0FFFFFFF)
            {
                Previous = null;
                return;
            }
            // Copy values so future snapshots cannot mutate previously recorded events.
            HandoverCell current = new(cell.Provider, cellId, cell.Band, cell.Earfcn, cell.Pci, cell.Tac, cell.RsrpDbm);
            HandoverCell? previous = Previous;
            if (previous is not null &&
                (!string.Equals(previous.Provider, current.Provider, StringComparison.OrdinalIgnoreCase) || previous.CellId != current.CellId))
            {
                Count++;
                Entries.Add(new HandoverEntry(Count, timestamp, previous, current));
                while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
            }
            Previous = current;
        }

        // Clears the history but keeps the baseline, so a change across the reset is still detected.
        public void Clear()
        {
            Entries.Clear();
            Count = 0;
        }
    }
}
