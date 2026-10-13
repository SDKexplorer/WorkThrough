# Testing your jobs

WorkThrough has no in-memory store: a test that claims, retries or settles should exercise the same SQL as production. Two levels cover most needs.

## Unit tests: call the job

A job is a plain class. Construct it with its dependencies and call `ExecuteAsync` with a context you build yourself:

```csharp
[Fact]
public async Task Rejects_non_positive_amounts()
{
    var job = new ChargePaymentJob();
    var context = new JobExecutionContext(JobId: Guid.NewGuid(), Attempt: 1, CorrelationId: null,
        WorkflowRunId: null, StepId: null, LeaseToken: Guid.NewGuid());

    var result = await job.ExecuteAsync(new ChargePayment("order-1", Amount: 0), context, CancellationToken.None);

    Assert.Equal(JobOutcome.Rejected, result.Outcome);
}
```

`context.IdempotencyKey` derives from `JobId`, so a test can check that an external call receives the same key on every attempt. Progress reports go nowhere unless you set `Progress` on the context.

## Integration tests: `WorkThrough.Testing`

For flows, retries, delays, signals, schedules or limits, use `WorkThroughTestHost` from the `WorkThrough.Testing` package. It runs the real store, worker and scheduler on a throwaway SQLite file, so tests exercise the same SQL and admission rules as production. Its clock can move forward.

```csharp
[Fact]
public async Task A_small_order_completes_its_checkout()
{
    // The Shop sample's jobs; a second callback replaces their dependencies, e.g. services.AddSingleton<IMailer, FakeMailer>().
    await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough.AddShopJobs(), queues: ShopJobRegistration.Queues);

    var order = new OrderPlaced("order-1", "tenant-a", "ada", 40m);
    var run = await host.Flows.StartAsync(CheckoutFlow.Definition, order, CheckoutFlow.Start(order));

    var flow = (await host.WaitForFlowAsync(run)).AssertSucceeded();
    Assert.False(flow.Step("charge").Output<Payment>().NeedsReview);
}
```

This test runs in [`samples/Shop.Tests`](../samples/Shop.Tests/CheckoutTests.cs).

### Flow logic: stub the jobs

To test what a flow does — which steps run, in which order, with which inputs, and what happens when one fails — replace its jobs with stubs. The engine, store and worker stay real; no payment or shipping code runs.

```csharp
[Fact]
public async Task A_declined_charge_apologises_instead_of_shipping()
{
    await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough
        .Reject(OrderJobs.Charge, "card declined")
        .Stub(OrderJobs.Ship)
        .Stub(OrderJobs.Apologise));

    var flow = (await host.RunFlowAsync(OrderFlow.Definition, new PlaceOrder("order-2", 40m))).AssertSucceeded();

    Assert.Equal(JobStatus.Skipped, flow.Step("ship").Status);
    Assert.Equal("order-2", flow.Step("apologise").Input<Apologise>().OrderId);
}
```

- `Stub(definition, input => output)` succeeds with an output computed from the input; `Stub(definition)` succeeds without one.
- `Reject(definition, reason)` rejects without retries, so the flow's failure policy or failure branch runs at once.
- `Stub(address, output)` and `Reject(address, reason)` take a `JobContractId` for steps declared by address.
- `RunFlowAsync` starts a run and waits for it. For a flow that waits for a signal, start it with `host.Flows`, wait with `WaitUntilIdleAsync`, send the signal, then `WaitForFlowAsync`.

`dotnet new workthrough-tests` creates a project with this test and a sample flow. [`CheckoutFlowLogicTests`](../samples/Shop.Tests/CheckoutFlowLogicTests.cs) tests the Shop checkout this way, including its manual-review signal.

- **Queues:** the worker serves the default queue unless you pass `queues`. A wait that times out names any queue with work the host does not serve.
- **Dependencies:** jobs are resolved from DI as in production. Replace their dependencies, such as a payment client, with test doubles in the second callback.
- **Time:**
  - `AdvanceAsync(TimeSpan)` moves the store's clock, which every delay, retry backoff, signal deadline, schedule and lease reads.
  - `host.UtcNow` is the moved time, for `NotBefore` values.
  - Advance while no job is running: moving past a running job's lease makes it look abandoned, as a crashed worker would.
- **Waits:**
  - `WaitForJobAsync` waits until a job is terminal.
  - `WaitForFlowAsync` waits until a run succeeds, fails or needs an operator.
  - `WaitUntilIdleAsync` waits until nothing runs and nothing is due.

  Each fails after 30 seconds with the current state, for example every step's status and error.
- **Assertions:** `AssertSucceeded()` and `AssertStatus(...)` describe every step when they fail. `Step("id").Output<T>()` and `Input<T>()` read DTOs. Fan-out items are `step[0]`, `step[1]`, and so on.
- **Direct access:** `host.Jobs`, `host.Flows`, `host.Queries` and `host.Store` are the production clients. For example, `host.Store.RecoverStepAsync(...)` drives an incident's recovery.
- **Other databases:** for PostgreSQL, SQL Server or Oracle, point the same tests at a disposable database or schema created per test run. `tests/WorkThrough.Tests/StoreHarness.cs` in this repository does that for all four providers.

## Contract tests: stored work still reads

Before changing an input type, deserialize a saved example with the definition you ship:

```csharp
var saved = JsonDocument.Parse("""{"orderId":"order-1","amount":40}""").RootElement;
var input = ShopJobs.ChargePayment.ReadInput(saved);
```

If an old payload no longer reads, give the new shape a new contract version instead; see [identity evolution](identity-evolution.md).
