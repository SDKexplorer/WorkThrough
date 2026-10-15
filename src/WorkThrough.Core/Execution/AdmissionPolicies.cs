using System.Text.Json.Serialization;

namespace WorkThrough;

/// <summary>The identity an admission key groups. Keys group admission; they are never authorization or deduplication boundaries.</summary>
/// <remarks>A partition key names a business resource to serialize or bound (an account, a document). A tenant ID names the
/// customer that owns the work, so one tenant's volume cannot take every slot. They are independent: a tenant has many partitions.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<KeyScope>))]
public enum KeyScope
{
    Partition = 1,
    Tenant = 2
}

/// <summary>Validates partition keys and tenant IDs.</summary>
public static class AdmissionKeys
{
    public const int MaximumLength = 256;

    public static void Validate(string? key, KeyScope scope, string parameterName)
    {
        if (key is not null && (key.Length is 0 or > MaximumLength || string.IsNullOrWhiteSpace(key)
            || char.IsWhiteSpace(key[0]) || char.IsWhiteSpace(key[^1])))
        {
            throw new ArgumentException($"{Label(scope)} values contain 1–{MaximumLength} characters without surrounding whitespace.", parameterName);
        }
    }

    private static string Label(KeyScope scope) => scope == KeyScope.Tenant ? "Tenant ID" : "Partition key";

    public static void ValidateScope(KeyScope scope, string parameterName)
    {
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Keyed policies apply per partition key or per tenant ID.");
        }
    }

    public static void ValidateJobKey(string? jobKey)
    {
        if (jobKey is not null)
        {
            _ = new JobContractId(jobKey, 1);
        }
    }
}

/// <summary>At most <paramref name="MaximumPerKey"/> admitted attempts per distinct partition key or tenant ID.</summary>
/// <remarks>A null JobKey applies to all work; otherwise to the job type's admission group. Work without a key of this scope is
/// not limited. Zero pauses keyed work.</remarks>
public sealed record KeyedLimit(KeyScope Scope, string? JobKey, int MaximumPerKey)
{
    public void Validate()
    {
        AdmissionKeys.ValidateScope(Scope, nameof(Scope));
        if (MaximumPerKey < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPerKey), "A keyed limit cannot be negative. Zero pauses keyed work.");
        }

        AdmissionKeys.ValidateJobKey(JobKey);
    }
}

/// <summary>At most <paramref name="Permits"/> attempt starts in any sliding <paramref name="Window"/>, measured by the database clock.</summary>
/// <remarks>A null JobKey applies to all work. PerKey counts each partition key or tenant ID separately and does not limit work
/// without that key. A limit counts the starts it admitted: starts before it was created, or before it was removed and recreated,
/// are not counted.</remarks>
public sealed record RateLimit(string? JobKey, int Permits, TimeSpan Window, KeyScope? PerKey = null)
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

        if (PerKey is { } scope)
        {
            AdmissionKeys.ValidateScope(scope, nameof(PerKey));
        }

        AdmissionKeys.ValidateJobKey(JobKey);
    }
}

/// <summary>Every persisted admission policy at one observation.</summary>
public sealed record AdmissionPolicies(IReadOnlyList<ConcurrencyLimit> Limits, IReadOnlyList<KeyedLimit> KeyedLimits,
    IReadOnlyList<RateLimit> RateLimits);

/// <summary>Shared admission policies. Changes go through audited <see cref="IJobAdministration"/> commands.</summary>
public interface IAdmissionPolicies
{
    Task<AdmissionPolicies> AdmissionPoliciesAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates missing policies only; existing database policies survive restarts and deployments.</summary>
    Task SeedAdmissionPoliciesAsync(IReadOnlyList<KeyedLimit> keyedLimits, IReadOnlyList<RateLimit> rateLimits,
        CancellationToken cancellationToken = default);
}
