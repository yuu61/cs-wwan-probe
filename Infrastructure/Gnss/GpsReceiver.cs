using Windows.Devices.Geolocation;

namespace WwanProbe.Infrastructure.Gnss
{
    // Plain copy of a WinRT Geocoordinate (taken in the position callback), so normalization and
    // tests do not depend on WinRT objects.
    internal sealed record GpsCoordinate
    {
        public string PositionSource { get; init; } = "";
        public bool IsRemoteSource { get; init; }
        public DateTimeOffset? Timestamp { get; init; }
        public DateTimeOffset? PositionSourceTimestamp { get; init; }
        public double? Latitude { get; init; }
        public double? Longitude { get; init; }
        public double? Altitude { get; init; }
        public string AltitudeReferenceSystem { get; init; } = "Unspecified";
        public double? Accuracy { get; init; }
        public double? AltitudeAccuracy { get; init; }
        public double? Speed { get; init; }
        public double? Heading { get; init; }
        public double? Hdop { get; init; }
        public double? Pdop { get; init; }
        public double? Vdop { get; init; }

        public static GpsCoordinate From(Geocoordinate c)
        {
            GeocoordinateSatelliteData? satellites = c.SatelliteData;
            BasicGeoposition position = c.Point.Position;
            return new GpsCoordinate
            {
                PositionSource = c.PositionSource.ToString(),
                IsRemoteSource = IsRemote(c),
                Timestamp = c.Timestamp,
                PositionSourceTimestamp = c.PositionSourceTimestamp,
                Latitude = position.Latitude,
                Longitude = position.Longitude,
                Altitude = position.Altitude,
                AltitudeReferenceSystem = c.Point.AltitudeReferenceSystem.ToString(),
                Accuracy = c.Accuracy,
                AltitudeAccuracy = c.AltitudeAccuracy,
                Speed = c.Speed,
                Heading = c.Heading,
                Hdop = satellites?.HorizontalDilutionOfPrecision,
                Pdop = satellites?.PositionDilutionOfPrecision,
                Vdop = satellites?.VerticalDilutionOfPrecision,
            };
        }

        // Geocoordinate.IsRemoteSource is missing on older Windows builds.
        private static bool IsRemote(Geocoordinate c)
        {
            try { return c.IsRemoteSource; }
            catch (Exception) { return false; }
        }
    }

    internal sealed record GpsReading(string Status, GpsCoordinate? Coordinate, string? Error);

    // WinRT callbacks run on their own threads. Keep only the latest report, including during
    // pause, so GPS acquisition never blocks LTE sampling or the UI.
    internal sealed class GpsReceiver : IDisposable
    {
        private readonly Lock gate = new();
        private readonly Geolocator locator;
        private GpsCoordinate? coordinate;
        private string status = "Initializing";
        private string? error;
        private bool disposed;

        public GpsReceiver()
        {
            locator = new Geolocator { DesiredAccuracy = PositionAccuracy.High, ReportInterval = 1000 };
            try
            {
                locator.StatusChanged += OnStatusChanged;
                locator.PositionChanged += OnPositionChanged;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void OnPositionChanged(Geolocator sender, PositionChangedEventArgs args)
        {
            lock (gate)
            {
                if (disposed) return;
                try
                {
                    coordinate = GpsCoordinate.From(args.Position.Coordinate);
                    status = "Ready";
                    error = null;
                }
                catch (Exception ex)
                {
                    coordinate = null;
                    error = ex.Message;
                }
            }
        }

        private void OnStatusChanged(Geolocator sender, StatusChangedEventArgs args)
        {
            lock (gate)
            {
                if (disposed) return;
                status = args.Status.ToString();
                if (args.Status != PositionStatus.Ready) coordinate = null;
            }
        }

        public GpsReading Read()
        {
            lock (gate)
            {
                return new GpsReading(status, coordinate, error);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                coordinate = null;
                status = "NotInitialized";
            }
            // Do not hold the lock while WinRT drains callbacks during unsubscription.
            try { locator.PositionChanged -= OnPositionChanged; }
            finally { locator.StatusChanged -= OnStatusChanged; }
        }
    }
}
