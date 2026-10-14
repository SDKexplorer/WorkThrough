using System.ComponentModel;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WorkThrough;

/// <summary>Prepare → person approves in the host → execute, for the actions the host enrolled (ADR-031).</summary>
[McpServerToolType]
internal sealed class ActionTools(WorkThroughMcpOptions options, IAuthorizationService authorization, IServiceProvider services)
{
    internal const string Prepare = "workthrough_prepare_action";
    internal const string Execute = "workthrough_execute_action";
    internal static readonly string[] Names = [Prepare, Execute];

    internal const int MaximumReasonLength = 500;

    private IMcpActionApprovals Approvals => services.GetRequiredService<IMcpActionApprovals>();
    private TimeProvider Clock => services.GetService<TimeProvider>() ?? TimeProvider.System;

    internal static string Permission(McpAction action) => action switch
    {
        McpAction.RetryJob => OperationPermissions.RetryJobs,
        McpAction.CancelJob => OperationPermissions.CancelJobs,
        McpAction.RecoverStep => OperationPermissions.RecoverFlows,
        McpAction.PauseQueue or McpAction.ResumeQueue => OperationPermissions.ManageQueues,
        _ => OperationPermissions.ManageWorkers
    };

    [McpServerTool(Name = Prepare, ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Propose one administrative action for a person to approve; nothing changes yet. target is the job ID for RetryJob, CancelJob and RecoverStep, the queue name for PauseQueue and ResumeQueue, and the worker ID for DrainWorker and ResumeWorker. reason is recorded in the audit. The proposal binds the target's current attempt or revision; if that changes before execution, execution fails without effect. Ask the operator to approve the returned proposal ID in their approval workflow, then call workthrough_execute_action. Your own confirmation is not approval.")]
    public async Task<McpActionOutcome> PrepareAction(RequestContext<CallToolRequestParams> context, McpAction action, string target, string reason,
        CancellationToken cancellationToken)
    {
        var user = context.User ?? new ClaimsPrincipal();
        if (!options.Actions.Contains(action) || string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(reason) || reason.Length > MaximumReasonLength)
        {
            throw new ArgumentException("Choose an enrolled action, a target and a reason of at most 500 characters.");
        }

        if (!await PermittedAsync(user, action))
        {
            return new(McpActionStatus.Forbidden);
        }

        var precondition = await PreconditionAsync(action, target, cancellationToken);
        if (precondition is null)
        {
            return new(McpActionStatus.NotFound);
        }

        var proposal = new McpActionProposal(Guid.NewGuid(), action, target, precondition.Value, reason, Claim(user, McpClaims.Issuer),
            Claim(user, McpClaims.Subject), options.Environment, Clock.GetUtcNow() + options.ApprovalLifetime, Effects(action, target));
        await Approvals.SubmitAsync(proposal, cancellationToken);

        return new(McpActionStatus.AwaitingApproval, proposal);
    }

    [McpServerTool(Name = Execute, ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Execute a proposal a person approved. Only the approved copy held by the host is used. Fails without effect when the proposal is unapproved, expired, belongs to someone else, your permission was revoked or the target changed since preparation. Calling it again after a lost response returns the first result.")]
    public async Task<McpActionOutcome> ExecuteAction(RequestContext<CallToolRequestParams> context, Guid proposalId, CancellationToken cancellationToken)
    {
        var user = context.User ?? new ClaimsPrincipal();
        var proposal = await Approvals.ApprovedAsync(proposalId, cancellationToken);
        if (proposal is null || proposal.Id != proposalId || proposal.Issuer != Claim(user, McpClaims.Issuer)
            || proposal.Subject != Claim(user, McpClaims.Subject) || proposal.Environment != options.Environment || !options.Actions.Contains(proposal.Action))
        {
            return new(McpActionStatus.NotApproved);
        }

        if (Clock.GetUtcNow() >= proposal.ExpiresAt)
        {
            return new(McpActionStatus.Expired, proposal);
        }

        if (!await PermittedAsync(user, proposal.Action))
        {
            return new(McpActionStatus.Forbidden, proposal);
        }

        var command = new AdministrativeCommand(proposal.Id, new(proposal.Issuer, proposal.Subject, user.FindFirst("name")?.Value), proposal.Reason);
        try
        {
            var (result, revision) = await RunAsync(proposal, command, cancellationToken);

            return result.IsSuccess ? new(McpActionStatus.Succeeded, proposal, revision)
                : new(result.Error!.Type == ErrorType.NotFound ? McpActionStatus.NotFound : McpActionStatus.PreconditionFailed, proposal);
        }
        catch (StateConflictException)
        {
            return new(McpActionStatus.PreconditionFailed, proposal);
        }
    }

    private async Task<bool> PermittedAsync(ClaimsPrincipal user, McpAction action) =>
        (await authorization.AuthorizeAsync(user, new OperationResource(options.Resource.AbsoluteUri, options.Environment),
            OperationPermissions.Policy(Permission(action)))).Succeeded;

    private async Task<int?> PreconditionAsync(McpAction action, string target, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case McpAction.RetryJob or McpAction.CancelJob or McpAction.RecoverStep:
                return Guid.TryParse(target, out var id) && await services.GetRequiredService<IJobQueries>().GetAsync(id, cancellationToken) is { } job
                    ? job.Attempt
                    : null;
            case McpAction.PauseQueue or McpAction.ResumeQueue:
                var queues = await services.GetRequiredService<IExecutionOperations>().QueuesAsync(cancellationToken);

                return queues.FirstOrDefault(queue => queue.Queue == target)?.Revision ?? 0;
            default:
                var workers = await services.GetRequiredService<IExecutionOperations>().WorkerControlsAsync(cancellationToken);

                return workers.FirstOrDefault(worker => worker.WorkerId == target)?.Revision ?? 0;
        }
    }

    private async Task<(Result Result, int? Revision)> RunAsync(McpActionProposal proposal, AdministrativeCommand command, CancellationToken cancellationToken)
    {
        var administration = services.GetRequiredService<IJobAdministration>();
        var operations = services.GetRequiredService<IExecutionOperations>();
        switch (proposal.Action)
        {
            case McpAction.RetryJob:
                return (await administration.RetryJobAsync(Guid.Parse(proposal.Target), proposal.Precondition, command, cancellationToken), null);
            case McpAction.CancelJob:
                return (await administration.CancelJobAsync(Guid.Parse(proposal.Target), proposal.Precondition, command, cancellationToken), null);
            case McpAction.RecoverStep:
                return (await administration.RecoverStepAsync(Guid.Parse(proposal.Target), proposal.Precondition, command, cancellationToken), null);
        }

        var result = proposal.Action switch
        {
            McpAction.PauseQueue or McpAction.ResumeQueue => await operations.SetQueuePausedAsync(proposal.Target, proposal.Action == McpAction.PauseQueue,
                proposal.Precondition, command, cancellationToken),
            _ => await operations.SetWorkerDrainingAsync(proposal.Target, proposal.Action == McpAction.DrainWorker, proposal.Precondition, command,
                cancellationToken)
        };

        return (result, result.IsSuccess ? result.Value : null);
    }

    private static string Effects(McpAction action, string target) => action switch
    {
        McpAction.RetryJob => $"Queues job {target} for another attempt. The handler runs again and may repeat external effects.",
        McpAction.CancelJob => $"Cancels job {target}. A running handler is asked to stop and may still complete its external effect.",
        McpAction.RecoverStep => $"Retries failed flow step {target} and resumes its flow. The step's handler runs again.",
        McpAction.PauseQueue => $"Stops workers claiming new work from queue {target}. Running jobs continue.",
        McpAction.ResumeQueue => $"Lets workers claim work from queue {target} again.",
        McpAction.DrainWorker => $"Stops worker {target} claiming new work. Its running jobs finish.",
        _ => $"Lets worker {target} claim work again."
    };

    private static string Claim(ClaimsPrincipal user, string type) => user.FindFirst(type)?.Value ?? "";
}
