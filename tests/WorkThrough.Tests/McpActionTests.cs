using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol.Client;
using Xunit;

namespace WorkThrough.Tests;

public sealed partial class McpTests
{
    private const string PrepareTool = "workthrough_prepare_action";
    private const string ExecuteTool = "workthrough_execute_action";
    private static readonly HashSet<McpAction> JobAndQueueActions = [McpAction.RetryJob, McpAction.PauseQueue];

    [Theory, MemberData(nameof(Providers))]
    public async Task An_approved_retry_runs_once_and_a_repeated_execute_returns_the_first_result(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var failed = await FailedJobAsync(harness);
        var approvals = new TestApprovals();
        await using var server = await McpTestHost.CreateAsync(harness, actions: JobAndQueueActions, approvals: approvals);
        await using var client = await ClientAsync(server, OperationPermissions.RetryJobs);

        var prepared = await ActionAsync(client, PrepareTool, new() { ["action"] = "RetryJob", ["target"] = failed.ToString(), ["reason"] = "Upstream fixed" });
        Assert.Equal(McpActionStatus.AwaitingApproval, Status(prepared));
        var proposalId = prepared.GetProperty("proposal").GetProperty("id").GetGuid();
        Assert.Equal(JobStatus.Failed, (await harness.Store.GetAsync(failed))!.Status);

        Assert.Equal(McpActionStatus.NotApproved, Status(await ExecuteAsync(client, proposalId)));
        approvals.Approve(proposalId);
        Assert.Equal(McpActionStatus.Succeeded, Status(await ExecuteAsync(client, proposalId)));
        Assert.Equal(McpActionStatus.Succeeded, Status(await ExecuteAsync(client, proposalId)));

        Assert.Equal(JobStatus.Queued, (await harness.Store.GetAsync(failed))!.Status);
        var audits = (await harness.Store.AuditAsync()).Where(entry => entry.Action == AuditActions.RetryJob).ToArray();
        Assert.Equal(proposalId, Assert.Single(audits).CommandId);
        Assert.Equal("Upstream fixed", audits[0].Reason);
    }

    [Fact]
    public async Task A_target_that_changed_after_approval_is_not_acted_on()
    {
        await using var harness = await StoreHarness.CreateAsync("sqlite");
        var failed = await FailedJobAsync(harness);
        var approvals = new TestApprovals();
        await using var server = await McpTestHost.CreateAsync(harness, actions: JobAndQueueActions, approvals: approvals);
        await using var client = await ClientAsync(server, OperationPermissions.RetryJobs);
        var proposalId = (await PrepareRetryAsync(client, failed)).GetProperty("proposal").GetProperty("id").GetGuid();
        approvals.Approve(proposalId);

        await harness.Store.RetryJobAsync(failed, 1, AdministrativeCommand.New(new("tests", "other-operator"), "Retried elsewhere")).SucceedsAsync();
        var claimed = (await harness.Store.ClaimAsync(new("worker", [Failing.Identity], ["default"], TimeSpan.FromMinutes(1))))!;
        await harness.Store.FailWithDetailsAsync(claimed.Id, claimed.LeaseToken!.Value, new("Type", "Failed again", null), TimeSpan.Zero);

        Assert.Equal(McpActionStatus.PreconditionFailed, Status(await ExecuteAsync(client, proposalId)));
        Assert.Equal(JobStatus.Failed, (await harness.Store.GetAsync(failed))!.Status);
    }

    [Fact]
    public async Task Approval_binds_the_caller_the_lifetime_and_the_permission()
    {
        await using var harness = await StoreHarness.CreateAsync("sqlite");
        var failed = await FailedJobAsync(harness);
        var approvals = new TestApprovals();
        await using var server = await McpTestHost.CreateAsync(harness, actions: JobAndQueueActions, approvals: approvals,
            approvalLifetime: TimeSpan.FromMilliseconds(500));
        await using var operatorClient = await ClientAsync(server, OperationPermissions.RetryJobs);
        await using var intruder = await ClientForAsync(server, "intruder", OperationPermissions.RetryJobs);
        await using var revoked = await ClientAsync(server);

        Assert.Equal(McpActionStatus.Forbidden, Status(await PrepareRetryAsync(revoked, failed)));
        var proposalId = (await PrepareRetryAsync(operatorClient, failed)).GetProperty("proposal").GetProperty("id").GetGuid();
        approvals.Approve(proposalId);
        Assert.Equal(McpActionStatus.NotApproved, Status(await ExecuteAsync(intruder, proposalId)));
        Assert.Equal(McpActionStatus.Forbidden, Status(await ExecuteAsync(revoked, proposalId)));
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Assert.Equal(McpActionStatus.Expired, Status(await ExecuteAsync(operatorClient, proposalId)));

        Assert.Equal(JobStatus.Failed, (await harness.Store.GetAsync(failed))!.Status);
        Assert.DoesNotContain(await harness.Store.AuditAsync(), entry => entry.Action == AuditActions.RetryJob);
    }

