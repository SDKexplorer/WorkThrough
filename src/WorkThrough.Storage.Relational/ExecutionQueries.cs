using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore : IExecutionQueries
{
    private const int OperationQuerySeconds = 5;
    private const long MaximumWindowMilliseconds = 7 * 24 * 60 * 60 * 1000;
    private string ExecutionColumns => $"""
        j.id,j.job_key,j.contract_version,{SubstringFunction}(j.queue,1,512) AS queue,
        {SubstringFunction}(j.correlation_id,1,512) AS correlation_id,j.status,j.attempt,j.max_attempts,
        j.created_at,j.available_at,j.terminal_at,j.expires_at,j.workflow_run_id,
        {SubstringFunction}(j.step_id,1,512) AS step_id,j.causation_job_id,
        CASE WHEN {LengthSql("j.queue")}>512 OR {LengthSql("j.correlation_id")}>512 OR {LengthSql("j.step_id")}>512 THEN 1 ELSE 0 END AS clipped
        """;

    public async Task<ExecutionStoreContext> GetExecutionContextAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = OperationQueryTimeout(cancellationToken);
        var ct = timeout.Token;
        await using var connection = await OpenAsync(ct);
        await using var transaction = await BeginReadAsync(connection, ct);
        var (storeId, observed) = await ReadOperationContextAsync(connection, transaction, ct);
        await transaction.CommitAsync(ct);

        return new(storeId, observed);
    }

    public async Task<ExecutionPage> SearchExecutionsAsync(ExecutionQuery query, CancellationToken cancellationToken = default)
    {
        ValidateExecutionQuery(query);
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(query with { Cursor = null })));
        var cursor = query.Cursor is null ? null : ReadExecutionCursor(query.Cursor);
        using var timeout = OperationQueryTimeout(cancellationToken);
        var ct = timeout.Token;
        await using var connection = await OpenAsync(ct);
        await using var transaction = await BeginReadAsync(connection, ct);
        var (storeId, observed) = await ReadOperationContextAsync(connection, transaction, ct);
        var until = cursor?.Until ?? CeilingMilliseconds(query.Until ?? observed.AddMilliseconds(1));
        var from = cursor?.From ?? CeilingMilliseconds(query.From ?? DateTimeOffset.FromUnixTimeMilliseconds(until).AddDays(-1));
        ValidateWindow(from, until);
        if (cursor is not null && (cursor.StoreId != storeId || cursor.Fingerprint != fingerprint))
        {
            throw new ArgumentException("The continuation cursor belongs to a different store or query.", nameof(query));
        }

        var conditions = new List<string>();
        var values = new List<(string, object?)> { ("from", from), ("until", until), ("limit", query.Limit + 1) };
        AddExecutionFilters(query, conditions, values);
        if (query.TimeBasis == ExecutionTimeBasis.Created)
        {
            conditions.Add("j.created_at>=@from AND j.created_at<@until");
        }
        else
        {
            conditions.Add("""
                j.id IN (SELECT a.job_id FROM dj_attempts a WHERE a.status='Failed'
                  AND a.finished_at>=@from AND a.finished_at<@until)
                """);
        }

        if (cursor is not null)
        {
            conditions.Add("(j.created_at<@created OR (j.created_at=@created AND j.id>@id))");
            values.Add(("created", cursor.CreatedAt));
            values.Add(("id", cursor.Id.ToString()));
        }

        await using var command = OperationCommand(connection, transaction,
            $"SELECT {ExecutionColumns} FROM dj_jobs j WHERE {string.Join(" AND ", conditions)} ORDER BY j.created_at DESC,j.id {PageSql("@limit")}", values.ToArray());
        var items = await ReadExecutionMetadataAsync(command, ct);
        string? next = null;
        if (items.Count > query.Limit)
        {
            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            next = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new ExecutionCursor(1, storeId, fingerprint, from, until,
                last.CreatedAt.ToUnixTimeMilliseconds(), last.Id)));
        }

        await transaction.CommitAsync(ct);

        return new(storeId, observed, DateTimeOffset.FromUnixTimeMilliseconds(from), DateTimeOffset.FromUnixTimeMilliseconds(until), query.TimeBasis, items, next);
    }

    public async Task<ExecutionLookup> FindExecutionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var timeout = OperationQueryTimeout(cancellationToken);
        var ct = timeout.Token;
        await using var connection = await OpenAsync(ct);
        await using var transaction = await BeginReadAsync(connection, ct);
        var (storeId, observed) = await ReadOperationContextAsync(connection, transaction, ct);
        await using var command = OperationCommand(connection, transaction,
            $"SELECT {ExecutionColumns} FROM dj_jobs j WHERE j.id=@id", ("id", id.ToString()));
        var items = await ReadExecutionMetadataAsync(command, ct);
        await transaction.CommitAsync(ct);

        return new(storeId, observed, items.SingleOrDefault());
    }

    public async Task<ExecutionAttemptPage> ReadAttemptsAsync(Guid id, int afterAttempt = 0, int limit = 25, CancellationToken cancellationToken = default)
    {
        ValidateOperationLimit(limit);
        if (afterAttempt < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterAttempt));
        }

        using var timeout = OperationQueryTimeout(cancellationToken);
        var ct = timeout.Token;
        await using var connection = await OpenAsync(ct);
        await using var transaction = await BeginReadAsync(connection, ct);
        var (storeId, observed) = await ReadOperationContextAsync(connection, transaction, ct);
        bool exists;
        await using (var lookup = OperationCommand(connection, transaction, "SELECT id FROM dj_jobs WHERE id=@id", ("id", id.ToString())))
        {
            exists = await lookup.ExecuteScalarAsync(ct) is not null;
        }

        var items = new List<ExecutionAttemptMetadata>();
        await using (var command = OperationCommand(connection, transaction, $"""
            SELECT {AttemptNumberSql},status,started_at,finished_at FROM dj_attempts
            WHERE job_id=@id AND {AttemptNumberSql}>@after ORDER BY {AttemptNumberSql} {PageSql("@limit")}
            """, ("id", id.ToString()), ("after", afterAttempt), ("limit", limit + 1)))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(new(reader.GetInt32(0), reader.GetString(1), Date(reader, "started_at")!.Value, Date(reader, "finished_at")));
            }
        }

        int? next = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            next = items[^1].Number;
        }

        await transaction.CommitAsync(ct);

        return new(storeId, observed, exists, items, next);
    }

    private async Task<List<ExecutionMetadata>> ReadExecutionMetadataAsync(DbCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<ExecutionMetadata>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new(Guid.Parse(reader.GetString(0)), new(reader.GetString(1), reader.GetInt32(2)),
                reader.GetString(3), Text(reader, "correlation_id"), Enum.Parse<JobStatus>(reader.GetString(5)),
                reader.GetInt32(6), reader.GetInt32(7), Date(reader, "created_at")!.Value, Date(reader, "available_at")!.Value,
                Date(reader, "terminal_at"), Date(reader, "expires_at"), ParseGuid(Text(reader, "workflow_run_id")),
                Text(reader, "step_id"), ParseGuid(Text(reader, "causation_job_id")), reader.GetInt32(15) != 0));
        }

        return items;
    }

    private async Task<(string StoreId, DateTimeOffset ObservedAt)> ReadOperationContextAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = OperationCommand(connection, transaction, "SELECT id FROM dj_store");
        var storeId = (string?)await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Initialize the store before querying operations.");
        await using var clock = OperationCommand(connection, transaction, ClockSql);
        var now = Convert.ToInt64(await clock.ExecuteScalarAsync(ct));

        return (storeId, DateTimeOffset.FromUnixTimeMilliseconds(now));
    }

    private static void AddExecutionFilters(ExecutionQuery query, List<string> conditions, List<(string, object?)> values)
    {
        Add("correlation_id", "correlation", query.CorrelationId);
        Add("status", "status", query.Status?.ToString());
        Add("job_key", "key", query.ProcessorKey);
        Add("contract_version", "version", query.ContractVersion);
        Add("queue", "queue", query.Queue);
        Add("workflow_run_id", "flow", query.WorkflowRunId?.ToString());

        void Add(string column, string parameter, object? value)
        {
            if (value is not null)
            {
                conditions.Add($"j.{column}=@{parameter}");
                values.Add((parameter, value));
            }
        }
    }

    private static void ValidateExecutionQuery(ExecutionQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateOperationLimit(query.Limit);
        if (!Enum.IsDefined(query.TimeBasis) || query.Status is { } status && !Enum.IsDefined(status))
        {
            throw new ArgumentException("Unknown execution state or time basis.", nameof(query));
        }

        if (query.ContractVersion is < 1 || query.ContractVersion is not null && query.ProcessorKey is null)
        {
            throw new ArgumentException("A positive contract version requires a processor key.", nameof(query));
        }

        if (query.ProcessorKey is not null)
        {
            _ = new JobContractId(query.ProcessorKey, query.ContractVersion ?? 1);
        }

        if (query.CorrelationId is { Length: > 512 } || query.Queue is { Length: > 512 } || query.Cursor is { Length: > 2048 })
        {
            throw new ArgumentException("Filters allow 512 characters; cursors allow 2048 characters.", nameof(query));
        }

        if (query.From is { } from && query.Until is { } until)
        {
            ValidateWindow(CeilingMilliseconds(from), CeilingMilliseconds(until));
        }
    }

    private static void ValidateOperationLimit(int limit)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Operation pages contain 1–100 items.");
        }
    }

    private static void ValidateWindow(long from, long until)
    {
        _ = DateTimeOffset.FromUnixTimeMilliseconds(from);
        _ = DateTimeOffset.FromUnixTimeMilliseconds(until);
        if (until <= from || until - from > MaximumWindowMilliseconds)
        {
            throw new ArgumentException("The half-open query window must be positive and at most seven days.");
        }
    }

    private static long CeilingMilliseconds(DateTimeOffset value)
    {
        var milliseconds = value.ToUnixTimeMilliseconds();

        return value.UtcTicks % TimeSpan.TicksPerMillisecond == 0 ? milliseconds : checked(milliseconds + 1);
    }

    private static ExecutionCursor ReadExecutionCursor(string value)
    {
        try
        {
            var cursor = JsonSerializer.Deserialize<ExecutionCursor>(Convert.FromBase64String(value));
            if (cursor is null || cursor.Version != 1 || cursor.StoreId is null || cursor.Fingerprint is null)
            {
                throw new ArgumentException("Invalid continuation cursor.");
            }

            ValidateWindow(cursor.From, cursor.Until);
            _ = DateTimeOffset.FromUnixTimeMilliseconds(cursor.CreatedAt);

            return cursor;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        {
            throw new ArgumentException("Invalid continuation cursor.", nameof(value), error);
        }
    }

    private static CancellationTokenSource OperationQueryTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(OperationQuerySeconds));

        return timeout;
    }

    private DbCommand OperationCommand(DbConnection connection, DbTransaction transaction, string sql, params (string, object?)[] values)
    {
        var command = Command(connection, transaction, sql, values);
        command.CommandTimeout = OperationQuerySeconds;

        return command;
    }

    private sealed record ExecutionCursor(int Version, string StoreId, string Fingerprint, long From, long Until, long CreatedAt, Guid Id);
}
