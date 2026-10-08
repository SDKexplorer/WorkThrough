using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore
{
    private static async Task EnsureArchiveFitsAsync(DbConnection connection, DbTransaction transaction, ArchiveCandidate candidate, string predicate, CancellationToken ct)
    {
        var size = Convert.ToInt64(await ScalarAsync(connection, transaction, $"SELECT COALESCE(SUM(LENGTH(payload)+LENGTH(COALESCE(output,''))),0) FROM dj_jobs WHERE {predicate}", ct, ("id", candidate.Id.ToString())));
        size += Convert.ToInt64(await ScalarAsync(connection, transaction, $"SELECT COALESCE(SUM(LENGTH(exception_type)+LENGTH(message)+LENGTH(COALESCE(stack_trace,''))),0) FROM dj_failures WHERE job_id IN (SELECT id FROM dj_jobs WHERE {predicate})", ct, ("id", candidate.Id.ToString())));
        size += Convert.ToInt64(await ScalarAsync(connection, transaction, $"SELECT COALESCE(SUM(LENGTH(COALESCE(error,''))+LENGTH(COALESCE(handler,''))),0) FROM dj_attempts WHERE job_id IN (SELECT id FROM dj_jobs WHERE {predicate})", ct, ("id", candidate.Id.ToString())));
        if (candidate.Kind == "Flow")
        {
            size += Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT LENGTH(f.input)+LENGTH(d.definition) FROM dj_flows f JOIN dj_definitions d ON f.flow_key=d.flow_key AND f.version=d.version WHERE f.id=@id", ct, ("id", candidate.Id.ToString())));
            size += Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT COALESCE(SUM(LENGTH(payload)),0) FROM dj_signals WHERE run_id=@id", ct, ("id", candidate.Id.ToString())));
        }

        var attemptCount = Convert.ToInt64(await ScalarAsync(connection, transaction, $"SELECT COUNT(*) FROM dj_attempts WHERE job_id IN (SELECT id FROM dj_jobs WHERE {predicate})", ct, ("id", candidate.Id.ToString())));
        var eventCount = Convert.ToInt64(await ScalarAsync(connection, transaction, $"SELECT COUNT(*) FROM dj_events WHERE job_id IN (SELECT id FROM dj_jobs WHERE {predicate})", ct, ("id", candidate.Id.ToString())));
        if (size > 8_000_000 || attemptCount > 10000 || eventCount > 10000)
        {
            throw new StateConflictException("Archive exceeds bounded batch evidence limits; export this execution separately.");
        }
    }

    private static async Task<ArchivedJob> ReadArchivedJobAsync(DbConnection connection, DbTransaction transaction, JobRecord job, CancellationToken ct)
    {
        var attempts = new List<JobAttempt>();
        await using (var query = Command(connection, transaction, "SELECT * FROM dj_attempts WHERE job_id=@id ORDER BY number", ("id", job.Id.ToString())))
        {
            await using (var reader = await query.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    attempts.Add(new(job.Id, reader.GetInt32(reader.GetOrdinal("number")), reader.GetString(reader.GetOrdinal("worker_id")), Date(reader, "started_at")!.Value, Date(reader, "finished_at"), reader.GetString(reader.GetOrdinal("status")), Text(reader, "error"), Text(reader, "handler")));
                }
            }
        }

        var failures = new List<AttemptFailure>();
        await using (var query = Command(connection, transaction, "SELECT attempt,exception_type,message,stack_trace FROM dj_failures WHERE job_id=@id ORDER BY attempt", ("id", job.Id.ToString())))
        {
            await using (var reader = await query.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    failures.Add(new(reader.GetInt32(0), new(reader.GetString(1), reader.GetString(2), Text(reader, "stack_trace"))));
                }
            }
        }

        return new(job, attempts, failures);
    }

    private static async Task<IReadOnlyList<ExecutionEvent>> ReadArchivedEventsAsync(DbConnection connection, DbTransaction transaction, ArchiveCandidate candidate, string predicate, CancellationToken ct)
    {
        var events = new List<ExecutionEvent>();
        await using (var query = Command(connection, transaction, $"SELECT * FROM dj_events WHERE job_id IN (SELECT id FROM dj_jobs WHERE {predicate}) ORDER BY sequence", ("id", candidate.Id.ToString())))
        {
            await using (var reader = await query.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    events.Add(new(reader.GetInt64(0), Guid.Parse(reader.GetString(1)), ParseGuid(Text(reader, "run_id")), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)), reader.GetString(4), reader.GetInt32(5)));
                }
            }
        }

        return events;
    }

    private static async Task<(WorkflowRun? Flow, FlowDefinition? Definition, JsonElement? Input, IReadOnlyList<ArchivedSignal> Signals)> ReadArchivedFlowAsync(DbConnection connection, DbTransaction transaction, ArchiveCandidate candidate, CancellationToken ct)
    {
        var signals = new List<ArchivedSignal>();
        if (candidate.Kind == "Flow")
        {
            await using var signalQuery = Command(connection, transaction, "SELECT name,payload,received_at FROM dj_signals WHERE run_id=@id ORDER BY name", ("id", candidate.Id.ToString()));
            await using var signalReader = await signalQuery.ExecuteReaderAsync(ct);
            while (await signalReader.ReadAsync(ct))
            {
                signals.Add(new(signalReader.GetString(0), Json(signalReader.GetString(1)), DateTimeOffset.FromUnixTimeMilliseconds(signalReader.GetInt64(2))));
            }
        }

        WorkflowRun? flow = null;
        FlowDefinition? definition = null;
        JsonElement? input = null;
        if (candidate.Kind == "Flow")
        {
            await using var query = Command(connection, transaction, "SELECT f.*,d.definition FROM dj_flows f JOIN dj_definitions d ON f.flow_key=d.flow_key AND f.version=d.version WHERE f.id=@id", ("id", candidate.Id.ToString()));
            await using var reader = await query.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            flow = new(candidate.Id, reader.GetString(reader.GetOrdinal("flow_key")), reader.GetInt32(reader.GetOrdinal("version")), candidate.CorrelationId, reader.GetString(reader.GetOrdinal("status")), Text(reader, "error"));
            input = Json(reader.GetString(reader.GetOrdinal("input")));
            definition = JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(reader.GetOrdinal("definition")), JobJson.Options);
        }

        return (flow, definition, input, signals);
    }
}
