using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore
{
    private const int MaximumProgressDescriptionLength = 500;
    private const int MaximumWorkerRows = 500;
    private const int MaximumFlowEvents = 500;

    public Task<bool> FailWithDetailsAsync(Guid id, Guid leaseToken, JobFailure failure, TimeSpan retryDelay, CancellationToken cancellationToken = default) =>
        SettleAsync(id, leaseToken, new(Error: failure.ToSummary(), Delay: retryDelay, Failure: failure), cancellationToken);

    public Task<bool> BeginAttemptAsync(Guid id, Guid leaseToken, TimeSpan? executionTimeout, TimeSpan? progressTimeout, CancellationToken cancellationToken = default)
    {
        if (executionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(executionTimeout));
        }

        if (progressTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(progressTimeout));
        }

        return WriteAsync(async (connection, transaction, ct) =>
        {
            var now = await NowAsync(connection, transaction, ct);
            if (!await OwnsAsync(connection, transaction, id, leaseToken, now, ct))
            {
                return false;
            }

            var upsert = UpsertSql("dj_progress", "job_id,lease_token,started_at,last_progress_at,deadline,progress_timeout,description",
                "@id,@token,@now,@now,@deadline,@timeout,NULL", "job_id", "lease_token,started_at,last_progress_at,deadline,progress_timeout,description");
            await ExecuteAsync(connection, transaction, upsert, ct, ("id", id.ToString()), ("token", leaseToken.ToString()), ("now", now),
                ("deadline", executionTimeout is { } timeout ? checked(now + (long)timeout.TotalMilliseconds) : null),
                ("timeout", progressTimeout is { } progress ? (long)progress.TotalMilliseconds : null));

            return true;
        }, cancellationToken);
    }

    public Task<bool> ReportProgressAsync(Guid id, Guid leaseToken, string? description, CancellationToken cancellationToken = default) =>
        WriteAsync(async (connection, transaction, ct) =>
        {
            var now = await NowAsync(connection, transaction, ct);
            if (!await OwnsAsync(connection, transaction, id, leaseToken, now, ct))
            {
                return false;
            }

            return await ExecuteAsync(connection, transaction,
                "UPDATE dj_progress SET last_progress_at=@now,description=@description WHERE job_id=@id AND lease_token=@token", ct,
                ("now", now), ("description", Clip(description, MaximumProgressDescriptionLength)), ("id", id.ToString()), ("token", leaseToken.ToString())) == 1;
        }, cancellationToken);

    private async Task<bool> OwnsAsync(DbConnection connection, DbTransaction transaction, Guid id, Guid token, long now, CancellationToken ct) =>
        Convert.ToInt64(await ScalarAsync(connection, transaction,
            "SELECT COUNT(*) FROM dj_jobs WHERE id=@id AND lease_token=@token AND status='Running' AND lease_expires_at>@now", ct,
            ("id", id.ToString()), ("token", token.ToString()), ("now", now))) == 1;

    public Task<IReadOnlyList<AttemptFailure>> FailuresAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return ReadAsync<IReadOnlyList<AttemptFailure>>(async (connection, transaction, ct) =>
        {
            return await ReadFailuresAsync(connection, transaction, id, ct);
        }, cancellationToken);
    }

    private async Task<List<AttemptFailure>> ReadFailuresAsync(DbConnection connection, DbTransaction? transaction, Guid id, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT attempt,exception_type,message,stack_trace FROM dj_failures WHERE job_id=@id ORDER BY attempt", ("id", id.ToString()));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var failures = new List<AttemptFailure>();
        while (await reader.ReadAsync(ct))
        {
            failures.Add(new(reader.GetInt32(0), new(reader.GetString(1), reader.GetString(2), Text(reader, "stack_trace"))));
        }

        return failures;
    }

    /// <summary>Uses the same explanation as <see cref="ReadReadinessAsync"/>, plus the owned attempt's progress record.</summary>
    public async Task<JobHealth?> HealthAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var readiness = await ReadReadinessAsync(id, cancellationToken: cancellationToken);
        if (!readiness.ExecutionExists)
        {
            return null;
        }

        return await ReadAsync<JobHealth?>(async (connection, transaction, ct) =>
        {
            await using var command = Command(connection, transaction, """
                SELECT p.started_at,p.last_progress_at,p.deadline,p.progress_timeout,p.description
                FROM dj_progress p JOIN dj_jobs j ON j.id=p.job_id AND j.lease_token=p.lease_token
                WHERE p.job_id=@id AND j.status='Running'
                """, ("id", id.ToString()));
            await using var reader = await command.ExecuteReaderAsync(ct);
            JobProgress? progress = null;
            if (await reader.ReadAsync(ct))
            {
                var timeout = reader.IsDBNull(3) ? (TimeSpan?)null : TimeSpan.FromMilliseconds(reader.GetInt64(3));
                progress = new(Date(reader, "started_at")!.Value, Date(reader, "last_progress_at")!.Value, Date(reader, "deadline"), timeout, Text(reader, "description"));
            }

            return new(readiness.Reason, readiness.SuspectedStalled, readiness.ObservedAt, progress);
        }, cancellationToken);
    }

    public Task HeartbeatAsync(WorkerHeartbeat worker, CancellationToken cancellationToken = default) => WriteAsync(async (connection, transaction, ct) =>
    {
        var now = await NowAsync(connection, transaction, ct);
        var observed = worker with { SeenAt = DateTimeOffset.FromUnixTimeMilliseconds(now) };
        await ExecuteAsync(connection, transaction, UpsertSql("dj_workers", "id,seen_at,details", "@id,@now,@details", "id", "seen_at,details"), ct,
            ("id", worker.WorkerId), ("now", now), ("details", JsonSerializer.Serialize(observed, JobJson.Options)));

        return true;
    }, cancellationToken);

    public Task<IReadOnlyList<WorkerHeartbeat>> WorkersAsync(CancellationToken cancellationToken = default)
    {
        return ReadAsync<IReadOnlyList<WorkerHeartbeat>>(async (connection, transaction, ct) =>
        {
            await using var command = Command(connection, transaction, $"SELECT details FROM dj_workers ORDER BY seen_at DESC {PageSql(MaximumWorkerRows)}");
            await using var reader = await command.ExecuteReaderAsync(ct);
            var workers = new List<WorkerHeartbeat>();
            while (await reader.ReadAsync(ct))
            {
                workers.Add(JsonSerializer.Deserialize<WorkerHeartbeat>(reader.GetString(0), JobJson.Options)!);
            }

            return workers;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ExecutionEvent>> EventsAsync(Guid runId, long after = 0, CancellationToken cancellationToken = default)
    {
        return ReadAsync<IReadOnlyList<ExecutionEvent>>(async (connection, transaction, ct) =>
        {
            await using var command = Command(connection, transaction, $"SELECT {EventColumns} FROM dj_events WHERE run_id=@run AND sequence>@after ORDER BY sequence {PageSql(MaximumFlowEvents)}",
                ("run", runId.ToString()), ("after", after));

            return await ReadEventsAsync(command, ct);
        }, cancellationToken);
    }
}
