namespace WwanProbe.Domain
{
    // Signal classification and statistics (pure, no I/O).
    internal static class SignalQuality
    {
        public static string? FromRsrp(double? dbm)
        {
            return dbm switch
            {
                null => null,
                >= -80 => "Excellent",
                >= -90 => "Good",
                >= -100 => "Fair",
                >= -110 => "Poor",
                _ => "Very Poor",
            };
        }
    }

    internal sealed record SignalStatistic(double Min, double Max, double Avg, int Count)
    {
        // Min/Max/Avg of a signal series; NaN (missing sample) is ignored. Null without valid samples.
        public static SignalStatistic? Compute(IEnumerable<double> values)
        {
            double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0;
            int count = 0;
            foreach (double v in values)
            {
                if (double.IsNaN(v)) continue;
                min = Math.Min(min, v);
                max = Math.Max(max, v);
                sum += v;
                count++;
            }
            return count == 0 ? null : new SignalStatistic(min, max, Math.Round(sum / count, 1), count);
        }
    }
}
