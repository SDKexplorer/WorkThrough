using System.Reflection;
using System.Text.Json;

namespace WorkThrough.Migration.Hangfire;

public enum HangfireJobKind
{
    Recurring, Enqueued, Scheduled
}

/// <summary>A Hangfire method invocation as Hangfire stored it: the method and its deserialized arguments.</summary>
public sealed record HangfireCall(Type Type, MethodInfo Method, IReadOnlyList<object?> Arguments)
{
    /// <summary>Argument <paramref name="index"/>, as the method's parameter type that Hangfire deserialized it to.</summary>
    public T Argument<T>(int index) => Arguments[index] is T value ? value
        : throw new InvalidOperationException($"Argument {index} of {Type.Name}.{Method.Name} is not a {typeof(T).Name}.");
}

/// <summary>One Hangfire job and what it becomes in WorkThrough, or why it cannot move.</summary>
public sealed record HangfireMigrationItem(HangfireJobKind Kind, string HangfireId, string Method)
{
    public JobContractId? Target { get; init; }
    public JsonElement? Input { get; init; }
    public string Queue { get; init; } = JobDefaults.Queue;

    /// <summary>When a scheduled job falls due.</summary>
    public DateTimeOffset? NotBefore { get; init; }

    /// <summary>For a recurring job: the WorkThrough schedule key, five-field cron expression and time zone.</summary>
    public string? ScheduleKey { get; init; }
    public string? Cron { get; init; }
    public string? TimeZone { get; init; }

    /// <summary>Why the job cannot be imported; null when it can.</summary>
    public string? Problem { get; init; }

    public bool IsReady => Problem is null;
}

/// <summary>
/// What an import would do, read from Hangfire without changing anything. <see cref="RunningServers"/> must be zero to import, so
/// no job runs in both systems.
/// </summary>
public sealed record HangfireMigrationPlan(IReadOnlyList<HangfireMigrationItem> Items, int RunningServers, long Processing, long Failed)
{
    public IEnumerable<HangfireMigrationItem> Ready => Items.Where(item => item.IsReady);
    public IEnumerable<HangfireMigrationItem> Problems => Items.Where(item => !item.IsReady);
}

/// <summary>An imported job, with the WorkThrough job it became; null for a schedule.</summary>
public sealed record HangfireImported(HangfireMigrationItem Item, Guid? JobId);

public sealed record HangfireMigrationResult(IReadOnlyList<HangfireImported> Imported, IReadOnlyList<HangfireMigrationItem> Skipped);
