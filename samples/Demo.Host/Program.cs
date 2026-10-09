using Demo.Contracts;
using WorkThrough;
using WorkThrough.Storage;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection(JobsSettings.SectionName).Get<JobsSettings>() ?? new();
var jobs = builder.Services.AddWorkThrough();
if (!string.IsNullOrWhiteSpace(settings.SqlServer) && !string.IsNullOrWhiteSpace(settings.PostgreSql))
{
    throw new InvalidOperationException("Configure only one server database provider.");
}

if (settings.SqlServer is { Length: > 0 } sqlServer)
{
    jobs.UseSqlServer(sqlServer);
}
else if (settings.PostgreSql is { Length: > 0 } postgres)
{
    jobs.UsePostgreSql(postgres);
}
else
{
    jobs.UseSqlite(settings.ConnectionString);
}

jobs.SeedClusterLimits(new(null, settings.FleetConcurrency), new(OrderJobs.DispatchKey, settings.ShippingConcurrency));
jobs.AddScheduler();
if (settings.RunWorker)
{
    jobs.AddGeneratedJobs(typeof(Program).Assembly).AddWorker(options =>
    {
        options.MaxConcurrentJobs = settings.LocalConcurrency;
        options.Queues = [OrderJobs.DefaultQueue, OrderJobs.ShippingQueue];
    });
}

builder.Services.AddWorkThroughDashboard();
var app = builder.Build();
app.MapWorkThroughDashboard("/operations/jobs", options => options.AllowAnonymous = app.Environment.IsDevelopment());
app.MapGet("/", () => Results.Redirect("/operations/jobs"));

// Demo producer endpoint is exposed only in Development, alongside the explicitly anonymous demo dashboard.
if (app.Environment.IsDevelopment())
{
    app.MapPost("/demo/orders", async (OrderRequest order, WorkflowClient flows, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(order.OrderId) || order.OrderId.Length > 200)
        {
            return Results.BadRequest(new
            {
                error = "OrderId must contain 1–200 characters."
            });
        }

        try
        {
            var runId = await flows.StartAsync(OrderWorkflow.Definition, order, OrderWorkflow.CorrelationId(order.OrderId), OrderWorkflow.StartKey(order.OrderId), ct);

            return Results.Accepted($"/operations/jobs/api/flows/{runId}", new
            {
                runId
            });
        }
        catch (StateConflictException conflict)
        {
            return Results.Conflict(new
            {
                error = conflict.Message
            });
        }
    });
}

await app.RunAsync();
