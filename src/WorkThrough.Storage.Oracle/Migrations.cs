using System.Data;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace WorkThrough.Storage;

public sealed partial class OracleJobStore
{
    private const string MigrationInstalling = "Installing";
    private const string MigrationReady = "Ready";

    /// <summary>Resumes an interrupted schema installation under a session lock that survives Oracle DDL commits.</summary>
    /// <remarks>Migration credentials require DDL privileges and EXECUTE on SYS.DBMS_LOCK. Runtime credentials do not.</remarks>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Closing a pooled connection would retain a session lock; migrations deliberately use an unpooled session.
        var settings = new OracleConnectionStringBuilder(options.MigrationConnectionString ?? connectionString) { Pooling = false };
        await using var connection = new OracleConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await ConfigureSessionAsync(connection, cancellationToken);
        await AcquireMigrationLockAsync(connection, cancellationToken);
        var charset = await ScalarAsync(connection, null,
            "SELECT value FROM nls_database_parameters WHERE parameter='NLS_CHARACTERSET'", cancellationToken);
        if (charset is not "AL32UTF8")
        {
            throw new InvalidOperationException("The Oracle provider requires AL32UTF8 database character encoding.");
        }

        if (!await SchemaObjectExistsAsync(connection, "DJ_SCHEMA", false, cancellationToken))
        {
            await ExecuteAsync(connection, null, """
                CREATE TABLE dj_schema(version NUMBER(10) PRIMARY KEY,status VARCHAR2(20) DEFAULT 'Installing' NOT NULL,
                  step NUMBER(10) DEFAULT 0 NOT NULL,definition_hash VARCHAR2(64) DEFAULT 'unknown' NOT NULL) SEGMENT CREATION IMMEDIATE
                """, cancellationToken);
        }

        var state = await ReadMigrationStateAsync(connection, cancellationToken);
        var checkpoint = 0;
        if (state is null)
        {
            await MigrationWriteAsync(connection, "INSERT INTO dj_schema(version,status,step,definition_hash) VALUES(@version,@status,0,@hash)", cancellationToken,
                ("version", SchemaVersion), ("status", MigrationInstalling), ("hash", SchemaFingerprint));
        }
        else if (state.IsCompletedPredecessor)
        {
            await StartUpgradeAsync(connection, state.Version, cancellationToken);
            checkpoint = state.Step;
        }
        else if (state.Version > SchemaVersion && state.Status == MigrationReady)
        {
            await UsesNewerSchemaAsync(connection, null, state.Version, cancellationToken);

            return;
        }
        else
        {
            if (state.Version != SchemaVersion || state.Fingerprint != SchemaFingerprint || state.Step < 0 || state.Step > OracleSchemaSteps.Length)
            {
                throw new InvalidOperationException("Unsupported Oracle migration version or definition. Use the same compatible migration to resume.");
            }

            if (state.Status == MigrationReady)
            {
                if (state.Step != OracleSchemaSteps.Length)
                {
                    throw new InvalidOperationException("Oracle migration completion does not match its checkpoint.");
                }

                return;
            }

            checkpoint = state.Step;
        }

        for (var index = checkpoint; index < OracleSchemaSteps.Length; index++)
        {
            var step = OracleSchemaSteps[index];
            if (!await SchemaStepAppliedAsync(connection, step, cancellationToken))
            {
                // DDL commits independently. If acknowledgment/checkpointing fails, the next initializer rechecks this object.
                await ExecuteAsync(connection, null, step.Sql, cancellationToken);
            }

            await MigrationWriteAsync(connection, "UPDATE dj_schema SET step=@step WHERE version=@version AND status=@status", cancellationToken,
                ("step", index + 1), ("version", SchemaVersion), ("status", MigrationInstalling));
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var seed in new[]
        {
            "INSERT INTO dj_admission(id) SELECT 1 FROM dual WHERE NOT EXISTS(SELECT 1 FROM dj_admission WHERE id=1)",
            "INSERT INTO dj_maintenance(id) SELECT 1 FROM dual WHERE NOT EXISTS(SELECT 1 FROM dj_maintenance WHERE id=1)",
            "INSERT INTO dj_registry(id,revision) SELECT 1,0 FROM dual WHERE NOT EXISTS(SELECT 1 FROM dj_registry WHERE id=1)",
            "INSERT INTO dj_event_sequence(id,value) SELECT 1,0 FROM dual WHERE NOT EXISTS(SELECT 1 FROM dj_event_sequence WHERE id=1)",
            "INSERT INTO dj_queue_clock(id,value) SELECT 1,0 FROM dual WHERE NOT EXISTS(SELECT 1 FROM dj_queue_clock WHERE id=1)"
        })
        {
            await ExecuteAsync(connection, transaction, seed, cancellationToken);
        }

