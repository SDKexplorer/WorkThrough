using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;

namespace WorkThrough.Storage;

/// <summary>Oracle schema/credential selection. Initialize separately when runtime credentials have DML-only grants.</summary>
public sealed record OracleStorageOptions(string? Schema = null, string? MigrationConnectionString = null);

/// <summary>Oracle storage with a singleton admission-row lock and serializable inspection snapshots.</summary>
public sealed partial class OracleJobStore : RelationalJobStore
{
    private readonly string connectionString;
    private readonly OracleStorageOptions options;
    private readonly string schema;

    public OracleJobStore(string connectionString, OracleStorageOptions? options = null)
    {
        var settings = new OracleConnectionStringBuilder(connectionString);
        this.options = options ?? new();
        schema = (this.options.Schema ?? settings.UserID).ToUpperInvariant();
        if (!Regex.IsMatch(schema, "^[A-Z][A-Z0-9_]{0,127}$") || schema is "SYS" or "SYSTEM")
        {
            throw new ArgumentException("Choose a dedicated Oracle schema using an unquoted identifier.", nameof(options));
        }

        this.connectionString = settings.ConnectionString;
    }

    protected override DbConnection CreateConnection() => new OracleConnection(connectionString);
    protected override string PageSql(string limit, string offset = "0") => $"OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY";
    protected override string TextType => "VARCHAR2(2000 CHAR)";
    protected override string FloatingPointType => "BINARY_DOUBLE";
    protected override string AttemptNumberSql => "\"NUMBER\"";
    protected override string DiagnosticExceptionSql => "COALESCE(f.exception_type,TO_CLOB('Unspecified'))";
    protected override string ClockSql => """
        SELECT FLOOR(EXTRACT(DAY FROM elapsed)*86400000+EXTRACT(HOUR FROM elapsed)*3600000+
          EXTRACT(MINUTE FROM elapsed)*60000+EXTRACT(SECOND FROM elapsed)*1000)
        FROM (SELECT SYS_EXTRACT_UTC(SYSTIMESTAMP)-TIMESTAMP '1970-01-01 00:00:00' AS elapsed FROM dual)
        """;

    protected override async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = (OracleConnection)await base.OpenAsync(ct);
        try
        {
            await ConfigureSessionAsync(connection, ct);
            await RequireReadyAsync(connection, null, ct);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task ConfigureSessionAsync(OracleConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, null, $"ALTER SESSION SET CURRENT_SCHEMA={schema}", ct);
        await ExecuteAsync(connection, null, "ALTER SESSION SET NLS_COMP=BINARY", ct);
        await ExecuteAsync(connection, null, "ALTER SESSION SET NLS_SORT=BINARY", ct);
    }

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
            var current = version == SchemaVersion && reader.GetString(1) == MigrationReady && reader.GetInt32(2) == OracleSchemaSteps.Length
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
        new("The Oracle WorkThrough schema is incomplete or incompatible. Complete initialization before starting runtime hosts.");

    // ORA-08177 can surface wrapped in ORA-00604 when recursive dictionary SQL meets concurrent DDL.
    private const int SerializationConflict = 8177;

    protected override bool IsReadSnapshotConflict(DbException error) => error is OracleException oracle
        && (oracle.Number == SerializationConflict || oracle.Errors.Cast<OracleError>().Any(e => e.Number == SerializationConflict)
            || oracle.Message.Contains("ORA-08177", StringComparison.Ordinal));

    protected override async Task<DbTransaction> BeginReadAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

    /// <summary>Owned-row writes do not lock the admission row; their lease token or worker ID fences them.</summary>
    protected override async Task<DbTransaction> BeginOwnedWriteAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

    /// <summary>Settlements and unrestricted claims do not lock the admission row; flow and job row locks order them against other writers.</summary>
    protected override async Task<DbTransaction> BeginRowLockedWriteAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

    protected override string SeedLimitLocksSql => $"""
        INSERT INTO dj_limit_locks(id) SELECT i FROM (SELECT LEVEL-1 AS i FROM dual CONNECT BY LEVEL<={LimitLockRows}) n
        WHERE NOT EXISTS (SELECT 1 FROM dj_limit_locks l WHERE l.id=n.i)
        """;

