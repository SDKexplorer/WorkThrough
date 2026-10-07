using WorkThrough;
using WorkThrough.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var database = Path.Combine(Path.GetTempPath(), $"durablejobs-package-{Guid.NewGuid():N}.db");
var builder = Host.CreateApplicationBuilder();
builder.Services.AddWorkThrough().UseSqlite($"Data Source={database};Pooling=False")
    .AddGeneratedJobs(typeof(Program).Assembly).AddWorker(options => options.PollInterval = TimeSpan.FromMilliseconds(20));
using var host = builder.Build();
await host.StartAsync();
try
{
    var id = await host.Services.GetRequiredService<JobClient>().EnqueueAsync(new PackageJob("package-only"));
    var rawId = await host.Services.GetRequiredService<JobClient>().EnqueueAsync(ProcessorId.For<RawProcessor>(), "{\"raw\":true}");
    var queries = host.Services.GetRequiredService<IJobQueries>();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while ((await queries.GetAsync(id, deadline.Token))?.Status != JobStatus.Succeeded) await Task.Delay(30, deadline.Token);
    while ((await queries.GetAsync(rawId, deadline.Token))?.Status != JobStatus.Succeeded) await Task.Delay(30, deadline.Token);
    if ((await queries.GetAsync(rawId))!.Output?.GetString() != "{\"raw\":true}") throw new InvalidOperationException("Raw input changed.");
    var flows = host.Services.GetRequiredService<WorkflowClient>();
    var flow = new FlowBuilder("smoke.flow", 1).Step("raw", ProcessorId.For<RawProcessor>(), "flow input").Build();
    var run = await flows.StartAsync(flow, new { }, "package-smoke", "start-once");
    if (run != await flows.StartAsync(flow, new { }, "package-smoke", "start-once")) throw new InvalidOperationException("Duplicate flow start.");
    while ((await queries.GetWorkflowAsync(run, deadline.Token))?.Status != "Succeeded") await Task.Delay(30, deadline.Token);
    Console.WriteLine("PASS: package-only consumer executed generated typed/raw processors and an idempotent flow start.");
}
finally
{
    await host.StopAsync();
    File.Delete(database);
}

[JobContract("smoke.package")]
public sealed record PackageJob(string Value);
public sealed record class PackageHandler : IJobHandler<PackageJob>
{
    public Task<JobResult> HandleAsync(PackageJob job, JobExecutionContext context, CancellationToken cancellationToken) => Task.FromResult(JobResult.From(job.Value));
}

[Processor("smoke.raw")]
public sealed class RawProcessor : IProcessor<string>
{
    public Task<JobResult> ProcessAsync(string input, JobExecutionContext context, CancellationToken cancellationToken) => Task.FromResult(JobResult.From(input));
}
