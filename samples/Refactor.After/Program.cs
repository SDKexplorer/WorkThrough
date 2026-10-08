using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WorkThrough;
using WorkThrough.Storage;

if (args.Length != 2)
{
    throw new ArgumentException("Expected connection string and receipt path.");
}

if (File.Exists(Path.Combine(AppContext.BaseDirectory, "Refactor.Before.dll")))
{
    throw new InvalidOperationException("The replacement deployment must not contain the original assembly.");
}

using var receipt = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(args[1]));
var id = receipt.RootElement.GetProperty("id").GetGuid();
var rawId = receipt.RootElement.GetProperty("rawId").GetGuid();
var flowId = receipt.RootElement.GetProperty("flowId").GetGuid();
var builder = Host.CreateApplicationBuilder();
builder.Services.AddWorkThrough().UseSqlite(args[0]).AddGeneratedJobs(typeof(Program).Assembly)
    .AddWorker(options => options.PollInterval = TimeSpan.FromMilliseconds(20));
using var host = builder.Build();
await host.StartAsync();
try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var queries = host.Services.GetRequiredService<IJobQueries>();
    JobRecord? job;
    do
    {
        await Task.Delay(50, deadline.Token);
        job = await queries.GetAsync(id, deadline.Token);
    } while (job?.Status != JobStatus.Succeeded);

    if (job.Output?.GetProperty("implementation").GetString() != "moved-and-renamed")
    {
        throw new InvalidOperationException("Wrong implementation executed.");
    }

    if (job.Contract != new JobContractId("documents.generate", 1) || job.Payload.GetProperty("documentId").GetString() != "invoice-123")
    {
        throw new InvalidOperationException("The persisted contract or input changed.");
    }

    JobRecord? raw;
    do
    {
        await Task.Delay(50, deadline.Token);
        raw = await queries.GetAsync(rawId, deadline.Token);
    } while (raw?.Status != JobStatus.Succeeded);

    if (raw.Contract != new JobContractId("documents.raw", 1)
        || raw.Payload.GetString() != receipt.RootElement.GetProperty("rawInput").GetString()
        || raw.Output?.GetProperty("implementation").GetString() != "relocated-raw")
    {
        throw new InvalidOperationException("The scheduled raw processor invocation changed.");
    }

    var attempt = (await queries.AttemptsAsync(rawId, deadline.Token)).Single();
    if (attempt.StartedAt < receipt.RootElement.GetProperty("due").GetDateTimeOffset())
    {
        throw new InvalidOperationException("Scheduled invocation started before its due time.");
    }

    while ((await queries.GetWorkflowAsync(flowId, deadline.Token))?.Status != "Succeeded")
    {
        await Task.Delay(50, deadline.Token);
    }

    var step = (await queries.ListFlowJobsAsync(flowId, deadline.Token)).Single();
    if (step.Contract != new JobContractId("documents.raw", 1) || step.Payload.GetString() != receipt.RootElement.GetProperty("rawInput").GetString())
    {
        throw new InvalidOperationException("Persisted flow step changed during relocation.");
    }

    Console.WriteLine($"Replacement worker runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
    Console.WriteLine("PASS: typed, future-due raw-string, and saved flow-step invocations survived namespace, class, and assembly moves without rewriting stored identity or input.");
}
finally
{
    await host.StopAsync();
}

// A deliberate address rename is a separate, coordinated deployment from moving the class.
var store = new SqliteJobStore(args[0]);
var oldAddress = new JobContractId("documents.raw", 1);
var newAddress = new JobDefinition<string>("documents.render");
var client = new JobClient(store);
var originalInput = receipt.RootElement.GetProperty("rawInput").GetString()!;
var waitingOld = await client.EnqueueAsync(oldAddress, originalInput);
var waitingNew = await client.EnqueueAsync(newAddress, originalInput);
await store.SetProcessorAliasesAsync([new(oldAddress, newAddress.Identity)], 0,
    new(Guid.NewGuid(), new("refactor-proof", "deployment"), "Activate the reviewed processor address alias"));
var replacement = Host.CreateApplicationBuilder();
replacement.Services.AddWorkThrough().UseSqlite(args[0])
    .AddProcessor<string, NewCompany.Documents.RenamedAddressProcessor>(newAddress, oldAddress)
    .AddWorker(options => options.PollInterval = TimeSpan.FromMilliseconds(20));
using var renamedHost = replacement.Build();
await renamedHost.StartAsync();
try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while ((await store.GetAsync(waitingOld, deadline.Token))?.Status != JobStatus.Succeeded ||
        (await store.GetAsync(waitingNew, deadline.Token))?.Status != JobStatus.Succeeded)
    {
        await Task.Delay(50, deadline.Token);
    }

    if ((await store.GetAsync(waitingOld))!.Contract != oldAddress || (await store.GetAsync(waitingNew))!.Contract != newAddress.Identity)
    {
        throw new InvalidOperationException("Alias dispatch must not rewrite saved addresses.");
    }

    Console.WriteLine("PASS: old and new addresses execute through the coordinated alias without migrating queued metadata.");
}
finally
{
    await renamedHost.StopAsync();
}
