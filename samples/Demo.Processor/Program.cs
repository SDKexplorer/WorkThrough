using System.Text.Json;
using WorkThrough;
using WorkThrough.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args.Where(arg => arg != "--once").ToArray());
builder.Services.AddWorkThrough().UseSqlite(builder.Configuration["Jobs:ConnectionString"] ?? "Data Source=processor-sample.db")
    .AddGeneratedJobs(typeof(Program).Assembly).SeedClusterLimits(new(null, 4), new("invoices.generate", 2))
    .AddScheduler().AddWorker(options => { options.MaxConcurrentJobs = 2; options.ExecutionTimeout = TimeSpan.FromSeconds(30); });
using var host = builder.Build();
await host.StartAsync();
var jobs = host.Services.GetRequiredService<JobClient>();
var processor = ProcessorId.For<GenerateInvoice>();
var id = await jobs.EnqueueAsync(processor, "{\"invoiceId\":\"sample-123\"}", new(CorrelationId: "invoice:sample-123"));
await jobs.EnqueueAsync(processor, "{\"invoiceId\":\"sample-later\"}", new(NotBefore: DateTimeOffset.UtcNow.AddMinutes(5)));

var schedules = host.Services.GetRequiredService<IJobSchedules>();
if (!(await schedules.SchedulesAsync()).Any(s => s.Definition.Key == "invoices.hourly"))
{
    var definition = new ScheduleDefinition("invoices.hourly", 1, "0 * * * *", "UTC", processor,
        JsonSerializer.SerializeToElement("{\"invoiceId\":\"hourly-example\"}"), Overlap: ScheduleOverlap.Coalesce);
    AdministrativeCommand Command(string reason) => new(Guid.NewGuid(), new("sample-deployment", "processor-sample"), reason);
    await schedules.SaveScheduleDraftAsync(definition, 0, Command("Create sample recurring schedule"));
    await schedules.PublishScheduleAsync(definition.Key, 1, 1, Command("Publish validated sample schedule"));
    await schedules.SetScheduleEnabledAsync(definition.Key, 1, 2, true, Command("Enable sample schedule"));
}

if (args.Contains("--once"))
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var queries = host.Services.GetRequiredService<IJobQueries>();
    while ((await queries.GetAsync(id, deadline.Token))?.Status != JobStatus.Succeeded) await Task.Delay(50, deadline.Token);
    Console.WriteLine("PASS: direct raw-string processor and persisted recurring schedule configured.");
    await host.StopAsync();
}
else await host.WaitForShutdownAsync();

[Processor("invoices.generate", DisplayName = "Generate invoice")]
public sealed class GenerateInvoice : IProcessor<string>
{
    public async Task<JobResult> ProcessAsync(string message, JobExecutionContext context, CancellationToken cancellationToken)
    {
        var invoice = JsonSerializer.Deserialize<InvoiceInput>(message, JobJson.Options)!;
        await context.ReportProgressAsync("Invoice input validated", cancellationToken);
        // Supply context.IdempotencyKey to a real invoice service to deduplicate external effects.
        return JobResult.From(new { invoice.InvoiceId, generated = true });
    }
}
public sealed record InvoiceInput(string InvoiceId);
