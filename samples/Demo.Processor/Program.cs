using Demo.Processor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WorkThrough;
using WorkThrough.Storage;

var once = args.Contains("--once", StringComparer.Ordinal);
var builder = Host.CreateApplicationBuilder(args.Where(arg => arg != "--once").ToArray());
var settings = builder.Configuration.GetSection(InvoiceSettings.SectionName).Get<InvoiceSettings>() ?? new();
builder.Services.AddWorkThrough()
    .UseSqlite(settings.ConnectionString)
    .AddProcessor<InvoiceInput, GenerateInvoice>(InvoiceJobs.Generate)
    .SeedClusterLimits(new(null, settings.FleetConcurrency), new(InvoiceJobs.Generate.Identity.Key, settings.InvoiceConcurrency))
    .AddScheduler()
    .AddWorker(options =>
    {
        options.MaxConcurrentJobs = settings.LocalConcurrency;
        options.ExecutionTimeout = settings.ExecutionTimeout;
    });

using var host = builder.Build();
await host.StartAsync();
var jobs = host.Services.GetRequiredService<JobClient>();
var invoice = new InvoiceInput(settings.InvoiceId);
var correlation = InvoiceJobs.CorrelationId(invoice.InvoiceId);
var id = await jobs.EnqueueAsync(InvoiceJobs.Generate, invoice, new(CorrelationId: correlation));
await jobs.EnqueueAsync(InvoiceJobs.Generate, invoice, new(NotBefore: DateTimeOffset.UtcNow.Add(settings.Delay), CorrelationId: correlation));
await InvoiceSchedule.EnsurePublishedAsync(host.Services.GetRequiredService<IJobSchedules>(), settings, CancellationToken.None);

if (once)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var queries = host.Services.GetRequiredService<IJobQueries>();
    while ((await queries.GetAsync(id, deadline.Token))?.Status != JobStatus.Succeeded)
    {
        await Task.Delay(50, deadline.Token);
    }

    Console.WriteLine("PASS: typed processor, delayed work, and persisted recurring schedule configured.");
    await host.StopAsync();
}
else
{
    await host.WaitForShutdownAsync();
}
