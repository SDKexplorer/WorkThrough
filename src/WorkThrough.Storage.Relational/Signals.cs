using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore : IWorkflowSignals
{
    private const int MaximumSignalBatch = 100;
    private const string SignalDeadlineError = "Signal deadline expired";

    public Task<bool> SignalAsync(Guid runId, string name, JsonElement input, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        _ = new JobContractId(name, 1);
        WorkflowClient.ValidateIdempotencyKey(idempotencyKey);
        if (input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || JobLimits.ExceedsInlineSize(input))
        {
            throw new ArgumentException("Signal input must be non-null and at most 1 MiB.");
        }

        var hash = Fingerprint(new { name, input });

        return WriteAsync(async (connection, transaction, ct) =>
        {
            await RequireProtocolAsync(connection, transaction, ct);
            var receipt = await ScalarAsync(connection, transaction, "SELECT fingerprint FROM dj_signal_receipts WHERE run_id=@run AND idempotency_key=@key", ct, ("run", runId.ToString()), ("key", idempotencyKey));
            if (receipt is string prior)
            {
                if (prior != hash)
                {
                    throw new StateConflictException("Signal receipt key was reused for different input.");
                }

                return true;
            }

            var saved = await ScalarAsync(connection, transaction, "SELECT d.definition FROM dj_flows f JOIN dj_definitions d ON f.flow_key=d.flow_key AND f.version=d.version WHERE f.id=@run AND f.status IN ('Running','Paused','Incident')", ct, ("run", runId.ToString()));
            if (saved is not string json || !JsonSerializer.Deserialize<FlowDefinition>(json, JobJson.Options)!.Steps.Any(s => s.Signal?.Name == name))
            {
                throw new StateConflictException("An active flow revision must declare this signal.");
            }

            var existing = await ScalarAsync(connection, transaction, "SELECT payload FROM dj_signals WHERE run_id=@run AND name=@name", ct, ("run", runId.ToString()), ("name", name));
            if (existing is not null)
            {
                throw new StateConflictException("This one-shot signal was already supplied; retry with its original receipt key.");
            }

            var now = await NowAsync(connection, transaction, ct);
            await ExecuteAsync(connection, transaction, "INSERT INTO dj_signals(run_id,name,payload,received_at) VALUES (@run,@name,@input,@now)", ct,
                ("run", runId.ToString()), ("name", name), ("input", input.GetRawText()), ("now", now));
            await ExecuteAsync(connection, transaction, "INSERT INTO dj_signal_receipts(run_id,idempotency_key,fingerprint) VALUES (@run,@key,@hash)", ct,
                ("run", runId.ToString()), ("key", idempotencyKey), ("hash", hash));
            await AdvanceWorkflowAsync(connection, transaction, runId, ct);

            return true;
        }, cancellationToken);
    }

    public Task<int> DispatchSignalsAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > MaximumSignalBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        return WriteAsync(async (connection, transaction, ct) => await DispatchSignalsCoreAsync(connection, transaction, await NowAsync(connection, transaction, ct), batchSize, ct), cancellationToken);
    }

    private async Task<int> DispatchSignalsCoreAsync(DbConnection connection, DbTransaction transaction, long now, int batchSize, CancellationToken ct)
    {
        var runs = new List<Guid>();
        await using (var command = Command(connection, transaction, $"""
            SELECT DISTINCT w.run_id FROM dj_signal_waits w JOIN dj_jobs j ON j.id=w.job_id JOIN dj_flows f ON f.id=w.run_id
            WHERE j.status='AwaitingSignal' AND f.status='Running' AND (w.deadline<=@now OR EXISTS (SELECT 1 FROM dj_signals s WHERE s.run_id=w.run_id AND s.name=w.name)) ORDER BY w.run_id {PageSql("@limit")}
            """, ("now", now), ("limit", batchSize)))
        {
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    runs.Add(Guid.Parse(reader.GetString(0)));
                }
            }
        }

        var advanced = 0;
        foreach (var run in runs)
        {
            // A flow that a settlement holds is advanced by that settlement or by a later sweep.
            if (await LockFlowAsync(connection, transaction, run, skipLocked: true, ct))
            {
                await AdvanceWorkflowAsync(connection, transaction, run, ct);
                advanced++;
            }
        }

        return advanced;
    }

    private async Task<bool> ResolveRunSignalsAsync(DbConnection connection, DbTransaction transaction, Guid run, long now, CancellationToken ct)
    {
        var changes = new List<(Guid Job, string? Input)>();
        await using (var command = Command(connection, transaction, """
            SELECT w.job_id,CASE WHEN s.received_at<=w.deadline THEN s.payload ELSE NULL END AS input
            FROM dj_signal_waits w JOIN dj_jobs j ON j.id=w.job_id LEFT JOIN dj_signals s ON s.run_id=w.run_id AND s.name=w.name
            WHERE w.run_id=@run AND j.status='AwaitingSignal' AND (s.received_at<=w.deadline OR w.deadline<=@now)
            """, ("run", run.ToString()), ("now", now)))
        {
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    changes.Add((Guid.Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : reader.GetString(1)));
                }
            }
        }

        foreach (var (jobId, signalInput) in changes)
        {
            var job = (await ReadJobAsync(connection, transaction, jobId, ct))!;
            var received = signalInput is not null;
            await ExecuteAsync(connection, transaction, "UPDATE dj_jobs SET status=@status,output=@input,error=@error WHERE id=@id", ct,
                ("id", jobId.ToString()), ("status", (received ? JobStatus.Succeeded : JobStatus.Expired).ToString()), ("input", signalInput),
                ("error", received ? null : SignalDeadlineError));
            await JournalAsync(connection, transaction, job, now, received ? ExecutionEventKinds.SignalReceived : ExecutionEventKinds.SignalExpired, ct);
        }

        return changes.Count > 0;
    }
}
