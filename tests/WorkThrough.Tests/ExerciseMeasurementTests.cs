using System.Text.Json;
using WorkThrough.Exercises;
using Xunit;

namespace WorkThrough.Tests;

public sealed class ExerciseMeasurementTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Invalid_latency_is_rejected_without_manufacturing_a_zero_sample(double invalid)
    {
        var histogram = new LatencyHistogram();
        histogram.Record(7);
        Assert.Throws<ArgumentOutOfRangeException>(() => histogram.Record(invalid));
        var report = JsonSerializer.SerializeToElement(histogram.Snapshot());
        Assert.Equal(1, report.GetProperty("count").GetInt64());
        Assert.Equal(7, report.GetProperty("meanMs").GetDouble());
        Assert.Equal(7, report.GetProperty("p95Ms").GetInt32());
    }

    [Theory]
    [InlineData(12_345)]
    [InlineData(3_000_000_000)]
    public void Overflow_remains_explicit_when_a_quantile_exceeds_the_fixed_bucket_range(double duration)
    {
        var histogram = new LatencyHistogram();
        histogram.Record(0.2);
        histogram.Record(duration);
        var report = JsonSerializer.SerializeToElement(histogram.Snapshot());
        Assert.Equal(2, report.GetProperty("count").GetInt64());
        Assert.Equal(1, report.GetProperty("overflowAbove10000Ms").GetInt64());
        Assert.Equal(10_000, report.GetProperty("p99Ms").GetInt32());
        Assert.Equal(duration, report.GetProperty("maximumMs").GetDouble());
        Assert.Equal((duration + 0.2) / 2, report.GetProperty("meanMs").GetDouble(), precision: 3);
    }
}
