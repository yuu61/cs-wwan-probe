namespace WwanProbe.Infrastructure.Modem
{
    // Wire-format signal values normalized to dBm / dB (pure, no I/O).
    public static class SignalConversion
    {
        // 3GPP TS 36.133 index -> dBm/dB conversion (AT+XMCI / +XCESQ / +GTCCINFO / +CESQ use this scale)
        public static int? RsrpIndexToDbm(long index)
        {
            if (index < 0 || index > 97) return null;
            return (int)(-141 + index);
        }

        public static double? RsrqIndexToDb(long index)
        {
            if (index < 0 || index > 34) return null;
            return -20 + 0.5 * index;
        }

        // WinRT MobileBroadbandCellLte RSRP / RSRQ -> dBm / dB, or null.
        // The MBIM spec (MBIM_LTE_SERVING_CELL_INFO / MBIM_LTE_MRL_INFO) defines RSRP -140..-44 dBm and
        // RSRQ -20..-3 dB, but the L860-GL reports 3GPP TS 36.133 indices (RSRP 0..97, RSRQ 0..34).
        // The ranges do not overlap, so both are accepted.
        public static int? FromWinRtRsrp(double? value)
        {
            if (value is not double v) return null;
            if (v >= -140 && v <= -44) return (int)Math.Round(v);
            if (v >= 0 && v <= 97 && v == Math.Floor(v)) return RsrpIndexToDbm((long)v);
            return null;
        }

        public static double? FromWinRtRsrq(double? value)
        {
            if (value is not double v) return null;
            if (v >= -20 && v <= -3) return v;
            if (v >= 0 && v <= 34 && v == Math.Floor(v)) return RsrqIndexToDb((long)v);
            return null;
        }
    }
}
