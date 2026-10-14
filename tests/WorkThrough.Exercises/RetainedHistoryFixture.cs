using System.Data.Common;

namespace WorkThrough.Exercises;

/// <summary>Seeds explicitly synthetic terminal history; measured live work still uses the engine and real workers.</summary>
internal static class RetainedHistoryFixture
{
    internal const string ProcessorKey = "qualification.retained";
    internal const string Secret = "SECRET-RETAINED-FIXTURE";
    internal static readonly Guid FirstId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    internal static async Task SeedAsync(ExerciseDatabase database, int count, CancellationToken ct)
    {
        if (count is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var observed = (await database.Store.GetExecutionContextAsync(ct)).ObservedAt;
        var start = observed.AddHours(-2).ToUnixTimeMilliseconds();
        await using var connection = database.OpenConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var (numbers, identifier, remainder, correlation) = database.Provider switch
        {
            "postgres" => ($"SELECT generate_series(1,{count}) AS n", "'00000000-0000-0000-0000-'||LPAD(CAST(n AS TEXT),12,'0')", "n%3", "'fixture:'||CAST(n%16 AS TEXT)"),
            "sqlserver" => ($"SELECT TOP ({count}) ROW_NUMBER() OVER(ORDER BY a.object_id,b.object_id) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b", "'00000000-0000-0000-0000-'+RIGHT(REPLICATE('0',12)+CAST(n AS varchar(12)),12)", "n%3", "'fixture:'+CAST(n%16 AS varchar(2))"),
            "oracle" => ($"SELECT LEVEL AS n FROM dual CONNECT BY LEVEL<={count}", "'00000000-0000-0000-0000-'||LPAD(TO_CHAR(n),12,'0')", "MOD(n,3)", "'fixture:'||TO_CHAR(MOD(n,16))"),
            "mysql" or "mariadb" => ($"SELECT n FROM (SELECT 1+{Decimal(0)}+10*{Decimal(1)}+100*{Decimal(2)}+1000*{Decimal(3)}+10000*{Decimal(4)}+100000*{Decimal(5)} AS n FROM {Digits(6)}) d WHERE n<={count}",
                "CONCAT('00000000-0000-0000-0000-',LPAD(n,12,'0'))", "n%3", "CONCAT('fixture:',n%16)"),
            _ => throw new InvalidOperationException("Unknown qualification provider.")
        };
        var failed = database.Provider == "oracle" ? "MOD(n,5)=0" : "n%5=0";
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO dj_jobs(id,job_key,contract_version,payload,queue,status,attempt,max_attempts,
              created_at,available_at,terminal_at,correlation_id)
            SELECT {identifier},'{ProcessorKey}',1,'"{Secret}"','retained-fixture',
              CASE WHEN {failed} THEN 'Failed' ELSE 'Succeeded' END,1+{remainder},3,
              {start}+n,{start}+n,{start}+n+100,{correlation} FROM ({numbers}) numbered
            """, ct);
        var number = database.Provider == "oracle" ? "\"NUMBER\"" : "number";
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await ExecuteAsync(connection, transaction, $"""
                INSERT INTO dj_attempts(job_id,{number},worker_id,started_at,finished_at,status,error)
                SELECT id,{attempt},'retained-fixture',created_at+{attempt * 10},created_at+{attempt * 10 + 5},
                  CASE WHEN attempt={attempt} THEN status ELSE 'Failed' END,
                  CASE WHEN attempt={attempt} AND status='Succeeded' THEN NULL ELSE '{Secret}' END
                FROM dj_jobs WHERE job_key='{ProcessorKey}' AND attempt>={attempt}
                """, ct);
        }

        await transaction.CommitAsync(ct);
        await using var check = connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM dj_jobs WHERE job_key='{ProcessorKey}'";
        if (Convert.ToInt32(await check.ExecuteScalarAsync(ct)) != count)
        {
            throw new InvalidOperationException("The retained-history fixture did not create the requested row count.");
        }
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Digit <paramref name="place"/> of a row of <see cref="Digits"/>.</summary>
    private static string Decimal(int place) => $"d{place}.d";

    /// <summary>Every combination of <paramref name="places"/> decimal digits, for servers without a row generator.</summary>
    private static string Digits(int places) => string.Join(" CROSS JOIN ", Enumerable.Range(0, places).Select(place =>
        "(" + string.Join(" UNION ALL ", Enumerable.Range(0, 10).Select(digit => $"SELECT {digit} AS d")) + $") d{place}"));
}
