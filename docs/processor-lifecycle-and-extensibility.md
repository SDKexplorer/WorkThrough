# Typed processors and execution extensions

Implemented in the local alpha. Use `IProcessor<TInput>` for normal application code; use `IProcessor<string>` when the application deliberately owns an opaque JSON/string contract. Processor identity is independent of either choice.

The executable invoice example is split into `InvoiceJobs`, `GenerateInvoice`, `InvoiceSchedule`, and the host composition root. It uses a shared typed definition and typed settings. The order example uses named flow steps and `JsonField.Of<T, TValue>` selectors: only resolved JSON property names enter the saved graph, never expressions or delegates. Renaming a serialized property still changes its data contract; use `JsonPropertyName` or a new contract version when required.

## State and extension points

Jobs can wait for dependencies, queue, run, succeed, fail, be cancelled/skipped, be rejected permanently, expire before admission, or await a durable signal. Definitions retain Draft → Published → Retired state separately from run state. A failed flow can suspend as an Incident; explicit recovery retains the logical step and effect key. Replay creates a new job, new effect key, and causation link.

```csharp
jobs.AddMiddleware<AttemptLogging>()
    .AddValidator<OrderInput, OrderValidator>()
    .UseRetryPolicy<ApplicationRetryPolicy>()
    .AddObserver<LocalTelemetry>();
```

`IJobMiddleware` is scoped per attempt. First registered is outermost. It must call and await its continuation exactly once on successful execution. Exceptions before/after the processor are attempt failures. Repeated continuation, skipped continuation, and early return are errors; a launched processor is still awaited before releasing scope and local capacity. Middleware executes outside the storage transaction, and its side effects may repeat.

`IJobValidator<TInput>` validates typed input before invocation. Throw `JobRejectedException` for permanently invalid input. The default retry policy also rejects JSON deserialization errors and retries other failures with exponential delay and jitter within the saved attempt budget. `IJobRetryPolicy` can return `RetryDecision.Stop` or `RetryDecision.After(delay)`; changing classification does not erase attempts.

A processor can return `JobResult.Reject(reason)` or `JobResult.Defer(delay, reason)`. Rejection is terminal unless deliberately recovered/replayed. Deferral releases the slot and schedules another attempt without consuming the failure retry budget; it remains visible in attempt history. Configure application limits on repeated deferral when business rules require an eventual deadline. Inline input and output are bounded to 1 MiB; store larger data externally and pass a reference.

`IJobObserver` runs after an acknowledged storage settlement. Resolution/callback failures are logged and cannot reverse the committed job result. This is best-effort local observation: a process may die between commit and callback. The timeout token is cooperative, and a non-cooperating observer delays scope disposal.

## Durable event consumption

Use `AddEventSubscriber<THandler>(stableName)` and `IExecutionEventHandler` for persisted attempt/state events. The subscriber reads the journal, invokes a scoped handler, then advances its durable cursor with compare-and-swap. A crash after handling but before acknowledgment repeats delivery. Deduplicate effects with `(subscriber name, event sequence)`; neither a local observer nor the subscriber provides exactly-once effects. Multiple instances of the same subscription can invoke the same event concurrently; use one instance for ordered effects or make the destination safely idempotent.

`IExecutionEvents` also exposes explicit read/ack APIs. Journal trimming requires an audited command, archived affected executions, and every subscriber past the cutoff. Sequence numbers never reset. A new subscriber requesting trimmed history must explicitly select a retained starting position; missing history is not silently ignored. There is no subscription-delete shortcut that bypasses lagging consumers.

See [operations and retention](operations-and-retention.md) for transactions, signals, queue controls, and backups. Validation, middleware ordering, invalid continuations, observer failure, durable cursors, and both provider failure paths are executable tests.
