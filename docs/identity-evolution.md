# Stable identity and deliberate renames

Implemented in the local alpha. `IdentityTests` covers both providers, and `scripts/check-refactor.sh` uses separately published applications with the original processor DLL removed.

Moving a class, namespace, folder, project, or assembly needs no job-data migration. Keep the permanent address, reference the new implementation project/package, rebuild the registration manifest, and deploy the worker. Stored work contains no CLR dispatch address.

## Define the behavior first, assign its address later

```csharp
[ManualProcessorRegistration]
public sealed class SubmitOrder : IProcessor<OrderInput>
{
    public Task<JobResult> ProcessAsync(OrderInput input,
        JobExecutionContext context, CancellationToken ct) =>
        Task.FromResult(JobResult.From(new OrderReceipt(input.OrderId)));
}

public static class OrderJobs
{
    public static JobDefinition<OrderInput> Submit { get; } =
        new("orders.submit", version: 1, displayName: "Submit order");
}

services.AddWorkThrough()
    .UsePostgreSql(connectionString)
    .AddProcessor<OrderInput, SubmitOrder>(OrderJobs.Submit)
    .AddWorker();

await jobs.EnqueueAsync(OrderJobs.Submit, order, cancellationToken: ct);
```

The shared definition belongs in a contracts project. Producers need that project and Core; they do not need the processor assembly. `ManualProcessorRegistration` tells the optional generator that the composition root owns registration. Attributed and manual registration use the same catalog and conflict checks. `ReadInput` can deserialize saved compatibility fixtures using the engine's JSON settings.

Display names describe behavior and may change independently. Permanent identities and their input/output meaning remain compatibility contracts. A new business meaning requires a new version or an explicit adapter; aliases do not transform payloads.

## Rename an address deliberately

A class move does not require this operation. When replacing an old arbitrary address with a meaningful one, retain the historical address as a direct alias:

```csharp
var previous = new JobContractId("processor-7f40", 1);
var current = new JobDefinition<OrderInput>("orders.submit");

services.AddWorkThrough()
    .UsePostgreSql(connectionString)
    .AddProcessor<OrderInput, SubmitOrder>(current, previous)
    .AddWorker();
```

The attributed equivalent is `[Processor("orders.submit")]` plus `[ProcessorAlias("processor-7f40")]`. Attribute and explicit canonical identity must agree. Aliases cannot collide, point at themselves, form chains, or divide versions of one key into different admission groups.

Activate the complete reviewed mapping through `IProcessorRegistry.SetProcessorAliasesAsync(mappings, expectedRevision, administrativeCommand)`. This commits an audit entry and returns the new revision as a `Result<long>`; a rejected mapping returns a validation or conflict error and commits nothing. Drain/stop workers and wait for every active valid lease to settle first. Deploy consumers for both addresses and restart them after activation. The registry refuses activation while leases are valid; workers presenting an old registry revision cannot claim another job: their next claim raises `ProcessorRegistryChangedException`, the worker stops acquiring, lets running attempts settle, and stops the host for a restart. Do not mix schema-2 engines with schema-3 engines.

Old and new names share global and local per-type capacity. Limits configured under either name continue to apply. Old queued jobs, saved flow revisions, schedules, diagnostics, and request receipts retain their original identities. New producers use the new definition. A key reused with a different request is a conflict, even if both addresses dispatch to the same processor.

Aliases cannot be removed through this API. Further renames must retain every historical address and flatten their mappings. Old producers, archived records, delayed schedules, and restored backups can outlive an empty queue. Automatic payload upcasting, Hangfire import, and alias retirement remain separate future work.
