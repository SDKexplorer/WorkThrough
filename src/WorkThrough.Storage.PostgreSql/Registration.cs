using Microsoft.Extensions.DependencyInjection;
using WorkThrough.Storage;

// Builder extensions live in the builder's namespace, so registering storage needs no extra using directive.
namespace WorkThrough;

public static class PostgresRegistration
{
    public static WorkThroughBuilder UsePostgreSql(this WorkThroughBuilder builder, string connectionString) =>
        builder.UsePostgreSql(connectionString, new PostgresStorageOptions());

    public static WorkThroughBuilder UsePostgreSql(this WorkThroughBuilder builder, string connectionString, PostgresStorageOptions options)
    {
        builder.UseStore(new PostgresJobStore(connectionString, options));
        builder.Services.AddSingleton<RelationalJobStore>(provider => provider.GetRequiredService<PostgresJobStore>());

        return builder;
    }
}
