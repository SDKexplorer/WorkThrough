using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

/// <summary>Schema reader ranges and the engine protocol, which let hosts of different releases share a store (ADR-021).</summary>
public abstract partial class RelationalJobStore : IEngineProtocol
{
    /// <summary>The first schema that records compatibility. Earlier stores cannot tell older code that a newer schema is safe.</summary>
    protected const int CompatibilitySchemaVersion = 6;

    /// <summary>
    /// The oldest code schema version that remains correct against the schema this package writes. An additive schema keeps it;
    /// a schema that older code would misread raises it to its own version.
    /// </summary>
    protected const int ReaderMinimum = CompatibilitySchemaVersion;

    /// <summary>
    /// True when the store is newer than this package but declares this package a compatible reader, so it is used without migrating.
    /// Throws when the newer schema excludes this package.
    /// </summary>
    protected async Task<bool> UsesNewerSchemaAsync(DbConnection connection, DbTransaction? transaction, int stored, CancellationToken ct)
    {
        if (stored <= SchemaVersion)
        {
            return false;
        }

        var minimum = await ScalarAsync(connection, transaction, "SELECT reader_minimum FROM dj_compatibility WHERE id=1", ct);
        if (minimum is null or DBNull || Convert.ToInt32(minimum) > SchemaVersion)
        {
            throw new InvalidOperationException(
                $"The WorkThrough schema is version {stored} and no longer accepts version-{SchemaVersion} hosts. Upgrade this host.");
        }

        return true;
    }

    /// <summary>
    /// Records this schema's reader minimum. A new store starts at this build's protocol, because no older host can share it yet;
    /// an upgraded store stays at the baseline until its fleet supports more.
    /// </summary>
    protected Task SeedCompatibilityAsync(DbConnection connection, DbTransaction transaction, bool newStore, CancellationToken ct) =>
        ExecuteBatchAsync(connection, transaction, ct,
            new Statement("""
                INSERT INTO dj_compatibility(id,reader_minimum,protocol) SELECT 1,@minimum,@protocol FROM dj_registry
                WHERE id=1 AND NOT EXISTS (SELECT 1 FROM dj_compatibility WHERE id=1)
                """, ("minimum", ReaderMinimum), ("protocol", newStore ? EngineProtocol.Supported : EngineProtocol.Baseline)),
            new Statement("UPDATE dj_compatibility SET reader_minimum=@minimum WHERE id=1 AND reader_minimum<@minimum", ("minimum", ReaderMinimum)));

    public Task<EngineProtocolState> EngineProtocolAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(async (connection, transaction, ct) =>
        {
            var active = await ActiveProtocolAsync(connection, transaction, ct);

            return new EngineProtocolState(active, EngineProtocol.Supported,
                active < EngineProtocol.Supported ? await ProtocolBlockersAsync(connection, transaction, EngineProtocol.Supported, ct) : []);
        }, cancellationToken);

    public Task<EngineProtocolState> ActivateEngineProtocolAsync(int level, CancellationToken cancellationToken = default)
    {
        if (level is < EngineProtocol.Baseline or > EngineProtocol.Supported)
        {
            throw new ArgumentOutOfRangeException(nameof(level), $"This build supports engine protocols {EngineProtocol.Baseline}–{EngineProtocol.Supported}.");
        }

        // The admission lock orders activation before every later claim, and claims refuse a level above their build.
        return WriteAsync(async (connection, transaction, ct) =>
        {
            var active = await ActiveProtocolAsync(connection, transaction, ct);
            if (active >= level)
            {
                return new EngineProtocolState(active, EngineProtocol.Supported, []);
            }

            var blockers = await ProtocolBlockersAsync(connection, transaction, level, ct);
            if (blockers.Count > 0)
            {
                return new EngineProtocolState(active, EngineProtocol.Supported, blockers);
            }

            await ExecuteAsync(connection, transaction, "UPDATE dj_compatibility SET protocol=@level WHERE id=1 AND protocol<@level", ct, ("level", level));

            return new EngineProtocolState(level, EngineProtocol.Supported, []);
        }, cancellationToken);
    }

    /// <summary>Refuses a write that this build could misinterpret because the store requires a newer protocol.</summary>
    private async Task RequireProtocolAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var active = await ActiveProtocolAsync(connection, transaction, ct);
        if (active > EngineProtocol.Supported)
        {
            throw new EngineProtocolException(active);
        }
    }

    /// <summary>Refuses to start a run whose definition needs a protocol the store has not activated yet.</summary>
    private async Task RequireDefinitionProtocolAsync(DbConnection connection, DbTransaction transaction, FlowDefinition definition, CancellationToken ct)
    {
        var active = await ActiveProtocolAsync(connection, transaction, ct);
        if (active > EngineProtocol.Supported)
        {
            throw new EngineProtocolException(active);
        }

        if (definition.RequiredProtocol > active)
        {
            throw new StateConflictException($"Flow {definition.Key} v{definition.Version} needs engine protocol {definition.RequiredProtocol}, " +
                $"but the store is at {active}. Upgrade every worker; the protocol activates when no older worker remains.");
        }
    }

    private async Task<int> ActiveProtocolAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct) =>
        Convert.ToInt32(await ScalarAsync(connection, transaction, "SELECT protocol FROM dj_compatibility WHERE id=1", ct));

    /// <summary>
    /// Live workers below <paramref name="level"/>, and workers holding unexpired leases without a live heartbeat at that level.
    /// Expired leases cannot settle, so they never block.
    /// </summary>
    private async Task<IReadOnlyList<string>> ProtocolBlockersAsync(DbConnection connection, DbTransaction? transaction, int level, CancellationToken ct)
    {
        var now = await NowAsync(connection, transaction, ct);
        var ready = new HashSet<string>(StringComparer.Ordinal);
        var blockers = new SortedSet<string>(StringComparer.Ordinal);
        await using (var command = Command(connection, transaction, "SELECT details FROM dj_workers WHERE seen_at>=@fresh",
            ("fresh", now - (long)WorkerHeartbeat.Freshness.TotalMilliseconds)))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var worker = JsonSerializer.Deserialize<WorkerHeartbeat>(reader.GetString(0), JobJson.Options)!;
                if (worker.Protocol >= level)
                {
                    ready.Add(worker.WorkerId);
                }
                else
                {
                    blockers.Add(worker.WorkerId);
                }
            }
        }

        await using (var command = Command(connection, transaction,
            "SELECT DISTINCT worker_id FROM dj_jobs WHERE status='Running' AND lease_expires_at>@now AND worker_id IS NOT NULL", ("now", now)))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var worker = reader.GetString(0);
                if (!ready.Contains(worker))
                {
                    blockers.Add(worker);
                }
            }
        }

        return [.. blockers];
    }
}
