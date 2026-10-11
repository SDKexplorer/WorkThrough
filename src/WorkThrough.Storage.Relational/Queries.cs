using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore
{
    private const int MaximumListPage = 500;
    private const string EventColumns = "sequence,job_id,run_id,at,kind,attempt";

    public Task<IReadOnlyList<JobRecord>> ListFlowJobsAsync(Guid runId, CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<JobRecord>>(async (connection, transaction, ct) => await ReadJobsAsync(connection, transaction,
            "SELECT * FROM dj_jobs WHERE workflow_run_id=@run ORDER BY step_id", ct, ("run", runId.ToString())), cancellationToken);

    public Task<IReadOnlyList<WorkflowRun>> ChildRunsAsync(Guid runId, CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<WorkflowRun>>(async (connection, transaction, ct) =>
        {
            await using var command = Command(connection, transaction, $"""
                SELECT f.id,f.flow_key,f.version,f.correlation_id,f.status,f.error,f.parent_run_id,f.parent_job_id FROM dj_flows f
                JOIN dj_jobs j ON j.id=f.parent_job_id WHERE f.parent_run_id=@run ORDER BY j.step_id,f.id {PageSql(IJobQueries.MaximumChildRuns)}
                """, ("run", runId.ToString()));
            await using var reader = await command.ExecuteReaderAsync(ct);
            var runs = new List<WorkflowRun>();
            while (await reader.ReadAsync(ct))
            {
                runs.Add(ReadWorkflowRun(reader, Guid.Parse(reader.GetString(0))));
            }

            return runs;
        }, cancellationToken);

    public Task<JobRecord?> GetAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        ReadAsync((connection, transaction, ct) => ReadJobAsync(connection, transaction, jobId, ct), cancellationToken);

    /// <summary>A bounded any-of tenant condition; an empty set matches nothing.</summary>
    private static string TenantFilterSql(string column, IReadOnlyList<string> tenantIds, List<(string, object?)> values)
    {
        if (tenantIds.Count > TenantAccess.MaximumTenants)
        {
            throw new ArgumentException($"Tenant filters are limited to {TenantAccess.MaximumTenants} tenants.", nameof(tenantIds));
        }

        if (tenantIds.Count == 0)
        {
            return "1=0";
        }

        var names = tenantIds.Select((tenant, index) =>
        {
            AdmissionKeys.Validate(tenant, KeyScope.Tenant, nameof(tenantIds));
            values.Add(($"tenant{index}", tenant));

            return $"@tenant{index}";
        }).ToArray();

        return $"{column} IN ({string.Join(",", names)})";
    }

    public Task<IReadOnlyList<JobRecord>> ListAsync(JobFilter filter, CancellationToken cancellationToken = default)
    {
        if (filter.Limit is < 1 or > MaximumListPage || filter.Offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(filter));
        }

        var conditions = new List<string>();
        var values = new List<(string, object?)> { ("limit", filter.Limit), ("offset", filter.Offset) };
        if (filter.CorrelationId is not null)
        {
            conditions.Add("correlation_id=@correlation");
            values.Add(("correlation", filter.CorrelationId));
        }

        if (filter.Status is not null)
        {
            conditions.Add("status=@status");
            values.Add(("status", filter.Status.ToString()));
        }

        if (filter.TenantIds is not null)
        {
            conditions.Add(TenantFilterSql("tenant_id", filter.TenantIds, values));
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var sql = $"SELECT * FROM dj_jobs {where} ORDER BY created_at DESC,id {PageSql("@limit", "@offset")}";

        return ReadAsync<IReadOnlyList<JobRecord>>(async (connection, transaction, ct) =>
            await ReadJobsAsync(connection, transaction, sql, ct, values.ToArray()), cancellationToken);
    }

    public Task<IReadOnlyList<JobAttempt>> AttemptsAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<JobAttempt>>(async (connection, transaction, ct) =>
            await ReadAttemptsAsync(connection, transaction, jobId, ct), cancellationToken);

    private async Task<List<JobAttempt>> ReadAttemptsAsync(DbConnection connection, DbTransaction? transaction, Guid jobId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, $"SELECT * FROM dj_attempts WHERE job_id=@id ORDER BY {AttemptNumberSql}", ("id", jobId.ToString()));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var attempts = new List<JobAttempt>();
        while (await reader.ReadAsync(ct))
        {
            attempts.Add(new(jobId, reader.GetInt32(reader.GetOrdinal("number")), reader.GetString(reader.GetOrdinal("worker_id")),
                Date(reader, "started_at")!.Value, Date(reader, "finished_at"), Enum.Parse<AttemptStatus>(reader.GetString(reader.GetOrdinal("status"))),
                Text(reader, "error"), Text(reader, "handler")));
        }

        return attempts;
    }

    public Task<JobStatistics> StatisticsAsync(CancellationToken cancellationToken = default) => ReadAsync(async (connection, transaction, ct) =>
    {
        var counts = new Dictionary<JobStatus, long>();
        await using (var command = Command(connection, transaction, "SELECT status,COUNT(*) FROM dj_jobs GROUP BY status"))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                counts.Add(Enum.Parse<JobStatus>(reader.GetString(0)), Convert.ToInt64(reader.GetValue(1)));
            }
        }

        var limits = new List<ConcurrencyLimit>();
        await using (var command = Command(connection, transaction, "SELECT job_key,maximum FROM dj_limits ORDER BY job_key"))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = reader.GetString(0);
                limits.Add(new(key == AllJobsLimitKey ? null : key, reader.GetInt32(1)));
            }
        }

        return new JobStatistics(counts, limits);
    }, cancellationToken);

    public Task<WorkflowRun?> GetWorkflowAsync(Guid runId, CancellationToken cancellationToken = default) => ReadAsync(async (connection, transaction, ct) =>
    {
        await using var command = Command(connection, transaction, "SELECT flow_key,version,correlation_id,status,error,parent_run_id,parent_job_id FROM dj_flows WHERE id=@id", ("id", runId.ToString()));
        await using var reader = await command.ExecuteReaderAsync(ct);

        return await reader.ReadAsync(ct) ? ReadWorkflowRun(reader, runId) : null;
    }, cancellationToken);

    private static WorkflowRun ReadWorkflowRun(DbDataReader reader, Guid runId) => new(runId, reader.GetString(reader.GetOrdinal("flow_key")),
        reader.GetInt32(reader.GetOrdinal("version")), Text(reader, "correlation_id"), Enum.Parse<FlowStatus>(reader.GetString(reader.GetOrdinal("status"))),
        Text(reader, "error"))
    {
        ParentRunId = ParseGuid(Text(reader, "parent_run_id")),
        ParentJobId = ParseGuid(Text(reader, "parent_job_id"))
    };

    private async Task<JobRecord?> ReadJobAsync(DbConnection connection, DbTransaction? transaction, Guid id, CancellationToken ct) =>
        (await ReadJobsAsync(connection, transaction, "SELECT * FROM dj_jobs WHERE id=@id", ct, ("id", id.ToString()))).SingleOrDefault();

    private Task<List<JobRecord>> ReadJobsAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken ct, params (string, object?)[] values) =>
        ReadJobsAsync(connection, transaction, sql, int.MaxValue, ct, values);

    /// <summary>Reads at most <paramref name="maximum"/> jobs and stops fetching there.</summary>
    private async Task<List<JobRecord>> ReadJobsAsync(DbConnection connection, DbTransaction? transaction, string sql, int maximum, CancellationToken ct,
        params (string, object?)[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var jobs = new List<JobRecord>();
        while (jobs.Count < maximum && await reader.ReadAsync(ct))
        {
            jobs.Add(new(
                Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                new(reader.GetString(reader.GetOrdinal("job_key")), reader.GetInt32(reader.GetOrdinal("contract_version"))),
                Json(reader.GetString(reader.GetOrdinal("payload"))), reader.GetString(reader.GetOrdinal("queue")), Text(reader, "correlation_id"),
                Enum.Parse<JobStatus>(reader.GetString(reader.GetOrdinal("status"))), reader.GetInt32(reader.GetOrdinal("attempt")), reader.GetInt32(reader.GetOrdinal("max_attempts")),
                Date(reader, "created_at")!.Value, Date(reader, "available_at")!.Value,
                Text(reader, "idempotency_key"), ParseGuid(Text(reader, "workflow_run_id")), Text(reader, "step_id"),
                Text(reader, "output") is { } output ? Json(output) : null, Text(reader, "error"), Text(reader, "worker_id"),
                ParseGuid(Text(reader, "lease_token")), Date(reader, "lease_expires_at"), Date(reader, "expires_at"), ParseGuid(Text(reader, "causation_job_id")))
            {
                PartitionKey = Text(reader, "partition_key"),
                TenantId = Text(reader, "tenant_id"),
                TraceParent = Text(reader, "trace_parent")
            });
        }

        return jobs;
    }

    /// <summary>Reads journal rows selected with <see cref="EventColumns"/>.</summary>
    private static async Task<List<ExecutionEvent>> ReadEventsAsync(DbCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        var events = new List<ExecutionEvent>();
        while (await reader.ReadAsync(ct))
        {
            events.Add(new(reader.GetInt64(0), Guid.Parse(reader.GetString(1)), ParseGuid(Text(reader, "run_id")),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)), reader.GetString(4), reader.GetInt32(5)));
        }

        return events;
    }

    private Task<int> InsertJobAsync(DbConnection connection, DbTransaction transaction, JobRecord job, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO dj_jobs(id,job_key,contract_version,payload,queue,correlation_id,status,attempt,max_attempts,
              created_at,available_at,idempotency_key,workflow_run_id,step_id,expires_at,causation_job_id,partition_key,tenant_id,trace_parent)
            VALUES (@id,@key,@version,@payload,@queue,@correlation,@status,@attempt,@maximum,@created,@available,@dedup,@flow,@step,@expiry,@cause,@partition,@tenant,@trace)
            """, ct, ("id", job.Id.ToString()), ("key", job.Contract.Key), ("version", job.Contract.Version),
            ("payload", job.Payload.GetRawText()), ("queue", job.Queue), ("correlation", job.CorrelationId),
            ("status", job.Status.ToString()), ("attempt", job.Attempt), ("maximum", job.MaxAttempts),
            ("created", job.CreatedAt.ToUnixTimeMilliseconds()), ("available", EligibilityMilliseconds(job.AvailableAt)),
            ("dedup", job.IdempotencyKey), ("flow", job.WorkflowRunId?.ToString()), ("step", job.StepId), ("expiry", job.ExpiresAt?.ToUnixTimeMilliseconds()), ("cause", job.CausationJobId?.ToString()),
            ("partition", job.PartitionKey), ("tenant", job.TenantId),
            ("trace", job.TraceParent is { Length: <= TraceContext.MaximumLength } trace ? trace : null));

    private static long EligibilityMilliseconds(DateTimeOffset instant)
    {
        // Storage has millisecond precision. Rounding down could make NotBefore eligible early.
        var utc = instant.ToUniversalTime();
        var remainder = utc.Ticks % TimeSpan.TicksPerMillisecond;

        return (remainder == 0 ? utc : utc.AddTicks(TimeSpan.TicksPerMillisecond - remainder)).ToUnixTimeMilliseconds();
    }

    private static string? Text(DbDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));

    private static DateTimeOffset? Date(DbDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal(name)));

    private static Guid? ParseGuid(string? value) => value is null ? null : Guid.Parse(value);

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);

        return document.RootElement.Clone();
    }
}
