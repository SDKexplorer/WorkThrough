# WorkThrough

[![Verify](https://github.com/SDKexplorer/WorkThrough/actions/workflows/verify.yml/badge.svg)](https://github.com/SDKexplorer/WorkThrough/actions/workflows/verify.yml)

**Move your code. Keep your work.**

Durable background jobs, flows and schedules for .NET 8 and 10, stored in SQLite, PostgreSQL, SQL Server or Oracle. Stored work names a stable address such as `invoices.generate` v1, never a CLR type, so job classes can move between classes, namespaces, projects and assemblies while jobs are queued.

> **Status: alpha.** All four providers pass their [verification](docs/verification.md), packages are built locally and not yet published, and there is no production certification. See [release boundaries](#release-boundaries).

## Why WorkThrough

- **Refactor-safe:** dispatch by explicit, versioned addresses. A [published relocation proof](scripts/check-refactor.sh) runs queued work after its job classes moved to a renamed assembly on another runtime.
- **Honest delivery:** at least once, with fenced leases, so a stale worker can never settle an attempt. Crashes neither lose work nor manufacture success. Transactional enqueue and an outbox commit work together with business data.
- **Flows:** persisted graphs with bindings, conditions, joins, durable signals, idempotent starts, incident recovery and immutable, versioned definitions.
- **Admission control:** fleet and per-type limits, per-key concurrency (serialize a customer), tenant quotas, sliding-window rate limits, bounded flow fan-out and weighted fair queues. All are exact across hosts.
- **Operations:** an embedded Angular console with audited commands, tenant-scoped access, retention with holds, deployment preflight, a read-only MCP endpoint for AI assistants, and OpenTelemetry traces and metrics.

## Quick start

A job is a class that implements `IJob<TInput>`. Its input type carries the address that the database stores:

```csharp
[JobContract("invoices.generate")]
public sealed record GenerateInvoice(string InvoiceId);

public sealed class GenerateInvoiceJob(IInvoiceService invoices) : IJob<GenerateInvoice>
{
    public async Task<JobResult> ExecuteAsync(GenerateInvoice input, JobExecutionContext context, CancellationToken ct)
    {
        // Attempts can repeat after a crash: let the external service deduplicate on the stable key.
        var number = await invoices.CreateAsync(input.InvoiceId, context.IdempotencyKey, ct);

        return JobResult.From(new { number });
    }
}
```

Register storage, the assembly's jobs and a worker:

```csharp
builder.Services.AddWorkThrough()
    .UsePostgreSql(connectionString) // or UseSqlite / UseSqlServer / UseOracle
    .AddJobs(typeof(GenerateInvoiceJob).Assembly)
    .AddWorker(options => options.MaxConcurrentJobs = 8);
```

Enqueue from anywhere that can reach the store:

```csharp
await jobs.EnqueueAsync(new GenerateInvoice("INV-42"),
    new(IdempotencyKey: "invoice:INV-42", NotBefore: DateTimeOffset.UtcNow.AddMinutes(5)), ct);
```

Run several processes, or several machines on a server database, against the same store. Workers claim work under leases; a crashed worker's attempt is recovered when its lease expires.

Or start from a template. Packages are not published yet, so install it from a local build (`./scripts/verify.sh` packs it):

```bash
dotnet new install artifacts/packages/WorkThrough.Templates.0.1.0-alpha.1.nupkg
dotnet new workthrough-web -n MyApp --storage postgresql    # API + worker + console; or workthrough-worker
```

`--storage` takes `sqlite` (default), `postgresql`, `sqlserver` or `oracle`.

Three samples build on each other ([samples](samples/README.md)):

- [`QuickStart`](samples/QuickStart/Program.cs): one file, SQLite, one job.
- [`QuickStart.Web`](samples/QuickStart.Web/Program.cs): an API that enqueues jobs, with the worker and console in the same process.
- [Shop](samples/README.md#shop-sample): a complete system with an intake API, workers, an approval flow, tenants, limits, a standalone OIDC console and MCP.

### Addresses shared with producers

When producers live in other services, or one input type feeds several jobs, declare the address once as a `JobDefinition` and bind the class at the composition root. The input type then needs no attribute, and schedules take the same definition:

```csharp
public static class InvoiceJobs
{
    public static JobDefinition<GenerateInvoice> Generate { get; } = new("invoices.generate", displayName: "Generate invoice");
}

builder.Services.AddWorkThrough()
    .UsePostgreSql(connectionString)
    .AddJob<GenerateInvoice, GenerateInvoiceJob>(InvoiceJobs.Generate)
    .SeedSchedules(ScheduleDefinition.Create("invoices.nightly", "0 2 * * *", InvoiceJobs.Generate, new GenerateInvoice("batch")))
    .AddScheduler()
    .AddWorker();

await jobs.EnqueueAsync(InvoiceJobs.Generate, new GenerateInvoice("INV-42"), cancellationToken: ct);
```

## Concepts

### Addresses and relocation

A job's address is its key and contract version. The database stores that address and the JSON input, never a type, method or assembly name. To move a job class, keep its address: the attribute moves with it, or `AddJob` maps the address to the new class. Change the version when the input's meaning changes.

Every job implements `IJob<TInput>`. Its address comes from `[JobContract("key")]` on the input type, from `[Job("key")]` on the class, or from a `JobDefinition` passed to `AddJob`. A definition suits addresses shared with producers in other services, and input types that several jobs accept. `AddJobs(assembly)` registers attributed jobs at startup; the `WorkThrough.Generators` source generator does the same at build time and reports a job without an address as a compile error.

Retired addresses can be [aliased](docs/identity-evolution.md) to new ones under fleet fencing. [Deployment preflight](docs/deployment-preflight.md) checks that a proposed worker fleet covers every address, version and queue that stored work still needs.

### Delivery

Execution is at least once. Handlers receive `context.IdempotencyKey`, which stays stable across retries.

- **Results:** `JobResult.Reject` stops without retrying invalid input. `Defer` reschedules without spending the retry budget. Retries use a configurable policy.
- **Deadlines:** progress reporting and execution deadlines separate a live lease from useful work. Non-cooperative handlers are reported as suspected-stalled.
- **Transactions:** `InTransactionAsync` commits jobs with business rows in the same database. `SendAsync` plus an outbox dispatcher delivers to a separate job store.
- **Failures:** exception details are kept behind a separate permission. Redact them before storage with `DescribeFailure`.

### Flows

```csharp
var fulfil = new FlowBuilder("orders.fulfil", 1)
    .Step("reserve", Stock.Reserve.Identity)
        .BindInput((ReserveStock step) => step.OrderId, (Order order) => order.Id)
    .Step("ship", Shipping.Ship.Identity).After("reserve")
        .BindInput((ShipOrder step) => step.OrderId, (Order order) => order.Id)
        .WhenOutput("reserve", (Reservation reservation) => reservation.Reserved, true)
    .Build();

var run = await flows.StartAsync(fulfil, new Order("42"), new FlowStart(IdempotencyKey: "fulfil:42"), ct);
```

- **Structure:** steps bind input properties from the flow input or earlier outputs, with typed selectors that resolve to JSON names when the definition is built. Flows support equality conditions, parallel branches and joins; `WaitForSignal` steps wait for an external event without holding a worker slot.
- **Fan-out:** `ForEach` runs a step once per element of an array in an earlier output, up to 1,000 items. Steps after it receive `{"items": [...]}` with every item's output, in order. A failed item follows the flow's failure policy and is recovered like any step. `MaxParallelSteps` bounds how many items run at once.

  ```csharp
  .ForEach("ship", Shipping.Ship.Identity, "pick", (PickList list) => list.Parcels, (ShipParcel ship) => ship.Parcel)
  .Step("notify", Notify.Identity).After("ship")
  ```
- **Versioning:** every run pins an immutable definition version. Definitions move Draft → Published → Retired.
- **Failure:** a step that fails by default suspends the run as an **incident**; operators recover the step with its history and idempotency key intact. See [flow starts](docs/adr/0009-idempotent-flow-starts.md) and [architecture](docs/architecture.md).

### Admission control

All limits are evaluated in the claim transaction, so they hold across every host. They count admitted, unexpired leases ([ADR-018](docs/adr/0018-execution-admission-controls.md)).

```csharp
builder.Services.AddWorkThrough()
    .SeedClusterLimits(new(null, 50), new(Shipping.Ship.Identity.Key, 5))      // fleet-wide and per type
    .SeedKeyedLimits(new(KeyScope.Partition, null, 1),                        // one at a time per partition key
                     new(KeyScope.Tenant, null, 10))                          // at most 10 slots per tenant
    .SeedRateLimits(new RateLimit(Payments.Charge.Identity.Key, 100, TimeSpan.FromMinutes(1), KeyScope.Tenant));

await jobs.EnqueueAsync(Payments.Charge, payment, new(TenantId: tenant, PartitionKey: $"{tenant}/customer/{id}"), ct);
```

- **Fan-out:** `FlowStart.MaxParallelSteps` releases a wide flow stage gradually.
- **Weighted queues:** queues sharing workers are served by weighted fair scheduling.
- **Seeding:** seeded values are created once. Afterwards operators change them in the console, and redeployments never overwrite those changes.
- **Partition keys:** keys are global, so namespace them by tenant when tenants reuse IDs.

### Operations and security

- **Console:** the [dashboard](docs/dashboard-security.md) reuses the host's ASP.NET Core authentication.
  - Permissions are separate for metadata, payloads, recovery, publication, schedules, limits and audit.
  - Every command is CSRF-protected, attributed to a trusted actor, deduplicated, and committed together with its audit record.
- **Tenant scoping:** register an `ITenantAccessResolver` to scope users to their tenants ([ADR-019](docs/adr/0019-tenant-access-isolation.md)).
- **Data operations:** [operations and retention](docs/operations-and-retention.md) covers queue pause, worker drain, replay, archives, retention with holds and verified exports, and event subscriptions.
- **MCP:** the [MCP endpoint](docs/mcp.md) gives AI assistants bounded, read-only, audited investigation tools behind bearer authentication.
- **OpenTelemetry:** subscribe to the `WorkThrough` ActivitySource and Meter. An attempt continues the trace that enqueued it, including through flows and the outbox. Job IDs are trace attributes, never metric labels.
- **Health checks:** `services.AddHealthChecks().AddWorkThrough()` reports storage reachability and, in worker hosts, repeated acquisition failures.
- **Rolling upgrades:** from schema 6, compatible schema changes and new engine semantics deploy while workers keep running. Older hosts refuse work they could misread ([ADR-021](docs/adr/0021-rolling-upgrades.md)).

## Documentation

| Topic | Where |
|---|---|
| How it compares with Hangfire, Quartz.NET, Wolverine, MassTransit, Temporal | [Comparison](docs/comparison.md) |
| Design and invariants | [Architecture](docs/architecture.md), [decision records](docs/adr), [requirements](specs/durable-jobs.spec.md) |
| Storage providers and schema upgrades | [Providers](docs/providers.md) |
| Scheduling | [Scheduling](docs/scheduling.md) |
| Address changes and aliases | [Identity evolution](docs/identity-evolution.md), [deployment preflight](docs/deployment-preflight.md) |
| Console, permissions, tenants | [Dashboard security](docs/dashboard-security.md) |
| Queries and MCP | [Execution queries](docs/execution-queries.md), [MCP](docs/mcp.md) |
| Operations and retention | [Operations and retention](docs/operations-and-retention.md) |
| Testing jobs and flows | [Testing](docs/testing.md) |
| Evidence | [Verification](docs/verification.md), [performance](docs/performance.md) |
| Roadmap | [Plan](docs/plan.md) |

## Build and verify

Repository builds need .NET SDK 10, a .NET 8 runtime for the compatibility gate, and Node 24 for the console assets. Package consumers need neither Node nor SDK 10.

```sh
./scripts/verify.sh
```

The script:

- builds the console and libraries;
- runs the test suite on .NET 10 and .NET 8;
- runs the relocation proof;
- packs the ten packages and the `WorkThrough.Templates` package into `artifacts/packages`;
- creates and builds every template with every storage provider, and runs the SQLite projects;
- runs NuGet-only consumers on SDK 8 and 10;
- runs the Shop sample's smoke test and preflight;
- runs process-kill and load smoke exercises.

Server providers are included when `DURABLEJOBS_POSTGRES`, `DURABLEJOBS_SQLSERVER` or `DURABLEJOBS_ORACLE` point at disposable databases ([providers](docs/providers.md)). Set `WORKTHROUGH_DOTNET8` when .NET 8 lives elsewhere. `scripts/format.sh` applies the code style that CI enforces.

## Release boundaries

- Delivery is at least once. Rewinding engine state cannot undo external side effects; compensation, checkpoint forks and child flows are planned.
- Tenant scoping governs WorkThrough's hosts, not direct database access. Archives are store-wide.
- External OIDC sign-in requires a configured identity provider and has not been verified against a real account.
- Throughput per store, on the documented test host with near-instant handlers and no limits: about 810–830/s on PostgreSQL, 720–810/s on SQLite, 430/s on SQL Server with `READ_COMMITTED_SNAPSHOT`, and 280/s on Oracle. Limits are exact because claims serialize while any limit exists; such stores reach a few hundred jobs per second ([ADR-020](docs/adr/0020-write-path-throughput.md)). WorkThrough suits business workflows; for high-volume event streams, use a message broker.
- There is no production certification and no Native AOT support. Performance figures describe the documented test host.
- Package publication and the project license are pending maintainer decisions.

Contributors: read [CONTRIBUTING.md](CONTRIBUTING.md) and [AGENTS.md](AGENTS.md). Security reports: [SECURITY.md](SECURITY.md). Branching and releases: [branching and release](docs/branching-and-release.md).
