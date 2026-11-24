using System.Globalization;
using WwanProbe.Domain;

namespace WwanProbe.Presentation
{
    // Handover history section (compact on the main screen, paged in the [h] view).
    internal static class HandoverSection
    {
        public static string FormatCell(HandoverCell cell)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} PCI:{1} PLMN:{2} EARFCN:{3} TAC:{4} RSRP:{5}",
                cell.Band, cell.Pci, cell.Provider, cell.Earfcn, cell.Tac, MonitorFrame.Optional(cell.RsrpDbm, "{0}dBm"));
        }

        // Compact recent changes on the main screen; a separate view keeps the retained
        // history accessible even when charts or a small console would clip the body.
        public static void Add(List<FrameLine> lines, HandoverLog? log, MonitorView view, int width)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            int count = log?.Count ?? 0;
            lines.Add(new FrameLine(MonitorFrame.SectionRule(string.Format(inv, "Handover history ({0}) [h]", count), width), ConsoleColor.DarkCyan));
            if (log is null || log.Entries.Count == 0)
            {
                lines.Add(new FrameLine(" (no LTE cell changes observed since start or reset)", ConsoleColor.DarkGray));
                return;
            }
            List<HandoverEntry> entries = log.Entries;
            if (view.HandoverVisible)
            {
                int height = view.LastHeight > 0 ? view.LastHeight : 30;
                // Title, rule, optional downgrade warning, section title, range and footer.
                int pageSize = Math.Max(1, (int)Math.Floor((height - 6) / 2.0));
                int offset = Math.Clamp(view.HandoverOffset, 0, Math.Max(0, entries.Count - pageSize));
                view.HandoverOffset = offset;
                int end = Math.Min(entries.Count, offset + pageSize);
                lines.Add(new FrameLine(string.Format(inv, " Newest first: {0}-{1} / {2} retained (limit {3})", offset + 1, end, entries.Count, log.MaxEntries),
                    ConsoleColor.DarkGray));
                for (int i = offset; i < end; i++)
                {
                    HandoverEntry entry = entries[entries.Count - 1 - i];
                    lines.Add(new FrameLine(string.Format(inv, " #{0} {1} Switched to CellID:{2}", entry.Number, entry.Timestamp, entry.To.CellId), ConsoleColor.Yellow));
                    lines.Add(new FrameLine("   " + FormatCell(entry.To)));
                }
            }
            else
            {
                for (int i = entries.Count - 1; i >= Math.Max(0, entries.Count - 3); i--)
                {
                    HandoverEntry entry = entries[i];
                    lines.Add(new FrameLine(string.Format(inv, " {0} Switched to CellID:{1} {2} PCI:{3}",
                        entry.Timestamp, entry.To.CellId, entry.To.Band, entry.To.Pci), ConsoleColor.Yellow));
                }
            }
        }
    }
}
