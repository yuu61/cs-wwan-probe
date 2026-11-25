using WwanProbe.Domain;
using WwanProbe.Infrastructure;
using WwanProbe.Infrastructure.Gnss;
using WwanProbe.Infrastructure.Modem;

namespace WwanProbe.Application
{
    // Device header shown on every frame; read once at startup.
    internal sealed record ModemSummary
    {
        public string Model { get; init; } = "";
        public string Manufacturer { get; init; } = "";
        public string Firmware { get; init; } = "";
        public string Imei { get; init; } = "";
        public string SimIccId { get; init; } = "";
        public string SimSpn { get; init; } = "";
        public string RadioState { get; init; } = "";
        public string DataClass { get; init; } = "";
        public ModemAt? At { get; init; }
        public RatConfig? RatConfig { get; init; }  // null = unavailable
        public bool LegacyAllowed { get; init; }

        public static ModemSummary Read(IModemDevice modem, string? atPort)
        {
            ModemDeviceInfo d = modem.GetDeviceInfo();
            ModemAt at = ModemAt.Initialize(modem, atPort);
            // The RAT / band configuration does not change while running, so query it once.
            RatConfig? rat = at.ReadConfig(modem);
            return new ModemSummary
            {
                Model = d.Model,
                Manufacturer = d.Manufacturer,
                Firmware = d.Firmware,
                Imei = d.Imei,
                SimIccId = d.SimIccId,
                SimSpn = d.SimSpn,
                RadioState = d.RadioState,
                DataClass = d.DataClass,
                At = at,
                RatConfig = rat,
                LegacyAllowed = Downgrade.IsLegacyRatAllowed(rat?.AllowedRats),
            };
        }
    }

    // One sample with monitoring rules applied.
    // Serving preserves every LTE serving cell in device order, even without signal values.
    // PrimaryCell is selected once; SecondaryCells contains the remaining carriers.
    // All consumers use these roles instead of selecting by measurement availability.
    // Missing values stay null; Error, AtError and TrafficError identify failed sources.
    // Gps / Satellites are null unless -Gps / -Nmea started their receivers.
    internal sealed class LteSnapshot
    {
        public string Timestamp { get; set; } = "";
        public string ProviderName { get; set; } = "";
        public string ProviderId { get; set; } = "";
        public string DataClass { get; set; } = "";
        public string Apn { get; set; } = "";
        public double? BwMbps { get; set; }
        public double? RxKB { get; set; }
        public double? TxKB { get; set; }
        public IReadOnlyList<ServingCell> Serving { get; set; } = [];
        public ServingCell? PrimaryCell { get; set; }
        public IReadOnlyList<ServingCell> SecondaryCells { get; set; } = [];
        public IReadOnlyList<UmtsCell> Umts { get; set; } = [];
        public IReadOnlyList<LteNeighbor>? Neighbors { get; set; }
        public double? TempC { get; set; }
        public double? Rssnr { get; set; }
        public CarrierAggregation? Ca { get; set; }
        public string? Error { get; set; }
        public string? AtError { get; set; }
        public string? TrafficError { get; set; }
        public GpsObservation? Gps { get; set; }
        public NmeaObservation? Satellites { get; set; }
        public DowngradeFinding? Downgrade { get; set; }

        public static LteSnapshot Take(IModemDevice modem, ModemAt? at, ITrafficCounter traffic, GpsSource? gps, NmeaReceiver? nmea,
            AtSessionTimeout? atTimeout = null)
        {
            ModemObservation o = ModemObservation.Observe(modem, at, traffic, atTimeout);
            List<ServingCell> serving = [.. o.Serving.Select(c => c with { Quality = SignalQuality.FromRsrp(c.RsrpDbm) })];
            LteSnapshot snapshot = new()
            {
                Timestamp = o.Timestamp,
                ProviderName = o.ProviderName,
                ProviderId = o.ProviderId,
                DataClass = o.DataClass,
                Apn = o.Apn,
                BwMbps = o.BwMbps,
                RxKB = o.RxKB,
                TxKB = o.TxKB,
                Serving = serving,
                PrimaryCell = serving.FirstOrDefault(),
                SecondaryCells = [.. serving.Skip(1)],
                Umts = o.Umts,
                Neighbors = o.Neighbors,
                TempC = o.TempC,
                Rssnr = o.Rssnr,
                Ca = o.Ca,
                Error = o.Error,
                AtError = o.AtError,
                TrafficError = o.TrafficError,
                Gps = gps?.Observe(),
                Satellites = nmea?.Observe(),
            };
            if (string.IsNullOrEmpty(o.Error))
            {
                snapshot.Downgrade = Domain.Downgrade.Evaluate(o.RegisteredRats, o.LegacyServingCount, o.AtCells, o.AtSource);
            }
            return snapshot;
        }
    }
}
