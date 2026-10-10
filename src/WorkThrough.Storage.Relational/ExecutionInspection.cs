using System.Data.Common;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore : IExecutionInspectionQueries
{
    // Bound the JSON parsed inside the database; templates and condition values never leave the database.
    private const int MaximumInspectionDefinitionCharacters = 262144;
    private const int MaximumDependencyOffset = 250000;

    protected virtual string FlowDependencySql => throw new NotSupportedException("This provider does not project flow dependencies.");

    public async Task<ExecutionDiagnostic> ReadDiagnosticAsync(Guid id, int attempt, int maximumCharacters = OperationQueryLimits.DefaultDiagnosticCharacters,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        if (maximumCharacters is < 1 or > OperationQueryLimits.MaximumDiagnosticCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters),
                $"Use a text limit of 1–{OperationQueryLimits.MaximumDiagnosticCharacters} characters.");
        }

        return await ReadOperationAsync<ExecutionDiagnostic>(async (connection, transaction, ct) =>
        {
            var (storeId, observed) = await ReadOperationContextAsync(connection, transaction, ct);
            var exists = false;
            var attemptExists = false;
            JobFailure? failure = null;
            var truncated = false;
            await using (var command = OperationCommand(connection, transaction, $"""
                SELECT a.{AttemptNumberSql},{SubstringFunction}({DiagnosticExceptionSql},1,@length) AS exception_type,
                  {SubstringFunction}(COALESCE(f.message,a.error),1,@length) AS message,{SubstringFunction}(f.stack_trace,1,@length) AS stack_trace,
                  CASE WHEN {LengthSql(DiagnosticExceptionSql)}>@length OR {LengthSql("COALESCE(f.message,a.error)")}>@length
                    OR {LengthSql("f.stack_trace")}>@length THEN 1 ELSE 0 END AS clipped
                FROM dj_jobs j LEFT JOIN dj_attempts a ON a.job_id=j.id AND a.{AttemptNumberSql}=@attempt
                LEFT JOIN dj_failures f ON f.job_id=a.job_id AND f.attempt=a.{AttemptNumberSql} WHERE j.id=@id
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

            return new(storeId, observed, id, attempt, exists, attemptExists, failure, truncated);
        }, cancellationToken);
    }

    public async Task<FlowInspection> ReadFlowAsync(Guid runId, int nodeOffset = 0, int dependencyOffset = 0,
        long afterEvent = 0, int limit = OperationQueryLimits.DefaultPageSize, CancellationToken cancellationToken = default)
    {
        ValidateOperationLimit(limit);
        if (nodeOffset is < 0 or > FlowValidator.MaximumSteps || dependencyOffset is < 0 or > MaximumDependencyOffset || afterEvent < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeOffset), "Invalid flow page position.");
        }

        return await ReadOperationAsync<FlowInspection>(async (connection, transaction, ct) =>
        {
            var (storeId, observed) = await ReadOperationContextAsync(connection, transaction, ct);
            var (flow, graphStatus) = await ReadFlowMetadataAsync(connection, transaction, runId, ct);
            await using var nodesCommand = OperationCommand(connection, transaction,
                $"SELECT {ExecutionColumns} FROM dj_jobs j WHERE j.workflow_run_id=@run ORDER BY j.id {PageSql("@limit", "@offset")}",
                ("run", runId.ToString()), ("limit", limit + 1), ("offset", nodeOffset));
            var nodes = await ReadExecutionMetadataAsync(nodesCommand, ct);
            var nextNode = TrimPage(nodes, limit) ? nodeOffset + limit : (int?)null;
            var dependencies = graphStatus == FlowGraphStatuses.Available
                ? await ReadDependenciesAsync(connection, transaction, runId, dependencyOffset, limit, ct) : [];
            var nextDependency = TrimPage(dependencies, limit) ? dependencyOffset + limit : (int?)null;
            EventPosition position;
            await using (var command = OperationCommand(connection, transaction, EventPositionSql))
            {
                position = await ReadEventPositionAsync(command, ct);
            }

            if (afterEvent > position.LastSequence)
            {
                throw new ArgumentOutOfRangeException(nameof(afterEvent), "The requested event position is beyond this store's journal.");
            }

            var events = await ReadFlowEventsAsync(connection, transaction, runId, Math.Max(afterEvent, position.RetainedAfter), limit, ct);
            var nextEvent = TrimPage(events, limit) ? events[^1].Sequence : (long?)null;

            return new(storeId, observed, flow, nodes, nextNode, dependencies, nextDependency, graphStatus, events,
                nextEvent, position.RetainedAfter, position.LastSequence, afterEvent < position.RetainedAfter);
        }, cancellationToken);
    }

    private async Task<(FlowMetadata? Flow, string GraphStatus)> ReadFlowMetadataAsync(
        DbConnection connection, DbTransaction transaction, Guid runId, CancellationToken ct)
    {
        await using var command = OperationCommand(connection, transaction, $"""
            SELECT f.flow_key,f.version,{ClippedLabelSql("f.correlation_id")} AS correlation_id,f.status,
              CASE WHEN {LabelTooLongSql("f.correlation_id")} THEN 1 ELSE 0 END AS clipped,{LengthSql("d.definition")} AS characters
            FROM dj_flows f LEFT JOIN dj_definitions d ON d.flow_key=f.flow_key AND d.version=f.version WHERE f.id=@run
            """, ("run", runId.ToString()));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return (null, FlowGraphStatuses.NotLive);
        }

        var graphStatus = reader.IsDBNull(5) ? FlowGraphStatuses.DefinitionMissing
            : Convert.ToInt64(reader.GetValue(5)) > MaximumInspectionDefinitionCharacters ? FlowGraphStatuses.DefinitionTooLarge
            : FlowGraphStatuses.Available;
        var flow = new FlowMetadata(runId, reader.GetString(0), reader.GetInt32(1), Text(reader, "correlation_id"),
            Enum.Parse<FlowStatus>(reader.GetString(3)), reader.GetInt32(4) != 0);

        return (flow, graphStatus);
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

    private async Task<List<ExecutionEvent>> ReadFlowEventsAsync(DbConnection connection, DbTransaction transaction,
        Guid runId, long after, int limit, CancellationToken ct)
    {
        await using var command = OperationCommand(connection, transaction,
            $"SELECT {EventColumns} FROM dj_events WHERE run_id=@run AND sequence>@after ORDER BY sequence {PageSql("@limit")}",
            ("run", runId.ToString()), ("after", after), ("limit", limit + 1));

        return await ReadEventsAsync(command, ct);
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
