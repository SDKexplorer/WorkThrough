using WorkThrough.Storage;

// Builder extensions live in the builder's namespace, so registering storage needs no extra using directive.
namespace WorkThrough;

public static class SqlServerRegistration
{
    public static WorkThroughBuilder UseSqlServer(this WorkThroughBuilder builder, string connectionString) =>
        builder.UseStore(new SqlServerJobStore(connectionString));
}
