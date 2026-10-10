namespace WorkThrough.Exercises;

/// <summary>Fixed-memory one-millisecond buckets, with explicit overflow and exact maximum.</summary>
internal sealed class LatencyHistogram
{
    private readonly object gate = new();
    private readonly long[] buckets = new long[10001];
    private long count;
    private long overflow;
    private double total;
    private double maximum;

    internal void Record(double milliseconds)
    {
        lock (gate)
        {
            count++;
            total += milliseconds;
            maximum = Math.Max(maximum, milliseconds);
            if (milliseconds > 10000)
            {
                overflow++;
            }

            buckets[Math.Clamp((int)Math.Ceiling(milliseconds), 0, 10000)]++;
        }
    }

    internal object Snapshot()
    {
        lock (gate)
        {
            return new
            {
                count,
                meanMs = count == 0 ? (double?)null : total / count,
                p50Ms = Quantile(.50),
                p95Ms = Quantile(.95),
                p99Ms = Quantile(.99),
                maximumMs = maximum,
                overflowAbove10000Ms = overflow,
                bucketWidthMs = 1,
                buckets = buckets.Select((value, index) => new { upperBoundMs = index, count = value }).Where(bucket => bucket.count > 0).ToArray()
            };
        }
    }

    private int? Quantile(double fraction)
    {
        if (count == 0)
        {
            return null;
        }

        var target = (long)Math.Ceiling(count * fraction);
        long cumulative = 0;
        for (var index = 0; index < buckets.Length; index++)
        {
            cumulative += buckets[index];
            if (cumulative >= target)
            {
                return index;
            }
        }

        throw new InvalidOperationException("Histogram counters are inconsistent.");
    }
}