        await ExecuteAsync(connection, transaction,
            "INSERT INTO dj_store(id) SELECT @id FROM dual WHERE NOT EXISTS(SELECT 1 FROM dj_store)", cancellationToken, ("id", Guid.NewGuid().ToString("N")));
        await SeedCompatibilityAsync(connection, transaction, newStore: state is null, cancellationToken);
        await ExecuteAsync(connection, transaction, "UPDATE dj_schema SET status=@status WHERE version=@version AND step=@step", cancellationToken,
            ("status", MigrationReady), ("version", SchemaVersion), ("step", OracleSchemaSteps.Length));
        await transaction.CommitAsync(cancellationToken);
    }

    private sealed record MigrationState(int Version, string Status, int Step, string Fingerprint)
    {
        /// <summary>A finished earlier installation, which this schema extends by resuming after its last step.</summary>
        public bool IsCompletedPredecessor => Status == MigrationReady && Predecessors.Contains((Version, Step, Fingerprint));
    }

    private async Task<MigrationState?> ReadMigrationStateAsync(OracleConnection connection, CancellationToken ct)
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
            throw new InvalidOperationException("Unrecognized Oracle migration state.");
        }

        return state;
    }

    // Stop schema-4 and schema-5 writers first: they predate the compatibility record and would ignore newer semantics.
    private Task StartUpgradeAsync(OracleConnection connection, int previous, CancellationToken ct) => MigrationWriteAsync(connection, """
        UPDATE dj_schema SET version=@version,status=@status,definition_hash=@hash WHERE version=@previous AND status=@ready
        """, ct, ("version", SchemaVersion), ("status", MigrationInstalling), ("hash", SchemaFingerprint),
        ("previous", previous), ("ready", MigrationReady));

    private async Task<bool> SchemaStepAppliedAsync(OracleConnection connection, SchemaStep step, CancellationToken ct) => step.Column is null
        ? await SchemaObjectExistsAsync(connection, step.Name, step.Index, ct)
        : Convert.ToInt32(await ScalarAsync(connection, null,
            "SELECT COUNT(*) FROM all_tab_columns WHERE owner=@owner AND table_name=@name AND column_name=@column", ct,
            ("owner", schema), ("name", step.Name), ("column", step.Column))) > 0;

    private async Task<bool> SchemaObjectExistsAsync(OracleConnection connection, string name, bool index, CancellationToken ct) =>
        Convert.ToInt32(await ScalarAsync(connection, null,
            index ? "SELECT COUNT(*) FROM all_indexes WHERE owner=@owner AND index_name=@name"
                : "SELECT COUNT(*) FROM all_tables WHERE owner=@owner AND table_name=@name", ct, ("owner", schema), ("name", name))) > 0;

    private async Task MigrationWriteAsync(OracleConnection connection, string sql, CancellationToken ct, params (string, object?)[] values)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await ExecuteAsync(connection, transaction, sql, ct, values);
        await transaction.CommitAsync(ct);
    }

    private async Task AcquireMigrationLockAsync(OracleConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = """
            BEGIN
              DBMS_LOCK.ALLOCATE_UNIQUE(:name,:handle);
              :result:=DBMS_LOCK.REQUEST(:handle,6,5,FALSE);
            END;
            """;
        command.Parameters.Add(new OracleParameter("name", OracleDbType.Varchar2) { Value = "WorkThrough:migrate:" + schema });
        command.Parameters.Add(new OracleParameter("handle", OracleDbType.Varchar2, 128) { Direction = ParameterDirection.InputOutput, Value = DBNull.Value });
        command.Parameters.Add(new OracleParameter("result", OracleDbType.Int32) { Direction = ParameterDirection.Output });
        await command.ExecuteNonQueryAsync(ct);
        var result = Convert.ToInt32(((OracleDecimal)command.Parameters["result"].Value).Value);
        if (result is not (0 or 4))
        {
            throw new InvalidOperationException($"The Oracle schema migration lock was not acquired (result {result}).");
        }
    }
}