    protected override string LockingSelectSql(string columns, string table, string predicate, bool skipLocked) =>
        $"SELECT {columns} FROM {table} WHERE {predicate} FOR UPDATE{(skipLocked ? " SKIP LOCKED" : "")}";

    /// <summary>
    /// Oracle cannot lock a row-limited query, and an ordered FOR UPDATE locks every matching row when it opens. Claims therefore read
    /// candidate IDs first and lock only as many as they still need.
    /// </summary>
    protected override bool LocksCandidatesByIdentity => true;

    protected override async Task<DbTransaction> BeginWriteAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            // This row belongs to this schema. No DBMS_LOCK privilege is required for runtime writers.
            if (Convert.ToInt32(await ScalarAsync(connection, transaction, "SELECT id FROM dj_admission WHERE id=1 FOR UPDATE WAIT 5", cancellationToken)) != 1)
            {
                throw new InvalidOperationException("The Oracle admission row is missing; writes are refused.");
            }

            await RequireReadyAsync(connection, transaction, cancellationToken);

            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    protected override string UpsertSql(string table, string columns, string values, string keys, string updates = "")
    {
        var names = columns.Split(',');
        var source = string.Join(",", names.Zip(values.Split(',')).Select(pair => $"{pair.Second} AS {pair.First}"));
        var predicate = string.Join(" AND ", keys.Split(',').Select(key => $"target.{key}=source.{key}"));
        var change = updates.Length == 0 ? "" : "WHEN MATCHED THEN UPDATE SET " +
            string.Join(",", updates.Split(',').Select(column => $"target.{column}=source.{column}"));

        return $"MERGE INTO {table} target USING (SELECT {source} FROM dual) source ON ({predicate}) {change} " +
            $"WHEN NOT MATCHED THEN INSERT ({columns}) VALUES ({string.Join(",", names.Select(column => $"source.{column}"))})";
    }

    // Match only bind markers outside SQL string literals. Caller business SQL never passes through this method.
    private static readonly Regex BindTokens = new("'(?:''|[^'])*'|@[A-Za-z][A-Za-z0-9_]*", RegexOptions.Compiled);

    /// <summary>Oracle commands carry one statement; an anonymous block runs several in one round trip.</summary>
    protected override string BatchSql(IReadOnlyList<string> statements) => $"BEGIN\n{string.Join(";\n", statements)};\nEND;";

    protected override DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] values)
    {
        var supplied = values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var bound = BindTokens.Replace(sql, match =>
        {
            if (match.Value[0] != '@')
            {
                return match.Value;
            }

            var name = match.Value[1..];
            if (!supplied.TryGetValue(name, out var value))
            {
                throw new InvalidOperationException($"The library SQL bind '{name}' has no value.");
            }

            if (value is string { Length: 0 })
            {
                if (BaseParameterName(name) is "error" or "description" or "message" or "stack" or "handler" or "type")
                {
                    return "EMPTY_CLOB()";
                }

                throw new ArgumentException($"Oracle does not accept an empty identity/filter '{name}'; use null for an absent value. JSON empty strings remain supported.");
            }

            used.Add(name);

            return ":wt_" + name;
        });
        var command = (OracleCommand)connection.CreateCommand();
        command.Transaction = (OracleTransaction?)transaction;
        command.BindByName = true;
        command.CommandText = bound;
        foreach (var name in used)
        {
            var value = supplied[name];
            var type = value switch
            {
                int => OracleDbType.Int32,
                long => OracleDbType.Int64,
                string text when text.Length > 2000 => OracleDbType.Clob,
                null when BaseParameterName(name) is "failure" or "expiry" or "deadline" or "timeout" or "next" => OracleDbType.Int64,
                null when BaseParameterName(name) is "output" or "error" or "description" or "stack" => OracleDbType.Clob,
                _ => OracleDbType.Varchar2
            };
            command.Parameters.Add(new OracleParameter("wt_" + name, type) { Value = value ?? DBNull.Value });
        }

        return command;
    }
}
