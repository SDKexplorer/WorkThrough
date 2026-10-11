using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore
{
    private const int MaximumArchiveJobs = 500;
    private const long MaximumArchiveEvidenceBytes = 8_000_000;
    private const long MaximumArchiveRows = 10_000;

    private static string ArchivePredicate(ArchiveKind kind) => kind == ArchiveKind.Flow ? "workflow_run_id=@id" : "id=@id";

    /// <summary>Rejects a unit whose evidence cannot be archived within one bounded batch, before anything is written.</summary>
    private async Task EnsureArchiveFitsAsync(DbConnection connection, DbTransaction transaction, ArchiveCandidate candidate, CancellationToken ct)
    {
        if (candidate.Jobs > MaximumArchiveJobs)
        {
            throw new StateConflictException($"Archive exceeds the {MaximumArchiveJobs}-job evidence limit; export this execution separately.");
        }

        var jobs = $"SELECT id FROM dj_jobs WHERE {ArchivePredicate(candidate.Kind)}";
        var queries = new List<string>
        {
            $"SELECT COALESCE(SUM({LengthSql("payload")}+COALESCE({LengthSql("output")},0)),0) FROM dj_jobs WHERE {ArchivePredicate(candidate.Kind)}",
            $"SELECT COALESCE(SUM({LengthSql("exception_type")}+{LengthSql("message")}+COALESCE({LengthSql("stack_trace")},0)),0) FROM dj_failures WHERE job_id IN ({jobs})",
            $"SELECT COALESCE(SUM(COALESCE({LengthSql("error")},0)+COALESCE({LengthSql("handler")},0)),0) FROM dj_attempts WHERE job_id IN ({jobs})"
        };
        if (candidate.Kind == ArchiveKind.Flow)
        {
            queries.Add($"SELECT {LengthSql("f.input")}+{LengthSql("d.definition")} FROM dj_flows f JOIN dj_definitions d ON f.flow_key=d.flow_key AND f.version=d.version WHERE f.id=@id");
            queries.Add($"SELECT COALESCE(SUM({LengthSql("payload")}),0) FROM dj_signals WHERE run_id=@id");
        }

        var size = 0L;
        foreach (var query in queries)
        {
            size += await CountAsync(query);
        }

        var attempts = await CountAsync($"SELECT COUNT(*) FROM dj_attempts WHERE job_id IN ({jobs})");
        var events = await CountAsync($"SELECT COUNT(*) FROM dj_events WHERE job_id IN ({jobs})");
        if (size > MaximumArchiveEvidenceBytes || attempts > MaximumArchiveRows || events > MaximumArchiveRows)
        {
            throw new StateConflictException("Archive exceeds bounded batch evidence limits; export this execution separately.");
        }

        async Task<long> CountAsync(string sql) => Convert.ToInt64(await ScalarAsync(connection, transaction, sql, ct, ("id", candidate.Id.ToString())));
    }

    private async Task<ArchivedJob> ReadArchivedJobAsync(DbConnection connection, DbTransaction transaction, JobRecord job, CancellationToken ct) =>
        new(job, await ReadAttemptsAsync(connection, transaction, job.Id, ct), await ReadFailuresAsync(connection, transaction, job.Id, ct));

    private async Task<IReadOnlyList<ExecutionEvent>> ReadArchivedEventsAsync(DbConnection connection, DbTransaction transaction, ArchiveCandidate candidate, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            $"SELECT {EventColumns} FROM dj_events WHERE job_id IN (SELECT id FROM dj_jobs WHERE {ArchivePredicate(candidate.Kind)}) ORDER BY sequence",
            ("id", candidate.Id.ToString()));

        return await ReadEventsAsync(command, ct);
    }

    private async Task<ArchivedFlow> ReadArchivedFlowAsync(DbConnection connection, DbTransaction transaction, Guid runId, CancellationToken ct)
    {
        var signals = new List<ArchivedSignal>();
        await using (var command = Command(connection, transaction, "SELECT name,payload,received_at FROM dj_signals WHERE run_id=@id ORDER BY name", ("id", runId.ToString())))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                signals.Add(new(reader.GetString(0), Json(reader.GetString(1)), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));
            }
        }

        await using (var command = Command(connection, transaction, """
            SELECT f.flow_key,f.version,f.correlation_id,f.status,f.error,f.parent_run_id,f.parent_job_id,f.input,d.definition
            FROM dj_flows f JOIN dj_definitions d ON f.flow_key=d.flow_key AND f.version=d.version WHERE f.id=@id
            """, ("id", runId.ToString())))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var definition = JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(reader.GetOrdinal("definition")), JobJson.Options);

            return new(ReadWorkflowRun(reader, runId), definition, Json(reader.GetString(reader.GetOrdinal("input"))), signals);
        }
    }

    private sealed record ArchivedFlow(WorkflowRun Run, FlowDefinition? Definition, JsonElement Input, IReadOnlyList<ArchivedSignal> Signals);
}
