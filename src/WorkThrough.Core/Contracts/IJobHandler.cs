using System.Text.Json;

namespace WorkThrough;

/// <summary>Executes one attempt. Implementations must tolerate repeated execution.</summary>
public interface IJobHandler<in TJob>
{
    Task<JobResult> HandleAsync(TJob job, JobExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>JSON output retained independently of handler assemblies.</summary>
public sealed record JobResult(JsonElement Output)
{
    public static JobResult From<T>(T output) => new(JsonSerializer.SerializeToElement(output, JobJson.Options));
    public static JobResult Done() => From<object?>(null);
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
