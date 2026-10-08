using System.Text.Json;
using System.Text.Json.Serialization;
using Demo.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WorkThrough;
using WorkThrough.Storage;

var preflight = args.Contains("--preflight", StringComparer.Ordinal);
var describe = args.Contains("--describe-worker", StringComparer.Ordinal);
var builder = Host.CreateApplicationBuilder(args.Where(a => a is not ("--preflight" or "--describe-worker")).ToArray());
var settings = builder.Configuration.GetSection(JobsSettings.SectionName).Get<JobsSettings>() ?? new();
builder.Services.AddWorkThrough()
    .UseSqlite(settings.ConnectionString)
    .AddGeneratedJobs(typeof(Program).Assembly)
    .SeedClusterLimits(new(null, settings.FleetConcurrency), new(OrderJobs.DispatchKey, settings.ShippingConcurrency))
    .AddWorker(options =>
    {
        options.MaxConcurrentJobs = settings.LocalConcurrency;
        options.Queues = [OrderJobs.DefaultQueue, OrderJobs.ShippingQueue];
    });
using var host = builder.Build();
if (preflight || describe)
{
    var catalog = host.Services.GetRequiredService<JobCatalog>();
    var workerSettings = host.Services.GetRequiredService<IOptions<JobWorkerOptions>>().Value;
    var capability = new WorkerCapability("candidate", catalog.Registrations.Select(r => r.Identity).ToArray(), workerSettings.Queues);
    var json = new JsonSerializerOptions(JobJson.Options) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    if (describe)
    {
        Console.WriteLine(JsonSerializer.Serialize(capability, json));
    }

    if (preflight)
    {
        var report = await host.Services.GetRequiredService<DeploymentPreflight>().CheckAsync([capability]);
        Console.WriteLine(JsonSerializer.Serialize(report, json));
        Environment.ExitCode = report.AllRequirementsCovered ? 0 : 2;
    }

    return;
}

await host.RunAsync();
