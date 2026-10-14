using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using MySqlConnector;

namespace WorkThrough.Storage;

public sealed partial class MySqlJobStore
{
    private const string MigrationInstalling = "Installing";
    private const string MigrationReady = "Ready";
    private const int MigrationLockSeconds = 30;

    /// <summary>The oldest servers whose locking, JSON and constraint behaviour this provider relies on.</summary>
    private static readonly Version MinimumMySql = new(8, 0, 21);
    private static readonly Version MinimumMariaDb = new(10, 6);

    /// <summary>
    /// Installs or resumes the schema under a named session lock. MySQL commits each DDL statement on its own, so every step is checked
    /// before it runs and checkpointed after, and runtime hosts refuse a store until the last step is recorded.
    /// </summary>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // A pooled session would keep the named lock after closing; migrations use their own session.
        var settings = new MySqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using var connection = new MySqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, SessionSql, cancellationToken);
        var tableOptions = $"ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE={await ExactCollationAsync(connection, cancellationToken)}";
        var lockName = "workthrough:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(database)))[..40];
        if (Convert.ToInt32(await ScalarAsync(connection, null, "SELECT GET_LOCK(@name,@seconds)", cancellationToken,
            ("name", lockName), ("seconds", MigrationLockSeconds))) != 1)
        {
            throw new InvalidOperationException("The MySQL schema migration lock was not acquired.");
        }

        try
        {
            await MigrateAsync(connection, tableOptions, cancellationToken);
        }
        finally
        {
            await ScalarAsync(connection, null, "SELECT RELEASE_LOCK(@name)", CancellationToken.None, ("name", lockName));
        }
    }

    private async Task MigrateAsync(MySqlConnection connection, string tableOptions, CancellationToken ct)
    {
        await ExecuteAsync(connection, null, $"""
            CREATE TABLE IF NOT EXISTS dj_schema(version INT PRIMARY KEY,status VARCHAR(20) NOT NULL DEFAULT 'Installing',
              step INT NOT NULL DEFAULT 0,definition_hash VARCHAR(64) NOT NULL DEFAULT 'unknown') {tableOptions}
            """, ct);
        var state = await ReadMigrationStateAsync(connection, ct);
        var checkpoint = 0;
        if (state is null)
        {
            await ExecuteAsync(connection, null, "INSERT INTO dj_schema(version,status,step,definition_hash) VALUES(@version,@status,0,@hash)", ct,
                ("version", SchemaVersion), ("status", MigrationInstalling), ("hash", SchemaFingerprint));
        }
        else if (state.IsCompletedPredecessor)
        {
            await ExecuteAsync(connection, null, """
                UPDATE dj_schema SET version=@version,status=@status,definition_hash=@hash WHERE version=@previous AND status=@ready
                """, ct, ("version", SchemaVersion), ("status", MigrationInstalling), ("hash", SchemaFingerprint), ("previous", state.Version),
                ("ready", MigrationReady));
            checkpoint = state.Step;
        }
        else if (state.Version > SchemaVersion)
        {
            await UsesNewerSchemaAsync(connection, null, state.Version, ct);

            return;
        }
        else
        {
            if (state.Version != SchemaVersion || state.Fingerprint != SchemaFingerprint || state.Step < 0 || state.Step > MySqlSchemaSteps.Length)
            {
                throw new InvalidOperationException("Unsupported MySQL migration version or definition. Use the same compatible migration to resume.");
            }

            if (state.Status == MigrationReady)
            {
                return;
            }

            checkpoint = state.Step;
        }

        for (var index = checkpoint; index < MySqlSchemaSteps.Length; index++)
        {
            var step = MySqlSchemaSteps[index];
            if (!await SchemaStepAppliedAsync(connection, step, ct))
            {
                // DDL commits on its own. If the checkpoint below fails, the next initializer finds the object and skips it.
                await ExecuteAsync(connection, null, step.Sql.Replace("{table}", tableOptions, StringComparison.Ordinal), ct);
            }

            await ExecuteAsync(connection, null, "UPDATE dj_schema SET step=@step WHERE version=@version AND status=@status", ct,
                ("step", index + 1), ("version", SchemaVersion), ("status", MigrationInstalling));
        }

        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var seed in new[]
        {
            "INSERT INTO dj_admission(id) SELECT 1 FROM dual WHERE NOT EXISTS(SELECT 1 FROM dj_admission WHERE id=1)",
            "INSERT INTO dj_maintenance(id) SELECT 1 FROM dual WHERE NOT EXISTS(SELECT 1 FROM dj_maintenance WHERE id=1)",
            "INSERT INTO dj_registry(id,revision) SELECT 1,0 FROM dual WHERE NOT EXISTS(SELECT 1 FROM dj_registry WHERE id=1)",
            "INSERT INTO dj_event_sequence(id,value) SELECT 1,0 FROM dual WHERE NOT EXISTS(SELECT 1 FROM dj_event_sequence WHERE id=1)",
            "INSERT INTO dj_queue_clock(id,value) SELECT 1,0 FROM dual WHERE NOT EXISTS(SELECT 1 FROM dj_queue_clock WHERE id=1)"
        })
        {
            await ExecuteAsync(connection, transaction, seed, ct);
        }

        await ExecuteAsync(connection, transaction,
            "INSERT INTO dj_store(id) SELECT @id FROM dual WHERE NOT EXISTS(SELECT 1 FROM dj_store)", ct, ("id", Guid.NewGuid().ToString("N")));
        await SeedCompatibilityAsync(connection, transaction, newStore: state is null, ct);
        await SeedLimitLocksAsync(connection, transaction, ct);
        await ExecuteAsync(connection, transaction, "UPDATE dj_schema SET status=@status WHERE version=@version AND step=@step", ct,
            ("status", MigrationReady), ("version", SchemaVersion), ("step", MySqlSchemaSteps.Length));
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// A binary collation that does not pad, so <c>'a'</c> and <c>'a '</c> are different identities. MySQL and MariaDB name it differently.
    /// </summary>
    private async Task<string> ExactCollationAsync(MySqlConnection connection, CancellationToken ct)
    {
        var reported = (string)(await ScalarAsync(connection, null, "SELECT VERSION()", ct))!;
        var mariaDb = reported.Contains("MariaDB", StringComparison.OrdinalIgnoreCase);
        var version = Version.Parse(new string(reported.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()));
        if (version < (mariaDb ? MinimumMariaDb : MinimumMySql))
        {
            throw new InvalidOperationException($"WorkThrough needs MySQL {MinimumMySql} or MariaDB {MinimumMariaDb} or later; the server is {reported}.");
        }

        return mariaDb ? "utf8mb4_nopad_bin" : "utf8mb4_0900_bin";
    }

    private sealed record MigrationState(int Version, string Status, int Step, string Fingerprint)
    {
        /// <summary>A finished earlier installation, which this schema extends by resuming after its last step.</summary>
        public bool IsCompletedPredecessor => Status == MigrationReady && Predecessors.Contains((Version, Step, Fingerprint));
    }

    private async Task<MigrationState?> ReadMigrationStateAsync(MySqlConnection connection, CancellationToken ct)
    {
        await using var command = Command(connection, null, "SELECT version,status,step,definition_hash FROM dj_schema");
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var state = new MigrationState(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3));
        if (state.Status is not (MigrationInstalling or MigrationReady) || await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException("Unrecognized MySQL migration state.");
        }

        return state;
    }

    private async Task<bool> SchemaStepAppliedAsync(MySqlConnection connection, SchemaStep step, CancellationToken ct) => step.Column is null
        ? Convert.ToInt32(await ScalarAsync(connection, null, step.Index
            ? "SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema=DATABASE() AND index_name=@name"
            : "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name=@name", ct, ("name", step.Name))) > 0
        : Convert.ToInt32(await ScalarAsync(connection, null, ColumnExistsSql, ct, ("table", step.Name), ("column", step.Column))) > 0;

    private async Task RequireReadyAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        int version;
        await using (var command = Command(connection, transaction, "SELECT version,status,step,definition_hash FROM dj_schema"))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                throw IncompleteSchema();
            }

            version = reader.GetInt32(0);
            var current = version == SchemaVersion && reader.GetString(1) == MigrationReady && reader.GetInt32(2) == MySqlSchemaSteps.Length
                && reader.GetString(3) == SchemaFingerprint;
            if (!current && version <= SchemaVersion || await reader.ReadAsync(ct))
            {
                throw IncompleteSchema();
            }
        }

        // A newer schema, installed or still installing, is usable while it declares this build a compatible reader.
        await UsesNewerSchemaAsync(connection, transaction, version, ct);
    }

    private static InvalidOperationException IncompleteSchema() =>
        new("The MySQL WorkThrough schema is incomplete or incompatible. Complete initialization before starting runtime hosts.");
}
