using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using WorkThrough;

internal static class MySqlPackageSmoke
{
    public static async Task RunAsync()
    {
        foreach (var (name, variable) in new[] { ("MySQL", "DURABLEJOBS_MYSQL"), ("MariaDB", "DURABLEJOBS_MARIADB") })
        {
            var admin = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(admin))
            {
                Console.WriteLine($"{name} package consumer not run: no disposable connection supplied.");

                continue;
            }

            await RunAsync(name, admin);
        }
    }

    private static async Task RunAsync(string name, string admin)
    {
        var database = "wt_package_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(admin, $"CREATE DATABASE {database}");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddWorkThrough().UseMySql(new MySqlConnectionStringBuilder(admin) { Database = database }.ConnectionString)
                .AddGeneratedJobs(typeof(PackageJob).Assembly)
                .AddWorker(options => options.PollInterval = TimeSpan.FromMilliseconds(20));
            using var host = builder.Build();
            await host.StartAsync();
            try
            {
                var id = await host.Services.GetRequiredService<JobClient>().EnqueueAsync(new PackageJob("mysql-package"));
                var queries = host.Services.GetRequiredService<IJobQueries>();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while ((await queries.GetAsync(id, timeout.Token))?.Status != JobStatus.Succeeded)
                {
                    await Task.Delay(20, timeout.Token);
                }

                if ((await host.Services.GetRequiredService<IExecutionQueries>().FindExecutionAsync(id)).Execution?.Status != JobStatus.Succeeded)
                {
                    throw new InvalidOperationException($"{name} packaged queries did not observe the completed job.");
                }

                _ = await host.Services.GetRequiredService<IRuntimeQueries>().ReadRuntimeMetricsAsync(new());
                Console.WriteLine($"PASS: {name} NuGet consumer executed generated registration, a hosted worker and metadata queries.");
            }
            finally
            {
                await host.StopAsync();
            }
        }
        finally
        {
            await MySqlConnection.ClearAllPoolsAsync();
            await ExecuteAsync(admin, $"DROP DATABASE {database}");
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
