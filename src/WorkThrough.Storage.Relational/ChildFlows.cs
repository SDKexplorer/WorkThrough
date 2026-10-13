using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkThrough.Storage;

/// <summary>Child steps that run another flow and wait for its outcome (ADR-023).</summary>
public abstract partial class RelationalJobStore
{
    private const string ChildSucceeded = "Succeeded";

    /// <summary>Where a run comes from: its admission keys and, for a child run, the parent step that waits for it.</summary>
    private sealed record RunOrigin(string? CorrelationId, string? PartitionKey, string? TenantId, int? MaxParallelSteps, string? TraceParent,
        Guid? ParentRunId = null, Guid? ParentJobId = null, int Depth = 0, Guid? ForkedFromRunId = null);

    /// <summary>The outcome a child run reports to its parent step.</summary>
    private sealed record ChildOutcome(string Status, JsonElement? Output, string? Error);

    /// <summary>
    /// Publishes a definition that starts its first run, and every child definition it carries that the store does not have yet.
    /// Stored versions are immutable and must still be published.
    /// </summary>
    private async Task PublishForStartAsync(DbConnection connection, DbTransaction transaction, FlowDefinition definition, HashSet<(string, int)> visited,
        CancellationToken ct)
    {
        if (!visited.Add((definition.Key, definition.Version)))
        {
            return;
        }

        FlowValidator.Validate(definition);

        var lifecycle = await ReadDefinitionAsync(connection, transaction, definition.Key, definition.Version, ct);
        if (lifecycle is not null && lifecycle.Status != DefinitionStatus.Published)
        {
            throw new StateConflictException("Only published flow definitions can start new runs.");
        }

        var serialized = JsonSerializer.Serialize(definition, JobJson.Options);
        var existing = await ScalarAsync(connection, transaction,
            "SELECT definition FROM dj_definitions WHERE flow_key=@key AND version=@version", ct,
            ("key", definition.Key), ("version", definition.Version));
        if (existing is string saved && JsonSerializer.Serialize(JsonSerializer.Deserialize<FlowDefinition>(saved, JobJson.Options), JobJson.Options) != serialized)
        {
            throw new StateConflictException($"Flow {VersionTarget(definition.Key, definition.Version)} is immutable. Publish a new version.");
        }

        await ExecuteAsync(connection, transaction, UpsertSql("dj_definitions", "flow_key,version,definition", "@key,@version,@definition", "flow_key,version"), ct,
            ("key", definition.Key), ("version", definition.Version), ("definition", serialized));
        await ExecuteAsync(connection, transaction, UpsertSql("dj_definition_lifecycle", "flow_key,version,definition,status,revision",
            "@key,@version,@definition,@status,1", "flow_key,version"), ct,
            ("key", definition.Key), ("version", definition.Version), ("definition", serialized), ("status", nameof(DefinitionStatus.Published)));

        foreach (var child in definition.Steps.Select(step => step.Child?.Definition).OfType<FlowDefinition>())
        {
            await PublishForStartAsync(connection, transaction, child, visited, ct);
        }
    }

    /// <summary>
    /// Inserts a running flow with one waiting job per step, then advances it. Steps in <paramref name="kept"/> start with that job's
    /// outcome instead, as a fork keeps the work before its checkpoint.
    /// </summary>
    private async Task<Guid> InsertRunAsync(DbConnection connection, DbTransaction transaction, FlowDefinition definition, JsonElement input, RunOrigin origin,
        CancellationToken ct, IReadOnlyDictionary<string, JobRecord>? kept = null)
    {
        var runId = Guid.NewGuid();
        await ExecuteAsync(connection, transaction, """
            INSERT INTO dj_flows(id,flow_key,version,input,correlation_id,status,max_parallel,parent_run_id,parent_job_id,depth,forked_from_run_id)
            VALUES (@id,@key,@version,@input,@correlation,@status,@parallel,@parentRun,@parentJob,@depth,@forkedFrom)
            """, ct, ("id", runId.ToString()), ("key", definition.Key), ("version", definition.Version), ("input", await StoredAsync(connection, transaction, input, ct)),
            ("correlation", origin.CorrelationId), ("status", nameof(FlowStatus.Running)), ("parallel", origin.MaxParallelSteps),
            ("parentRun", origin.ParentRunId?.ToString()), ("parentJob", origin.ParentJobId?.ToString()), ("depth", origin.Depth),
            ("forkedFrom", origin.ForkedFromRunId?.ToString()));
        var now = DateTimeOffset.FromUnixTimeMilliseconds(await NowAsync(connection, transaction, ct));
        foreach (var step in definition.Steps)
        {
            var job = new JobRecord(Guid.NewGuid(), step.Contract, step.Input, step.Queue,
                origin.CorrelationId, JobStatus.Waiting, 0, step.MaxAttempts, now, now, WorkflowRunId: runId, StepId: step.Id)
            {
                PartitionKey = origin.PartitionKey,
                TenantId = origin.TenantId,
                TraceParent = origin.TraceParent
            };
            if (kept?.GetValueOrDefault(step.Id) is { } source)
            {
                job = job with { Status = source.Status, Payload = source.Payload, Output = source.Output };
            }

            await InsertJobAsync(connection, transaction, job, ct);
        }

        await AdvanceWorkflowAsync(connection, transaction, runId, ct);

        return runId;
    }

