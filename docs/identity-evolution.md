# Job addresses: stable identity and deliberate renames

Implemented in the local alpha. `IdentityTests` runs on every provider, and `scripts/check-refactor.sh` uses separately published applications with the original processor DLL removed.

Moving a class, namespace, folder, project, or assembly needs no job-data migration. Keep the permanent address, reference the new implementation project/package, rebuild the registration manifest, and deploy the worker. Stored work contains no CLR dispatch address.

## Define the behavior first, assign its address later

```csharp
[ManualJobRegistration]
public sealed class SubmitOrder : IJob<OrderInput>
{
    public Task<JobResult> ExecuteAsync(OrderInput input,
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
    .AddJob<OrderInput, SubmitOrder>(OrderJobs.Submit)
    .AddWorker();

await jobs.EnqueueAsync(OrderJobs.Submit, order, cancellationToken: ct);
```

The shared definition belongs in a contracts project. Producers need that project and Core; they do not need the job assembly. `[ManualJobRegistration]` tells the optional generator that the composition root owns registration. Attributed and manual registration use the same catalog and conflict checks. `ReadInput` can deserialize saved compatibility fixtures using the engine's JSON settings.

Display names describe behavior and may change independently. Permanent identities and their input/output meaning remain compatibility contracts. A new business meaning requires a new version or an explicit adapter; aliases do not transform payloads.

## Change what an input means: new version plus upcast

When a job's input changes shape or meaning, give the new shape a new version of the same key, and keep old stored work runnable. Queued jobs, delayed jobs, schedules and pinned flow steps keep the address they were saved with. There are two ways to serve them:

- Keep the old version's job class deployed beside the new one.
- Register an upcast, which converts each stored input and runs the new job:

```csharp
builder.Services.AddWorkThrough()
    .AddJob<GenerateInvoiceV3, GenerateInvoiceJob>(InvoiceJobs.GenerateV3)
    .AddUpcast(InvoiceJobs.GenerateV2.Identity, InvoiceJobs.GenerateV3, (GenerateInvoiceV2 old) => new(old.Id, old.Currency, Total: 0m))
    .AddUpcast(new JobContractId("invoices.generate", 1), InvoiceJobs.GenerateV2,
        (JsonElement old) => new GenerateInvoiceV2(old.GetProperty("id").GetString()!, "EUR"));
```

- **Order and chains:** register the target job first. Upcasts chain, so v1 runs as v2 and then v3.
- **Old types:** read the input as `JsonElement` once its type is gone from the codebase.
- **Stored identity:** the job keeps its stored address and input. Only how this build executes it changes. Each worker serves an old version its own way, so an upcast needs no registry change or fleet fencing.
- **Admission:** an upcast bridges versions of one key, so they share the same limits. To change the key itself, use an alias, below.
- **Rollout:** [deployment preflight](deployment-preflight.md) counts an upcast address as covered. Run it with the candidate worker before retiring the old job class. Worker heartbeats show where each upcast address runs (`upcastTo`).

## Rename an address deliberately

A class move does not require this operation. When replacing an old arbitrary address with a meaningful one, retain the historical address as a direct alias:

```csharp
var previous = new JobContractId("processor-7f40", 1);
var current = new JobDefinition<OrderInput>("orders.submit");

services.AddWorkThrough()
    .UsePostgreSql(connectionString)
    .AddJob<OrderInput, SubmitOrder>(current, previous)
    .AddWorker();
```

The attributed equivalent is `[Job("orders.submit")]` plus `[JobAlias("processor-7f40")]`. Attribute and explicit canonical identity must agree. Aliases cannot collide, point at themselves, form chains, or divide versions of one key into different admission groups.

Activate the complete reviewed mapping through `IProcessorRegistry.SetProcessorAliasesAsync(mappings, expectedRevision, administrativeCommand)`. This commits an audit entry and returns the new revision as a `Result<long>`; a rejected mapping returns a validation or conflict error and commits nothing. Drain/stop workers and wait for every active valid lease to settle first. Deploy consumers for both addresses and restart them after activation. The registry refuses activation while leases are valid; workers presenting an old registry revision cannot claim another job: their next claim raises `ProcessorRegistryChangedException`, the worker stops acquiring, lets running attempts settle, and stops the host for a restart. Do not mix schema-2 engines with schema-3 engines.

Old and new names share global and local per-type capacity. Limits configured under either name continue to apply. Old queued jobs, saved flow revisions, schedules, diagnostics, and request receipts retain their original identities. New producers use the new definition. A key reused with a different request is a conflict, even if both addresses dispatch to the same processor.

Aliases cannot be removed through this API. Further renames must retain every historical address and flatten their mappings. Old producers, archived records, delayed schedules, and restored backups can outlive an empty queue. Alias retirement remains future work. Input upcasts are described above; jobs stored by Hangfire move through the [Hangfire importer](migrating-from-hangfire.md).
