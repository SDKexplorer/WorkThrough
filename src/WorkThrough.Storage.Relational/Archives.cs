using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore : IExecutionArchives
{
    private static void ValidateArchivePolicy(ArchivePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.BatchSize is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Archive batches contain 1–10 units; a whole flow is one unit.");
        }
    }

    public async Task<IReadOnlyList<ArchiveCandidate>> ArchiveCandidatesAsync(ArchivePolicy policy, CancellationToken cancellationToken = default)
    {
        ValidateArchivePolicy(policy);
        await using var connection = await OpenAsync(cancellationToken);

        return await ReadCandidatesAsync(connection, null, policy, cancellationToken);
    }

    private static async Task<IReadOnlyList<ArchiveCandidate>> ReadCandidatesAsync(DbConnection connection, DbTransaction? transaction, ArchivePolicy policy, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT id,kind,correlation_id,completed,jobs FROM (
              SELECT j.id,'Job' AS kind,j.correlation_id,j.terminal_at AS completed,1 AS jobs FROM dj_jobs j
              WHERE j.workflow_run_id IS NULL AND
                ((j.status IN ('Succeeded','Skipped') AND j.terminal_at<=@success) OR
                 (j.status IN ('Failed','Cancelled','Rejected','Expired') AND j.terminal_at<=@failure))
              UNION ALL
              SELECT f.id,'Flow' AS kind,f.correlation_id,MAX(j.terminal_at) AS completed,COUNT(*) AS jobs
              FROM dj_flows f JOIN dj_jobs j ON j.workflow_run_id=f.id WHERE f.status IN ('Succeeded','Failed')
              GROUP BY f.id,f.correlation_id,f.status
              HAVING COUNT(*)=COUNT(j.terminal_at) AND COUNT(*)<=500
                AND MAX(j.terminal_at)<=CASE WHEN f.status='Succeeded' THEN @success ELSE @failure END
            ) candidates ORDER BY completed,id LIMIT @limit
            """, ("success", policy.SucceededBefore.ToUnixTimeMilliseconds()), ("failure", policy.FailedBefore?.ToUnixTimeMilliseconds()), ("limit", policy.BatchSize));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ArchiveCandidate>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)), Convert.ToInt32(reader.GetValue(4))));
        }

        return result;
    }

    public Task<IReadOnlyList<Guid>> ArchiveCompletedAsync(ArchivePolicy policy, AdministrativeCommand command, CancellationToken cancellationToken = default)
    {
        ValidateArchivePolicy(policy);

        return AdminAsync<IReadOnlyList<Guid>>(command, "archives.create", "completed executions", policy, async (connection, transaction, ct) =>
        {
            var archived = new List<Guid>();
            var candidates = await ReadCandidatesAsync(connection, transaction, policy, ct);
            foreach (var candidate in candidates)
            {
                await ArchiveCandidateAsync(connection, transaction, candidate, ct);
                archived.Add(candidate.Id);
            }

            return archived;
        }, cancellationToken);
    }

    private async Task ArchiveCandidateAsync(DbConnection connection, DbTransaction transaction, ArchiveCandidate candidate, CancellationToken ct)
    {
        var predicate = candidate.Kind == "Flow" ? "workflow_run_id=@id" : "id=@id";
        await EnsureArchiveFitsAsync(connection, transaction, candidate, predicate, ct);
        var jobs = await ReadJobsAsync(connection, transaction, $"SELECT * FROM dj_jobs WHERE {predicate} ORDER BY id", ct, ("id", candidate.Id.ToString()));
        var evidence = new List<ArchivedJob>();
        foreach (var job in jobs)
        {
            evidence.Add(await ReadArchivedJobAsync(connection, transaction, job, ct));
        }

        var events = await ReadArchivedEventsAsync(connection, transaction, candidate, predicate, ct);
        var (flow, definition, input, signals) = await ReadArchivedFlowAsync(connection, transaction, candidate, ct);
        var now = await NowAsync(connection, transaction, ct);
        var package = new ExecutionArchive(1, candidate.Id, candidate.Kind, candidate.CorrelationId, DateTimeOffset.FromUnixTimeMilliseconds(now), evidence, flow, definition, input, events, signals);
        var json = JsonSerializer.Serialize(package, JobJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > 32_000_000)
        {
            throw new StateConflictException("Archive exceeds 32 MB; export this execution separately.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        await ExecuteAsync(connection, transaction, "INSERT INTO dj_archives(id,kind,correlation_id,archived_at,jobs,sha256,body) VALUES (@id,@kind,@correlation,@now,@jobs,@hash,@body)", ct,
            ("id", candidate.Id.ToString()), ("kind", candidate.Kind), ("correlation", candidate.CorrelationId), ("now", now), ("jobs", jobs.Count), ("hash", hash), ("body", json));
        foreach (var job in jobs)
        {
            await SaveJobReceiptAsync(connection, transaction, job, ct);
            await ExecuteAsync(connection, transaction, "DELETE FROM dj_attempts WHERE job_id=@id; DELETE FROM dj_failures WHERE job_id=@id; DELETE FROM dj_progress WHERE job_id=@id; DELETE FROM dj_jobs WHERE id=@id", ct, ("id", job.Id.ToString()));
        }

        if (flow is not null)
        {
            await ExecuteAsync(connection, transaction, "DELETE FROM dj_signal_waits WHERE run_id=@id; DELETE FROM dj_signals WHERE run_id=@id; DELETE FROM dj_flows WHERE id=@id", ct, ("id", flow.Id.ToString()));
        }
    }

    public async Task<IReadOnlyList<ArchiveSummary>> ArchivesAsync(string? correlationId = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, null, "SELECT id,kind,correlation_id,archived_at,jobs,sha256,CASE WHEN body IS NULL THEN 0 ELSE 1 END AS has_body FROM dj_archives WHERE (CAST(@correlation AS TEXT) IS NULL OR correlation_id=@correlation) ORDER BY archived_at DESC,id LIMIT @limit", ("correlation", correlationId), ("limit", limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ArchiveSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadArchiveSummary(reader));
        }

        return result;
    }

    public async Task<ArchiveExport?> ExportArchiveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, null, "SELECT *,CASE WHEN body IS NULL THEN 0 ELSE 1 END AS has_body FROM dj_archives WHERE id=@id", ("id", id.ToString()));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(reader.GetOrdinal("body")))
        {
            return null;
        }

        return new(ReadArchiveSummary(reader), reader.GetString(reader.GetOrdinal("body")));
    }

    private static ArchiveSummary ReadArchiveSummary(DbDataReader reader) => new(Guid.Parse(reader.GetString(reader.GetOrdinal("id"))), reader.GetString(reader.GetOrdinal("kind")), Text(reader, "correlation_id"), Date(reader, "archived_at")!.Value, reader.GetInt32(reader.GetOrdinal("jobs")), reader.GetString(reader.GetOrdinal("sha256")), reader.GetInt32(reader.GetOrdinal("has_body")) == 1);

    public Task<bool> PurgeArchiveAsync(Guid id, string expectedSha256, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, "archives.purge", id.ToString(), new
        {
            expectedSha256
        }, async (connection, transaction, ct) =>
        {
            var hash = await ScalarAsync(connection, transaction, "SELECT sha256 FROM dj_archives WHERE id=@id", ct, ("id", id.ToString()));
            if (hash is not string saved || !string.Equals(saved, expectedSha256, StringComparison.Ordinal))
            {
                throw new StateConflictException("Verify the exported archive hash before removing its stored body.");
            }

            return await ExecuteAsync(connection, transaction, "UPDATE dj_archives SET body=NULL WHERE id=@id AND body IS NOT NULL", ct, ("id", id.ToString())) == 1;
        }, cancellationToken);
}