    /// <summary>
    /// Arms a step that just started waiting: a signal deadline, or a child run and the deadline for its outcome. A child that cannot
    /// start fails the step and returns false, so the caller plans the run again.
    /// </summary>
    private async Task<bool> ArmWaitAsync(DbConnection connection, DbTransaction transaction, Guid runId, int depth, JobRecord job, FlowStep step, long now,
        CancellationToken ct)
    {
        string name;
        TimeSpan timeout;
        if (step.Child is { } child)
        {
            var started = await StartChildAsync(connection, transaction, runId, depth, step.Loop is null ? job : Iteration(job, 1, null), child, ct);
            if (started is not { } childRun)
            {
                return false;
            }

            (name, timeout) = (FlowChild.SignalName(childRun), child.Timeout);
        }
        else
        {
            (name, timeout) = (step.Signal!.Name, step.Signal.Timeout);
        }

        await ExecuteAsync(connection, transaction, UpsertSql("dj_signal_waits", "job_id,run_id,name,deadline", "@job,@run,@name,@deadline", "job_id", "name,deadline"), ct,
            ("job", job.Id.ToString()), ("run", runId.ToString()), ("name", name), ("deadline", checked(now + (long)Math.Ceiling(timeout.TotalMilliseconds))));

        return true;
    }

    /// <summary>Starts the published child flow with the step's input and keys, or fails the step and returns null.</summary>
    private async Task<Guid?> StartChildAsync(DbConnection connection, DbTransaction transaction, Guid runId, int depth, JobRecord job, FlowChild child,
        CancellationToken ct)
    {
        var saved = await ScalarAsync(connection, transaction, """
            SELECT d.definition FROM dj_definitions d JOIN dj_definition_lifecycle l ON l.flow_key=d.flow_key AND l.version=d.version
            WHERE d.flow_key=@key AND d.version=@version AND l.status=@published
            """, ct, ("key", child.FlowKey), ("version", child.FlowVersion), ("published", nameof(DefinitionStatus.Published)));
        var error = saved is not string ? $"Child flow {VersionTarget(child.FlowKey, child.FlowVersion)} is not published."
            : depth >= FlowChild.MaximumDepth ? $"Child flows nest at most {FlowChild.MaximumDepth} deep."
            : null;
        if (error is not null)
        {
            await ExecuteAsync(connection, transaction, "UPDATE dj_jobs SET status=@status,error=@error WHERE id=@id AND status=@waiting", ct,
                ("status", nameof(JobStatus.Failed)), ("error", error), ("id", job.Id.ToString()), ("waiting", nameof(JobStatus.AwaitingSignal)));
            await JournalAsync(connection, transaction, job, await NowAsync(connection, transaction, ct), nameof(JobStatus.Failed), ct);

            return null;
        }

        var definition = JsonSerializer.Deserialize<FlowDefinition>((string)saved!, JobJson.Options)!;
        var childRun = await InsertRunAsync(connection, transaction, definition, job.Payload,
            new(job.CorrelationId, job.PartitionKey, job.TenantId, null, job.TraceParent, runId, job.Id, depth + 1), ct);
        await JournalAsync(connection, transaction, job, await NowAsync(connection, transaction, ct), ExecutionEventKinds.ChildFlowStarted, ct);

        return childRun;
    }

