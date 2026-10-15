using System.Text.Json;

namespace WorkThrough;

/// <summary>A flow definition and its input, carried by a schedule so each occurrence starts one run.</summary>
public sealed record ScheduledFlow(FlowDefinition Definition, JsonElement Input);

/// <summary>
/// Schedules that start flows instead of single jobs. Every host registered with <c>AddWorkThrough</c> serves <see cref="Start"/>, which
/// starts the run carried in its input; a retried attempt finds the run it already started.
/// </summary>
public static class ScheduledFlows
{
    public static JobDefinition<ScheduledFlow> Start { get; } = new("workthrough.flows.start", displayName: "Start a scheduled flow");

    /// <summary>The idempotency key of the run an occurrence's job starts.</summary>
    public static string RunKey(Guid jobId) => "scheduled-flow:" + jobId.ToString("N");

    /// <summary>A schedule that starts <paramref name="flow"/> with <paramref name="input"/> on <paramref name="cron"/>.</summary>
    public static ScheduleDefinition Create<TInput>(string key, string cron, FlowDefinition flow, TInput input,
        string timeZone = ScheduleDefinition.DefaultTimeZone, int version = 1)
    {
        FlowValidator.Validate(flow);

        return ScheduleDefinition.Create(key, cron, Start, new ScheduledFlow(flow, JobJson.ToElement(input)), timeZone, version);
    }
}
