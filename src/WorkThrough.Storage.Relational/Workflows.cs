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

        // Unpartitioned starts keep the fingerprint shape of earlier releases, so existing receipts still match.
        var fingerprint = idempotencyKey is null ? null
            : partitionKey is null ? Fingerprint(new { definition, input, correlationId })
            : Fingerprint(new { definition, input, correlationId, partitionKey });

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
            var runId = Guid.NewGuid();
            await ExecuteAsync(connection, transaction, """
                INSERT INTO dj_flows(id,flow_key,version,input,correlation_id,status)
                VALUES (@id,@key,@version,@input,@correlation,@status)
                """, ct, ("id", runId.ToString()), ("key", definition.Key), ("version", definition.Version),
                ("input", input.GetRawText()), ("correlation", correlationId), ("status", nameof(FlowStatus.Running)));
            var now = DateTimeOffset.FromUnixTimeMilliseconds(await NowAsync(connection, transaction, ct));
            foreach (var step in definition.Steps)
            {
                await InsertJobAsync(connection, transaction, new(Guid.NewGuid(), step.Contract, step.Input, step.Queue,
                    correlationId, JobStatus.Waiting, 0, step.MaxAttempts, now, now, WorkflowRunId: runId, StepId: step.Id)
                {
                    PartitionKey = partitionKey
                }, ct);
            }

            await AdvanceWorkflowAsync(connection, transaction, runId, ct);
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
        FlowDefinition definition;
        JsonElement input;
        await using (var command = Command(connection, transaction, """
            SELECT f.input,d.definition FROM dj_flows f JOIN dj_definitions d
            ON f.flow_key=d.flow_key AND f.version=d.version WHERE f.id=@id AND f.status='Running'
            """, ("id", runId.ToString())))
        {
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct))
                {
                    return;
                }

                input = Json(reader.GetString(0));
                definition = JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(1), JobJson.Options)!;
            }
        }

        for (var pass = 0; pass <= definition.Steps.Length; pass++)
        {
            var jobs = await ReadJobsAsync(connection, transaction, "SELECT * FROM dj_jobs WHERE workflow_run_id=@id", ct, ("id", runId.ToString()));
            var plan = FlowPlanner.Advance(definition, input, jobs);
            var now = await NowAsync(connection, transaction, ct);
            foreach (var change in plan.Changes)
            {
                await ExecuteAsync(connection, transaction,
                    "UPDATE dj_jobs SET status=@status,payload=@payload,error=@error,available_at=CASE WHEN @status='Queued' THEN @now ELSE available_at END WHERE id=@id", ct,
                    ("status", change.Status.ToString()), ("payload", change.Payload.GetRawText()), ("error", change.Error), ("id", change.Id.ToString()), ("now", now));
                if (change.Status == JobStatus.AwaitingSignal)
                {
                    var signal = definition.Steps.Single(s => s.Id == change.StepId).Signal!;
                    await ExecuteAsync(connection, transaction, UpsertSql("dj_signal_waits", "job_id,run_id,name,deadline", "@job,@run,@name,@deadline", "job_id", "deadline"), ct,
                        ("job", change.Id.ToString()), ("run", runId.ToString()), ("name", signal.Name), ("deadline", checked(now + (long)Math.Ceiling(signal.Timeout.TotalMilliseconds))));
                }

                await JournalAsync(connection, transaction, change, now, change.Status.ToString(), ct);
            }

            await SetFlowStatusAsync(connection, transaction, runId, plan.Status, ct, plan.Error);
            if (plan.Status != FlowStatus.Running || !await ResolveRunSignalsAsync(connection, transaction, runId, now, ct))
            {
                return;
            }
        }

        throw new InvalidOperationException("Signal advancement exceeded the acyclic graph bound.");
    }
}
