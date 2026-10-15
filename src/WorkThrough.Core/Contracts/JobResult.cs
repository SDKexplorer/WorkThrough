using System.Text.Json;

namespace WorkThrough;

public enum JobOutcome
{
    Succeeded, Rejected, Deferred
}

/// <summary>JSON output retained independently of handler assemblies.</summary>
public sealed record JobResult(JsonElement Output, JobOutcome Outcome = JobOutcome.Succeeded, string? Reason = null, TimeSpan? RetryAfter = null)
{
    private static JsonElement NoOutput { get; } = JsonDocument.Parse("null").RootElement.Clone();

    public static JobResult From<T>(T output) => new(JobJson.ToElement(output));

    public static JobResult Done() => new(NoOutput);

    /// <summary>Rejects invalid work without automatic retries; flow failure policy still applies.</summary>
    public static JobResult Reject(string reason) => new(NoOutput, JobOutcome.Rejected, reason);

    /// <summary>Releases the slot and schedules a fresh attempt without consuming the failure retry budget.</summary>
    public static JobResult Defer(TimeSpan delay, string reason) => new(NoOutput, JobOutcome.Deferred, reason, delay);

    public void Validate()
    {
        if (Output.ValueKind != JsonValueKind.Undefined && JobLimits.ExceedsInlineSize(Output))
        {
            throw new ArgumentException("Inline output is limited to 1 MiB. Store large results externally and return a reference.");
        }

        if (!Enum.IsDefined(Outcome) || Output.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("Invalid processor outcome.");
        }

        if (Outcome != JobOutcome.Succeeded && (string.IsNullOrWhiteSpace(Reason) || Reason.Length > JobLimits.MaximumMessageLength))
        {
            throw new ArgumentException($"Rejection and deferral require a reason of 1–{JobLimits.MaximumMessageLength} characters.");
        }

        if (Outcome == JobOutcome.Deferred && (RetryAfter is not { } delay || !JobLimits.IsSupportedDelay(delay)))
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

