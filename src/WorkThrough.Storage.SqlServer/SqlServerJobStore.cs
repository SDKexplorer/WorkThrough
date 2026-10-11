using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace WorkThrough.Storage;

/// <summary>SQL Server storage with transaction-owned admission locking and snapshot inspection.</summary>
/// <remarks>Use a dedicated database with ALLOW_SNAPSHOT_ISOLATION enabled. Identity strings may not end in a space.</remarks>
public sealed partial class SqlServerJobStore : RelationalJobStore
{
    private const int PartitionedSchemaPredecessor = 4;
    private readonly string connectionString;

    public SqlServerJobStore(string connectionString)
    {
        var settings = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(settings.InitialCatalog) ||
            new[] { "master", "model", "msdb", "tempdb" }.Contains(settings.InitialCatalog, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Select a dedicated WorkThrough database explicitly; system databases are not supported.", nameof(connectionString));
        }

        this.connectionString = settings.ConnectionString;
    }

    protected override DbConnection CreateConnection() => new SqlConnection(connectionString);
    protected override string ClockSql => "SELECT DATEDIFF_BIG(millisecond,CONVERT(datetime2,'19700101',112),SYSUTCDATETIME())";
    protected override string PageSql(string limit, string offset = "0") => $"OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY";
    protected override string LengthSql(string expression) => $"(DATALENGTH({expression})/2)";
    protected override string SubstringFunction => "SUBSTRING";
    protected override string TextType => "nvarchar(max)";
    protected override string FloatingPointType => "float";

    protected override string UpsertSql(string table, string columns, string values, string keys, string updates = "")
    {
        var supplied = columns.Split(',').Zip(values.Split(',')).ToDictionary(pair => pair.First, pair => pair.Second);
        var predicate = string.Join(" AND ", keys.Split(',').Select(key => $"{key}={supplied[key]}"));
        var insert = $"INSERT INTO {table}({columns}) VALUES ({values});";
        if (updates.Length == 0)
        {
            return $"IF NOT EXISTS(SELECT 1 FROM {table} WHERE {predicate}) {insert}";
        }

        var assignments = string.Join(",", updates.Split(',').Select(column => $"{column}={supplied[column]}"));

        // All callers already hold the same transaction-owned exclusive store lock.
        return $"UPDATE {table} SET {assignments} WHERE {predicate}; IF @@ROWCOUNT=0 {insert}";
    }

    protected override DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] values)
    {
        foreach (var (name, value) in values)
        {
            if (value is string text && IsIdentityParameter(name) && text.EndsWith(' '))
            {
                throw new ArgumentException($"SQL Server identity parameter '{name}' cannot end in a space. No identity is trimmed or normalized.");
            }
        }

        var command = base.Command(connection, transaction, sql, values);
        foreach (SqlParameter parameter in command.Parameters)
        {
            if (parameter.Value is string or DBNull)
            {
                parameter.SqlDbType = SqlDbType.NVarChar;
                parameter.Size = parameter.Value is string { Length: > 4000 } ? -1 : 4000;
            }
        }

        return command;
    }

    private static bool IsIdentityParameter(string name) => name is "id" or "job" or "run" or "flow" or "worker" or "key" or "partition" or "tenant"
        or "name" or "correlation" or "queue" or "step" or "target" or "alias" or "group" or "destination" or "action"
        || name.StartsWith("queue", StringComparison.Ordinal) || name.StartsWith("key", StringComparison.Ordinal)
        || name.StartsWith("tenant", StringComparison.Ordinal);

    protected override async Task<DbTransaction> BeginReadAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await connection.BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken);

    protected override async Task<DbTransaction> BeginWriteAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await ExecuteAsync(connection, transaction, """
                SET XACT_ABORT ON;
                DECLARE @result int;
                EXEC @result=sys.sp_getapplock @Resource=N'WorkThrough:admission',@LockMode='Exclusive',
                  @LockOwner='Transaction',@LockTimeout=5000;
                IF @result<0 THROW 51000,'WorkThrough admission lock was not acquired.',1;
                """, cancellationToken);

            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    public override Task InitializeAsync(CancellationToken cancellationToken = default) => WriteAsync(async (connection, transaction, ct) =>
    {
        if (Convert.ToInt32(await ScalarAsync(connection, transaction,
            "SELECT snapshot_isolation_state FROM sys.databases WHERE database_id=DB_ID()", ct)) != 1)
        {
            throw new InvalidOperationException("Enable ALLOW_SNAPSHOT_ISOLATION on the WorkThrough database before initialization.");
        }

        await ExecuteAsync(connection, transaction, "IF OBJECT_ID(N'dj_schema',N'U') IS NULL CREATE TABLE dj_schema(version int NOT NULL PRIMARY KEY)", ct);
        var version = await ScalarAsync(connection, transaction, "SELECT MAX(version) FROM dj_schema", ct);
        // Schema 4 stores upgrade in place; stop schema-4 writers first because they ignore the new admission policies.
        if (version is not null and not DBNull && Convert.ToInt32(version) is not (PartitionedSchemaPredecessor or SchemaVersion))
        {
            throw new InvalidOperationException("Unsupported SQL Server WorkThrough schema version. Stop writers and use a compatible migration.");
        }

        await ExecuteAsync(connection, transaction, SqlServerSchema, ct);
        await ExecuteAsync(connection, transaction, SqlServerAdmissionSchema, ct);
        await ExecuteAsync(connection, transaction, SqlServerAdmissionIndexes, ct);
        await ExecuteAsync(connection, transaction, """
            IF NOT EXISTS(SELECT 1 FROM dj_store) INSERT INTO dj_store(id) VALUES (@id);
            IF NOT EXISTS(SELECT 1 FROM dj_schema WHERE version=@version) INSERT INTO dj_schema(version) VALUES (@version);
            """, ct, ("id", Guid.NewGuid().ToString("N")), ("version", SchemaVersion));

        return true;
    }, cancellationToken);
}
