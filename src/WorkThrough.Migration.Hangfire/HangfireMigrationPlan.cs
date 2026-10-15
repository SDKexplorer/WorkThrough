using System.Reflection;
using System.Text.Json;

namespace WorkThrough.Migration.Hangfire;

public enum HangfireJobKind
{
    Recurring, Enqueued, Scheduled,

    /// <summary>A job that failed in Hangfire, imported to run again when <see cref="HangfireMigration.IncludeFailed"/> is set.</summary>
    Failed
}

/// <summary>A Hangfire method invocation as Hangfire stored it: the method and its deserialized arguments.</summary>
public sealed record HangfireCall(Type Type, MethodInfo Method, IReadOnlyList<object?> Arguments)
{
    /// <summary>Argument <paramref name="index"/>, as the method's parameter type that Hangfire deserialized it to.</summary>
    public T Argument<T>(int index) => Arguments[index] is T value ? value
        : throw new InvalidOperationException($"Argument {index} of {Type.Name}.{Method.Name} is not a {typeof(T).Name}.");
}

/// <summary>
/// A Hangfire invocation as stored, for jobs whose types no longer load: the type's full name, the method name and each argument's JSON.
/// </summary>
public sealed record HangfireArguments(string Type, string Method, IReadOnlyList<JsonElement> Arguments)
{
    private static readonly JsonSerializerOptions Options = new(JobJson.Options) { PropertyNameCaseInsensitive = true };

    /// <summary>Argument <paramref name="index"/> read as <typeparamref name="T"/>; property names match regardless of case.</summary>
    public T Argument<T>(int index) => Arguments[index].Deserialize<T>(Options)
        ?? throw new InvalidOperationException($"Argument {index} of {Type}.{Method} is null.");
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
    /// <summary>Continuations waiting in Hangfire for a parent job. They are not imported; import refuses while any wait.</summary>
    public long Awaiting { get; init; }

    public IEnumerable<HangfireMigrationItem> Ready => Items.Where(item => item.IsReady);
    public IEnumerable<HangfireMigrationItem> Problems => Items.Where(item => !item.IsReady);
}

/// <summary>An imported job, with the WorkThrough job it became; null for a schedule.</summary>
public sealed record HangfireImported(HangfireMigrationItem Item, Guid? JobId);

public sealed record HangfireMigrationResult(IReadOnlyList<HangfireImported> Imported, IReadOnlyList<HangfireMigrationItem> Skipped);
