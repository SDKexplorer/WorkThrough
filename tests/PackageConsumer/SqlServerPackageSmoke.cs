using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WorkThrough;
using WorkThrough.Storage;

internal static class SqlServerPackageSmoke
{
    public static async Task RunAsync()
    {
        var admin = Environment.GetEnvironmentVariable("DURABLEJOBS_SQLSERVER");
        if (string.IsNullOrEmpty(admin))
        {
            Console.WriteLine("SQL Server package consumer not run: no disposable connection supplied.");

            return;
        }

        var database = "wt_package_" + Guid.NewGuid().ToString("N");
        await using var connection = new SqlConnection(admin);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{database}]; ALTER DATABASE [{database}] SET ALLOW_SNAPSHOT_ISOLATION ON;";
        await command.ExecuteNonQueryAsync();
        var cs = new SqlConnectionStringBuilder(admin) { InitialCatalog = database, Pooling = false }.ToString();
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddWorkThrough().UseSqlServer(cs).AddGeneratedJobs(typeof(PackageJob).Assembly)
                .AddWorker(options => options.PollInterval = TimeSpan.FromMilliseconds(20));
            using var host = builder.Build();
            await host.StartAsync();
            try
            {
                var id = await host.Services.GetRequiredService<JobClient>().EnqueueAsync(new PackageJob("sqlserver-package"));
                var queries = host.Services.GetRequiredService<IJobQueries>();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while ((await queries.GetAsync(id, timeout.Token))?.Status != JobStatus.Succeeded)
                {
                    await Task.Delay(20, timeout.Token);
                }

                if ((await host.Services.GetRequiredService<IExecutionQueries>().FindExecutionAsync(id)).Execution?.Status != JobStatus.Succeeded)
                {
                    throw new InvalidOperationException("SQL Server packaged queries did not observe the completed job.");
                }

                _ = await host.Services.GetRequiredService<IRetentionQueries>().ReadRetentionStatusAsync();
                _ = await host.Services.GetRequiredService<IRuntimeQueries>().ReadRuntimeMetricsAsync(new());
                Console.WriteLine("PASS: SQL Server NuGet consumer executed generated registration, a hosted worker and metadata queries.");
            }
            finally
            {
                await host.StopAsync();
            }
        }
        finally
        {
            command.CommandText = $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];";
            await command.ExecuteNonQueryAsync();
        }
    }
}
