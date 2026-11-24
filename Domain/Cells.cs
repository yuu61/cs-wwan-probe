namespace WwanProbe.Domain
{
    // Normalized radio access technology identities, independent of display labels.
    internal static class Rat
    {
        public const string Gsm = "GSM";
        public const string Umts = "UMTS";
        public const string Cdma = "CDMA";
        public const string Lte = "LTE";
        public const string Nr = "NR";

        public static bool IsLegacy(string? rat) => rat is not null && (Is(rat, Gsm) || Is(rat, Umts) || Is(rat, Cdma));

        public static bool IsModern(string? rat) => rat is not null && (Is(rat, Lte) || Is(rat, Nr));

        public static bool Is(string? rat, string name) => string.Equals(rat, name, StringComparison.OrdinalIgnoreCase);
    }

    internal enum CellRole
    {
        Serving,
        Neighbor,
    }

    // Common AT cell shape shared by every vendor's parser:
    //   Rat = GSM/UMTS/LTE/NR, Role = Serving/Neighbor, Channel = ARFCN/UARFCN/EARFCN/NR-ARFCN (null = unknown),
    //   LTE cells also: Earfcn, Pci, RsrpDbm, RsrqDb (null = not measured) and optionally Tac, CellId.
    // DlBandwidthMHz / Rssnr are set by the parsers whose cell lists carry them (serving LTE cells).
    internal sealed record AtCell
    {
        public required string Rat { get; init; }
        public required CellRole Role { get; init; }
        public long? Channel { get; init; }
        public long? Earfcn { get; init; }
        public long? Pci { get; init; }
        public long? Tac { get; init; }
        public long? CellId { get; init; }
        public int? RsrpDbm { get; init; }
        public double? RsrqDb { get; init; }
        public int? RsrpIdx { get; init; }
        public int? RsrqIdx { get; init; }
        public double? DlBandwidthMHz { get; init; }
        public double? Rssnr { get; init; }
    }

    // LTE serving cell in device order. Raw values are the WinRT values before normalization
    // (dBm / dB per the MBIM spec, or 3GPP indices on the L860-GL).
    internal sealed record ServingCell
    {
        public int? RsrpDbm { get; init; }
        public double? RsrpRaw { get; init; }
        public double? RsrqDb { get; init; }
        public double? RsrqRaw { get; init; }
        public string Band { get; init; } = "B?";
        public long? Earfcn { get; init; }
        public long? Pci { get; init; }
        public long? CellId { get; init; }
        public long? Tac { get; init; }
        public int? Ta { get; init; }
        public string? Provider { get; init; }
        public string? Quality { get; init; }
    }

    // LTE neighbor cell with a measurement (AT list or WinRT fallback).
    internal sealed record LteNeighbor(int RsrpDbm, double? RsrqDb, string Band, long Earfcn, long? Pci);

    internal sealed record UmtsCell(long? CellId, long? Uarfcn, double? RscpDbm);

    // Carrier aggregation: Cells = 0 (not on LTE), 1 (primary only), 2.. (secondary cells added);
    // one bandwidth per carrier, null when unknown.
    internal sealed record CarrierAggregation(int Cells, IReadOnlyList<double?> BandwidthsMHz);

    // RAT / band configuration read once at startup:
    //   AllowedRats (GSM/UMTS/LTE/NR), Allowed (e.g. "3G+4G"), Preferred (null = unknown),
    //   GsmBands (MHz), UmtsBands, LteBands, NrBands.
    internal sealed record RatConfig
    {
        public IReadOnlyList<string> AllowedRats { get; init; } = [];
        public string Allowed { get; init; } = "";
        public string? Preferred { get; init; }
        public IReadOnlyList<int> GsmBands { get; init; } = [];
        public IReadOnlyList<int> UmtsBands { get; init; } = [];
        public IReadOnlyList<int> LteBands { get; init; } = [];
        public IReadOnlyList<int> NrBands { get; init; } = [];
    }
}
