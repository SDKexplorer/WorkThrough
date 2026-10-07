using WorkThrough;
using WorkThrough.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Length != 2) throw new ArgumentException("Expected connection string and receipt path.");
var builder = Host.CreateApplicationBuilder();
builder.Services.AddWorkThrough().UseSqlite(args[0]).AddGeneratedJobs(typeof(Program).Assembly);
using var host = builder.Build();
await host.StartAsync();
var jobs = host.Services.GetRequiredService<JobClient>();
var id = await jobs.EnqueueAsync(new Legacy.Application.GenerateDocument("invoice-123"),
    new(CorrelationId: "refactor-proof"));
var due = DateTimeOffset.UtcNow.AddSeconds(3);
const string rawInput = "{\"documentId\":\"invoice-raw\"}";
var rawId = await jobs.EnqueueAsync(ProcessorId.For<Legacy.Application.OldRawProcessor>(), rawInput,
    new(CorrelationId: "refactor-proof", NotBefore: due));
var store = host.Services.GetRequiredService<IJobStore>();
var premature = await store.ClaimAsync(new("proof", [ProcessorId.For<Legacy.Application.OldRawProcessor>()], ["default"], TimeSpan.FromSeconds(30)));
if (premature is not null) throw new InvalidOperationException("Future-due work executed too early.");
var flowId = await host.Services.GetRequiredService<WorkflowClient>().StartAsync(
    new FlowBuilder("documents.raw-flow", 1).Step("generate", ProcessorId.For<Legacy.Application.OldRawProcessor>(), rawInput).Build(), new { });
await File.WriteAllTextAsync(args[1], System.Text.Json.JsonSerializer.Serialize(new { id, rawId, flowId, due, rawInput }));
Console.WriteLine($"Producer runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
await host.StopAsync();

namespace Legacy.Application
{
    [Processor("documents.raw")]
    public sealed class OldRawProcessor : IProcessor<string>
    {
        public Task<JobResult> ProcessAsync(string input, JobExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The old processor must never execute in this proof.");
    }

    [JobContract("documents.generate")]
    public sealed record GenerateDocument(string DocumentId);

    public sealed class OldDocumentProcessor : IJobHandler<GenerateDocument>
    {
        public Task<JobResult> HandleAsync(GenerateDocument job, JobExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The old handler must never execute in this proof.");
    }
}
