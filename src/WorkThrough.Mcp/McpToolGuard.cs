using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WorkThrough;

internal sealed class McpToolGuard(WorkThroughMcpOptions options, IAuthorizationService authorization, IMcpAccessSink accessSink)
{
    /// <summary>The response budget advertised to clients.</summary>
    internal const int MaximumResponseBytes = 65536;

    // Reserve space for the protocol envelope and request ID within the response budget.
    private const int MaximumResultBytes = 48 * 1024;
    private const string Unknown = "unknown";
    private const string UnregisteredTool = "unregistered";
    private const string ResponseTooLargeGuidance = "Reduce the page limit or maximumCharacters; no partial result was returned.";
    private static readonly TimeSpan AccessAuditTimeout = TimeSpan.FromSeconds(2);

    internal async Task<bool> AuthorizeAsync(ClaimsPrincipal? user, string? tool = null, Guid? id = null, Guid? runId = null, Guid? archiveId = null)
    {
        var principal = user ?? new ClaimsPrincipal();
        var resource = new OperationResource(options.Resource.AbsoluteUri, options.Environment, id) { WorkflowRunId = runId, ArchiveId = archiveId };
        if (!(await authorization.AuthorizeAsync(principal, resource, OperationPermissions.Policy(OperationPermissions.ReadExecutions))).Succeeded)
        {
            return false;
        }

        var additional = tool switch
        {
            InspectionTools.Diagnostics => OperationPermissions.ReadPayloads,
            HistoryTools.Audit => OperationPermissions.ReadAudit,
            HistoryTools.Retention or RetentionTools.Status => OperationPermissions.ReadRetention,
            _ => null
        };

        return additional is null || (await authorization.AuthorizeAsync(principal, resource, OperationPermissions.Policy(additional))).Succeeded;
    }

    internal async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> context,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var started = Stopwatch.GetTimestamp();
        var call = ToolCall.Read(context);
        var (outcome, result) = await DecideAsync(context, call, next, requestId, cancellationToken);
        if (!await TryRecordAccessAsync(context.User, call, outcome, requestId, started))
        {
            return Error(McpAccessOutcomes.AccessAuditUnavailable, requestId);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return result;
    }

    private async Task<(string Outcome, CallToolResult Result)> DecideAsync(RequestContext<CallToolRequestParams> context, ToolCall call,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next, string requestId, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationQueryLimits.Deadline);
        try
        {
            var rejection = !call.Known ? McpAccessOutcomes.UnsupportedTool
                : call.MissingTarget ? McpAccessOutcomes.InvalidArguments
                : !await AuthorizeAsync(context.User, call.Name, call.ExecutionId, call.RunId, call.ArchiveId) ? McpAccessOutcomes.Forbidden
                : !McpCapabilities.IsAvailable(context.Services!, call.Name!) ? McpAccessOutcomes.CapabilityUnavailable
                : null;
            if (rejection is not null)
            {
                return (rejection, Error(rejection, requestId));
            }

            var result = await next(context, deadline.Token);
            if (result.IsError == true)
            {
                return (McpAccessOutcomes.InvalidOrUnavailable, Error(McpAccessOutcomes.InvalidOrUnavailable, requestId));
            }

            return JsonSerializer.SerializeToUtf8Bytes(result, McpJsonUtilities.DefaultOptions).Length > MaximumResultBytes
                ? (McpAccessOutcomes.ResponseTooLarge, Error(McpAccessOutcomes.ResponseTooLarge, requestId, ResponseTooLargeGuidance))
                : (McpAccessOutcomes.Succeeded, result);
        }
        catch (OperationCanceledException)
        {
            var outcome = cancellationToken.IsCancellationRequested ? McpAccessOutcomes.Cancelled : McpAccessOutcomes.DeadlineExceeded;

            return (outcome, Error(outcome, requestId));
        }
        catch (ArgumentException)
        {
            return (McpAccessOutcomes.InvalidArguments, Error(McpAccessOutcomes.InvalidArguments, requestId));
        }
        catch (Exception)
        {
            return (McpAccessOutcomes.Unavailable, Error(McpAccessOutcomes.Unavailable, requestId));
        }
    }

    /// <summary>A disconnected client must not suppress access evidence, so the sink gets its own deadline.</summary>
    private async Task<bool> TryRecordAccessAsync(ClaimsPrincipal? user, ToolCall call, string outcome, string requestId, long started)
    {
        try
        {
            using var timeout = new CancellationTokenSource(AccessAuditTimeout);
            var access = new McpAccessEvent(requestId, Claim(user, McpClaims.Issuer) ?? Unknown, Claim(user, McpClaims.Subject) ?? Unknown,
                Claim(user, McpClaims.ClientId) ?? Claim(user, McpClaims.AuthorizedParty), call.Known ? call.Name! : UnregisteredTool,
                options.Resource.AbsoluteUri, options.Environment, call.ExecutionId, outcome, Stopwatch.GetElapsedTime(started).TotalMilliseconds)
            {
                WorkflowRunId = call.RunId,
                ArchiveId = call.ArchiveId,
                SensitiveRead = call.Name is InspectionTools.Diagnostics or HistoryTools.Audit
            };
            await accessSink.WriteAsync(access, timeout.Token).AsTask().WaitAsync(timeout.Token);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? Claim(ClaimsPrincipal? user, string type) => user?.FindFirst(type)?.Value;

    private static CallToolResult Error(string code, string requestId, string? guidance = null)
    {
        var data = JsonSerializer.SerializeToElement(new { code, requestId, guidance });

        return new() { IsError = true, StructuredContent = data, Content = [new TextContentBlock { Text = data.GetRawText() }] };
    }

    /// <summary>The requested tool and the resource identifiers authorization is evaluated against.</summary>
    private sealed record ToolCall(string? Name, bool Known, Guid? ExecutionId, Guid? RunId, Guid? ArchiveId, bool MissingTarget)
    {
        public static ToolCall Read(RequestContext<CallToolRequestParams> context)
        {
            var name = context.Params?.Name;
            var known = name is not null && ExecutionTools.Names.Contains(name, StringComparer.Ordinal);
            var targetsExecution = name is ExecutionTools.Get or ExecutionTools.Attempts or InspectionTools.Diagnostics or RuntimeTools.Capacity;
            var executionId = targetsExecution ? ReadTarget(context, "id") : null;
            var runId = name == InspectionTools.Flow ? ReadTarget(context, "runId") : null;
            var archiveId = name == HistoryTools.Archive ? ReadTarget(context, "archiveId") : null;
            var missingTarget = targetsExecution && executionId is null
                || name == InspectionTools.Flow && runId is null
                || name == HistoryTools.Archive && archiveId is null;

            return new(name, known, executionId, runId, archiveId, missingTarget);
        }

        private static Guid? ReadTarget(RequestContext<CallToolRequestParams> context, string argument) =>
            context.Params?.Arguments?.TryGetValue(argument, out var value) == true && value.ValueKind == JsonValueKind.String
            && Guid.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }
}
