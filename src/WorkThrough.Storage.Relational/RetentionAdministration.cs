using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore : IRetentionAdministration, IRetentionQueries
{
    public Task<int> ConfigureRetentionAsync(RetentionPolicy policy, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default)
    {
        ValidateRetentionPolicy(policy);

        return AdminAsync(command, "retention.configure", "retention policy", new { policy, expectedRevision }, async (connection, transaction, ct) =>
        {
            var current = await ReadRetentionPolicyAsync(connection, transaction, ct);
            if ((current?.Revision ?? 0) != expectedRevision)
            {
                throw new StateConflictException("Retention policy changed. Refresh before configuring it.");
            }

            var revision = checked(expectedRevision + 1);
            await ExecuteAsync(connection, transaction, UpsertSql("dj_retention_policy", "id,revision,policy", "1,@revision,@policy", "id", "revision,policy"), ct, ("revision", revision), ("policy", JsonSerializer.Serialize(policy, JobJson.Options)));

            return revision;
        }, cancellationToken);
    }

    public Task<RetentionHold> SetRetentionHoldAsync(Guid unitId, bool held, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, "retention.hold", unitId.ToString(), new { held, expectedRevision }, async (connection, transaction, ct) =>
        {
            var resolved = await ScalarAsync(connection, transaction, "SELECT COALESCE(workflow_run_id,id) FROM dj_jobs WHERE id=@id", ct, ("id", unitId.ToString())) as string ?? unitId.ToString();
            var exists = Convert.ToInt64(await ScalarAsync(connection, transaction, """
                SELECT (SELECT COUNT(*) FROM dj_jobs WHERE id=@id)+(SELECT COUNT(*) FROM dj_flows WHERE id=@id)
                  +(SELECT COUNT(*) FROM dj_archives WHERE id=@id)
                """, ct, ("id", resolved))) > 0;
            if (!exists)
            {
                throw new StateConflictException("No live or archived retention unit has this ID.");
            }

            var revision = Convert.ToInt32(await ScalarAsync(connection, transaction, "SELECT revision FROM dj_retention_holds WHERE unit_id=@id", ct, ("id", resolved)) ?? 0);
            if (revision != expectedRevision)
            {
                throw new StateConflictException("Retention hold changed. Refresh before acting.");
            }

            await ExecuteAsync(connection, transaction, UpsertSql("dj_retention_holds", "unit_id,held,revision", "@id,@held,@revision", "unit_id", "held,revision"), ct, ("id", resolved), ("held", held ? 1 : 0), ("revision", checked(revision + 1)));

            return new RetentionHold(Guid.Parse(resolved), held, revision + 1);
        }, cancellationToken);

    public Task<bool> RetryRetentionIncidentAsync(Guid unitId, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, "retention.retry", unitId.ToString(), new { unitId }, async (connection, transaction, ct) =>
            await ExecuteAsync(connection, transaction, "DELETE FROM dj_retention_incidents WHERE unit_id=@id", ct, ("id", unitId.ToString())) == 1, cancellationToken);

    public async Task<RetentionHold?> FindRetentionHoldAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        using var timeout = OperationQueryTimeout(cancellationToken);
        await using var connection = await OpenAsync(timeout.Token);
        await using var transaction = await BeginReadAsync(connection, timeout.Token);
        await using var command = OperationCommand(connection, transaction, "SELECT held,revision FROM dj_retention_holds WHERE unit_id=@id", ("id", unitId.ToString()));
        await using var reader = await command.ExecuteReaderAsync(timeout.Token);
        if (!await reader.ReadAsync(timeout.Token))
        {
            return null;
        }

        return new(unitId, reader.GetInt32(0) == 1, reader.GetInt32(1));
    }

    public async Task<RetentionStatus> ReadRetentionStatusAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = OperationQueryTimeout(cancellationToken);
        var ct = timeout.Token;
        await using var connection = await OpenAsync(ct);
        await using var transaction = await BeginReadAsync(connection, ct);
        var (storeId, now) = await ReadOperationContextAsync(connection, transaction, ct);
        var configuration = await ReadRetentionPolicyAsync(connection, transaction, ct);
        Guid? pending;
        DateTimeOffset? expires;
        MaintenanceResult? last;
        string? failure;
        DateTimeOffset? failedAt;
        await using (var command = OperationCommand(connection, transaction, """
            SELECT m.batch_id,m.lease_expires_at,b.result,m.failure_code,m.failed_at FROM dj_maintenance m
            LEFT JOIN dj_maintenance_batches b ON b.id=m.last_batch_id WHERE m.id=1
            """))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            pending = ParseGuid(Text(reader, "batch_id"));
            expires = Date(reader, "lease_expires_at");
            last = reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<MaintenanceResult>(reader.GetString(2), JobJson.Options);
            failure = Text(reader, "failure_code");
            failedAt = Date(reader, "failed_at");
        }

        var holds = Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM dj_retention_holds WHERE held=1", ct));
        var incidents = Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM dj_retention_incidents", ct));
        var examples = new List<RetentionIncident>();
        await using (var command = OperationCommand(connection, transaction, $"SELECT unit_id,code,at FROM dj_retention_incidents ORDER BY at DESC,unit_id {PageSql("100")} "))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                examples.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));
            }
        }

        var position = await ReadEventPositionAsync(connection, transaction, ct);
        var slowest = await ScalarAsync(connection, transaction, "SELECT MIN(sequence) FROM dj_subscriptions", ct);
        await transaction.CommitAsync(ct);

        return new(storeId, now, configuration, pending, expires > now, expires, last, holds, incidents, examples,
            position.RetainedAfter, position.LastSequence, slowest is null or DBNull ? null : Convert.ToInt64(slowest), failure, failedAt);
    }

    private async Task<StoredRetentionPolicy?> ReadRetentionPolicyAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = OperationCommand(connection, transaction, "SELECT policy,revision FROM dj_retention_policy WHERE id=1");
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new(JsonSerializer.Deserialize<RetentionPolicy>(reader.GetString(0), JobJson.Options)!, reader.GetInt32(1));
    }

    private async Task RejectHeldArchiveAsync(DbConnection connection, DbTransaction transaction, Guid id, CancellationToken ct)
    {
        if (Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM dj_retention_holds WHERE unit_id=@id AND held=1", ct, ("id", id.ToString()))) > 0)
        {
            throw new StateConflictException("This archive is on hold. Its evidence must be retained.");
        }
    }

    private static void ValidateRetentionPolicy(RetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        foreach (var duration in new[] { policy.SucceededAfter, policy.FailedAfter, policy.PurgeAfter, policy.EventsAfter })
        {
            if (duration is { } value && (value < TimeSpan.FromMilliseconds(1) || value > TimeSpan.FromDays(36500)))
            {
                throw new ArgumentOutOfRangeException(nameof(policy), "Retention durations must be between one millisecond and 100 years.");
            }
        }

        if (policy.BatchSize is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "A maintenance batch contains 1–10 units.");
        }

        if ((policy.PurgeAfter is null) != (policy.ExportDestination is null)
            || policy.ExportDestination is { } destination && (string.IsNullOrWhiteSpace(destination) || destination.Length > 100))
        {
            throw new ArgumentException("Automatic purge needs an explicit export destination of 1–100 characters and a separate retention duration.", nameof(policy));
        }
    }
}
