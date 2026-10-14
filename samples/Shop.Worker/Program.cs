using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shop;
using Shop.Jobs;
using WorkThrough;

// A headless worker: no HTTP listener. Scale out by running more copies against the same store.
//   --describe-worker  prints what this build can execute
//   --preflight        checks that this build covers every job address, version and queue the store still needs (exit 2 if not)
var preflight = args.Contains("--preflight", StringComparer.Ordinal);
var describe = args.Contains("--describe-worker", StringComparer.Ordinal);
var builder = Host.CreateApplicationBuilder(args.Where(arg => arg is not ("--preflight" or "--describe-worker")).ToArray());
var settings = ShopSetup.Settings(builder.Configuration);
builder.Services.AddWorkThrough()
    .UseShopStorage(settings)
    .SeedShopPolicies(settings)
    .AddShopJobs(settings)
    .AddScheduler()
    .AddWorker(options =>
    {
        options.MaxConcurrentJobs = settings.WorkerSlots;
        options.Queues = ShopJobRegistration.Queues;
    });
using var host = builder.Build();
if (!preflight && !describe)
{
    await host.RunAsync();

    return;
}

var catalog = host.Services.GetRequiredService<JobCatalog>();
var worker = host.Services.GetRequiredService<IOptions<JobWorkerOptions>>().Value;
var capability = new WorkerCapability("candidate", catalog.Registrations.Select(registration => registration.Identity).ToArray(), worker.Queues);
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
