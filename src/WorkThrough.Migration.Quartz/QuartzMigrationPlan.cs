using System.Globalization;
using System.Text.Json;

namespace WorkThrough.Migration.Quartz;

/// <summary>A trigger's job data as Quartz merges it: the job's entries, overridden by the trigger's.</summary>
public sealed record QuartzJobData(string JobKey, string TriggerKey, IReadOnlyDictionary<string, object?> Values)
{
    /// <summary>Entry <paramref name="key"/> as <typeparamref name="T"/>, converting the strings Quartz stores in its database.</summary>
    public T Get<T>(string key)
    {
        if (!Values.TryGetValue(key, out var value) || value is null)
        {
            throw new InvalidOperationException($"Job data of {JobKey} has no entry '{key}'.");
        }

        return value is T typed ? typed : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }

    /// <summary>Entry <paramref name="key"/> as text, or null when absent.</summary>
    public string? GetString(string key) => Values.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
}

public enum QuartzImportKind
{
    /// <summary>A repeating trigger, imported as a schedule.</summary>
    Schedule,

    /// <summary>A trigger that fires once more, imported as a delayed job.</summary>
    OneTime
}

/// <summary>One Quartz trigger and what it becomes in WorkThrough, or why it cannot move.</summary>
public sealed record QuartzMigrationItem(string JobKey, string TriggerGroup, string TriggerName, string JobType)
{
    /// <summary>The trigger as Quartz writes it, <c>group.name</c>.</summary>
    public string TriggerKey => $"{TriggerGroup}.{TriggerName}";

    public QuartzImportKind Kind { get; init; }
    public JobContractId? Target { get; init; }
    public JsonElement? Input { get; init; }

    /// <summary>The schedule a repeating trigger becomes.</summary>
    public ScheduleDefinition? Schedule { get; init; }

    /// <summary>False for a trigger paused in Quartz: its schedule is published but left paused.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>When a one-time trigger fires.</summary>
    public DateTimeOffset? NotBefore { get; init; }
    public string Queue { get; init; } = JobDefaults.Queue;

    /// <summary>
    /// For a job with jobs chained after it, the flow of the whole chain: a repeating trigger becomes a schedule that starts it, a
    /// one-time trigger a run that starts at its time.
    /// </summary>
    public FlowDefinition? Flow { get; init; }

    /// <summary>Why the trigger cannot be imported; null when it can.</summary>
    public string? Problem { get; init; }

    public bool IsReady => Problem is null;
}

/// <summary>What an import would do, read from Quartz without changing anything.</summary>
public sealed record QuartzMigrationPlan(IReadOnlyList<QuartzMigrationItem> Items, bool SchedulerRunning, int ExecutingJobs)
{
    public IEnumerable<QuartzMigrationItem> Ready => Items.Where(item => item.IsReady);
    public IEnumerable<QuartzMigrationItem> Problems => Items.Where(item => !item.IsReady);
}

/// <summary>An imported trigger, with the WorkThrough job it became; null for a schedule or a chain.</summary>
public sealed record QuartzImported(QuartzMigrationItem Item, Guid? JobId)
{
    /// <summary>For a one-time trigger of a chained job, the flow run it became.</summary>
    public Guid? RunId { get; init; }
}

public sealed record QuartzMigrationResult(IReadOnlyList<QuartzImported> Imported, IReadOnlyList<QuartzMigrationItem> Skipped);
