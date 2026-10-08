using WorkThrough;
using WorkThrough.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

var preflight = args.Contains("--preflight", StringComparer.Ordinal);
var describe = args.Contains("--describe-worker", StringComparer.Ordinal);
var builder = Host.CreateApplicationBuilder(args.Where(a => a is not ("--preflight" or "--describe-worker")).ToArray());
builder.Services.AddWorkThrough()
    .UseSqlite(builder.Configuration["Jobs:ConnectionString"] ?? "Data Source=durablejobs-demo.db")
    .AddGeneratedJobs(typeof(Program).Assembly)
    .SeedClusterLimits(new(null, 12), new("orders.dispatch", 2))
    .AddWorker(options => { options.MaxConcurrentJobs = 4; options.Queues = ["default", "shipping"]; });
using var host = builder.Build();
if (preflight || describe)
{
    var catalog = host.Services.GetRequiredService<JobCatalog>();
    var settings = host.Services.GetRequiredService<IOptions<JobWorkerOptions>>().Value;
    var capability = new WorkerCapability("candidate", catalog.Registrations.Select(r => r.Identity).ToArray(), settings.Queues);
    var json = new JsonSerializerOptions(JobJson.Options) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    if (describe) Console.WriteLine(JsonSerializer.Serialize(capability, json));
    if (preflight)
    {
        var report = await host.Services.GetRequiredService<DeploymentPreflight>().CheckAsync([capability]);
        Console.WriteLine(JsonSerializer.Serialize(report, json));
        Environment.ExitCode = report.AllRequirementsCovered ? 0 : 2;
    }
    return;
}
await host.RunAsync();
