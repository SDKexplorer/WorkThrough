using Microsoft.Extensions.Logging;

namespace WorkThrough;

/// <summary>Metadata-only access event. No tool arguments, business labels, tokens or job contents are recorded.</summary>
public sealed record McpAccessEvent(
    string RequestId, string Issuer, string Subject, string? ClientId, string Tool,
    string Endpoint, string Environment, Guid? ExecutionId, string Outcome, double DurationMilliseconds);

/// <summary>Security sink independent of the job database. Sink failure prevents a tool result being returned.</summary>
public interface IMcpAccessSink
{
    ValueTask WriteAsync(McpAccessEvent access, CancellationToken cancellationToken);
}

internal sealed class LoggingMcpAccessSink(ILogger<LoggingMcpAccessSink> logger) : IMcpAccessSink
{
    public ValueTask WriteAsync(McpAccessEvent access, CancellationToken cancellationToken)
    {
        logger.LogInformation("WorkThrough MCP access: {RequestId} {Issuer} {Subject} {ClientId} {Tool} {Endpoint} {Environment} {ExecutionId} {Outcome} {DurationMilliseconds}",
            access.RequestId, access.Issuer, access.Subject, access.ClientId, access.Tool, access.Endpoint,
            access.Environment, access.ExecutionId, access.Outcome, access.DurationMilliseconds);

        return ValueTask.CompletedTask;
    }
}
