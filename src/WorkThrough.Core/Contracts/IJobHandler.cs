using System.Text.Json;

namespace WorkThrough;

/// <summary>Executes one attempt. Implementations must tolerate repeated execution.</summary>
public interface IJobHandler<in TJob>
{
    Task<JobResult> HandleAsync(TJob job, JobExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>JSON output retained independently of handler assemblies.</summary>
public enum JobOutcome
{
    Succeeded, Rejected, Deferred
}
public sealed record JobResult(JsonElement Output, JobOutcome Outcome = JobOutcome.Succeeded, string? Reason = null, TimeSpan? RetryAfter = null)
{
    public static JobResult From<T>(T output) => new(JsonSerializer.SerializeToElement(output, JobJson.Options));
    public static JobResult Done() => From<object?>(null);
    /// <summary>Rejects invalid work without automatic retries; flow failure policy still applies.</summary>
    public static JobResult Reject(string reason) => new(JsonSerializer.SerializeToElement<object?>(null), JobOutcome.Rejected, reason);
    /// <summary>Releases the slot and schedules a fresh attempt without consuming the failure retry budget.</summary>
    public static JobResult Defer(TimeSpan delay, string reason) => new(JsonSerializer.SerializeToElement<object?>(null), JobOutcome.Deferred, reason, delay);

    public void Validate()
    {
        if (Output.ValueKind != JsonValueKind.Undefined && System.Text.Encoding.UTF8.GetByteCount(Output.GetRawText()) > 1_048_576)
        {
            throw new ArgumentException("Inline output is limited to 1 MiB. Store large results externally and return a reference.");
        }

        if (!Enum.IsDefined(Outcome) || Output.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("Invalid processor outcome.");
        }

        if (Outcome != JobOutcome.Succeeded && (string.IsNullOrWhiteSpace(Reason) || Reason.Length > 2000))
        {
            throw new ArgumentException("Rejection and deferral require a reason of 1–2000 characters.");
        }

        if (Outcome == JobOutcome.Deferred && (RetryAfter is null || RetryAfter < TimeSpan.FromMilliseconds(1) || RetryAfter > TimeSpan.FromDays(365)))
        {
            throw new ArgumentException("Deferral must be between one millisecond and 365 days.");
        }
    }
}

/// <summary>Execution metadata. The idempotency key remains stable across retries.</summary>
public sealed record JobExecutionContext(
    Guid JobId, int Attempt, string? CorrelationId, Guid? WorkflowRunId, string? StepId, Guid LeaseToken)
{
    public string IdempotencyKey => JobId.ToString("N");
    public IJobProgress? Progress { get; init; }
    public Task<bool> ReportProgressAsync(string? description = null, CancellationToken cancellationToken = default) =>
        Progress?.ReportAsync(description, cancellationToken) ?? Task.FromResult(false);
}

public interface IJobProgress
{
    Task<bool> ReportAsync(string? description, CancellationToken cancellationToken = default);
}

/// <summary>Serializer settings for plain contract data; CLR type metadata is never persisted.</summary>
public static class JobJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };
}
