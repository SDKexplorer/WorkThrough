using Microsoft.Extensions.DependencyInjection;
using WorkThrough.Storage;

// Builder extensions live in the builder's namespace, so registering storage needs no extra using directive.
namespace WorkThrough;

public static class MySqlRegistration
{
    /// <summary>Stores work in a dedicated MySQL 8.4+ or MariaDB 11.4+ database named by the connection string.</summary>
    public static WorkThroughBuilder UseMySql(this WorkThroughBuilder builder, string connectionString)
    {
        builder.UseStore(new MySqlJobStore(connectionString));
        builder.Services.AddSingleton<RelationalJobStore>(provider => provider.GetRequiredService<MySqlJobStore>());

        return builder;
    }
}
