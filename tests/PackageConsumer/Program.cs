using WorkThrough;
using WorkThrough.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var database = Path.Combine(Path.GetTempPath(), $"durablejobs-package-{Guid.NewGuid():N}.db");
var manualDefinition = new JobDefinition<InvoiceInput>("smoke.manual", displayName: "Explicit invoice");
var builder = Host.CreateApplicationBuilder();
builder.Services.AddWorkThrough().UseSqlite($"Data Source={database};Pooling=False")
    .AddGeneratedJobs(typeof(Program).Assembly).AddProcessor<InvoiceInput, ExplicitInvoiceProcessor>(manualDefinition).AddWorker(options => options.PollInterval = TimeSpan.FromMilliseconds(20));
using var host = builder.Build();
await host.StartAsync();
try
{
    var id = await host.Services.GetRequiredService<JobClient>().EnqueueAsync(new PackageJob("package-only"));
    var rawId = await host.Services.GetRequiredService<JobClient>().EnqueueAsync(ProcessorId.For<RawProcessor>(), "{\"raw\":true}");
    var typedId = await host.Services.GetRequiredService<JobClient>().EnqueueAsync(ProcessorId.For<InvoiceProcessor>(), new InvoiceInput("INV-123"));
    var manualId = await host.Services.GetRequiredService<JobClient>().EnqueueAsync(manualDefinition, new InvoiceInput("INV-MANUAL"));
    var queries = host.Services.GetRequiredService<IJobQueries>();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while ((await queries.GetAsync(id, deadline.Token))?.Status != JobStatus.Succeeded)
    {
        await Task.Delay(30, deadline.Token);
    }

    while ((await queries.GetAsync(rawId, deadline.Token))?.Status != JobStatus.Succeeded)
    {
        await Task.Delay(30, deadline.Token);
    }

    while ((await queries.GetAsync(typedId, deadline.Token))?.Status != JobStatus.Succeeded)
    {
        await Task.Delay(30, deadline.Token);
    }

    if ((await queries.GetAsync(rawId))!.Output?.GetString() != "{\"raw\":true}")
    {
        throw new InvalidOperationException("Raw input changed.");
    }

    if ((await queries.GetAsync(typedId))!.Output?.GetString() != "INV-123")
    {
        throw new InvalidOperationException("Typed processor input changed.");
    }

    while ((await queries.GetAsync(manualId, deadline.Token))?.Status != JobStatus.Succeeded)
    {
        await Task.Delay(30, deadline.Token);
    }

    if ((await queries.GetAsync(manualId))!.Output?.GetString() != "INV-MANUAL")
    {
        throw new InvalidOperationException("Explicit definition did not dispatch.");
    }

    var flows = host.Services.GetRequiredService<WorkflowClient>();
    var flow = new FlowBuilder("smoke.flow", 1).Step("raw", ProcessorId.For<RawProcessor>(), "flow input").Build();
    var run = await flows.StartAsync(flow, new { }, "package-smoke", "start-once");
    if (run != await flows.StartAsync(flow, new { }, "package-smoke", "start-once"))
    {
        throw new InvalidOperationException("Duplicate flow start.");
    }

    while ((await queries.GetWorkflowAsync(run, deadline.Token))?.Status != "Succeeded")
    {
        await Task.Delay(30, deadline.Token);
    }

    var executionQueries = host.Services.GetRequiredService<IExecutionQueries>();
    var page = await executionQueries.SearchExecutionsAsync(new(ProcessorKey: manualDefinition.Identity.Key), deadline.Token);
    if (page.Items.Count != 1 || page.Items[0].Id != manualId || page.Items[0].Status != JobStatus.Succeeded)
    {
        throw new InvalidOperationException("The packaged metadata query did not find the completed processor.");
    }

    Console.WriteLine("PASS: package-only consumer executed generated typed/raw processors, an idempotent flow start and bounded metadata queries.");
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

public sealed record InvoiceInput(string InvoiceId);

[Processor("smoke.invoice")]
public sealed class InvoiceProcessor : IProcessor<InvoiceInput>
{
    public Task<JobResult> ProcessAsync(InvoiceInput input, JobExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(JobResult.From(input.InvoiceId));
}

[ManualProcessorRegistration]
public sealed class ExplicitInvoiceProcessor : IProcessor<InvoiceInput>
{
    public Task<JobResult> ProcessAsync(InvoiceInput input, JobExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(JobResult.From(input.InvoiceId));
}
