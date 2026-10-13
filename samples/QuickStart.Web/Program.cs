using WorkThrough;

// Run with --urls http://127.0.0.1:5296 in Development. An API that hands work to background jobs, runs them in the same process, and shows them in the console at /jobs.
//   curl -X POST http://127.0.0.1:5296/signups -H 'Content-Type: application/json' -d '{"email":"ada@example.com"}'
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWorkThrough()
    .UseSqlite("Data Source=quickstart-web.db")
    .AddJobs(typeof(SendWelcomeEmail).Assembly)
    .AddWorker();
builder.Services.AddWorkThroughDashboard();
var app = builder.Build();

// The console is anonymous only in Development; production hosts protect it with their own authentication.
app.MapWorkThroughDashboard("/jobs", options => options.AllowAnonymous = app.Environment.IsDevelopment());
app.MapGet("/", () => Results.Redirect("/jobs"));
app.MapPost("/signups", async (Signup signup, JobClient jobs, CancellationToken ct) =>
{
    // The idempotency key makes a repeated request return the jobs it already stored instead of sending twice.
    var welcome = await jobs.Enqueue(new WelcomeEmail(signup.Email)).Idempotent($"welcome:{signup.Email}").SendAsync(ct);
    var tips = await jobs.Enqueue(new TipsEmail(signup.Email)).Idempotent($"tips:{signup.Email}").After(TimeSpan.FromMinutes(1)).SendAsync(ct);

    return Results.Accepted(value: new { welcome, tips });
});

await app.RunAsync();

public sealed record Signup(string Email);

[JobContract("signups.welcome-email")]
public sealed record WelcomeEmail(string Address);

[JobContract("signups.tips-email")]
public sealed record TipsEmail(string Address);

public sealed class SendWelcomeEmail(ILogger<SendWelcomeEmail> logger) : IJob<WelcomeEmail>
{
    public Task<JobResult> ExecuteAsync(WelcomeEmail input, JobExecutionContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("Welcome email to {Address}", input.Address);

        return Task.FromResult(JobResult.Done());
    }
}

public sealed class SendTipsEmail(ILogger<SendTipsEmail> logger) : IJob<TipsEmail>
{
    public Task<JobResult> ExecuteAsync(TipsEmail input, JobExecutionContext context, CancellationToken cancellationToken)
    {
        // Failures retry with backoff; after the last attempt the job is kept as Failed for an operator to inspect and retry.
        logger.LogInformation("Tips email to {Address}", input.Address);

        return Task.FromResult(JobResult.Done());
    }
}
