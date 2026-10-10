using System.Data;
using System.Security.Cryptography;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace WorkThrough.Storage;

public sealed partial class OracleJobStore
{
    private static readonly Lazy<string> MigrationFingerprint = new(ComputeSchemaFingerprint);
    private static string ComputeSchemaFingerprint() => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join("\n", OracleSchema.Select(step => step.Sql.ReplaceLineEndings("\n"))))));
    private static string SchemaFingerprint => MigrationFingerprint.Value;

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

        var checkpoint = 0;
        var exists = false;
        await using (var command = Command(connection, null, "SELECT version,status,step,definition_hash FROM dj_schema"))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                exists = true;
                checkpoint = reader.GetInt32(2);
                if (reader.GetInt32(0) != 4 || reader.GetString(3) != SchemaFingerprint || checkpoint < 0 || checkpoint > OracleSchema.Length)
                {
                    throw new InvalidOperationException("Unsupported Oracle migration version or definition. Use the same compatible migration to resume.");
                }

                var ready = reader.GetString(1) == "Ready";
                if (reader.GetString(1) is not ("Installing" or "Ready") || await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException("Unrecognized Oracle migration state.");
                }

                if (ready)
                {
                    if (checkpoint != OracleSchema.Length)
                    {
                        throw new InvalidOperationException("Oracle migration completion does not match its checkpoint.");
                    }

                    return;
                }
            }
        }

        if (!exists)
        {
            await MigrationWriteAsync(connection, "INSERT INTO dj_schema(version,status,step,definition_hash) VALUES(4,'Installing',0,@hash)", cancellationToken,
                ("hash", SchemaFingerprint));
        }

        for (var index = checkpoint; index < OracleSchema.Length; index++)
        {
            var step = OracleSchema[index];
            if (!await SchemaObjectExistsAsync(connection, step.Name, step.Index, cancellationToken))
            {
                // DDL commits independently. If acknowledgment/checkpointing fails, the next initializer rechecks this object.
                await ExecuteAsync(connection, null, step.Sql, cancellationToken);
            }

            await MigrationWriteAsync(connection, "UPDATE dj_schema SET step=@step WHERE version=4 AND status='Installing'", cancellationToken,
                ("step", index + 1));
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var seed in new[]
        {
            "INSERT INTO dj_admission(id) SELECT 1 FROM dual WHERE NOT EXISTS(SELECT 1 FROM dj_admission WHERE id=1)",
            "INSERT INTO dj_maintenance(id) SELECT 1 FROM dual WHERE NOT EXISTS(SELECT 1 FROM dj_maintenance WHERE id=1)",
            "INSERT INTO dj_registry(id,revision) SELECT 1,0 FROM dual WHERE NOT EXISTS(SELECT 1 FROM dj_registry WHERE id=1)",
            "INSERT INTO dj_event_sequence(id,value) SELECT 1,0 FROM dual WHERE NOT EXISTS(SELECT 1 FROM dj_event_sequence WHERE id=1)"
        })
        {
            await ExecuteAsync(connection, transaction, seed, cancellationToken);
        }

        await ExecuteAsync(connection, transaction,
            "INSERT INTO dj_store(id) SELECT @id FROM dual WHERE NOT EXISTS(SELECT 1 FROM dj_store)", cancellationToken, ("id", Guid.NewGuid().ToString("N")));
        await ExecuteAsync(connection, transaction, "UPDATE dj_schema SET status='Ready' WHERE version=4 AND step=@step", cancellationToken,
            ("step", OracleSchema.Length));
        await transaction.CommitAsync(cancellationToken);
    }

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
