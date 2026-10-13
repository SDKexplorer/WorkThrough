using WorkThroughWeb;
using WorkThrough;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("WorkThrough")
    ?? throw new InvalidOperationException("Set ConnectionStrings:WorkThrough.");

// This process accepts requests and runs jobs. Move AddWorker into a separate worker host when the work outgrows it.
builder.Services.AddWorkThrough()
#if (UseSqlite)
    .UseSqlite(connectionString)
#elif (UsePostgreSql)
    .UsePostgreSql(connectionString)
#elif (UseSqlServer)
    .UseSqlServer(connectionString)
#elif (UseMySql)
    .UseMySql(connectionString)
#else
    .UseOracle(connectionString)
#endif
    .AddJobs(typeof(Program).Assembly)
    .AddWorker();
builder.Services.AddWorkThroughDashboard();
builder.Services.AddHealthChecks().AddWorkThrough();
var app = builder.Build();

// The console is anonymous only in Development. Before deploying, add authentication and grant dashboard permissions.
app.MapWorkThroughDashboard("/jobs", options => options.AllowAnonymous = app.Environment.IsDevelopment());
app.MapHealthChecks("/health");
app.MapPost("/signups", async (Signup signup, JobClient jobs, CancellationToken ct) =>
{
    // The idempotency key turns a repeated request into the job it already stored.
    var jobId = await jobs.Enqueue(new WelcomeEmail(signup.Email)).Idempotent($"welcome:{signup.Email}").SendAsync(ct);

    return Results.Accepted($"/jobs", new { jobId });
});

await app.RunAsync();

public sealed record Signup(string Email);
