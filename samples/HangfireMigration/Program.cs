using Hangfire;
using Hangfire.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WorkThrough;
using WorkThrough.Migration.Hangfire;
using WorkThrough.Storage;

// An existing Hangfire application: a queued email, a delayed one and a nightly report. Its servers are stopped for the cutover.
var hangfire = new InMemoryStorage();
var jobs = new BackgroundJobClient(hangfire);
jobs.Enqueue<Mailer>(mailer => mailer.SendWelcome("ada@example.com"));
jobs.Schedule<Mailer>(mailer => mailer.SendWelcome("grace@example.com"), TimeSpan.FromMinutes(30));
new RecurringJobManager(hangfire).AddOrUpdate<Reports>("nightly-report", reports => reports.Run(1), "0 2 * * *");

// The WorkThrough side: an address and input per job, served by the same services.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<Mailer>().AddSingleton<Reports>();
builder.Services.AddWorkThrough()
    .UseSqlite("Data Source=hangfire-migration.db")
    .AddJob(Emails.Welcome, job =>
    {
        job.Service<Mailer>().SendWelcome(job.Input.Address);

        return Task.CompletedTask;
    })
    .AddJob(ReportJobs.Nightly, job => job.Service<Reports>().Run(job.Input.Days))
    .AddScheduler()
    .AddWorker();
using var host = builder.Build();
await host.StartAsync();

// Map each Hangfire method to its address, review the plan, import, then retire the imported jobs in Hangfire.
var migration = new HangfireMigration(hangfire)
    .Map((Mailer mailer) => mailer.SendWelcome(default!), Emails.Welcome, call => new WelcomeEmail(call.Argument<string>(0)))
    .Map((Reports reports) => reports.Run(default), ReportJobs.Nightly, call => new NightlyReport(call.Argument<int>(0)));
var plan = migration.Plan();
foreach (var item in plan.Items)
{
    Console.WriteLine($"{item.Kind,-9} {item.HangfireId,-15} {item.Method} -> {item.Target?.ToString() ?? item.Problem}");
}

var store = (RelationalJobStore)host.Services.GetRequiredService<IJobStore>();
var result = await migration.ImportAsync(plan, store, store);
migration.Retire(result);
Console.WriteLine($"Imported {result.Imported.Count}, skipped {result.Skipped.Count}. The queued email runs now, the delayed one in 30 minutes.");

var welcome = result.Imported.First(imported => imported.Item.Kind == HangfireJobKind.Enqueued).JobId!.Value;
var queries = host.Services.GetRequiredService<IJobQueries>();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while ((await queries.GetAsync(welcome, timeout.Token))?.Status != JobStatus.Succeeded)
{
    await Task.Delay(100, timeout.Token);
}

await host.StopAsync();

public sealed record WelcomeEmail(string Address);
public sealed record NightlyReport(int Days);

public static class Emails
{
    public static JobDefinition<WelcomeEmail> Welcome { get; } = new("emails.welcome");
}

public static class ReportJobs
{
    public static JobDefinition<NightlyReport> Nightly { get; } = new("reports.nightly");
}

public class Mailer
{
    public void SendWelcome(string address) => Console.WriteLine($"Welcome email to {address}");
}

public class Reports
{
    public Task Run(int days)
    {
        Console.WriteLine($"Report for the last {days} day(s)");

        return Task.CompletedTask;
    }
}
