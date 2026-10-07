using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace WorkThrough;

/// <summary>Subscribe using OpenTelemetry AddSource/AddMeter("WorkThrough"); job IDs are trace attributes, never metric labels.</summary>
public static class WorkThroughTelemetry
{
    public const string Name = "WorkThrough";
    public static ActivitySource Activities { get; } = new(Name);
    public static Meter Meter { get; } = new(Name);
    public static Counter<long> Attempts { get; } = Meter.CreateCounter<long>("workthrough.attempts");
    public static Histogram<double> Duration { get; } = Meter.CreateHistogram<double>("workthrough.attempt.duration", "s");
}
