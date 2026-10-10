using Microsoft.Extensions.Logging;

namespace WorkThrough;

/// <summary>Values of <see cref="McpAccessEvent.Outcome"/>; failed calls also return the outcome as their error code.</summary>
public static class McpAccessOutcomes
{
    public const string Succeeded = "succeeded";
    public const string UnsupportedTool = "unsupported_tool";
    public const string InvalidArguments = "invalid_arguments";
    public const string Forbidden = "forbidden";
    public const string CapabilityUnavailable = "capability_unavailable";
    public const string InvalidOrUnavailable = "invalid_or_unavailable";
    public const string ResponseTooLarge = "response_too_large";
    public const string Cancelled = "cancelled";
    public const string DeadlineExceeded = "deadline_exceeded";
    public const string Unavailable = "unavailable";
    public const string AccessAuditUnavailable = "access_audit_unavailable";
}

/// <summary>Metadata-only access event. No tool arguments, business labels, tokens or job contents are recorded.</summary>
public sealed record McpAccessEvent(
    string RequestId, string Issuer, string Subject, string? ClientId, string Tool,
    string Endpoint, string Environment, Guid? ExecutionId, string Outcome, double DurationMilliseconds)
{
    public Guid? WorkflowRunId { get; init; }
    public Guid? ArchiveId { get; init; }
    public bool SensitiveRead { get; init; }
}

/// <summary>Security sink independent of the job database. Sink failure prevents a tool result being returned.</summary>
public interface IMcpAccessSink
{
    ValueTask WriteAsync(McpAccessEvent access, CancellationToken cancellationToken);
}

internal sealed class LoggingMcpAccessSink(ILogger<LoggingMcpAccessSink> logger) : IMcpAccessSink
{
    public ValueTask WriteAsync(McpAccessEvent access, CancellationToken cancellationToken)
    {
        logger.LogInformation("WorkThrough MCP access: {RequestId} {Issuer} {Subject} {ClientId} {Tool} {Endpoint} {Environment} {ExecutionId} {WorkflowRunId} {ArchiveId} {SensitiveRead} {Outcome} {DurationMilliseconds}",
            access.RequestId, access.Issuer, access.Subject, access.ClientId, access.Tool, access.Endpoint,
            access.Environment, access.ExecutionId, access.WorkflowRunId, access.ArchiveId, access.SensitiveRead, access.Outcome, access.DurationMilliseconds);

        return ValueTask.CompletedTask;
    }
}
