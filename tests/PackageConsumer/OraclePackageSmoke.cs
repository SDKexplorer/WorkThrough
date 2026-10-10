using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WorkThrough;
using WorkThrough.Storage;

internal static class OraclePackageSmoke
{
    public static async Task RunAsync()
    {
        var admin = Environment.GetEnvironmentVariable("DURABLEJOBS_ORACLE");
        if (string.IsNullOrEmpty(admin))
        {
            Console.WriteLine("Oracle package consumer not run: no disposable connection supplied.");

            return;
        }

        await using var database = await OracleTestDatabase.CreateAsync(admin);
        var cs = database.ConnectionString;
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWorkThrough().UseOracle(cs).AddGeneratedJobs(typeof(PackageJob).Assembly)
            .AddWorker(options => options.PollInterval = TimeSpan.FromMilliseconds(20));
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var id = await host.Services.GetRequiredService<JobClient>().EnqueueAsync(new PackageJob("oracle-package"));
            var queries = host.Services.GetRequiredService<IJobQueries>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while ((await queries.GetAsync(id, timeout.Token))?.Status != JobStatus.Succeeded)
            {
                await Task.Delay(20, timeout.Token);
            }

            if ((await host.Services.GetRequiredService<IExecutionQueries>().FindExecutionAsync(id)).Execution?.Status != JobStatus.Succeeded)
            {
                throw new InvalidOperationException("Oracle packaged queries did not observe the completed job.");
            }

            _ = await host.Services.GetRequiredService<IRetentionQueries>().ReadRetentionStatusAsync();
            _ = await host.Services.GetRequiredService<IRuntimeQueries>().ReadRuntimeMetricsAsync(new());
            Console.WriteLine("PASS: Oracle NuGet consumer executed generated registration, a hosted worker and metadata queries.");
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
