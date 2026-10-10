using System.Data.Common;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore
{
    public async Task<IReadOnlyList<ArchiveSummary>> ReadExportCandidatesAsync(MaintenanceLease lease, CancellationToken cancellationToken = default)
    {
        using var timeout = OperationQueryTimeout(cancellationToken);
        var ct = timeout.Token;
        await using var connection = await OpenAsync(ct);
        await using var transaction = await BeginReadAsync(connection, ct);
        await RequireMaintenanceLeaseAsync(connection, transaction, lease, ct);
        var batch = await ReadMaintenanceBatchAsync(connection, transaction, lease.BatchId, ct);
        var policy = await ReadRetentionPolicyAsync(connection, transaction, ct);
        var candidates = new List<ArchiveSummary>();
        if (policy?.Policy.Enabled == true && policy.Revision == batch.PolicyRevision && batch.PurgeBefore is { } before)
        {
            await using var command = OperationCommand(connection, transaction, $"""
                SELECT a.id,a.kind,a.correlation_id,a.archived_at,a.jobs,a.sha256,1 AS has_body FROM dj_archives a
                WHERE a.body IS NOT NULL AND a.archived_at<=@before
                  AND NOT EXISTS (SELECT 1 FROM dj_retention_holds h WHERE h.unit_id=a.id AND h.held=1)
                  AND NOT EXISTS (SELECT 1 FROM dj_retention_incidents i WHERE i.unit_id=a.id)
                ORDER BY a.archived_at,a.id {PageSql("@limit")}
                """, ("before", before.ToUnixTimeMilliseconds()), ("limit", batch.ArchivePolicy.BatchSize));
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                candidates.Add(ReadArchiveSummary(reader));
            }
        }

        await transaction.CommitAsync(ct);

        return candidates;
    }

    public Task RecordVerifiedExportAsync(MaintenanceLease lease, VerifiedArchiveExport export, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(export);
        if (export.Bytes is < 1 or > 32_000_000 || string.IsNullOrWhiteSpace(export.ObjectVersion) || export.ObjectVersion.Length > 512)
        {
            throw new ArgumentException("Export verification requires a bounded object version and byte count.", nameof(export));
        }

        return WriteAsync(async (connection, transaction, ct) =>
        {
            await RequireMaintenanceLeaseAsync(connection, transaction, lease, ct);
            var batch = await ReadMaintenanceBatchAsync(connection, transaction, lease.BatchId, ct);
            var policy = await ReadRetentionPolicyAsync(connection, transaction, ct);
            if (policy?.Policy.Enabled != true || policy.Revision != batch.PolicyRevision || batch.Destination != export.Destination || batch.PurgeBefore is null)
            {
                throw new StateConflictException("The active maintenance policy does not authorize this export receipt.");
            }

            await RejectHeldArchiveAsync(connection, transaction, export.ArchiveId, ct);
            var expected = await ScalarAsync(connection, transaction, "SELECT sha256 FROM dj_archives WHERE id=@id AND body IS NOT NULL AND archived_at<=@before", ct,
                ("id", export.ArchiveId.ToString()), ("before", batch.PurgeBefore.Value.ToUnixTimeMilliseconds())) as string;
            if (expected is null || !string.Equals(expected, export.Sha256, StringComparison.Ordinal))
            {
                throw new StateConflictException("The verified copy does not match the eligible archive body.");
            }

            await ExecuteAsync(connection, transaction, UpsertSql("dj_archive_exports", "archive_id,destination,sha256,object_version,bytes,verified_at,batch_id", "@id,@destination,@hash,@version,@bytes,@at,@batch", "archive_id,destination", "sha256,object_version,bytes,verified_at,batch_id"), ct, ("id", export.ArchiveId.ToString()), ("destination", export.Destination), ("hash", export.Sha256),
                ("version", export.ObjectVersion), ("bytes", export.Bytes), ("at", await NowAsync(connection, transaction, ct)), ("batch", batch.Id.ToString()));

            return true;
        }, cancellationToken);
    }

    public Task RecordExportIncidentAsync(MaintenanceLease lease, Guid archiveId, CancellationToken cancellationToken = default) =>
        WriteAsync(async (connection, transaction, ct) =>
        {
            await RequireMaintenanceLeaseAsync(connection, transaction, lease, ct);
            if (await ScalarAsync(connection, transaction, "SELECT id FROM dj_archives WHERE id=@id AND body IS NOT NULL", ct, ("id", archiveId.ToString())) is not null)
            {
                await ExecuteAsync(connection, transaction,
                    UpsertSql("dj_retention_incidents", "unit_id,code,at", "@id,'ArchiveExportVerificationFailed',@at", "unit_id", "code,at"),
                    ct, ("id", archiveId.ToString()), ("at", await NowAsync(connection, transaction, ct)));
            }

            return true;
        }, cancellationToken);

    private async Task<IReadOnlyList<Guid>> PurgeVerifiedArchivesAsync(DbConnection connection, DbTransaction transaction, MaintenanceBatch batch, CancellationToken ct)
    {
        var ids = new List<Guid>();
        if (batch.PurgeBefore is not { } before)
        {
            return ids;
        }

        await using (var command = Command(connection, transaction, $"""
            SELECT a.id FROM dj_archives a JOIN dj_archive_exports e ON e.archive_id=a.id
            WHERE a.body IS NOT NULL AND a.archived_at<=@before AND e.destination=@destination AND e.sha256=a.sha256 AND e.batch_id=@batch
              AND NOT EXISTS (SELECT 1 FROM dj_retention_holds h WHERE h.unit_id=a.id AND h.held=1)
            ORDER BY a.archived_at,a.id {PageSql("@limit")}
            """, ("before", before.ToUnixTimeMilliseconds()), ("destination", batch.Destination), ("batch", batch.Id.ToString()), ("limit", batch.ArchivePolicy.BatchSize)))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                ids.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        foreach (var id in ids)
        {
            await ExecuteAsync(connection, transaction, "UPDATE dj_archives SET body=NULL WHERE id=@id", ct, ("id", id.ToString()));
        }

        return ids;
    }
}
