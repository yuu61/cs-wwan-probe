namespace WwanProbe.Domain
{
    // Signal classification and statistics (pure, no I/O).
    public static class SignalQuality
    {
        public static string? FromRsrp(double? dbm)
        {
            if (dbm is not double v) return null;
            if (v >= -80) return "Excellent";
            if (v >= -90) return "Good";
            if (v >= -100) return "Fair";
            if (v >= -110) return "Poor";
            return "Very Poor";
        }
    }

    public sealed record SignalStatistic(double Min, double Max, double Avg, int Count)
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
            if (count == 0) return null;
            return new SignalStatistic(min, max, Math.Round(sum / count, 1), count);
        }
    }
}
