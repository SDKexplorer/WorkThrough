using Shop;
using Shop.Api;
using Shop.Jobs;
using WorkThrough;
using WorkThrough.Storage;

// --smoke places orders in-process, waits for their checkouts and exits; repository verification runs it.
var smoke = args.Contains(Smoke.Argument, StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != Smoke.Argument).ToArray());
if (smoke)
{
    builder.WebHost.UseUrls(Smoke.Url);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
}

var settings = ShopSetup.Settings(builder.Configuration);
builder.Services.AddWorkThrough()
    .UseShopStorage(settings)
    .SeedShopPolicies(settings)
    .AddShopJobs(settings)
    .AddScheduler()
    .AddWorker(options =>
    {
        options.MaxConcurrentJobs = settings.WorkerSlots;
        options.Queues = ShopJobRegistration.Queues;
    });

// Transactional intake needs the relational store itself, not only its job ports.
builder.Services.AddSingleton(services => (RelationalJobStore)services.GetRequiredService<IJobStore>());
builder.Services.AddSingleton<Orders>();
builder.Services.AddWorkThroughDashboard();
var app = builder.Build();
await app.Services.GetRequiredService<Orders>().InitializeAsync(CancellationToken.None);

if (smoke)
{
    await Smoke.RunAsync(app);

    return;
}

// The embedded dashboard is anonymous only in Development; see Shop.Dashboard for an authenticated, tenant-scoped console.
app.MapWorkThroughDashboard("/operations", options => options.AllowAnonymous = app.Environment.IsDevelopment());
app.MapGet("/", () => Results.Redirect("/operations"));
if (app.Environment.IsDevelopment())
{
    app.MapPost("/orders", async (PlaceOrder order, Orders orders, CancellationToken ct) =>
    {
        try
        {
            var id = await orders.PlaceAsync(order, ct);

            return Results.Accepted($"/orders/{id}", new { orderId = id });
        }
        catch (ArgumentException invalid)
        {
            return Results.BadRequest(new { error = invalid.Message });
        }
    });
    app.MapPost("/orders/{id}/approve", async (string id, Orders orders, CancellationToken ct) =>
    {
        try
        {
            return await orders.ApproveAsync(id, "development-reviewer", ct) ? Results.Accepted() : Results.NotFound();
        }
        catch (StateConflictException conflict)
        {
            return Results.Conflict(new { error = conflict.Message });
        }
    });
}

await app.RunAsync();
