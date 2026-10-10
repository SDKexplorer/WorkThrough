using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore : IRetentionMaintenance
{
    private const int MaximumOwnerLength = 200;
    private const int MaximumEventTrimScan = 1000;
    private static readonly TimeSpan MinimumMaintenanceLease = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumMaintenanceLease = TimeSpan.FromMinutes(5);

    private sealed record MaintenanceBatch(Guid Id, OperationActor Actor, int PolicyRevision, ArchivePolicy ArchivePolicy,
        string? Destination, DateTimeOffset? PurgeBefore, DateTimeOffset? EventsBefore);

    public Task<MaintenanceLease?> AcquireMaintenanceAsync(string owner, OperationActor actor, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        const int label = OperationQueryLimits.MaximumLabelLength;
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > MaximumOwnerLength || actor is null
            || string.IsNullOrWhiteSpace(actor.Issuer) || actor.Issuer.Length > label
            || string.IsNullOrWhiteSpace(actor.Subject) || actor.Subject.Length > label || actor.DisplayName?.Length > label
            || leaseDuration < MinimumMaintenanceLease || leaseDuration > MaximumMaintenanceLease)
        {
            throw new ArgumentException("Maintenance needs a bounded owner/actor and a lease of 10 seconds to 5 minutes.");
        }

        return WriteAsync<MaintenanceLease?>(async (connection, transaction, ct) =>
        {
            var now = await NowAsync(connection, transaction, ct);
            string? pending;
            await using (var command = Command(connection, transaction, "SELECT batch_id,lease_expires_at FROM dj_maintenance WHERE id=1"))
            {
                await using var reader = await command.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                if (!reader.IsDBNull(1) && reader.GetInt64(1) > now)
                {
                    return null;
                }

                pending = Text(reader, "batch_id");
            }

            MaintenanceBatch batch;
            if (pending is not null)
            {
                batch = await ReadMaintenanceBatchAsync(connection, transaction, Guid.Parse(pending), ct);
            }
            else
            {
                var current = await ReadRetentionPolicyAsync(connection, transaction, ct);
                if (current?.Policy.Enabled != true)
                {
                    return null;
                }

                var observed = DateTimeOffset.FromUnixTimeMilliseconds(now);
                var policy = current.Policy;
                batch = new(Guid.NewGuid(), actor, current.Revision,
                    new(observed - policy.SucceededAfter, policy.FailedAfter is { } failed ? observed - failed : null, policy.BatchSize),
                    policy.ExportDestination, policy.PurgeAfter is { } purge ? observed - purge : null,
                    policy.EventsAfter is { } events ? observed - events : null);
                await ExecuteAsync(connection, transaction, "INSERT INTO dj_maintenance_batches(id,descriptor) VALUES (@id,@descriptor)", ct,
                    ("id", batch.Id.ToString()), ("descriptor", JsonSerializer.Serialize(batch, JobJson.Options)));
            }

            var token = Guid.NewGuid();
            var expires = checked(now + (long)leaseDuration.TotalMilliseconds);
            await ExecuteAsync(connection, transaction, "UPDATE dj_maintenance SET batch_id=@batch,token=@token,owner=@owner,lease_expires_at=@expires WHERE id=1", ct,
                ("batch", batch.Id.ToString()), ("token", token.ToString()), ("owner", owner), ("expires", expires));
            var storeId = (string)(await ScalarAsync(connection, transaction, "SELECT id FROM dj_store", ct))!;

            return new(batch.Id, token, DateTimeOffset.FromUnixTimeMilliseconds(expires), storeId, batch.Destination, batch.PurgeBefore, batch.ArchivePolicy.BatchSize);
        }, cancellationToken);
    }

    public async Task<MaintenanceResult> RunMaintenanceBatchAsync(MaintenanceLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var batch = await ReadAsync((connection, transaction, ct) => ReadMaintenanceBatchAsync(connection, transaction, lease.BatchId, ct), cancellationToken);

        var command = new AdministrativeCommand(batch.Id, batch.Actor, "Apply the persisted retention policy in one bounded maintenance batch.");

        // The token is deliberately excluded from the command fingerprint: a replacement host reconciles the same batch.
        return await AuditedAsync(command, AuditActions.RunRetentionBatch, batch.Id.ToString(), batch, async (connection, transaction, ct) =>
        {
            await RequireMaintenanceLeaseAsync(connection, transaction, lease, ct);
            var current = await ReadRetentionPolicyAsync(connection, transaction, ct);
            var archived = new List<Guid>();
            var purged = new List<Guid>();
            var blocked = new List<Guid>();
            var trimmed = 0;
            var applicable = current?.Policy.Enabled == true && current.Revision == batch.PolicyRevision;
            if (applicable)
            {
                var candidates = await ReadCandidatesAsync(connection, transaction, batch.ArchivePolicy, ct, maintenance: true);
                foreach (var candidate in candidates)
                {
                    try
                    {
                        await ArchiveCandidateAsync(connection, transaction, candidate, ct);
                        archived.Add(candidate.Id);
                    }
                    catch (StateConflictException)
                    {
                        // Archive size checks happen before the first write. Provider failures are never swallowed.
                        await RecordRetentionIncidentAsync(connection, transaction, candidate.Id, RetentionIncidentCodes.ArchiveEvidenceLimit, ct);
                        blocked.Add(candidate.Id);
                    }
                }

                purged.AddRange(await PurgeVerifiedArchivesAsync(connection, transaction, batch, ct));
                if (batch.EventsBefore is { } before)
                {
                    trimmed = await TrimMaintenanceEventsAsync(connection, transaction, before, ct);
                }
            }

            await RequireMaintenanceLeaseAsync(connection, transaction, lease, ct);
            var result = new MaintenanceResult(batch.Id, applicable ? MaintenanceOutcomes.Completed : MaintenanceOutcomes.PolicySuperseded,
                DateTimeOffset.FromUnixTimeMilliseconds(await NowAsync(connection, transaction, ct)), archived, purged, blocked, trimmed);
            await ExecuteAsync(connection, transaction, "UPDATE dj_maintenance_batches SET result=@result WHERE id=@id", ct,
                ("result", JsonSerializer.Serialize(result, JobJson.Options)), ("id", batch.Id.ToString()));
            await ExecuteAsync(connection, transaction, """
                UPDATE dj_maintenance SET batch_id=NULL,token=NULL,owner=NULL,lease_expires_at=NULL,last_batch_id=@id,failure_code=NULL,failed_at=NULL WHERE id=1
                """, ct, ("id", batch.Id.ToString()));

            return result;
        }, cancellationToken);
    }

    public Task RecordMaintenanceFailureAsync(MaintenanceLease lease, bool budgetExceeded, CancellationToken cancellationToken = default) =>
        WriteAsync(async (connection, transaction, ct) =>
        {
            await RequireMaintenanceLeaseAsync(connection, transaction, lease, ct);
            await ExecuteAsync(connection, transaction, "UPDATE dj_maintenance SET failure_code=@code,failed_at=@at WHERE id=1", ct,
                ("code", budgetExceeded ? MaintenanceFailureCodes.BatchBudgetExceeded : MaintenanceFailureCodes.BatchFailed), ("at", await NowAsync(connection, transaction, ct)));

            return true;
        }, cancellationToken);

    private async Task<MaintenanceBatch> ReadMaintenanceBatchAsync(DbConnection connection, DbTransaction? transaction, Guid id, CancellationToken ct)
    {
        var json = await ScalarAsync(connection, transaction, "SELECT descriptor FROM dj_maintenance_batches WHERE id=@id", ct, ("id", id.ToString())) as string;

        return json is null ? throw new StateConflictException("This maintenance batch does not exist.")
            : JsonSerializer.Deserialize<MaintenanceBatch>(json, JobJson.Options)!;
    }

    private async Task RequireMaintenanceLeaseAsync(DbConnection connection, DbTransaction transaction, MaintenanceLease lease, CancellationToken ct)
    {
        var now = await NowAsync(connection, transaction, ct);
        if (Convert.ToInt64(await ScalarAsync(connection, transaction, """
            SELECT COUNT(*) FROM dj_maintenance WHERE id=1 AND batch_id=@id AND token=@token AND lease_expires_at>@now
            """, ct, ("id", lease.BatchId.ToString()), ("token", lease.Token.ToString()), ("now", now))) != 1)
        {
            throw new StateConflictException("Maintenance ownership expired or changed. Reacquire before proceeding.");
        }
    }

    private async Task<int> TrimMaintenanceEventsAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset before, CancellationToken ct)
    {
        var position = await ReadEventPositionAsync(connection, transaction, ct);
        var maximum = await ReadSlowestSubscriberAsync(connection, transaction, ct) ?? position.LastSequence;
        var through = position.RetainedAfter;
        await using (var command = Command(connection, transaction, $"""
            SELECT e.sequence,e.at,CASE WHEN EXISTS (SELECT 1 FROM dj_jobs j WHERE j.id=e.job_id)
              OR EXISTS (SELECT 1 FROM dj_retention_holds h WHERE h.unit_id=COALESCE(e.run_id,e.job_id) AND h.held=1)
              THEN 1 ELSE 0 END AS protected
            FROM dj_events e WHERE e.sequence>@floor AND e.sequence<=@maximum ORDER BY e.sequence {PageSql(MaximumEventTrimScan)}
            """, ("floor", position.RetainedAfter), ("maximum", maximum)))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.GetInt64(1) > before.ToUnixTimeMilliseconds() || reader.GetInt32(2) == 1)
                {
                    break;
                }

                through = reader.GetInt64(0);
            }
        }

        return through == position.RetainedAfter ? 0 : await DeleteEventsThroughAsync(connection, transaction, through, ct);
    }

    private async Task RecordRetentionIncidentAsync(DbConnection connection, DbTransaction transaction, Guid unitId, string code, CancellationToken ct) =>
        await ExecuteAsync(connection, transaction, UpsertSql("dj_retention_incidents", "unit_id,code,at", "@id,@code,@at", "unit_id", "code,at"), ct,
            ("id", unitId.ToString()), ("code", code), ("at", await NowAsync(connection, transaction, ct)));
}
