# Testing your processors

WorkThrough has no in-memory store: a test that claims, retries or settles should exercise the same SQL as production. Two levels cover most needs.

## Unit tests: call the processor

A processor is a plain class. Construct it with its dependencies and call `ProcessAsync` with a context you build yourself:

```csharp
[Fact]
public async Task Rejects_non_positive_amounts()
{
    var processor = new ChargePaymentProcessor();
    var context = new JobExecutionContext(JobId: Guid.NewGuid(), Attempt: 1, CorrelationId: null,
        WorkflowRunId: null, StepId: null, LeaseToken: Guid.NewGuid());

    var result = await processor.ProcessAsync(new ChargePayment("order-1", Amount: 0), context, CancellationToken.None);

    Assert.Equal(JobOutcome.Rejected, result.Outcome);
}
```

`context.IdempotencyKey` derives from `JobId`, so a test can check that an external call receives the same key on every attempt. Progress reports go nowhere unless you set `Progress` on the context.

## Integration tests: a temporary SQLite file

For flows, retries, limits or the outbox, run the real engine against a throwaway file. SQLite needs no server, and a file per test keeps tests independent:

```csharp
public sealed class CheckoutTests : IAsyncLifetime
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"workthrough-{Guid.NewGuid():N}.db");
    private IHost host = null!;

    public async Task InitializeAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWorkThrough()
            .UseSqlite($"Data Source={path}")
            .AddShopProcessors()
            .AddWorker(options => options.PollInterval = TimeSpan.FromMilliseconds(20));
        host = builder.Build();
        await host.StartAsync();
    }

    [Fact]
    public async Task A_small_order_completes_its_checkout()
    {
        var flows = host.Services.GetRequiredService<WorkflowClient>();
        var queries = host.Services.GetRequiredService<IJobQueries>();
        var order = new OrderPlaced("order-1", "tenant-a", "ada", 40m);
        var run = await flows.StartAsync(CheckoutFlow.Definition, order, CheckoutFlow.Start(order));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await queries.GetWorkflowAsync(run, timeout.Token))?.Status != FlowStatus.Succeeded)
        {
            await Task.Delay(50, timeout.Token);
        }
    }

    public async Task DisposeAsync()
    {
        await host.StopAsync();
        host.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(path);
    }
}
```

- Poll for the expected state with a deadline rather than sleeping a fixed time; workers run asynchronously.
- To test a step in isolation, enqueue its job with `JobClient` and wait for `IJobQueries.GetAsync` to report `Succeeded`.
- To test a failure path, make a fake dependency throw and assert on `AttemptsAsync` and `FailuresAsync`, which also show retries.
- For PostgreSQL, SQL Server or Oracle, point the same test at a disposable database or schema created per test run. `tests/WorkThrough.Tests/StoreHarness.cs` in this repository does that for all four providers.

## Contract tests: stored work still reads

Before changing an input type, deserialize a saved example with the definition you ship:

```csharp
var saved = JsonDocument.Parse("""{"orderId":"order-1","amount":40}""").RootElement;
var input = ShopJobs.ChargePayment.ReadInput(saved);
```

If an old payload no longer reads, give the new shape a new contract version instead; see [identity evolution](identity-evolution.md).
