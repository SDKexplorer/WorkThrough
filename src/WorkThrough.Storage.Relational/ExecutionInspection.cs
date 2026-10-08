using System.Data.Common;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore : IExecutionInspectionQueries
{
    // Bound the JSON parsed inside the database; templates and condition values never leave the database.
    private const int MaximumInspectionDefinitionCharacters = 262144;
    protected virtual string FlowDependencySql => throw new NotSupportedException("This provider does not project flow dependencies.");

    public async Task<ExecutionDiagnostic> ReadDiagnosticAsync(Guid id, int attempt, int maximumCharacters = 2048,
        CancellationToken cancellationToken = default)
    {
        if (attempt < 1 || maximumCharacters is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt), "Use a positive attempt and a text limit of 1–4096 characters.");
        }

        using var timeout = OperationQueryTimeout(cancellationToken);
        var ct = timeout.Token;
        await using var connection = await OpenAsync(ct);
        await using var transaction = await BeginReadAsync(connection, ct);
        var (storeId, observed) = await ReadOperationContextAsync(connection, transaction, ct);
        var exists = false;
        var attemptExists = false;
        JobFailure? failure = null;
        var truncated = false;
        await using (var command = OperationCommand(connection, transaction, """
            SELECT a.number,SUBSTR(COALESCE(f.exception_type,'Unspecified'),1,@length) AS exception_type,
              SUBSTR(COALESCE(f.message,a.error),1,@length) AS message,SUBSTR(f.stack_trace,1,@length) AS stack_trace,
              CASE WHEN LENGTH(COALESCE(f.exception_type,'Unspecified'))>@length OR LENGTH(COALESCE(f.message,a.error))>@length
                OR LENGTH(f.stack_trace)>@length THEN 1 ELSE 0 END AS clipped
            FROM dj_jobs j LEFT JOIN dj_attempts a ON a.job_id=j.id AND a.number=@attempt
            LEFT JOIN dj_failures f ON f.job_id=a.job_id AND f.attempt=a.number WHERE j.id=@id
            """, ("id", id.ToString()), ("attempt", attempt), ("length", maximumCharacters)))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                exists = true;
                attemptExists = !reader.IsDBNull(0);
                var message = Text(reader, "message");
                if (message is not null)
                {
                    failure = new(reader.GetString(1), message, Text(reader, "stack_trace"));
                }

                truncated = failure is not null && reader.GetInt32(4) != 0;
            }
        }

        await transaction.CommitAsync(ct);

        return new(storeId, observed, id, attempt, exists, attemptExists, failure, truncated);
    }

    public async Task<FlowInspection> ReadFlowAsync(Guid runId, int nodeOffset = 0, int dependencyOffset = 0,
        long afterEvent = 0, int limit = 25, CancellationToken cancellationToken = default)
    {
        ValidateOperationLimit(limit);
        if (nodeOffset is < 0 or > 500 || dependencyOffset is < 0 or > 250000 || afterEvent < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeOffset), "Invalid flow page position.");
        }

        using var timeout = OperationQueryTimeout(cancellationToken);
        var ct = timeout.Token;
        await using var connection = await OpenAsync(ct);
        await using var transaction = await BeginReadAsync(connection, ct);
        var (storeId, observed) = await ReadOperationContextAsync(connection, transaction, ct);
        var (flow, graphStatus) = await ReadFlowMetadataAsync(connection, transaction, runId, ct);
        await using var nodesCommand = OperationCommand(connection, transaction,
            $"SELECT {ExecutionColumns} FROM dj_jobs j WHERE j.workflow_run_id=@run ORDER BY j.id LIMIT @limit OFFSET @offset",
            ("run", runId.ToString()), ("limit", limit + 1), ("offset", nodeOffset));
        var nodes = await ReadExecutionMetadataAsync(nodesCommand, ct);
        var nextNode = TrimPage(nodes, limit) ? nodeOffset + limit : (int?)null;
        var dependencies = graphStatus == "available"
            ? await ReadDependenciesAsync(connection, transaction, runId, dependencyOffset, limit, ct) : [];
        var nextDependency = TrimPage(dependencies, limit) ? dependencyOffset + limit : (int?)null;
        var position = await ReadInspectionEventPositionAsync(connection, transaction, ct);
        if (afterEvent > position.LastSequence)
        {
            throw new ArgumentOutOfRangeException(nameof(afterEvent), "The requested event position is beyond this store's journal.");
        }

        var events = await ReadFlowEventsAsync(connection, transaction, runId, Math.Max(afterEvent, position.RetainedAfter), limit, ct);
        var nextEvent = TrimPage(events, limit) ? events[^1].Sequence : (long?)null;
        await transaction.CommitAsync(ct);

        return new(storeId, observed, flow, nodes, nextNode, dependencies, nextDependency, graphStatus, events,
            nextEvent, position.RetainedAfter, position.LastSequence, afterEvent < position.RetainedAfter);
    }

    private static async Task<(FlowMetadata? Flow, string GraphStatus)> ReadFlowMetadataAsync(
        DbConnection connection, DbTransaction transaction, Guid runId, CancellationToken ct)
    {
        await using var command = OperationCommand(connection, transaction, """
            SELECT f.flow_key,f.version,SUBSTR(f.correlation_id,1,512) AS correlation_id,f.status,
              CASE WHEN LENGTH(f.correlation_id)>512 THEN 1 ELSE 0 END AS clipped,LENGTH(d.definition) AS characters
            FROM dj_flows f LEFT JOIN dj_definitions d ON d.flow_key=f.flow_key AND d.version=f.version WHERE f.id=@run
            """, ("run", runId.ToString()));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return (null, "not_live");
        }

        var status = reader.IsDBNull(5) ? "definition_missing" : Convert.ToInt64(reader.GetValue(5)) > MaximumInspectionDefinitionCharacters ? "definition_too_large" : "available";

        return (new(runId, reader.GetString(0), reader.GetInt32(1), Text(reader, "correlation_id"), reader.GetString(3), reader.GetInt32(4) != 0), status);
    }

    private async Task<List<FlowDependency>> ReadDependenciesAsync(DbConnection connection, DbTransaction transaction,
        Guid runId, int offset, int limit, CancellationToken ct)
    {
        await using var command = OperationCommand(connection, transaction, FlowDependencySql,
            ("run", runId.ToString()), ("limit", limit + 1), ("offset", offset), ("maximum", MaximumInspectionDefinitionCharacters));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var dependencies = new List<FlowDependency>();
        while (await reader.ReadAsync(ct))
        {
            dependencies.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1))));
        }

        return dependencies;
    }

    private static async Task<EventPosition> ReadInspectionEventPositionAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = OperationCommand(connection, transaction, "SELECT value,floor FROM dj_event_sequence WHERE id=1");
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return new(reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<List<ExecutionEvent>> ReadFlowEventsAsync(DbConnection connection, DbTransaction transaction,
        Guid runId, long after, int limit, CancellationToken ct)
    {
        await using var command = OperationCommand(connection, transaction, """
            SELECT sequence,job_id,at,kind,attempt FROM dj_events WHERE run_id=@run AND sequence>@after ORDER BY sequence LIMIT @limit
            """, ("run", runId.ToString()), ("after", after), ("limit", limit + 1));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var events = new List<ExecutionEvent>();
        while (await reader.ReadAsync(ct))
        {
            events.Add(new(reader.GetInt64(0), Guid.Parse(reader.GetString(1)), runId,
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)), reader.GetString(3), reader.GetInt32(4)));
        }

        return events;
    }

    private static bool TrimPage<T>(List<T> items, int limit)
    {
        if (items.Count <= limit)
        {
            return false;
        }

        items.RemoveAt(items.Count - 1);

        return true;
    }
}
