using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore
{
    private const int MaximumListPage = 500;
    private const string EventColumns = "sequence,job_id,run_id,at,kind,attempt";

    public async Task<IReadOnlyList<JobRecord>> ListFlowJobsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        return await ReadJobsAsync(connection, null, "SELECT * FROM dj_jobs WHERE workflow_run_id=@run ORDER BY step_id", cancellationToken, ("run", runId.ToString()));
    }

    public async Task<JobRecord?> GetAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        return await ReadJobAsync(connection, null, jobId, cancellationToken);
    }

    public async Task<IReadOnlyList<JobRecord>> ListAsync(JobFilter filter, CancellationToken cancellationToken = default)
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

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        await using var connection = await OpenAsync(cancellationToken);

        return await ReadJobsAsync(connection, null, $"SELECT * FROM dj_jobs {where} ORDER BY created_at DESC,id {PageSql("@limit", "@offset")}", cancellationToken, values.ToArray());
    }

    public async Task<IReadOnlyList<JobAttempt>> AttemptsAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        return await ReadAttemptsAsync(connection, null, jobId, cancellationToken);
    }

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

    public async Task<JobStatistics> StatisticsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var counts = new Dictionary<JobStatus, long>();
        await using (var command = Command(connection, null, "SELECT status,COUNT(*) FROM dj_jobs GROUP BY status"))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                counts.Add(Enum.Parse<JobStatus>(reader.GetString(0)), Convert.ToInt64(reader.GetValue(1)));
            }
        }

        var limits = new List<ConcurrencyLimit>();
        await using (var command = Command(connection, null, "SELECT job_key,maximum FROM dj_limits ORDER BY job_key"))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = reader.GetString(0);
                limits.Add(new(key == AllJobsLimitKey ? null : key, reader.GetInt32(1)));
            }
        }

        return new(counts, limits);
    }

    public async Task<WorkflowRun?> GetWorkflowAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, null, "SELECT flow_key,version,correlation_id,status,error FROM dj_flows WHERE id=@id", ("id", runId.ToString()));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) ? ReadWorkflowRun(reader, runId) : null;
    }

    private static WorkflowRun ReadWorkflowRun(DbDataReader reader, Guid runId) => new(runId, reader.GetString(reader.GetOrdinal("flow_key")),
        reader.GetInt32(reader.GetOrdinal("version")), Text(reader, "correlation_id"), Enum.Parse<FlowStatus>(reader.GetString(reader.GetOrdinal("status"))),
        Text(reader, "error"));

    private async Task<JobRecord?> ReadJobAsync(DbConnection connection, DbTransaction? transaction, Guid id, CancellationToken ct) =>
        (await ReadJobsAsync(connection, transaction, "SELECT * FROM dj_jobs WHERE id=@id", ct, ("id", id.ToString()))).SingleOrDefault();

    private async Task<List<JobRecord>> ReadJobsAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken ct, params (string, object?)[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var jobs = new List<JobRecord>();
        while (await reader.ReadAsync(ct))
        {
            jobs.Add(new(
                Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                new(reader.GetString(reader.GetOrdinal("job_key")), reader.GetInt32(reader.GetOrdinal("contract_version"))),
                Json(reader.GetString(reader.GetOrdinal("payload"))), reader.GetString(reader.GetOrdinal("queue")), Text(reader, "correlation_id"),
                Enum.Parse<JobStatus>(reader.GetString(reader.GetOrdinal("status"))), reader.GetInt32(reader.GetOrdinal("attempt")), reader.GetInt32(reader.GetOrdinal("max_attempts")),
                Date(reader, "created_at")!.Value, Date(reader, "available_at")!.Value,
                Text(reader, "idempotency_key"), ParseGuid(Text(reader, "workflow_run_id")), Text(reader, "step_id"),
                Text(reader, "output") is { } output ? Json(output) : null, Text(reader, "error"), Text(reader, "worker_id"),
                ParseGuid(Text(reader, "lease_token")), Date(reader, "lease_expires_at"), Date(reader, "expires_at"), ParseGuid(Text(reader, "causation_job_id"))));
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
              created_at,available_at,idempotency_key,workflow_run_id,step_id,expires_at,causation_job_id)
            VALUES (@id,@key,@version,@payload,@queue,@correlation,@status,@attempt,@maximum,@created,@available,@dedup,@flow,@step,@expiry,@cause)
            """, ct, ("id", job.Id.ToString()), ("key", job.Contract.Key), ("version", job.Contract.Version),
            ("payload", job.Payload.GetRawText()), ("queue", job.Queue), ("correlation", job.CorrelationId),
            ("status", job.Status.ToString()), ("attempt", job.Attempt), ("maximum", job.MaxAttempts),
            ("created", job.CreatedAt.ToUnixTimeMilliseconds()), ("available", EligibilityMilliseconds(job.AvailableAt)),
            ("dedup", job.IdempotencyKey), ("flow", job.WorkflowRunId?.ToString()), ("step", job.StepId), ("expiry", job.ExpiresAt?.ToUnixTimeMilliseconds()), ("cause", job.CausationJobId?.ToString()));

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
