namespace WwanProbe.Infrastructure.Modem
{
    // Wire-format signal values normalized to dBm / dB (pure, no I/O).
    internal static class SignalConversion
    {
        // 3GPP TS 36.133 index -> dBm/dB conversion (AT+XMCI / +XCESQ / +GTCCINFO / +CESQ use this scale)
        public static int? RsrpIndexToDbm(long index) => index is < 0 or > 97 ? null : (int)(-141 + index);

        public static double? RsrqIndexToDb(long index) => index is < 0 or > 34 ? null : -20 + (0.5 * index);

        // WinRT MobileBroadbandCellLte RSRP / RSRQ -> dBm / dB, or null.
        // The MBIM spec (MBIM_LTE_SERVING_CELL_INFO / MBIM_LTE_MRL_INFO) defines RSRP -140..-44 dBm and
        // RSRQ -20..-3 dB, but the L860-GL reports 3GPP TS 36.133 indices (RSRP 0..97, RSRQ 0..34).
        // The ranges do not overlap, so both are accepted.
        public static int? FromWinRtRsrp(double? value)
        {
            return value switch
            {
                double v when v is >= -140 and <= -44 => (int)Math.Round(v),
                double v when v is >= 0 and <= 97 && v == Math.Floor(v) => RsrpIndexToDbm((long)v),
                _ => null,
            };
        }

        public static double? FromWinRtRsrq(double? value)
        {
            return value switch
            {
                double v when v is >= -20 and <= -3 => v,
                double v when v is >= 0 and <= 34 && v == Math.Floor(v) => RsrqIndexToDb((long)v),
                _ => null,
            };
        }
    }
}
