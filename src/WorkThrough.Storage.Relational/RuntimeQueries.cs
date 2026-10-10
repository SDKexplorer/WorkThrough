using System.Data.Common;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore : IRuntimeQueries
{
    // Providers examine at most this many recent heartbeats, plus one to detect incomplete coverage.
    private const int MaximumReadinessWorkers = 500;

    protected virtual string ReadinessWorkersSql => throw new NotSupportedException("Worker readiness projection is unavailable.");

    public async Task<ExecutionReadiness> ReadReadinessAsync(Guid id, int workerExamples = ExecutionReadiness.DefaultWorkerExamples, CancellationToken cancellationToken = default)
    {
        ValidateOperationLimit(workerExamples);

        return await ReadOperationAsync<ExecutionReadiness>(async (connection, transaction, ct) =>
        {
            var (storeId, observed) = await ReadOperationContextAsync(connection, transaction, ct);
            var state = await ReadReadinessStateAsync(connection, transaction, id, ct);
            if (state is null)
            {
                return new(storeId, observed, id, false, ReadinessReasons.NotLive, false, null, false, null, null, null, null, null, [], 0, false, false);
            }

            var capacity = await ReadCapacityAsync(connection, transaction, state.Key, observed.ToUnixTimeMilliseconds(), ct);
            var workers = new List<ReadinessWorker>();
            await using (var command = OperationCommand(connection, transaction, ReadinessWorkersSql,
                ("id", id.ToString()), ("fresh", (observed - WorkerHeartbeat.Freshness).ToUnixTimeMilliseconds())))
            {
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    workers.Add(new(reader.GetString(0), Date(reader, "seen_at")!.Value,
                        Convert.ToInt32(reader.GetValue(2)), Convert.ToInt32(reader.GetValue(3)), reader.GetInt32(4) != 0,
                        reader.GetInt32(5) != 0, reader.GetInt32(6) != 0, reader.GetInt32(7) != 0,
                        reader.IsDBNull(8) ? null : Convert.ToInt32(reader.GetValue(8))));
                }
            }

            var incomplete = workers.Count > MaximumReadinessWorkers || workers.Any(worker => !worker.DetailsAvailable);
            var matching = workers.Where(worker => worker.Matches).ToArray();
            var reason = ExplainReadiness(state, capacity, matching, incomplete, observed);

            return new(storeId, observed, id, true, reason, ReadinessReasons.IsSuspectedStall(reason),
                state.FlowStatus, state.QueuePaused, state.AvailableAt, state.LeaseExpiresAt, state.Deadline, state.LastProgressAt,
                capacity, matching.Take(workerExamples).ToArray(), matching.Length, matching.Length > workerExamples, incomplete);
        }, cancellationToken);
    }

    private static string ExplainReadiness(ReadinessState state, CapacityObservation capacity,
        IReadOnlyList<ReadinessWorker> matching, bool incomplete, DateTimeOffset now)
    {
        if (state.Status == JobStatus.Running)
        {
            return ExplainRunning(state, now);
        }

        if (state.Status.IsTerminal())
        {
            return state.Status.ToString();
        }

        if (state.FlowStatus is FlowStatus.Incident or FlowStatus.Paused)
        {
            return state.FlowStatus == FlowStatus.Incident ? ReadinessReasons.FlowIncident : ReadinessReasons.FlowPaused;
        }

        if (state.Status is JobStatus.AwaitingSignal or JobStatus.Waiting)
        {
            return state.Status == JobStatus.AwaitingSignal ? ReadinessReasons.AwaitingSignal : ReadinessReasons.Dependencies;
        }

        if (state.ExpiresAt <= now)
        {
            return ReadinessReasons.PendingExpiry;
        }

        if (state.QueuePaused)
        {
            return ReadinessReasons.QueuePaused;
        }

        if (state.AvailableAt > now)
        {
            return state.Attempt > 0 ? ReadinessReasons.RetryScheduled : ReadinessReasons.Scheduled;
        }

        if (capacity.GlobalMaximum <= capacity.GlobalAdmitted || capacity.ProcessorMaximum <= capacity.ProcessorAdmitted)
        {
            return ReadinessReasons.SharedCapacity;
        }

        return incomplete ? ReadinessReasons.WorkerCoverageIncomplete : ExplainWorkers(matching);
    }

    private static string ExplainRunning(ReadinessState state, DateTimeOffset now)
    {
        if (state.LeaseExpiresAt <= now)
        {
            return ReadinessReasons.LeaseExpired;
        }

        if (state.Deadline <= now)
        {
            return ReadinessReasons.ExecutionDeadlineExceeded;
        }

        var overdue = state.LastProgressAt is { } progress && state.ProgressTimeout is { } threshold
            && now.ToUnixTimeMilliseconds() - progress.ToUnixTimeMilliseconds() >= threshold;

        return overdue ? ReadinessReasons.ProgressOverdue : ReadinessReasons.Running;
    }

    private static string ExplainWorkers(IReadOnlyList<ReadinessWorker> matching)
    {
        if (matching.Count == 0)
        {
            return ReadinessReasons.NoRecentCapableWorker;
        }

        var acquiring = matching.Where(worker => !worker.Draining).ToArray();
        if (acquiring.Length == 0)
        {
            return ReadinessReasons.WorkersDraining;
        }

        if (acquiring.All(worker => worker.AcquisitionFailures > 0))
        {
            return ReadinessReasons.AcquisitionFailing;
        }

        // Per-processor local limits and future acquisition success are not proved by a heartbeat.
        return acquiring.All(worker => worker.Running >= worker.Capacity) ? ReadinessReasons.WorkerCapacity : ReadinessReasons.AwaitingAcquisition;
    }

    private async Task<ReadinessState?> ReadReadinessStateAsync(DbConnection connection, DbTransaction transaction, Guid id, CancellationToken ct)
    {
        await using var command = OperationCommand(connection, transaction, """
            SELECT j.job_key,j.status,j.attempt,j.available_at,j.expires_at,j.lease_expires_at,f.status AS flow_status,
              COALESCE(q.paused,0) AS paused,p.deadline,p.last_progress_at,p.progress_timeout
            FROM dj_jobs j LEFT JOIN dj_flows f ON f.id=j.workflow_run_id
            LEFT JOIN dj_queue_controls q ON q.queue=j.queue
            LEFT JOIN dj_progress p ON p.job_id=j.id AND p.lease_token=j.lease_token AND j.status='Running'
            WHERE j.id=@id
            """, ("id", id.ToString()));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var flowStatus = Text(reader, "flow_status") is { } flow ? Enum.Parse<FlowStatus>(flow) : (FlowStatus?)null;

        return new(reader.GetString(0), Enum.Parse<JobStatus>(reader.GetString(1)), reader.GetInt32(2), Date(reader, "available_at")!.Value,
            Date(reader, "expires_at"), Date(reader, "lease_expires_at"), flowStatus, reader.GetInt32(7) != 0,
            Date(reader, "deadline"), Date(reader, "last_progress_at"), reader.IsDBNull(10) ? null : reader.GetInt64(10));
    }

    private async Task<CapacityObservation> ReadCapacityAsync(DbConnection connection, DbTransaction transaction, string key, long now, CancellationToken ct)
    {
        await using var command = OperationCommand(connection, transaction, """
            SELECT (SELECT maximum FROM dj_limits WHERE job_key=@all),
              (SELECT COUNT(*) FROM dj_jobs WHERE status='Running' AND lease_expires_at>@now),
              (SELECT MIN(maximum) FROM dj_limits l WHERE l.job_key<>@all AND
                COALESCE((SELECT group_key FROM dj_processor_groups WHERE job_key=l.job_key),l.job_key)=
                COALESCE((SELECT group_key FROM dj_processor_groups WHERE job_key=@key),@key)),
              (SELECT COUNT(*) FROM dj_jobs j WHERE j.status='Running' AND j.lease_expires_at>@now AND
                COALESCE((SELECT group_key FROM dj_processor_groups WHERE job_key=j.job_key),j.job_key)=
                COALESCE((SELECT group_key FROM dj_processor_groups WHERE job_key=@key),@key))
            """, ("key", key), ("now", now), ("all", AllJobsLimitKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return new(reader.IsDBNull(0) ? null : reader.GetInt32(0), Convert.ToInt64(reader.GetValue(1)),
            reader.IsDBNull(2) ? null : reader.GetInt32(2), Convert.ToInt64(reader.GetValue(3)));
    }

    public async Task<RuntimeMetrics> ReadRuntimeMetricsAsync(RuntimeMetricsQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return await ReadOperationAsync<RuntimeMetrics>(async (connection, transaction, ct) =>
        {
            var (storeId, observed) = await ReadOperationContextAsync(connection, transaction, ct);
            var until = CeilingMilliseconds(query.Until ?? observed);
            var from = CeilingMilliseconds(query.From ?? DateTimeOffset.FromUnixTimeMilliseconds(until) - DefaultMetricsWindow);
            ValidateWindow(from, until);
            RuntimeMetrics result;
            await using (var command = OperationCommand(connection, transaction, $"""
                SELECT COUNT(*),COALESCE(SUM(CASE WHEN status='Succeeded' THEN 1 ELSE 0 END),0),
                  COALESCE(SUM(CASE WHEN status='Failed' THEN 1 ELSE 0 END),0),
                  AVG(CAST(finished_at-started_at AS {FloatingPointType})),MAX(finished_at-started_at)
                FROM dj_attempts WHERE finished_at>=@from AND finished_at<@until
                """, ("from", from), ("until", until)))
            {
                await using var reader = await command.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                result = new(storeId, observed, DateTimeOffset.FromUnixTimeMilliseconds(from), DateTimeOffset.FromUnixTimeMilliseconds(until),
                    Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)), Convert.ToInt64(reader.GetValue(2)),
                    reader.IsDBNull(3) ? null : Convert.ToDouble(reader.GetValue(3)), reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4)));
            }

            return result;
        }, cancellationToken);
    }

    private static readonly TimeSpan DefaultMetricsWindow = TimeSpan.FromHours(1);

    private sealed record ReadinessState(string Key, JobStatus Status, int Attempt, DateTimeOffset AvailableAt,
        DateTimeOffset? ExpiresAt, DateTimeOffset? LeaseExpiresAt, FlowStatus? FlowStatus, bool QueuePaused,
        DateTimeOffset? Deadline, DateTimeOffset? LastProgressAt, long? ProgressTimeout);
}
