using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Networking.NetworkOperators;
using Windows.Storage.Streams;
using WwanProbe.Domain;

namespace WwanProbe.Infrastructure.Modem
{
    internal sealed record ModemDeviceInfo(string Model, string Manufacturer, string Firmware, string Imei,
        string SimIccId, string SimSpn, string RadioState, string DataClass);

    internal sealed record ModemNetworkInfo(string ProviderName, string ProviderId, string DataClass, string Apn);

    // One WinRT MobileBroadbandCellLte as raw values: RSRP / RSRQ are dBm / dB per the MBIM spec,
    // or 3GPP indices on the L860-GL (normalized by SignalConversion).
    internal sealed record LteCellInfo(long? CellId, string? ProviderId, double? Rsrp, double? Rsrq,
        long? Channel, long? Pci, long? Tac, int? Ta);

    internal sealed record CellsInfo
    {
        public IReadOnlyList<LteCellInfo> ServingLte { get; init; } = [];
        public IReadOnlyList<LteCellInfo> NeighborLte { get; init; } = [];
        public IReadOnlyList<UmtsCell> ServingUmts { get; init; } = [];
        // WinRT GSM / UMTS / TD-SCDMA / CDMA serving cells.
        public int LegacyServingCount { get; init; }
    }

    // The modem as the monitor sees it (WinRtModem on hardware, fakes in tests).
    internal interface IModemDevice : IAtSender
    {
        ModemDeviceInfo GetDeviceInfo();
        ModemNetworkInfo GetNetwork();
        CellsInfo GetCellsInfo();
    }

    // Mobile broadband modem access via WinRT.
    internal sealed class WinRtModem : IModemDevice
    {
        private readonly MobileBroadbandModem modem;

        private WinRtModem(MobileBroadbandModem modem) => this.modem = modem;

        public static WinRtModem? GetDefault()
        {
            MobileBroadbandModem? modem = MobileBroadbandModem.GetDefault();
            return modem is null ? null : new WinRtModem(modem);
        }

        public ModemDeviceInfo GetDeviceInfo()
        {
            MobileBroadbandDeviceInformation d = modem.DeviceInformation;
            return new ModemDeviceInfo(d.Model ?? "", d.Manufacturer ?? "", d.FirmwareInformation ?? "", d.MobileEquipmentId ?? "",
                d.SimIccId ?? "", d.SimSpn ?? "", d.CurrentRadioState.ToString(), d.DataClasses.ToString());
        }

        public ModemNetworkInfo GetNetwork()
        {
            MobileBroadbandNetwork? n = modem.CurrentNetwork;
            return n is null
                ? new ModemNetworkInfo("", "", "", "")
                : new ModemNetworkInfo(n.RegisteredProviderName ?? "", n.RegisteredProviderId ?? "",
                n.RegisteredDataClass.ToString(), n.AccessPointName ?? "");
        }

        // On the L860-GL a cells query normally completes in 10-60 ms, but a few are never answered (with
        // AT running: 6 of ~400 samples in one run, 1 of 400 in another; the one observed failed after about
        // 60 s) while a new query succeeds at once (measured 2026-09-24, docs/neighbor-cells.md). So a query
        // still pending after 2 s is sent once more and the first answer of the two wins; a slow first answer
        // is still taken within 10 s, but that modem then gets a second query (untested on other modems).
        public CellsInfo GetCellsInfo()
        {
            MobileBroadbandNetwork network = modem.CurrentNetwork ?? throw new InvalidOperationException("The modem reports no current network.");
            MobileBroadbandCellsInfo info = TaskWait.FirstResult(() => network.GetCellsInfoAsync().AsTask(), 10000, 2000);
            static LteCellInfo Lte(MobileBroadbandCellLte c)
            {
                return new LteCellInfo(c.CellId, c.ProviderId,
                    c.ReferenceSignalReceivedPowerInDBm, c.ReferenceSignalReceivedQualityInDBm,
                    c.ChannelNumber, c.PhysicalCellId, c.TrackingAreaCode, c.TimingAdvanceInBitPeriods);
            }
            int legacy = (info.ServingCellsGsm?.Count ?? 0) + (info.ServingCellsUmts?.Count ?? 0) +
                (info.ServingCellsTdscdma?.Count ?? 0) + (info.ServingCellsCdma?.Count ?? 0);
            return new CellsInfo
            {
                ServingLte = [.. (info.ServingCellsLte ?? []).Where(c => c is not null).Select(Lte)],
                NeighborLte = [.. (info.NeighboringCellsLte ?? []).Where(c => c is not null).Select(Lte)],
                ServingUmts = [.. (info.ServingCellsUmts ?? []).Where(c => c is not null).Select(c => new UmtsCell(c.CellId, c.ChannelNumber, c.ReceivedSignalCodePowerInDBm))],
                LegacyServingCount = legacy,
            };
        }

        public IReadOnlyDictionary<string, string?> SendAt(AtChannel channel, IReadOnlyList<string> commands, int timeoutMs = 3000)
        {
            if (channel.Port is not null) return SerialAt.Send(channel.Port, commands, timeoutMs);

            MobileBroadbandDeviceService service = modem.GetDeviceService(channel.ServiceId)
                ?? throw new InvalidOperationException($"{channel.Name} device service not available");
            Dictionary<string, string?> responses = new(StringComparer.OrdinalIgnoreCase);
            foreach (string cmd in commands) responses[cmd] = null;
            MobileBroadbandDeviceServiceCommandSession session = service.OpenCommandSession();
            try
            {
                foreach (string cmd in commands)
                {
                    IBuffer request = MbimAtFraming.ToRequest(channel.Framing, cmd).AsBuffer();
                    MobileBroadbandDeviceServiceCommandResult result;
                    try
                    {
                        result = TaskWait.Await(channel.IsQuery
                            ? session.SendQueryCommandAsync(channel.Cid, request)
                            : session.SendSetCommandAsync(channel.Cid, request), timeoutMs);
                    }
                    catch (Exception)
                    {
                        break;
                    }
                    string? text = null;
                    if (result.StatusCode == 0)
                    {
                        IBuffer? buffer = result.ResponseData;
                        text = MbimAtFraming.FromResponse(channel.Framing, buffer is null ? [] : buffer.ToArray());
                    }
                    responses[cmd] = text;
                }
            }
            finally
            {
                session.CloseSession();
            }
            return responses;
        }
    }
}
