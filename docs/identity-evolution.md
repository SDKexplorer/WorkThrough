# Defining a processor before naming it, and renaming an identity

Status: design exploration, 2026-10-08. Attributed registration and stable execution identity already exist. The explicit-identity registration overload and alias attribute below are **proposed APIs**, not features in the current packages.

## Separate implementation, identity, and display name

The processor class defines behavior. Its permanent identity addresses that behavior in stored jobs, schedules, and flow steps. A display name makes it readable in the console. These are separate choices.

A processor class can be written before choosing its identity. Durable enqueue must still specify an identity before acceptance; a stored job cannot safely depend on a temporary class name or a fresh random value generated at startup. An identifier generated once by a scaffolding command and committed as a constant could be supported later. Regenerating it on builds, moves, or restarts would break queued work.

The current package supports `[Processor("process.myorder", Version = 1)]`. Producers can enqueue with `new JobContractId("process.myorder", 1)` without referencing that class. That already survives class, namespace, project, and assembly moves.

The existing identifier format is a lowercase key beginning with a letter. An opaque key such as `job.92d42c3a-62a9-4e50-ae24-89bd0b9b07ad` fits that format. Bare GUID strings are not a separate supported identity type. If this key is already persisted, changing a human-readable name should leave it intact. Display-name propagation to saved definitions/the console remains a separate roadmap item.

## Proposed: assign the identity at composition time

```csharp
public sealed record OrderInput(Guid OrderId);

// No identity tied to this class's name or location.
public sealed class ProcessOrder : IProcessor<OrderInput>
{
    public Task<JobResult> ProcessAsync(
        OrderInput message, JobExecutionContext context, CancellationToken ct)
        => Task.FromResult(JobResult.Done());
}

public static class OrderJobs
{
    public static readonly JobContractId Process = new("process.myorder", 1);
}

// Proposed overload: chooses the identity when assembling the worker.
builder.Services.AddWorkThrough()
    .AddProcessor<OrderInput, ProcessOrder>(OrderJobs.Process);

// Existing producer API, independent of processor registration.
await jobs.EnqueueAsync(OrderJobs.Process, new OrderInput(orderId), cancellationToken: ct);
```

The constant can live in a small contracts package shared by producers and workers. `ProcessorId.For<ProcessOrder>()` remains an attribute helper; it must not guess identities by looking at mutable global DI state. Explicit registration should reject a conflicting attribute rather than silently let generated and explicit registration create two different identities.

The current generator reports missing identity attributes. This addition therefore needs a deliberate manual-registration opt-out, including missing-registration/startup tests. Silencing diagnostics globally or silently skipping all unmarked processors would remove a useful guardrail. Both registration paths must compose into the existing catalog, with the same collision validation and invocation path.

## Proposed: retain an old identity as an explicit compatibility alias

For a real identity change, prefer an old-to-current mapping to an attribute that mutates stored data at startup:

```csharp
// Proposed attribute, not available today.
[Processor("process.myorder", Version = 1)]
[ProcessorAlias("job.92d42c3a-62a9-4e50-ae24-89bd0b9b07ad", Version = 1)]
public sealed class ProcessOrder : IProcessor<OrderInput>
{
    // Compatible implementation of the original input/output behavior.
}
```

New producers use `process.myorder/v1`. Already saved work keeps its original identity and input and resolves through the declared alias to the deployed processor. Old producers can continue enqueueing the old key during a controlled rollout. Saved flow revisions and recurring schedule definitions remain immutable; an existing schedule continues creating jobs under its saved target until explicitly replaced. Do not silently rewrite that target just because a worker starts.

This is a compatibility declaration, not a payload migration. An incompatible input/output shape or changed business meaning needs a versioned adapter/upcaster with tests. Aliasing arbitrary versions to the latest code is unsafe.

### Required safety rules

- An old identity/version maps directly to exactly one target; reject collisions, alias chains, cycles, self-aliases, and ambiguous generated registrations before claiming work.
- New workers advertise support for both identities. Preflight includes alias coverage and reports which compatibility mapping supplies it. Removing an alias must account for delayed work, active/paused/incident runs, enabled or paused schedule definitions, manual retries, old producers, and restore/retention windows; an empty ready queue is insufficient.
- Keep the original identity in execution and attempt history. Record the resolved implementation/mapping as diagnostics. Ordinary reads and dashboards still need no application assemblies.
- **Concurrency must span both names.** A catalog-only alias could let old-key and new-key jobs each consume a full per-type limit. A shared, persisted admission group and a fleet rollout rule are required before promising alias-transparent limits. Local worker limits must use the same group. Old workers unaware of the grouping must be drained before activating that guarantee.
- Preserve the original logical JobId and effect idempotency key across retries. Define enqueue receipt behavior for old/new names explicitly: the current implementation rejects reuse of an enqueue key with a different processor identity. Do not silently change that rule or manufacture a second logical job during deployment.
- Define who owns the mapping and how it is reconciled across workers. An old deployment must never overwrite a newer mapping. Changes affecting shared admission need revision checks and audit, not an uncoordinated write by every startup.

These cross-cutting rules are why an alias needs more than another dictionary entry. Until implemented and provider-tested, keeping the permanent key unchanged is the supported refactoring path.

## Explicit data migration remains a different operation

Importing legacy Hangfire metadata, converting old payloads, and moving selected runs to another definition version are migration tools, not class-discovery behavior. They need a preview, counts and conflicts, explicit mappings, resumable/idempotent batches, source evidence, and audit. A random old key can only be mapped if it is known; no engine can infer a safe replacement from a renamed class.

Recommended order: explicit identity-at-registration, useful display names, then coordinated aliases with shared admission and deployment checks. Keep payload upcasting and legacy import separate. Verification must include a package-only consumer, generated/manual coexistence, mixed-name concurrency on both providers, unchanged persisted jobs/flow definitions, idempotency conflicts, and a published old-to-new assembly replacement.
