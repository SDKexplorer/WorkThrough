using System.Data.Common;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore
{
    private const int MaximumProcessorAliases = 1000;

    public async Task<ProcessorRegistrySnapshot> ProcessorRegistryAsync(CancellationToken cancellationToken = default)
    {
        return await ReadAsync<ProcessorRegistrySnapshot>(async (connection, transaction, ct) =>
        {
            var snapshot = await ReadRegistryAsync(connection, transaction, ct);

            return snapshot;
        }, cancellationToken);
    }

    private async Task<ProcessorRegistrySnapshot> ReadRegistryAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var revision = await ReadRegistryRevisionAsync(connection, transaction, ct);
        var aliases = new List<ProcessorAliasMapping>();
        await using var command = Command(connection, transaction, "SELECT alias_key,alias_version,target_key,target_version FROM dj_processor_aliases ORDER BY alias_key,alias_version");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            aliases.Add(new(new(reader.GetString(0), reader.GetInt32(1)), new(reader.GetString(2), reader.GetInt32(3))));
        }

        return new(revision, aliases);
    }

    private async Task<long> ReadRegistryRevisionAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct) =>
        Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT revision FROM dj_registry WHERE id=1", ct));

    public Task<Result<long>> SetProcessorAliasesAsync(IReadOnlyList<ProcessorAliasMapping> aliases, long expectedRevision,
        AdministrativeCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        var ordered = aliases.Where(a => a?.Alias is not null).OrderBy(a => a.Alias.Key, StringComparer.Ordinal).ThenBy(a => a.Alias.Version).ToArray();

        return AdminAsync(command, AuditActions.SetProcessorAliases, "registry", new { aliases = ordered, expectedRevision }, async (connection, transaction, ct) =>
        {
            var previous = await ReadRegistryAsync(connection, transaction, ct);
            if (previous.Revision != expectedRevision)
            {
                throw new StateConflictException("Processor registry revision changed.");
            }

            foreach (var old in previous.Aliases)
            {
                var replacement = ordered.SingleOrDefault(a => a.Alias == old.Alias)
                    ?? throw new StateConflictException("Historical aliases cannot be removed; saved work, producers, or restored backups may still require them.");
                if (replacement.Target != old.Target && !ordered.Any(a => a.Alias == old.Target && a.Target == replacement.Target))
                {
                    throw new StateConflictException("A further rename must retain the former target as a direct alias too.");
                }
            }

            var now = await NowAsync(connection, transaction, ct);
            if (Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM dj_jobs WHERE status='Running' AND lease_expires_at>@now", ct, ("now", now))) != 0)
            {
                throw new StateConflictException("Drain active leases before changing processor mappings.");
            }

            await ExecuteAsync(connection, transaction, "DELETE FROM dj_processor_aliases", ct);
            await ExecuteAsync(connection, transaction, "DELETE FROM dj_processor_groups", ct);
            foreach (var alias in ordered)
            {
                await ExecuteAsync(connection, transaction, "INSERT INTO dj_processor_aliases(alias_key,alias_version,target_key,target_version) VALUES (@alias,@version,@target,@targetVersion)", ct,
                    ("alias", alias.Alias.Key), ("version", alias.Alias.Version), ("target", alias.Target.Key), ("targetVersion", alias.Target.Version));
            }

            foreach (var group in AdmissionGroups(ordered))
            {
                await ExecuteAsync(connection, transaction, "INSERT INTO dj_processor_groups(job_key,group_key) VALUES (@key,@group)", ct,
                    ("key", group.Key), ("group", group.First()));
            }

            var revision = checked(expectedRevision + 1);
            await ExecuteAsync(connection, transaction, "UPDATE dj_registry SET revision=@revision WHERE id=1", ct, ("revision", revision));

            return revision;
        }, cancellationToken, validate: () => ValidateAliases(aliases));
    }

    /// <summary>Every key named by an alias, with the canonical keys that would share its admission group.</summary>
    private static IGrouping<string, string>[] AdmissionGroups(IEnumerable<ProcessorAliasMapping> aliases) => aliases
        .SelectMany(a => new[] { (Key: a.Alias.Key, Group: a.Target.Key), (Key: a.Target.Key, Group: a.Target.Key) })
        .GroupBy(a => a.Key, a => a.Group).ToArray();

    private static void ValidateAliases(IReadOnlyList<ProcessorAliasMapping> aliases)
    {
        if (aliases.Count > MaximumProcessorAliases
            || aliases.Any(a => a?.Alias is null || a.Target is null || a.Alias == a.Target || a.Alias.Key == SignalWait.Processor.Key || a.Target.Key == SignalWait.Processor.Key)
            || aliases.Select(a => a.Alias).Distinct().Count() != aliases.Count
            || aliases.Any(a => aliases.Any(b => b.Alias == a.Target)))
        {
            throw new ArgumentException($"Provide at most {MaximumProcessorAliases} unique, direct aliases without cycles, chains, or self aliases.", nameof(aliases));
        }

        if (AdmissionGroups(aliases).Any(group => group.Distinct().Count() != 1))
        {
            throw new ArgumentException("All versions of a key must share one admission group.", nameof(aliases));
        }
    }
}
