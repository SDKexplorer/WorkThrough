using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore : IExecutionArchives
{
    private const int ArchiveFormatVersion = 1;
    private const int MaximumArchivePage = 500;

    private const string EligibleArchiveUnitsSql = """
        SELECT j.id,'Job' AS kind,j.correlation_id,j.terminal_at AS completed,1 AS jobs FROM dj_jobs j
        WHERE j.workflow_run_id IS NULL AND NOT EXISTS (SELECT 1 FROM dj_retention_holds h WHERE h.unit_id=j.id AND h.held=1) AND
          ((j.status IN ('Succeeded','Skipped') AND j.terminal_at<=@success) OR
           (j.status IN ('Failed','Cancelled','Rejected','Expired') AND j.terminal_at<=@failure))
        UNION ALL
        SELECT f.id,'Flow' AS kind,f.correlation_id,MAX(j.terminal_at) AS completed,COUNT(*) AS jobs
        FROM dj_flows f JOIN dj_jobs j ON j.workflow_run_id=f.id WHERE f.status IN ('Succeeded','Failed')
          AND NOT EXISTS (SELECT 1 FROM dj_retention_holds h WHERE h.unit_id=f.id AND h.held=1)
        GROUP BY f.id,f.correlation_id,f.status
        HAVING COUNT(*)=COUNT(j.terminal_at)
          AND MAX(j.terminal_at)<=CASE WHEN f.status='Succeeded' THEN @success ELSE @failure END
        """;

    private static void ValidateArchivePolicy(ArchivePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.BatchSize is < 1 or > ArchivePolicy.MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), $"Archive batches contain 1–{ArchivePolicy.MaximumBatchSize} units; a whole flow is one unit.");
        }
    }

    public async Task<IReadOnlyList<ArchiveCandidate>> ArchiveCandidatesAsync(ArchivePolicy policy, CancellationToken cancellationToken = default)
    {
        ValidateArchivePolicy(policy);
        await using var connection = await OpenAsync(cancellationToken);

        return await ReadCandidatesAsync(connection, null, policy, cancellationToken);
    }

    private async Task<IReadOnlyList<ArchiveCandidate>> ReadCandidatesAsync(DbConnection connection, DbTransaction? transaction, ArchivePolicy policy, CancellationToken ct, bool maintenance = false)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT id,kind,correlation_id,completed,jobs FROM ({EligibleArchiveUnitsSql}) candidates
            WHERE (@maintenance=0 OR NOT EXISTS (SELECT 1 FROM dj_retention_incidents i WHERE i.unit_id=candidates.id))
            ORDER BY completed,id {PageSql("@limit")}
            """, ("success", policy.SucceededBefore.ToUnixTimeMilliseconds()), ("failure", policy.FailedBefore?.ToUnixTimeMilliseconds()),
            ("limit", policy.BatchSize), ("maintenance", maintenance ? 1 : 0));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ArchiveCandidate>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new(Guid.Parse(reader.GetString(0)), Enum.Parse<ArchiveKind>(reader.GetString(1)), Text(reader, "correlation_id"),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)), Convert.ToInt32(reader.GetValue(4))));
        }

        return result;
    }

    public Task<Result<IReadOnlyList<Guid>>> ArchiveCompletedAsync(ArchivePolicy policy, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync<IReadOnlyList<Guid>>(command, AuditActions.CreateArchives, "completed executions", policy, async (connection, transaction, ct) =>
        {
            var archived = new List<Guid>();
            foreach (var candidate in await ReadCandidatesAsync(connection, transaction, policy, ct))
            {
                await ArchiveCandidateAsync(connection, transaction, candidate, ct);
                archived.Add(candidate.Id);
            }

            return archived;
        }, cancellationToken, validate: () => ValidateArchivePolicy(policy));

    private async Task ArchiveCandidateAsync(DbConnection connection, DbTransaction transaction, ArchiveCandidate candidate, CancellationToken ct)
    {
        await EnsureArchiveFitsAsync(connection, transaction, candidate, ct);
        var jobs = await ReadJobsAsync(connection, transaction, $"SELECT * FROM dj_jobs WHERE {ArchivePredicate(candidate.Kind)} ORDER BY id", ct, ("id", candidate.Id.ToString()));
        var evidence = new List<ArchivedJob>();
        foreach (var job in jobs)
        {
            evidence.Add(await ReadArchivedJobAsync(connection, transaction, job, ct));
        }

        var events = await ReadArchivedEventsAsync(connection, transaction, candidate, ct);
        var flow = candidate.Kind == ArchiveKind.Flow ? await ReadArchivedFlowAsync(connection, transaction, candidate.Id, ct) : null;
        var now = await NowAsync(connection, transaction, ct);
        var package = new ExecutionArchive(ArchiveFormatVersion, candidate.Id, candidate.Kind, candidate.CorrelationId, DateTimeOffset.FromUnixTimeMilliseconds(now),
            evidence, flow?.Run, flow?.Definition, flow?.Input, events, flow?.Signals ?? []);
        var json = JsonSerializer.Serialize(package, JobJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > ExecutionArchive.MaximumBytes)
        {
            throw new StateConflictException("Archive exceeds 32 MB; export this execution separately.");
        }

        await ExecuteAsync(connection, transaction, "INSERT INTO dj_archives(id,kind,correlation_id,archived_at,jobs,sha256,body) VALUES (@id,@kind,@correlation,@now,@jobs,@hash,@body)", ct,
            ("id", candidate.Id.ToString()), ("kind", candidate.Kind.ToString()), ("correlation", candidate.CorrelationId), ("now", now), ("jobs", jobs.Count),
            ("hash", Convert.ToHexString(SHA256.HashData(bytes))), ("body", json));
        foreach (var job in jobs)
        {
            await DeleteArchivedJobAsync(connection, transaction, job, ct);
        }

        if (flow is not null)
        {
            foreach (var table in new[] { "dj_signal_waits", "dj_signals" })
            {
                await ExecuteAsync(connection, transaction, $"DELETE FROM {table} WHERE run_id=@id", ct, ("id", candidate.Id.ToString()));
            }

            await ExecuteAsync(connection, transaction, "DELETE FROM dj_flows WHERE id=@id", ct, ("id", candidate.Id.ToString()));
        }

        await ExecuteAsync(connection, transaction, "DELETE FROM dj_retention_incidents WHERE unit_id=@id", ct, ("id", candidate.Id.ToString()));
    }

    private async Task DeleteArchivedJobAsync(DbConnection connection, DbTransaction transaction, JobRecord job, CancellationToken ct)
    {
        await SaveJobReceiptAsync(connection, transaction, job, ct);
        foreach (var table in new[] { "dj_attempts", "dj_failures", "dj_progress" })
        {
            await ExecuteAsync(connection, transaction, $"DELETE FROM {table} WHERE job_id=@id", ct, ("id", job.Id.ToString()));
        }

        await ExecuteAsync(connection, transaction, "DELETE FROM dj_jobs WHERE id=@id", ct, ("id", job.Id.ToString()));
    }

    public async Task<IReadOnlyList<ArchiveSummary>> ArchivesAsync(string? correlationId = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaximumArchivePage)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, null, $"""
            SELECT id,kind,correlation_id,archived_at,jobs,sha256,CASE WHEN body IS NULL THEN 0 ELSE 1 END AS has_body FROM dj_archives
            WHERE (CAST(@correlation AS {TextType}) IS NULL OR correlation_id=@correlation) ORDER BY archived_at DESC,id {PageSql("@limit")}
            """, ("correlation", correlationId), ("limit", limit));
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
        await using var command = Command(connection, null, "SELECT a.*,CASE WHEN body IS NULL THEN 0 ELSE 1 END AS has_body FROM dj_archives a WHERE id=@id", ("id", id.ToString()));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(reader.GetOrdinal("body")))
        {
            return null;
        }

        return new(ReadArchiveSummary(reader), reader.GetString(reader.GetOrdinal("body")));
    }

    private static ArchiveSummary ReadArchiveSummary(DbDataReader reader) => new(Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
        Enum.Parse<ArchiveKind>(reader.GetString(reader.GetOrdinal("kind"))), Text(reader, "correlation_id"), Date(reader, "archived_at")!.Value,
        reader.GetInt32(reader.GetOrdinal("jobs")), reader.GetString(reader.GetOrdinal("sha256")), reader.GetInt32(reader.GetOrdinal("has_body")) == 1);

    public Task<Result<bool>> PurgeArchiveAsync(Guid id, string expectedSha256, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, AuditActions.PurgeArchive, id.ToString(), new { expectedSha256 }, async (connection, transaction, ct) =>
        {
            await RejectHeldArchiveAsync(connection, transaction, id, ct);
            var hash = await ScalarAsync(connection, transaction, "SELECT sha256 FROM dj_archives WHERE id=@id", ct, ("id", id.ToString()));
            if (hash is not string saved || !string.Equals(saved, expectedSha256, StringComparison.Ordinal))
            {
                throw new StateConflictException("Verify the exported archive hash before removing its stored body.");
            }

            return await ExecuteAsync(connection, transaction, "UPDATE dj_archives SET body=NULL WHERE id=@id AND body IS NOT NULL", ct, ("id", id.ToString())) == 1;
        }, cancellationToken);
}
