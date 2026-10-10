using System.Data.Common;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore : IExecutionOperations
{
    private const int MaximumControlRows = 500;
    private const int MaximumControlKeyLength = 256;
    private const string QueueExpiredError = "The queued execution expired before admission";

    public async Task<IReadOnlyList<QueueControl>> QueuesAsync(CancellationToken cancellationToken = default)
    {
        var rows = await ReadControlsAsync("SELECT queue,paused,revision FROM dj_queue_controls ORDER BY queue", cancellationToken);

        return rows.Select(row => new QueueControl(row.Key, row.Enabled, row.Revision)).ToArray();
    }

    public async Task<IReadOnlyList<WorkerControlState>> WorkerControlsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await ReadControlsAsync("SELECT worker_id,draining,revision FROM dj_worker_controls ORDER BY worker_id", cancellationToken);

        return rows.Select(row => new WorkerControlState(row.Key, row.Enabled, row.Revision)).ToArray();
    }

    private Task<List<(string Key, bool Enabled, int Revision)>> ReadControlsAsync(string sql, CancellationToken cancellationToken) =>
        ReadAsync(async (connection, transaction, ct) =>
        {
            await using var command = Command(connection, transaction, $"{sql} {PageSql(MaximumControlRows)}");
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<(string Key, bool Enabled, int Revision)>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetString(0), reader.GetInt32(1) != 0, reader.GetInt32(2)));
            }

            return rows;
        }, cancellationToken);

    public Task<Result<int>> SetQueuePausedAsync(string queue, bool paused, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        SetControlAsync(new("dj_queue_controls", "queue", "paused"), queue, paused, expectedRevision, command,
            paused ? AuditActions.PauseQueue : AuditActions.ResumeQueue, cancellationToken);

    public Task<Result<int>> SetWorkerDrainingAsync(string workerId, bool draining, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        SetControlAsync(new("dj_worker_controls", "worker_id", "draining"), workerId, draining, expectedRevision, command,
            draining ? AuditActions.DrainWorker : AuditActions.ResumeWorker, cancellationToken);

    private sealed record ControlTable(string Name, string KeyColumn, string ValueColumn);

    private Task<Result<int>> SetControlAsync(ControlTable table, string key, bool enabled, int expectedRevision,
        AdministrativeCommand command, string action, CancellationToken ct) =>
        AdminAsync(command, action, key, new { enabled, expectedRevision }, async (connection, transaction, token) =>
        {
            var previous = await ScalarAsync(connection, transaction, $"SELECT revision FROM {table.Name} WHERE {table.KeyColumn}=@key", token, ("key", key));
            if (Convert.ToInt32(previous) != expectedRevision)
            {
                throw new StateConflictException("Control revision changed.");
            }

            var next = checked(expectedRevision + 1);
            var upsert = UpsertSql(table.Name, $"{table.KeyColumn},{table.ValueColumn},revision", "@key,@value,@revision", table.KeyColumn, $"{table.ValueColumn},revision");
            await ExecuteAsync(connection, transaction, upsert, token, ("key", key), ("value", enabled ? 1 : 0), ("revision", next));

            return next;
        }, ct, validate: () => ValidateControl(key, expectedRevision));

    private static void ValidateControl(string key, int expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaximumControlKeyLength || expectedRevision < 0)
        {
            throw new ArgumentException($"A control needs a 1–{MaximumControlKeyLength} character key and non-negative revision.");
        }
    }

    public Task<Result<Guid>> ReplayAsync(Guid jobId, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, AuditActions.ReplayJob, jobId.ToString(), new { jobId }, async (connection, transaction, ct) =>
        {
            var original = await ReadJobAsync(connection, transaction, jobId, ct)
                ?? throw new MissingStateException("Execution does not exist in the active store. Export archived input explicitly before starting new work.");
            if (!original.Status.IsTerminal() || original.Contract.Key == SignalWait.Processor.Key)
            {
                throw new StateConflictException("Only terminal processor executions can be replayed.");
            }

            var now = DateTimeOffset.FromUnixTimeMilliseconds(await NowAsync(connection, transaction, ct));
            var replay = original with
            {
                Id = Guid.NewGuid(),
                CausationJobId = original.Id,
                Status = JobStatus.Queued,
                Attempt = 0,
                CreatedAt = now,
                AvailableAt = now,
                ExpiresAt = null,
                IdempotencyKey = null,
                WorkflowRunId = null,
                StepId = null,
                Output = null,
                Error = null,
                WorkerId = null,
                LeaseToken = null,
                LeaseExpiresAt = null
            };
            await InsertJobAsync(connection, transaction, replay, ct);
            await JournalAsync(connection, transaction, replay, now.ToUnixTimeMilliseconds(), ExecutionEventKinds.Replayed, ct);

            return replay.Id;
        }, cancellationToken);

    private async Task ExpireQueuedAsync(DbConnection connection, DbTransaction transaction, long now, CancellationToken ct)
    {
        var expired = await ReadJobsAsync(connection, transaction,
            $"SELECT * FROM dj_jobs WHERE status='Queued' AND expires_at<=@now ORDER BY expires_at {PageSql(SweepBatchSize)}", ct, ("now", now));
        foreach (var job in expired)
        {
            await ExecuteAsync(connection, transaction, "UPDATE dj_jobs SET status=@status,error=@error WHERE id=@id", ct,
                ("status", nameof(JobStatus.Expired)), ("error", QueueExpiredError), ("id", job.Id.ToString()));
            await JournalAsync(connection, transaction, job, now, nameof(JobStatus.Expired), ct);
            if (job.WorkflowRunId is { } flow)
            {
                await AdvanceWorkflowAsync(connection, transaction, flow, ct);
            }
        }
    }
}
