using WorkThrough;
using WorkThrough.Storage;

internal static class RefactorStorage
{
    private static string Provider => Environment.GetEnvironmentVariable("WT_REFACTOR_PROVIDER") ?? "sqlite";

    internal static WorkThroughBuilder Configure(WorkThroughBuilder builder, string connection) => Provider switch
    {
        "sqlite" => builder.UseSqlite(connection),
        "postgres" => builder.UsePostgreSql(connection),
        "sqlserver" => builder.UseSqlServer(connection),
        _ => throw new InvalidOperationException("Unknown refactor-proof provider.")
    };

    internal static RelationalJobStore Open(string connection) => Provider switch
    {
        "sqlite" => new SqliteJobStore(connection),
        "postgres" => new PostgresJobStore(connection),
        "sqlserver" => new SqlServerJobStore(connection),
        _ => throw new InvalidOperationException("Unknown refactor-proof provider.")
    };
}
