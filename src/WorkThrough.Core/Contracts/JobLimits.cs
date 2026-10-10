using System.Text;
using System.Text.Json;

namespace WorkThrough;

/// <summary>Bounds shared by producers, workers and storage providers.</summary>
public static class JobLimits
{
    public const int MaximumInlineBytes = 1_048_576;
    public const int MaximumMessageLength = 2000;
    public static TimeSpan MinimumDelay { get; } = TimeSpan.FromMilliseconds(1);
    public static TimeSpan MaximumDelay { get; } = TimeSpan.FromDays(365);

    public static bool ExceedsInlineSize(JsonElement value) => Encoding.UTF8.GetByteCount(value.GetRawText()) > MaximumInlineBytes;

    public static bool IsSupportedDelay(TimeSpan delay) => delay >= MinimumDelay && delay <= MaximumDelay;
}

/// <summary>Defaults applied when producers, schedules or flow steps omit a value.</summary>
public static class JobDefaults
{
    public const string Queue = "default";
    public const int MaxAttempts = 3;
}
