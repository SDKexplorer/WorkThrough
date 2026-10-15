using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore
{
    public Task<Guid> StartWorkflowAsync(FlowDefinition definition, JsonElement input, string? correlationId, CancellationToken cancellationToken = default)
        => StartWorkflowAsync(definition, input, new FlowStart(correlationId), cancellationToken);

    public Task<Guid> StartWorkflowAsync(FlowDefinition definition, JsonElement input, string? correlationId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        WorkflowClient.ValidateIdempotencyKey(idempotencyKey);

        return StartWorkflowAsync(definition, input, new FlowStart(correlationId, idempotencyKey), cancellationToken);
    }

    public Task<Guid> StartWorkflowAsync(FlowDefinition definition, JsonElement input, FlowStart start, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        FlowValidator.Validate(definition);
        start.Validate();
        var (correlationId, idempotencyKey, partitionKey) = (start.CorrelationId, start.IdempotencyKey, start.PartitionKey);
        var (tenantId, maxParallelSteps) = (start.TenantId, start.MaxParallelSteps);
        var traceParent = TraceContext.Capture();

        // Starts without admission options keep the fingerprint shape of earlier releases, so existing receipts still match.
        var fingerprint = idempotencyKey is null ? null
            : start.NotBefore is { } due
                ? Fingerprint(Args(("definition", definition), ("input", input), ("correlationId", correlationId), ("partitionKey", partitionKey),
                    ("tenantId", tenantId), ("maxParallelSteps", maxParallelSteps), ("notBefore", due)))
            : tenantId is null && maxParallelSteps is null
                ? partitionKey is null ? Fingerprint(Args(("definition", definition), ("input", input), ("correlationId", correlationId))) : Fingerprint(Args(("definition", definition), ("input", input), ("correlationId", correlationId), ("partitionKey", partitionKey)))
                : Fingerprint(Args(("definition", definition), ("input", input), ("correlationId", correlationId), ("partitionKey", partitionKey), ("tenantId", tenantId), ("maxParallelSteps", maxParallelSteps)));

        return WriteAsync(async (connection, transaction, ct) =>
        {
            // Check the receipt before lifecycle rules: retirement must not hide an already accepted run.
            if (idempotencyKey is not null)
            {
                await using var command = Command(connection, transaction,
                    "SELECT fingerprint,run_id FROM dj_flow_starts WHERE idempotency_key=@key", ("key", idempotencyKey));
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    if (reader.GetString(0) != fingerprint)
                    {
                        throw new StateConflictException("Flow idempotency key is already associated with a different request.");
                    }

                    return Guid.Parse(reader.GetString(1));
                }
            }

            await RequireDefinitionProtocolAsync(connection, transaction, definition, ct);
            await PublishForStartAsync(connection, transaction, definition, [], ct);
            var runId = await InsertRunAsync(connection, transaction, definition, input,
                new(correlationId, partitionKey, tenantId, maxParallelSteps, traceParent), ct);
            if (start.NotBefore is { } notBefore)
            {
                // Eligibility rounds up to the millisecond, so a run never starts before its instant.
                var due = notBefore.ToUnixTimeMilliseconds() + (notBefore.UtcTicks % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1);
                await ExecuteAsync(connection, transaction,
                    "UPDATE dj_jobs SET available_at=@due WHERE workflow_run_id=@run AND status='Queued' AND available_at<@due", ct,
                    ("due", due), ("run", runId.ToString()));
            }

            if (idempotencyKey is not null)
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO dj_flow_starts(idempotency_key,fingerprint,run_id) VALUES (@key,@fingerprint,@run)", ct,
                    ("key", idempotencyKey), ("fingerprint", fingerprint), ("run", runId.ToString()));
            }

            return runId;
        }, cancellationToken);
    }

    private async Task AdvanceWorkflowAsync(DbConnection connection, DbTransaction transaction, Guid runId, CancellationToken ct)
    {
        // Callers that change steps first lock the flow before them; this covers callers that change only the flow or its signals.
        await LockFlowAsync(connection, transaction, runId, skipLocked: false, ct);
        FlowDefinition definition;
        JsonElement input;
        Guid? parentRun;
        int depth;
        await using (var command = Command(connection, transaction, """
            SELECT f.input,d.definition,f.parent_run_id,f.depth FROM dj_flows f JOIN dj_definitions d
            ON f.flow_key=d.flow_key AND f.version=d.version WHERE f.id=@id AND f.status='Running'
            """, ("id", runId.ToString())))
        {
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct))
                {
                    return;
                }

                input = Required(Json(reader.GetString(0)));
                definition = JobJson.Deserialize<FlowDefinition>(reader.GetString(1))!;
                parentRun = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2));
                depth = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3));
            }
        }

        for (var pass = 0; pass <= definition.Steps.Length; pass++)
        {
            var jobs = await ReadJobsAsync(connection, transaction, "SELECT * FROM dj_jobs WHERE workflow_run_id=@id", ct, ("id", runId.ToString()));
            var read = jobs.ToDictionary(job => job.Id, job => job.Status);
            foreach (var output in jobs.Select(job => job.Output).OfType<JsonElement>())
            {
                Required(output);
            }

            var plan = FlowPlanner.Advance(definition, input, jobs);
            var now = await NowAsync(connection, transaction, ct);
            var replan = false;
            foreach (var change in plan.Changes)
            {
                // A claim may lease a queued step that a failing flow is skipping; the lease then stands, as if it came first.
                if (await ExecuteAsync(connection, transaction, """
                    UPDATE dj_jobs SET status=@status,payload=@payload,error=@error,output=@output,
                    available_at=CASE WHEN @status='Queued' THEN @now ELSE available_at END
                    WHERE id=@id AND status=@read
                    """, ct, ("status", change.Status.ToString()), ("payload", await StoredAsync(connection, transaction, change.Payload, ct)), ("error", change.Error), ("id", change.Id.ToString()),
                    ("now", now), ("read", read[change.Id].ToString()), ("output", await StoredAsync(connection, transaction, change.Output, ct))) == 0)
                {
                    continue;
                }

                await JournalAsync(connection, transaction, change, now, change.Status.ToString(), ct);
                if (change.Status == JobStatus.AwaitingSignal)
                {
                    replan |= !await ArmWaitAsync(connection, transaction, runId, depth, change, definition.Steps.Single(s => s.Id == change.StepId), now, ct);
                }
            }

            // Item steps of a fan-out are created queued; the flow row lock keeps a second expansion out.
            foreach (var item in plan.Added)
            {
                var created = DateTimeOffset.FromUnixTimeMilliseconds(now);
                await InsertJobAsync(connection, transaction, item with { CreatedAt = created, AvailableAt = created }, ct);
                await JournalAsync(connection, transaction, item, now, item.Status.ToString(), ct);
                if (item.Status == JobStatus.AwaitingSignal)
                {
                    var step = definition.Steps.Single(s => s.Id == FlowFanOut.BaseStepId(item.StepId!));
                    replan |= !await ArmWaitAsync(connection, transaction, runId, depth, item, step, now, ct);
                }
            }

            await SetFlowStatusAsync(connection, transaction, runId, plan.Status, ct, plan.Error);
            if (parentRun is { } parent && plan.Status is FlowStatus.Succeeded or FlowStatus.Failed)
            {
                await NotifyParentAsync(connection, transaction, runId, parent, definition, jobs, plan, now, ct);
            }

            if (plan.Status != FlowStatus.Running || !(replan | await ResolveRunSignalsAsync(connection, transaction, runId, definition, depth, now, ct)))
            {
                return;
            }
        }

        throw new InvalidOperationException("Signal advancement exceeded the acyclic graph bound.");
    }
}