    [Fact]
    public async Task Queue_pause_runs_through_approval_and_unenrolled_actions_are_refused()
    {
        await using var harness = await StoreHarness.CreateAsync("sqlite");
        var approvals = new TestApprovals();
        await using var server = await McpTestHost.CreateAsync(harness, actions: JobAndQueueActions, approvals: approvals);
        await using var client = await ClientAsync(server, OperationPermissions.ManageQueues, OperationPermissions.CancelJobs);

        var prepared = await ActionAsync(client, PrepareTool, new() { ["action"] = "PauseQueue", ["target"] = "default", ["reason"] = "Incident 7" });
        var proposalId = prepared.GetProperty("proposal").GetProperty("id").GetGuid();
        approvals.Approve(proposalId);
        var executed = await ExecuteAsync(client, proposalId);

        Assert.Equal(McpActionStatus.Succeeded, Status(executed));
        Assert.True(Assert.Single(await harness.Store.QueuesAsync(), queue => queue.Queue == "default").Paused);
        var cancel = await client.CallToolAsync(PrepareTool, new Dictionary<string, object?> { ["action"] = "CancelJob", ["target"] = Guid.NewGuid().ToString(), ["reason"] = "No" });
        Assert.True(cancel.IsError);
    }

    [Fact]
    public async Task A_read_only_endpoint_lists_no_action_tools_and_refuses_them()
    {
        await using var harness = await StoreHarness.CreateAsync("sqlite");
        var failed = await FailedJobAsync(harness);
        await using var server = await McpTestHost.CreateAsync(harness);
        await using var client = await ClientAsync(server, OperationPermissions.RetryJobs);

        Assert.DoesNotContain(await client.ListToolsAsync(), tool => tool.Name is PrepareTool or ExecuteTool);
        var direct = await client.CallToolAsync(PrepareTool, new Dictionary<string, object?> { ["action"] = "RetryJob", ["target"] = failed.ToString(), ["reason"] = "x" });

        Assert.True(direct.IsError);
        Assert.Equal(JobStatus.Failed, (await harness.Store.GetAsync(failed))!.Status);
    }

    private static readonly JobDefinition<string> Failing = new JobDefinition<string>("mcp.failing").WithMaxAttempts(1);

    private static async Task<Guid> FailedJobAsync(StoreHarness harness)
    {
        var id = await new JobClient(harness.Store).EnqueueAsync(Failing, "input");
        var claimed = (await harness.Store.ClaimAsync(new("worker", [Failing.Identity], ["default"], TimeSpan.FromMinutes(1))))!;
        await harness.Store.FailWithDetailsAsync(claimed.Id, claimed.LeaseToken!.Value, new("Type", "Failed", null), TimeSpan.Zero);

        return id;
    }

    private static Task<McpClient> ClientAsync(McpTestHost server, params string[] grants) => ClientForAsync(server, "operator", grants);

    private static async Task<McpClient> ClientForAsync(McpTestHost server, string subject, params string[] grants) =>
        await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new(McpTestHost.Resource),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false
        }, server.Client(subject: subject, grants: grants)));

    private static Task<JsonElement> PrepareRetryAsync(McpClient client, Guid job) =>
        ActionAsync(client, PrepareTool, new() { ["action"] = "RetryJob", ["target"] = job.ToString(), ["reason"] = "Retry" });

    private static Task<JsonElement> ExecuteAsync(McpClient client, Guid proposalId) =>
        ActionAsync(client, ExecuteTool, new() { ["proposalId"] = proposalId });

    private static async Task<JsonElement> ActionAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.False(result.IsError ?? false, result.StructuredContent?.GetRawText());

        return result.StructuredContent!.Value;
    }

    private static string Status(JsonElement outcome) => outcome.GetProperty("status").GetString()!;

    /// <summary>Stands in for a person approving in the host's workflow.</summary>
    private sealed class TestApprovals : IMcpActionApprovals
    {
        private readonly ConcurrentDictionary<Guid, McpActionProposal> submitted = new();
        private readonly ConcurrentDictionary<Guid, McpActionProposal> approved = new();

        public ValueTask SubmitAsync(McpActionProposal proposal, CancellationToken cancellationToken)
        {
            submitted[proposal.Id] = proposal;

            return ValueTask.CompletedTask;
        }

        public ValueTask<McpActionProposal?> ApprovedAsync(Guid proposalId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(approved.TryGetValue(proposalId, out var proposal) ? proposal : null);

        public void Approve(Guid proposalId) => approved[proposalId] = submitted[proposalId];
    }
}
