using System.Security.Cryptography;
using System.Text;

namespace WorkThrough;

public static class ScheduleLifecycle
{
    /// <summary>
    /// Saves, publishes and, when <paramref name="enable"/>, enables a new schedule through the audited lifecycle. Command IDs derive
    /// from <paramref name="source"/>, the schedule and the step, so repeating the call, from any host, applies each step once.
    /// </summary>
    /// <returns>The final revision, or the first step's error.</returns>
    public static async Task<Result<int>> CreatePublishedAsync(this IJobSchedules schedules, ScheduleDefinition definition, bool enable,
        OperationActor actor, string source, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(definition);
        AdministrativeCommand Command(string step) =>
            new(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{source}/{definition.Key}/{definition.Version}/{step}")).AsSpan(0, 16)), actor,
                $"{reason} ({step})");

        var draft = await schedules.SaveScheduleDraftAsync(definition, 0, Command("draft"), cancellationToken);
        var published = draft.IsSuccess
            ? await schedules.PublishScheduleAsync(definition.Key, definition.Version, draft.Value, Command("publish"), cancellationToken)
            : draft;

        return published.IsSuccess && enable
            ? await schedules.SetScheduleEnabledAsync(definition.Key, definition.Version, published.Value, true, Command("enable"), cancellationToken)
            : published;
    }
}