    /// <summary>
    /// Records a finished child run's outcome as a signal on its parent. The parent advances under its own flow lock when signals are
    /// dispatched, so no transaction locks a child flow and then its parent.
    /// </summary>
    private async Task NotifyParentAsync(DbConnection connection, DbTransaction transaction, Guid runId, Guid parentRunId, FlowDefinition definition,
        IEnumerable<JobRecord> jobs, FlowPlan plan, long now, CancellationToken ct)
    {
        ChildOutcome outcome;
        if (plan.Status == FlowStatus.Succeeded)
        {
            var steps = jobs.Concat(plan.Changes).GroupBy(job => job.StepId!).ToDictionary(group => group.Key, group => group.Last());
            var dependents = definition.Steps.SelectMany(step => step.After).ToHashSet(StringComparer.Ordinal);
            var output = new JsonObject();
            foreach (var step in definition.Steps.Where(step => !dependents.Contains(step.Id)))
            {
                output[step.Id] = steps[step.Id].Output is { } value ? JsonNode.Parse(value.GetRawText()) : null;
            }

            outcome = new(ChildSucceeded, JsonSerializer.SerializeToElement(output, JobJson.Options), null);
        }
        else
        {
            outcome = new(nameof(FlowStatus.Failed), null, plan.Error ?? "The child flow failed.");
        }

        var name = FlowChild.SignalName(runId);

        var payload = await StoredAsync(connection, transaction, JsonSerializer.SerializeToElement(outcome, JobJson.Options), ct);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO dj_signals(run_id,name,payload,received_at) SELECT @run,@name,@payload,@now FROM dj_registry
            WHERE id=1 AND NOT EXISTS (SELECT 1 FROM dj_signals WHERE run_id=@run AND name=@name)
            """, ct, ("run", parentRunId.ToString()), ("name", name), ("payload", payload), ("now", now));
    }

    /// <summary>A loop step's job with its iteration number and the previous iteration's output added to its input.</summary>
    private static JobRecord Iteration(JobRecord job, int iteration, string? previous)
    {
        var input = JsonNode.Parse(job.Payload.GetRawText())!;
        input[FlowLoop.IterationProperty] = iteration;
        input[FlowLoop.PreviousProperty] = previous is null ? null : JsonNode.Parse(previous);

        return job with { Payload = JsonSerializer.SerializeToElement(input, JobJson.Options) };
    }

    /// <summary>
    /// Starts the next iteration of a loop step whose child succeeded without meeting the condition, and returns true; returns false
    /// when the condition holds or no iteration is left, so the caller settles the step.
    /// </summary>
    private async Task<bool> ContinueLoopAsync(DbConnection connection, DbTransaction transaction, Guid runId, int depth, JobRecord job, FlowStep step,
        string? output, long now, CancellationToken ct)
    {
        var loop = step.Loop!;
        var value = output is null ? null : JsonNode.Parse(output)?[loop.UntilStep]?[loop.UntilProperty];
        if (JsonNode.DeepEquals(value, JsonNode.Parse(loop.Expected.GetRawText())))
        {
            return false;
        }

        var iterations = Convert.ToInt32(await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM dj_flows WHERE parent_job_id=@job", ct,
            ("job", job.Id.ToString())));
        if (iterations >= loop.MaximumIterations)
        {
            return false;
        }

        var next = await StartChildAsync(connection, transaction, runId, depth, Iteration(job, iterations + 1, output), step.Child!, ct);
        if (next is { } childRun)
        {
            await ExecuteAsync(connection, transaction, UpsertSql("dj_signal_waits", "job_id,run_id,name,deadline", "@job,@run,@name,@deadline", "job_id", "name,deadline"), ct,
                ("job", job.Id.ToString()), ("run", runId.ToString()), ("name", FlowChild.SignalName(childRun)),
                ("deadline", checked(now + (long)Math.Ceiling(step.Child!.Timeout.TotalMilliseconds))));
        }

        return true;
    }

    /// <summary>Reads a child outcome: the step's output when the child succeeded, otherwise the failure to record.</summary>
    private static (bool Succeeded, string? Output, string? Error) ReadChildOutcome(JsonElement payload)
    {
        var outcome = payload.Deserialize<ChildOutcome>(JobJson.Options)!;

        return outcome.Status == ChildSucceeded
            ? (true, outcome.Output?.GetRawText(), null)
            : (false, null, outcome.Error);
    }
}
