using WorkThrough;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var database = Path.Combine(Path.GetTempPath(), $"durablejobs-package-{Guid.NewGuid():N}.db");
var manualDefinition = new JobDefinition<InvoiceInput>("smoke.manual", displayName: "Explicit invoice");
var builder = Host.CreateApplicationBuilder();
builder.Services.AddWorkThrough().UseSqlite($"Data Source={database};Pooling=False")
    .AddGeneratedJobs(typeof(Program).Assembly).AddJob<InvoiceInput, ExplicitInvoiceProcessor>(manualDefinition).AddWorker(options => options.PollInterval = TimeSpan.FromMilliseconds(20));
using var host = builder.Build();

// Reflection registration needs no generator and must find the same jobs, leaving manual ones to AddJob.
var scanning = new ServiceCollection();
scanning.AddWorkThrough().AddJobs(typeof(Program).Assembly);
var scanned = scanning.BuildServiceProvider().GetRequiredService<JobCatalog>().Registrations.Select(registration => registration.Identity.ToString()).Order(StringComparer.Ordinal).ToArray();
var generated = host.Services.GetRequiredService<JobCatalog>().Registrations.Where(registration => registration.Identity != manualDefinition.Identity)
    .Select(registration => registration.Identity.ToString()).Order(StringComparer.Ordinal).ToArray();
if (!scanned.SequenceEqual(generated))
{
    throw new InvalidOperationException($"AddJobs registered [{string.Join(", ", scanned)}]; the generator registered [{string.Join(", ", generated)}].");
}

await host.StartAsync();
try
{
    var id = await host.Services.GetRequiredService<JobClient>().EnqueueAsync(new PackageJob("package-only"));
    var rawId = await host.Services.GetRequiredService<JobClient>().EnqueueAsync(JobIdentity.For<RawProcessor>(), "{\"raw\":true}");
    var typedId = await host.Services.GetRequiredService<JobClient>().EnqueueAsync(JobIdentity.For<InvoiceProcessor>(), new InvoiceInput("INV-123"));
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
    var flow = new FlowBuilder("smoke.flow", 1).Step("raw", JobIdentity.For<RawProcessor>(), "flow input")
        .Step("last", JobIdentity.For<RawProcessor>(), "follow-up").After("raw").Build();
    var run = await flows.StartAsync(flow, new { }, "package-smoke", "start-once");
    if (run != await flows.StartAsync(flow, new { }, "package-smoke", "start-once"))
    {
        throw new InvalidOperationException("Duplicate flow start.");
    }

    while ((await queries.GetWorkflowAsync(run, deadline.Token))?.Status != FlowStatus.Succeeded)
    {
        await Task.Delay(30, deadline.Token);
    }

    var executionQueries = host.Services.GetRequiredService<IExecutionQueries>();
    var page = await executionQueries.SearchExecutionsAsync(new(ProcessorKey: manualDefinition.Identity.Key), deadline.Token);
    if (page.Items.Count != 1 || page.Items[0].Id != manualId || page.Items[0].Status != JobStatus.Succeeded)
    {
        throw new InvalidOperationException("The packaged metadata query did not find the completed processor.");
    }

    await McpPackageSmoke.RunAsync(database, manualId, run);
    Console.WriteLine("PASS: package-only consumer executed processors, flows, bounded queries and a protected MCP call over loopback HTTP.");
}
finally
{
    await host.StopAsync();
    File.Delete(database);
}

await TestingPackageSmoke.RunAsync();
await SqlServerPackageSmoke.RunAsync();
await OraclePackageSmoke.RunAsync();
await MySqlPackageSmoke.RunAsync();

[JobContract("smoke.package")]
public sealed record PackageJob(string Value);
public sealed record class PackageHandler : IJob<PackageJob>
{
    public Task<JobResult> ExecuteAsync(PackageJob job, JobExecutionContext context, CancellationToken cancellationToken) => Task.FromResult(JobResult.From(job.Value));
}

[Job("smoke.raw")]
public sealed class RawProcessor : IJob<string>
{
    public Task<JobResult> ExecuteAsync(string input, JobExecutionContext context, CancellationToken cancellationToken) => Task.FromResult(JobResult.From(input));
}

public sealed record InvoiceInput(string InvoiceId);

// Generated registration skips abstract generic bases and registers their concrete processors.
public abstract class EchoProcessor<TInput> : IJob<TInput>
{
    public Task<JobResult> ExecuteAsync(TInput input, JobExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(JobResult.From(Echo(input)));

    protected abstract string Echo(TInput input);
}

[Job("smoke.invoice")]
public sealed class InvoiceProcessor : EchoProcessor<InvoiceInput>
{
    protected override string Echo(InvoiceInput input) => input.InvoiceId;
}

[ManualJobRegistration]
public sealed class ExplicitInvoiceProcessor : IJob<InvoiceInput>
{
    public Task<JobResult> ExecuteAsync(InvoiceInput input, JobExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(JobResult.From(input.InvoiceId));
}
