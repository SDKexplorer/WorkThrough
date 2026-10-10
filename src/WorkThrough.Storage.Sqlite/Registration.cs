namespace WorkThrough.Storage;

public static class SqliteRegistration
{
    public static WorkThroughBuilder UseSqlite(this WorkThroughBuilder builder, string connectionString) =>
        UseSqlite(builder, connectionString, new SqliteStorageOptions());

    public static WorkThroughBuilder UseSqlite(this WorkThroughBuilder builder, string connectionString, SqliteStorageOptions options) =>
        builder.UseStore(new SqliteJobStore(connectionString, options));
}
