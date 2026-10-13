using System.Data;
using System.Data.Common;
using MySqlConnector;

namespace WorkThrough.Storage;

/// <summary>MySQL 8.4 and MariaDB 11.4 storage with a singleton admission-row lock and consistent-snapshot reads.</summary>
/// <remarks>
/// Use a dedicated database named in the connection string. Initialization creates InnoDB tables with an exact binary collation that does
/// not pad, so identities compare byte for byte.
/// </remarks>
public sealed partial class MySqlJobStore : RelationalJobStore
{
    private static readonly string[] SystemDatabases = ["mysql", "sys", "information_schema", "performance_schema"];

    // Strict mode turns an over-long or invalid value into an error instead of a silently truncated identity.
    private const string SessionSql = "SET SESSION sql_mode='STRICT_ALL_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION'";

    private readonly string connectionString;
    private readonly string database;

    public MySqlJobStore(string connectionString)
    {
        var settings = new MySqlConnectionStringBuilder(connectionString) { GuidFormat = MySqlGuidFormat.None };
        if (string.IsNullOrWhiteSpace(settings.Database) || SystemDatabases.Contains(settings.Database, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Select a dedicated WorkThrough database explicitly; system databases are not supported.", nameof(connectionString));
        }

        database = settings.Database;
        this.connectionString = settings.ConnectionString;
    }

    protected override DbConnection CreateConnection() => new MySqlConnection(connectionString);

    // UTC_TIMESTAMP does not depend on the session time zone, so the clock has no daylight-saving gaps.
    protected override string ClockSql => "SELECT TIMESTAMPDIFF(MICROSECOND,'1970-01-01 00:00:00',UTC_TIMESTAMP(6)) DIV 1000";
    protected override string LengthSql(string expression) => $"CHAR_LENGTH({expression})";
    protected override string SubstringFunction => "SUBSTRING";
    protected override string TextType => "CHAR";
    protected override string FloatingPointType => "DOUBLE";
    protected override string ColumnExistsSql =>
        "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table AND column_name=@column";

    protected override string SeedLimitLocksSql => $"""
        INSERT INTO dj_limit_locks(id)
        SELECT n.i FROM (SELECT a.d+16*b.d+256*c.d AS i FROM {Digits} a CROSS JOIN {Digits} b CROSS JOIN {Digits} c) n
        WHERE n.i<{LimitLockRows} AND NOT EXISTS (SELECT 1 FROM dj_limit_locks l WHERE l.id=n.i)
        """;

    private static string Digits => "(" + string.Join(" UNION ALL ", Enumerable.Range(0, 16).Select(digit => $"SELECT {digit} AS d")) + ")";

    protected override async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = await base.OpenAsync(ct);
        try
        {
            // Pooled sessions are reset when returned, so each open applies the session settings again.
            await ExecuteAsync(connection, null, SessionSql, ct);
            await RequireReadyAsync(connection, null, ct);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    protected override async Task<DbTransaction> BeginReadAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await ((MySqlConnection)connection).BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true, cancellationToken);

    /// <summary>Owned-row writes do not lock the admission row; their lease token or worker ID fences them.</summary>
    protected override async Task<DbTransaction> BeginOwnedWriteAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

    /// <summary>Settlements and unrestricted claims do not lock the admission row; flow and job row locks order them against other writers.</summary>
    protected override async Task<DbTransaction> BeginRowLockedWriteAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

    protected override string LockingSelectSql(string columns, string table, string predicate, bool skipLocked) =>
        $"SELECT {columns} FROM {table} WHERE {predicate} FOR UPDATE{(skipLocked ? " SKIP LOCKED" : "")}";

    /// <summary>
    /// InnoDB locks the rows a locking read examines, not only those it returns. An ordered, row-limited page locked more jobs than its
    /// claim could lease, and concurrent claims found nothing free. Claims therefore read candidate IDs first and lock only as many as
    /// they still need.
    /// </summary>
    protected override bool LocksCandidatesByIdentity => true;

    protected override async Task<DbTransaction> BeginWriteAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        // Read committed avoids InnoDB gap locks, which would block inserts next to rows that a writer only read.
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await LockAdmissionAsync(connection, transaction, cancellationToken);

            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    protected override async Task LockAdmissionAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        if (Convert.ToInt32(await ScalarAsync(connection, transaction, "SELECT id FROM dj_admission WHERE id=1 FOR UPDATE", cancellationToken)) != 1)
        {
            throw new InvalidOperationException("The MySQL admission row is missing; writes are refused.");
        }
    }

    /// <summary>
    /// Inserts a row, or updates <paramref name="updates"/> when a unique key already holds it. MySQL resolves a conflict on any unique
    /// key; every table written this way has only its primary key, so that is <paramref name="keys"/>.
    /// </summary>
    protected override string UpsertSql(string table, string columns, string values, string keys, string updates = "")
    {
        var change = updates.Length == 0
            ? $"{keys.Split(',')[0]}={keys.Split(',')[0]}"
            : string.Join(",", updates.Split(',').Select(column => $"{column}=VALUES({column})"));

        return $"INSERT INTO {table}({columns}) VALUES ({values}) ON DUPLICATE KEY UPDATE {change}";
    }
}
