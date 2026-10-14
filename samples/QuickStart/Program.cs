using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkThrough;

// One process and a PostgreSQL database (samples/compose.yaml): it stores a job, runs it on the in-process worker, and exits once
// the job has succeeded.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWorkThrough()
    .UsePostgreSql(Environment.GetEnvironmentVariable("WORKTHROUGH_POSTGRES")
        ?? "Host=127.0.0.1;Port=5432;Database=workthrough;Username=postgres;Password=workthrough;Search Path=quickstart")
    .AddJobs(typeof(SendWelcomeEmail).Assembly)
    .AddWorker();
using var host = builder.Build();
await host.StartAsync();

var jobId = await host.Services.GetRequiredService<JobClient>().EnqueueAsync(new WelcomeEmail("ada@example.com"));
var queries = host.Services.GetRequiredService<IJobQueries>();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while ((await queries.GetAsync(jobId, timeout.Token))?.Status != JobStatus.Succeeded)
{
    await Task.Delay(100, timeout.Token);
}

Console.WriteLine($"Job {jobId} succeeded.");
await host.StopAsync();

// The address "welcome.email" is what the database stores. Rename or move these types freely; keep the address.
[JobContract("welcome.email")]
public sealed record WelcomeEmail(string Address);

public sealed class SendWelcomeEmail(ILogger<SendWelcomeEmail> logger) : IJob<WelcomeEmail>
{
    public Task<JobResult> ExecuteAsync(WelcomeEmail input, JobExecutionContext context, CancellationToken cancellationToken)
    {
        // A real sender would pass context.IdempotencyKey to the mail service, because an attempt can repeat after a crash.
        logger.LogInformation("Sending a welcome email to {Address} (attempt {Attempt})", input.Address, context.Attempt);

        return Task.FromResult(JobResult.Done());
    }
}
