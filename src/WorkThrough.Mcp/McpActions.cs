namespace WorkThrough;

/// <summary>Administrative actions an MCP endpoint can propose and, once a person approves, execute (ADR-031).</summary>
public enum McpAction
{
    RetryJob, CancelJob, RecoverStep, PauseQueue, ResumeQueue, DrainWorker, ResumeWorker
}

/// <summary>
/// An exact proposed action, bound to the caller, the target's current state and an expiry. Its ID becomes the administrative command
/// ID, so executing an approved proposal twice returns the first result instead of acting twice.
/// </summary>
public sealed record McpActionProposal(Guid Id, McpAction Action, string Target, int Precondition, string Reason, string Issuer, string Subject,
    string Environment, DateTimeOffset ExpiresAt, string Effects);

/// <summary>
/// The host's approval workflow: a dashboard page, a chat approval or a ticket. WorkThrough hands it each prepared proposal and executes
/// only the copy it returns as approved, never anything the assistant supplies. Approval must come from a person other than the model.
/// </summary>
public interface IMcpActionApprovals
{
    /// <summary>Records a proposal for a person to review.</summary>
    ValueTask SubmitAsync(McpActionProposal proposal, CancellationToken cancellationToken);

    /// <summary>The proposal as submitted, if a person approved it; otherwise null.</summary>
    ValueTask<McpActionProposal?> ApprovedAsync(Guid proposalId, CancellationToken cancellationToken);
}

/// <summary>What happened to a prepare or execute request. Only <see cref="McpActionStatus.Succeeded"/> changed engine state.</summary>
public sealed record McpActionOutcome(string Status, McpActionProposal? Proposal = null, int? Revision = null);

public static class McpActionStatus
{
    public const string AwaitingApproval = "awaiting-approval";
    public const string NotApproved = "not-approved";
    public const string Expired = "expired";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not-found";
    public const string PreconditionFailed = "precondition-failed";
    public const string Succeeded = "succeeded";
}
