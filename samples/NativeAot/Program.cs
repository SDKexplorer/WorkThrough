using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkThrough;

// A native AOT process: jobs are registered explicitly, and the application's inputs and outputs serialize through its own
// source-generated context, because reflection-based JSON is unavailable.
JobJson.AddContext(OrderJsonContext.Default);
var stockCounted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWorkThrough()
    .UsePostgreSql(Environment.GetEnvironmentVariable("WORKTHROUGH_POSTGRES")
        ?? "Host=127.0.0.1;Port=5432;Database=workthrough;Username=postgres;Password=workthrough;Search Path=native_aot")
    .AddJob<ReserveStock, ReserveStockJob>(OrderJobs.Reserve)
    .AddJob(OrderJobs.Ship, call =>
    {
        call.Services.GetRequiredService<ILogger<Program>>().LogInformation("Shipping order {Order}", call.Input.OrderId);

        return Task.CompletedTask;
    })
    .Job(OrderJobs.CountStock, job => job
        .Run(call =>
        {
            stockCounted.TrySetResult();

            return Task.CompletedTask;
        })
        .Every(TimeSpan.FromSeconds(1), new CountStock("warehouse-1")))
    .AddWorker()
    .AddScheduler();
using var host = builder.Build();
await host.StartAsync();

var flow = new FlowBuilder(OrderJobs.FulfilmentKey, 1)
    .Step("reserve", OrderJobs.Reserve, new ReserveStock("order-42", 3))
    .Step("ship", OrderJobs.Ship, new ShipOrder("order-42")).After("reserve")
    .Build();
var runId = await host.Services.GetRequiredService<WorkflowClient>().StartAsync(flow, new FulfilOrder("order-42"));
var queries = host.Services.GetRequiredService<IJobQueries>();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while ((await queries.GetWorkflowAsync(runId, timeout.Token))?.Status is not FlowStatus.Succeeded)
{
    await Task.Delay(100, timeout.Token);
}

Console.WriteLine($"Flow {runId} succeeded.");
await stockCounted.Task.WaitAsync(timeout.Token);
Console.WriteLine("The scheduled stock count ran.");
await host.StopAsync();

public static class OrderJobs
{
    public const string FulfilmentKey = "orders.fulfil";

    public static readonly JobDefinition<ReserveStock> Reserve = new("orders.reserve");
    public static readonly JobDefinition<ShipOrder> Ship = new("orders.ship");
    public static readonly JobDefinition<CountStock> CountStock = new("stock.count");
}

public sealed record FulfilOrder(string OrderId);

public sealed record ReserveStock(string OrderId, int Quantity);

public sealed record Reservation(string OrderId, Guid ReservationId);

public sealed record ShipOrder(string OrderId);

public sealed record CountStock(string Warehouse);

public sealed class ReserveStockJob : IJob<ReserveStock>
{
    public Task<JobResult> ExecuteAsync(ReserveStock input, JobExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(JobResult.From(new Reservation(input.OrderId, Guid.NewGuid())));
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(FulfilOrder))]
[JsonSerializable(typeof(ReserveStock))]
[JsonSerializable(typeof(Reservation))]
[JsonSerializable(typeof(ShipOrder))]
[JsonSerializable(typeof(CountStock))]
internal sealed partial class OrderJsonContext : JsonSerializerContext;
