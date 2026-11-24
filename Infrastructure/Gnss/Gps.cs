using System.Globalization;

namespace WwanProbe.Infrastructure.Gnss
{
    // Normalized GPS state for one sample. Status: Fix | NoFix | Stale | Disabled | Unavailable.
    // Position values are set only for a fresh local satellite fix; missing values stay null.
    public sealed record GpsObservation
    {
        public string Status { get; init; } = "NoFix";
        public string Source { get; init; } = "";
        public string? Timestamp { get; init; }
        public double? Latitude { get; init; }
        public double? Longitude { get; init; }
        public double? AltitudeM { get; init; }
        public double? AccuracyM { get; init; }
        public double? SpeedMps { get; init; }
        public double? HeadingDeg { get; init; }
        public double? Hdop { get; init; }
        public double? Pdop { get; init; }
        public double? Vdop { get; init; }
        public string? Error { get; init; }

        // Pure normalization. Accept only local satellite fixes: Windows may also return
        // Wi-Fi/cellular/IP positions, which must not be presented as GPS. No stale coordinates are
        // retained after a source change, loss of permission, or a report older than 15 s.
        public static GpsObservation FromReading(GpsReading reading, DateTimeOffset? now = null)
        {
            DateTimeOffset at = now ?? DateTimeOffset.UtcNow;
            GpsObservation gps = new GpsObservation();
            if (!string.IsNullOrEmpty(reading.Error)) return gps with { Status = "Unavailable", Error = reading.Error };
            switch (reading.Status?.ToUpperInvariant())
            {
                case "DISABLED": return gps with { Status = "Disabled", Error = "Enable Windows location services and desktop app location access" };
                case "NOTAVAILABLE": return gps with { Status = "Unavailable", Error = "No Windows location provider available" };
                case "NODATA":
                case "INITIALIZING":
                case "NOTINITIALIZED":
                    return gps;
            }
            GpsCoordinate? c = reading.Coordinate;
            if (c is null) return gps;
            gps = gps with { Source = c.PositionSource };
            if (c.IsRemoteSource) return gps with { Source = "Remote" };
            if (!string.Equals(c.PositionSource, "Satellite", StringComparison.OrdinalIgnoreCase)) return gps;
            DateTimeOffset? timestamp = c.PositionSourceTimestamp ?? c.Timestamp;
            if (timestamp is not DateTimeOffset ts) return gps;
            double age = (at - ts).TotalSeconds;
            if (age > 15 || age < -5) return gps with { Status = "Stale" };
            double? latitude = Number(c.Latitude, -90, 90);
            double? longitude = Number(c.Longitude, -180, 180);
            if (latitude is null || longitude is null) return gps;
            // Unknown altitude reference or missing vertical accuracy must not turn into 0 m.
            bool altitudeKnown = !string.IsNullOrEmpty(c.AltitudeReferenceSystem) &&
                !string.Equals(c.AltitudeReferenceSystem, "Unspecified", StringComparison.OrdinalIgnoreCase) &&
                Number(c.AltitudeAccuracy, 0) is not null;
            return gps with
            {
                Status = "Fix",
                Timestamp = ts.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                Latitude = latitude,
                Longitude = longitude,
                AltitudeM = altitudeKnown ? Number(c.Altitude) : null,
                AccuracyM = Number(c.Accuracy, 0),
                SpeedMps = Number(c.Speed, 0),
                HeadingDeg = Number(c.Heading, 0, 360),
                Hdop = Number(c.Hdop, 0),
                Pdop = Number(c.Pdop, 0),
                Vdop = Number(c.Vdop, 0),
            };
        }

        private static double? Number(double? value, double min = double.NegativeInfinity, double max = double.PositiveInfinity)
        {
            if (value is not double v || !double.IsFinite(v) || v < min || v > max) return null;
            return v;
        }
    }

    // Windows GNSS reports (-Gps). Geolocator cannot select a particular modem or identify the
    // physical GPS receiver. A startup failure is kept in Error and reported by every sample.
    public sealed class GpsSource
    {
        public GpsSource(GpsReceiver? client, string? error)
        {
            Client = client;
            Error = error;
        }

        public GpsReceiver? Client { get; }
        public string? Error { get; }

        public static GpsSource Start()
        {
            try { return new GpsSource(new GpsReceiver(), null); }
            catch (Exception ex) { return new GpsSource(null, ex.Message); }
        }

        public void Stop() => Client?.Dispose();

        public GpsObservation Observe()
        {
            try
            {
                GpsReading reading = Error is not null || Client is null
                    ? new GpsReading("", null, Error ?? "GPS receiver not started")
                    : Client.Read();
                return GpsObservation.FromReading(reading);
            }
            catch (Exception ex)
            {
                return GpsObservation.FromReading(new GpsReading("", null, ex.Message));
            }
        }
    }
}
