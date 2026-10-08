using Demo.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WorkThrough;
using WorkThrough.Storage;

var builder = Host.CreateApplicationBuilder(args);
var settings = builder.Configuration.GetSection(LifecycleSettings.SectionName).Get<LifecycleSettings>() ?? new();
var business = new SqliteJobStore(settings.BusinessConnection);
var checkout = new Checkout(business);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await business.InitializeAsync(deadline.Token);
await checkout.InitializeAsync(deadline.Token);
builder.Services.AddWorkThrough()
    .UseSqlite(settings.JobsConnection)
    .AddProcessor<OrderPlaced, OrderNotification>(OrderDefinitions.Notify)
    .AddOutboxDispatcher(business)
    .AddScheduler()
    .AddWorker(options => options.PollInterval = TimeSpan.FromMilliseconds(20));

using var host = builder.Build();
await host.StartAsync(deadline.Token);
try
{
    var order = new OrderPlaced(settings.OrderId, settings.CustomerEmail);
    var receipt = await checkout.AcceptAsync(order, deadline.Token);
    while ((await business.OutboxDeliveryAsync(receipt, deadline.Token))?.Status != "Delivered")
    {
        await Task.Delay(50, deadline.Token);
    }

    var flows = host.Services.GetRequiredService<WorkflowClient>();
    var run = await flows.StartAsync(OrderDefinitions.ApprovalFlow, order, OrderDefinitions.Correlation(order.OrderId), cancellationToken: deadline.Token);
    await flows.SignalAsync(run, OrderDefinitions.Approved, new Approval(order.OrderId, order.CustomerEmail), OrderDefinitions.AcceptanceKey(order.OrderId), deadline.Token);
    var queries = host.Services.GetRequiredService<IJobQueries>();
    var notification = (await business.OutboxDeliveryAsync(receipt, deadline.Token))!.JobId!.Value;
    while ((await queries.GetWorkflowAsync(run, deadline.Token))?.Status != "Succeeded" ||
        (await queries.GetAsync(notification, deadline.Token))?.Status != JobStatus.Succeeded)
    {
        await Task.Delay(50, deadline.Token);
    }

    Console.WriteLine("PASS: committed business outbox delivered to a separate job database; a durable approval resumed its flow.");
}
finally
{
    await host.StopAsync();
}
