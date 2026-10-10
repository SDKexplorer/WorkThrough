namespace WorkThrough;

/// <summary>Validates partition keys. A key groups admission; it is never an authorization or deduplication boundary.</summary>
public static class PartitionKeys
{
    public const int MaximumLength = 256;

    public static void Validate(string? partitionKey, string parameterName = "partitionKey")
    {
        if (partitionKey is not null && (partitionKey.Length is 0 or > MaximumLength || string.IsNullOrWhiteSpace(partitionKey)
            || char.IsWhiteSpace(partitionKey[0]) || char.IsWhiteSpace(partitionKey[^1])))
        {
            throw new ArgumentException($"Partition keys contain 1–{MaximumLength} characters without surrounding whitespace.", parameterName);
        }
    }
}

/// <summary>At most <paramref name="MaximumPerPartition"/> admitted attempts per partition key value.</summary>
/// <remarks>A null JobKey applies to all work; otherwise to the job type's admission group. Zero pauses partitioned work.</remarks>
public sealed record PartitionLimit(string? JobKey, int MaximumPerPartition)
{
    public void Validate()
    {
        if (MaximumPerPartition < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPerPartition), "A partition limit cannot be negative. Zero pauses partitioned work.");
        }

        if (JobKey is not null)
        {
            _ = new JobContractId(JobKey, 1);
        }
    }
}

/// <summary>At most <paramref name="Permits"/> attempt starts in any sliding <paramref name="Window"/>, measured by the database clock.</summary>
/// <remarks>A null JobKey applies to all work. PerPartition counts each partition key separately and does not limit unpartitioned work.
/// Archiving work while its window is open removes its starts from the count.</remarks>
public sealed record RateLimit(string? JobKey, int Permits, TimeSpan Window, bool PerPartition = false)
{
    public static TimeSpan MinimumWindow { get; } = TimeSpan.FromSeconds(1);
    public static TimeSpan MaximumWindow { get; } = TimeSpan.FromDays(1);

    public void Validate()
    {
        if (Permits < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Permits), "A rate limit permits at least one start per window.");
        }

        if (Window < MinimumWindow || Window > MaximumWindow || Window.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Window), "Rate windows are whole milliseconds from one second to one day.");
        }

        if (JobKey is not null)
        {
            _ = new JobContractId(JobKey, 1);
        }
    }
}

/// <summary>Every persisted admission policy at one observation.</summary>
public sealed record AdmissionPolicies(IReadOnlyList<ConcurrencyLimit> Limits, IReadOnlyList<PartitionLimit> PartitionLimits,
    IReadOnlyList<RateLimit> RateLimits);

/// <summary>Shared admission policies. Changes go through audited <see cref="IJobAdministration"/> commands.</summary>
public interface IAdmissionPolicies
{
    Task<AdmissionPolicies> AdmissionPoliciesAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates missing policies only; existing database policies survive restarts and deployments.</summary>
    Task SeedAdmissionPoliciesAsync(IReadOnlyList<PartitionLimit> partitionLimits, IReadOnlyList<RateLimit> rateLimits,
        CancellationToken cancellationToken = default);
}
