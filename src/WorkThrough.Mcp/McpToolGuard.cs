using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WorkThrough;

internal sealed class McpToolGuard(WorkThroughMcpOptions options, IAuthorizationService authorization, IMcpAccessSink accessSink)
{
    internal async Task<bool> AuthorizeAsync(ClaimsPrincipal? user, string? tool = null, Guid? id = null, Guid? runId = null)
    {
        var principal = user ?? new ClaimsPrincipal();
        var resource = new OperationResource(options.Resource.AbsoluteUri, options.Environment, id) { WorkflowRunId = runId };
        if (!(await authorization.AuthorizeAsync(principal, resource,
            OperationPermissions.PolicyPrefix + OperationPermissions.ReadExecutions)).Succeeded)
        {
            return false;
        }

        return tool != InspectionTools.Diagnostics || (await authorization.AuthorizeAsync(principal, resource,
            OperationPermissions.PolicyPrefix + OperationPermissions.ReadPayloads)).Succeeded;
    }

    internal async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> context,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var started = Stopwatch.GetTimestamp();
        var name = context.Params?.Name;
        var known = name is not null && ExecutionTools.Names.Contains(name, StringComparer.Ordinal);
        var executionTarget = name is ExecutionTools.Get or ExecutionTools.Attempts or InspectionTools.Diagnostics;
        var id = executionTarget ? ReadTarget(context, "id") : null;
        var runId = name == InspectionTools.Flow ? ReadTarget(context, "runId") : null;

        var outcome = "unavailable";
        CallToolResult result;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (!known)
            {
                outcome = "unsupported_tool";
                result = Error(outcome, requestId);
            }
            else if (executionTarget && id is null
                || name == InspectionTools.Flow && runId is null)
            {
                outcome = "invalid_arguments";
                result = Error(outcome, requestId);
            }
            else if (!await AuthorizeAsync(context.User, name, id, runId))
            {
                outcome = "forbidden";
                result = Error(outcome, requestId);
            }
            else if (InspectionTools.Names.Contains(name!) && context.Services!.GetService<IExecutionInspectionQueries>() is null)
            {
                outcome = "capability_unavailable";
                result = Error(outcome, requestId);
            }
            else
            {
                result = await next(context, deadline.Token);
                outcome = result.IsError == true ? "invalid_or_unavailable" : "succeeded";
                if (result.IsError == true)
                {
                    result = Error(outcome, requestId);
                }
                else if (JsonSerializer.SerializeToUtf8Bytes(result, McpJsonUtilities.DefaultOptions).Length > 48 * 1024)
                {
                    // Reserve space for the protocol envelope/request ID within the 64 KiB response budget.
                    outcome = "response_too_large";
                    result = Error(outcome, requestId, "Reduce the page limit or maximumCharacters; no partial result was returned.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            outcome = cancellationToken.IsCancellationRequested ? "cancelled" : "deadline_exceeded";
            result = Error(outcome, requestId);
        }
        catch (ArgumentException)
        {
            outcome = "invalid_arguments";
            result = Error(outcome, requestId);
        }
        catch (Exception)
        {
            result = Error(outcome, requestId);
        }

        try
        {
            // A disconnected client must not automatically suppress access evidence.
            using var auditDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await accessSink.WriteAsync(new(requestId, Claim(context.User, "iss") ?? "unknown", Claim(context.User, "sub") ?? "unknown",
                Claim(context.User, "client_id") ?? Claim(context.User, "azp"), known ? name! : "unregistered",
                options.Resource.AbsoluteUri, options.Environment, id, outcome, Stopwatch.GetElapsedTime(started).TotalMilliseconds)
            { WorkflowRunId = runId, SensitiveRead = name == InspectionTools.Diagnostics }, auditDeadline.Token)
                .AsTask().WaitAsync(auditDeadline.Token);
        }
        catch (Exception)
        {
            return Error("access_audit_unavailable", requestId);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return result;
    }

    private static string? Claim(ClaimsPrincipal? user, string type) => user?.FindFirst(type)?.Value;

    private static Guid? ReadTarget(RequestContext<CallToolRequestParams> context, string name) =>
        context.Params?.Arguments?.TryGetValue(name, out var argument) == true && argument.ValueKind == JsonValueKind.String
        && Guid.TryParse(argument.GetString(), out var parsed) ? parsed : null;

    private static CallToolResult Error(string code, string requestId, string? guidance = null)
    {
        var data = JsonSerializer.SerializeToElement(new { code, requestId, guidance });

        return new() { IsError = true, StructuredContent = data, Content = [new TextContentBlock { Text = data.GetRawText() }] };
    }
}
