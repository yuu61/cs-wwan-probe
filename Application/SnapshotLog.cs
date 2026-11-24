using System.Globalization;
using WwanProbe.Domain;
using WwanProbe.Infrastructure;

namespace WwanProbe.Application
{
    // Snapshot -> CSV logging (column order is part of the output contract).
    // One row per sample, including samples without an LTE serving cell (cell columns empty),
    // so gaps stay visible. Cell columns are PrimaryCell; CA SecondaryCells
    // cells are '+'-joined lists in the SCell_* columns. Unavailable values are empty.
    public static class SnapshotLog
    {
        public static readonly IReadOnlyList<string> Columns = new[]
        {
            "Timestamp",
            "RSRP_dBm", "RSRQ_dB", "RSSNR_dB",
            "Band", "EARFCN", "PCI", "CellID", "TAC", "TA",
            "Provider", "DataClass",
            "CA_Cells", "CA_BW_MHz",
            "SCell_Band", "SCell_EARFCN", "SCell_PCI", "SCell_RSRP_dBm", "SCell_RSRQ_dB",
            "RX_KBps", "TX_KBps", "BW_Mbps",
            "Temp_C",
            "Downgrade", "Downgrade_Reason",
            "Error",
            "GPS_Status", "GPS_Source", "GPS_Timestamp_UTC", "GPS_Latitude", "GPS_Longitude",
            "GPS_Altitude_m", "GPS_Accuracy_m", "GPS_Speed_mps", "GPS_Heading_deg", "GPS_HDOP", "GPS_Error",
        };

        public static void Initialize(string path) => CsvFile.Initialize(path, Columns);

        public static void Append(string path, LteSnapshot snapshot)
        {
            Dictionary<string, object?> row = ToRow(snapshot);
            CsvFile.AppendRow(path, Columns.Select(c => row.TryGetValue(c, out object? v) ? v : null));
        }

        // Snapshot -> column -> value for Columns (missing key = empty field).
        public static Dictionary<string, object?> ToRow(LteSnapshot s)
        {
            static string? Join<T>(IEnumerable<T>? items)
            {
                List<T> list = items?.ToList() ?? new List<T>();
                if (list.Count == 0) return null;
                return string.Join('+', list.Select(i => Convert.ToString(i, CultureInfo.InvariantCulture) ?? ""));
            }

            ServingCell? p = s.PrimaryCell;
            IReadOnlyList<ServingCell> sc = s.SecondaryCells;
            List<string> errors = new List<string>();
            if (!string.IsNullOrEmpty(s.Error)) errors.Add($"WinRT: {s.Error}");
            if (!string.IsNullOrEmpty(s.AtError)) errors.Add($"AT: {s.AtError}");
            if (!string.IsNullOrEmpty(s.TrafficError)) errors.Add($"Traffic: {s.TrafficError}");

            Dictionary<string, object?> row = new Dictionary<string, object?>
            {
                ["Timestamp"] = s.Timestamp,
                ["RSSNR_dB"] = s.Rssnr,
                ["Provider"] = s.ProviderId,
                ["DataClass"] = s.DataClass,
                ["CA_Cells"] = s.Ca?.Cells,
                ["CA_BW_MHz"] = Join(s.Ca?.BandwidthsMHz),
                ["SCell_Band"] = Join(sc.Select(c => c.Band)),
                ["SCell_EARFCN"] = Join(sc.Select(c => c.Earfcn)),
                ["SCell_PCI"] = Join(sc.Select(c => c.Pci)),
                ["SCell_RSRP_dBm"] = Join(sc.Select(c => c.RsrpDbm)),
                ["SCell_RSRQ_dB"] = Join(sc.Select(c => c.RsrqDb)),
                ["RX_KBps"] = s.RxKB,
                ["TX_KBps"] = s.TxKB,
                ["BW_Mbps"] = s.BwMbps,
                ["Temp_C"] = s.TempC,
                ["Downgrade"] = s.Downgrade?.Level.ToString(),
                ["Downgrade_Reason"] = string.Join("; ", s.Downgrade?.Reasons ?? Array.Empty<string>()),
                ["Error"] = string.Join("; ", errors),
                ["GPS_Status"] = s.Gps?.Status,
                ["GPS_Source"] = s.Gps?.Source,
                ["GPS_Timestamp_UTC"] = s.Gps?.Timestamp,
                ["GPS_Latitude"] = s.Gps?.Latitude,
                ["GPS_Longitude"] = s.Gps?.Longitude,
                ["GPS_Altitude_m"] = s.Gps?.AltitudeM,
                ["GPS_Accuracy_m"] = s.Gps?.AccuracyM,
                ["GPS_Speed_mps"] = s.Gps?.SpeedMps,
                ["GPS_Heading_deg"] = s.Gps?.HeadingDeg,
                ["GPS_HDOP"] = s.Gps?.Hdop,
                ["GPS_Error"] = s.Gps?.Error,
            };
            if (p is not null)
            {
                row["RSRP_dBm"] = p.RsrpDbm;
                row["RSRQ_dB"] = p.RsrqDb;
                row["Band"] = p.Band;
                row["EARFCN"] = p.Earfcn;
                row["PCI"] = p.Pci;
                row["CellID"] = p.CellId;
                row["TAC"] = p.Tac;
                row["TA"] = p.Ta;
            }
            return row;
        }
    }
}
