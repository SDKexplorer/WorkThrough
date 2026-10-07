using Demo.Contracts;
using WorkThrough;
using WorkThrough.Storage;

var builder = WebApplication.CreateBuilder(args);
var jobs = builder.Services.AddWorkThrough();
if (builder.Configuration["Jobs:PostgreSql"] is { Length: > 0 } postgres) jobs.UsePostgreSql(postgres);
else jobs.UseSqlite(builder.Configuration["Jobs:ConnectionString"] ?? "Data Source=durablejobs-demo.db");

jobs.SeedClusterLimits(new(null, 12), new("orders.dispatch", 2));
jobs.AddScheduler();
if (builder.Configuration.GetValue("Jobs:RunWorker", true))
    jobs.AddGeneratedJobs(typeof(Program).Assembly).AddWorker(options =>
    {
        options.MaxConcurrentJobs = 4;
        options.Queues = ["default", "shipping"];
    });
builder.Services.AddWorkThroughDashboard();
var app = builder.Build();
app.MapWorkThroughDashboard("/operations/jobs", options => options.AllowAnonymous = app.Environment.IsDevelopment());
app.MapGet("/", () => Results.Redirect("/operations/jobs"));

// Demo producer endpoint is exposed only in Development, alongside the explicitly anonymous demo dashboard.
if (app.Environment.IsDevelopment())
{
    app.MapPost("/demo/orders", async (DemoOrder order, WorkflowClient flows, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(order.OrderId) || order.OrderId.Length > 200)
            return Results.BadRequest(new { error = "OrderId must contain 1–200 characters." });
        var definition = new FlowBuilder("orders.process", 2)
            .Step<ValidateOrder>("validate", maxAttempts: 1)
                .BindInput("orderId", "orderId").BindInput("amount", "amount")
            .Step<DispatchOrder>("dispatch", queue: "shipping").After("validate")
                .BindInput("orderId", "orderId", "validate").WhenOutput("validate", "approved", true)
            .Step<RejectOrder>("reject").After("validate")
                .BindInput("orderId", "orderId", "validate").WhenOutput("validate", "approved", false)
            .Build();
        try
        {
            var runId = await flows.StartAsync(definition, order, $"order:{order.OrderId}", $"process-order:{order.OrderId}", ct);
            return Results.Accepted($"/operations/jobs/api/flows/{runId}", new { runId });
        }
        catch (StateConflictException conflict)
        {
            return Results.Conflict(new { error = conflict.Message });
        }
    });
}
await app.RunAsync();

public sealed record DemoOrder(string OrderId, decimal Amount);
