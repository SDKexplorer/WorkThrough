
namespace WorkThrough.Storage;

public static class SqlServerRegistration
{
    public static WorkThroughBuilder UseSqlServer(this WorkThroughBuilder builder, string connectionString) =>
        builder.UseStore(new SqlServerJobStore(connectionString));
}
