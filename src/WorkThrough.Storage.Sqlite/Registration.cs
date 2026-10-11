using WorkThrough.Storage;

// Builder extensions live in the builder's namespace, so registering storage needs no extra using directive.
namespace WorkThrough;

public static class SqliteRegistration
{
    public static WorkThroughBuilder UseSqlite(this WorkThroughBuilder builder, string connectionString) =>
        UseSqlite(builder, connectionString, new SqliteStorageOptions());

    public static WorkThroughBuilder UseSqlite(this WorkThroughBuilder builder, string connectionString, SqliteStorageOptions options) =>
        builder.UseStore(new SqliteJobStore(connectionString, options));
}
