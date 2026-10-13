# WorkThrough

[![Verify](https://github.com/SDKexplorer/WorkThrough/actions/workflows/verify.yml/badge.svg)](https://github.com/SDKexplorer/WorkThrough/actions/workflows/verify.yml)

**Move your code. Keep your work.**

Durable background jobs, flows and schedules for .NET 8 and 10, stored in SQLite, PostgreSQL, SQL Server, Oracle, MySQL or MariaDB. Stored work names a stable address such as `invoices.generate` v1, never a CLR type, so job classes can move between classes, namespaces, projects and assemblies while jobs are queued.

> **Status: alpha.** All six databases pass their [verification](docs/verification.md), packages are built locally and not yet published, and there is no production certification. See [release boundaries](#release-boundaries).

## Why WorkThrough

- **Refactor-safe:** dispatch by explicit, versioned addresses. A [published relocation proof](scripts/check-refactor.sh) runs queued work after its job classes moved to a renamed assembly on another runtime.
- **Honest delivery:** at least once, with fenced leases, so a stale worker can never settle an attempt. Crashes neither lose work nor manufacture success. Transactional enqueue and an outbox commit work together with business data.
- **Flows:** persisted graphs with bindings, conditions, joins, fan-out, child flows, loops, failure branches, compensation, durable signals, idempotent starts, incident recovery and immutable, versioned definitions.
- **Admission control:** fleet and per-type limits, per-key concurrency (serialize a customer), tenant quotas, sliding-window rate limits, bounded flow fan-out and weighted fair queues. All are exact across hosts.
- **Operations:** an embedded Angular console with audited commands, tenant-scoped access, retention with holds, deployment preflight, payload encryption at rest, a read-only MCP endpoint for AI assistants, and OpenTelemetry traces and metrics.
- **Testable and easy to adopt:** a test host runs flows on the real engine with stubbed jobs, `dotnet new` templates start a worker, a web app or a test project, and importers move existing Hangfire and Quartz.NET jobs and schedules ([Hangfire](docs/migrating-from-hangfire.md), [Quartz.NET](docs/migrating-from-quartz.md)).

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
    .UsePostgreSql(connectionString) // or UseSqlite / UseSqlServer / UseOracle / UseMySql
    .AddJobs(typeof(GenerateInvoiceJob).Assembly)
    .AddWorker(options => options.MaxConcurrentJobs = 8);
```

Enqueue from anywhere that can reach the store, refining the request fluently:

```csharp
await jobs.Enqueue(new GenerateInvoice("INV-42"))
    .After(TimeSpan.FromMinutes(5))
    .Idempotent("invoice:INV-42");
```

### One place per job

`Job(...)` declares everything about a job together: what runs it, when it recurs and how much of it may run. A small job can be a delegate instead of a class; its address stays explicit:

```csharp
public static class Emails
{
    public static JobDefinition<WelcomeEmail> Welcome { get; } = new JobDefinition<WelcomeEmail>("emails.welcome").InQueue("emails");
}

builder.Services.AddWorkThrough()
    .UseSqlite("Data Source=jobs.db")
    .Job(Emails.Welcome, job => job
        .Run(call => call.Service<IMailer>().SendAsync(call.Input.Address, call.CancellationToken))
        .AtMostRunning(10))
    .Job(Reports.Daily, job => job
        .RunWith<DailyReportJob>()
        .Every("0 6 * * *", new DailyReport(Days: 1), "Europe/Bucharest"))
    .AddScheduler()
    .AddWorker(options => options.Queues = ["default", "emails"]);

await jobs.Enqueue(Emails.Welcome, new WelcomeEmail("ada@example.com")).ForTenant("acme");
```

A definition's queue and attempt budget apply wherever it is used: enqueues, schedules and flow steps. Seeded schedules and limits are created once; operators own them afterwards.

Compared with Hangfire:

| | Hangfire | WorkThrough |
|---|---|---|
| Enqueue | `BackgroundJob.Enqueue<IMailer>(m => m.Send("ada"))` | `await jobs.Enqueue(Emails.Welcome, new WelcomeEmail("ada"))` |
| Delay | `BackgroundJob.Schedule<IMailer>(m => m.Send("ada"), TimeSpan.FromMinutes(5))` | `.After(TimeSpan.FromMinutes(5))` |
| Recurring | `RecurringJob.AddOrUpdate<Reports>("daily", r => r.Run(1), Cron.Daily)` | `.Job(Reports.Daily, job => job.RunWith<DailyReportJob>().Every("0 0 * * *", new DailyReport(1)))` |
| Every 30 seconds | `"*/30 * * * * *"` | `.Every(TimeSpan.FromSeconds(30), input)` or `.Every("*/30 * * * * *", input)` |
| What is stored | The method and its arguments | An address and an input record |
| After a rename | Queued jobs fail | Queued jobs run on the moved class |

Run several processes, or several machines on a server database, against the same store. Workers claim work under leases; a crashed worker's attempt is recovered when its lease expires.

Or start from a template. Packages are not published yet, so install it from a local build (`./scripts/verify.sh` packs it):

```bash
dotnet new install artifacts/packages/WorkThrough.Templates.0.1.0-alpha.1.nupkg
dotnet new workthrough-web -n MyApp --storage postgresql    # API + worker + console; or workthrough-worker
dotnet new workthrough-tests -n MyApp.Tests                  # xUnit tests of a flow with stubbed jobs
```

`--storage` takes `sqlite` (default), `postgresql`, `sqlserver`, `oracle` or `mysql` (MySQL and MariaDB).

Samples ([samples](samples/README.md)):

- [`QuickStart`](samples/QuickStart/Program.cs): one file, SQLite, one job.
- [`QuickStart.Web`](samples/QuickStart.Web/Program.cs): an API that enqueues jobs, with the worker and console in the same process.
- [Shop](samples/README.md#shop-sample): a complete system with an intake API, workers, an approval flow, tenants, limits, a standalone OIDC console, MCP, and tests of its checkout flow with stubbed jobs.
- [`HangfireMigration`](samples/HangfireMigration/Program.cs): moves a Hangfire application's queued, delayed and recurring jobs into WorkThrough.
- [`QuartzMigration`](samples/QuartzMigration/Program.cs): moves Quartz.NET jobs and triggers into WorkThrough schedules.

### Testing a flow

`WorkThrough.Testing` runs the real engine on a throwaway SQLite file. Stub the jobs to test a flow's logic alone ([testing](docs/testing.md)):

```csharp
await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough
    .Stub(Payments.Charge, (ChargePayment charge) => new Payment(charge.OrderId))
    .Reject(Shipping.Ship, "address unknown"));

var flow = await host.RunFlowAsync(Checkout.Definition, order);
flow.AssertStatus(FlowStatus.Incident);
Assert.Equal(order.Id, flow.Step("charge").Input<ChargePayment>().OrderId);
```

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

When an input changes, add a new version and an upcast that converts stored older inputs, so queued work, schedules and pinned flow steps run on the new job without keeping old classes ([identity evolution](docs/identity-evolution.md#change-what-an-input-means-new-version-plus-upcast)). Retired addresses can be [aliased](docs/identity-evolution.md) to new ones under fleet fencing. [Deployment preflight](docs/deployment-preflight.md) checks that a proposed worker fleet covers every address, version and queue that stored work still needs.

### Delivery

Execution is at least once. Handlers receive `context.IdempotencyKey`, which stays stable across retries.

- **Results:** `JobResult.Reject` stops without retrying invalid input. `Defer` reschedules without spending the retry budget. Retries use a configurable policy.
- **Deadlines:** progress reporting and execution deadlines separate a live lease from useful work. Non-cooperative handlers are reported as suspected-stalled.
- **Transactions:** `InTransactionAsync` commits jobs with business rows in the same database. `SendAsync` plus an outbox dispatcher delivers to a separate job store.
- **Failures:** exception details are kept behind a separate permission. Redact them before storage with `DescribeFailure`.
- **Input correction:** operators can fix the input of failed or rejected work and run it again, audited, with the original input kept.
- **Checkpoint forks:** operators can start a new run from any step of a finished or failed flow, keeping earlier outcomes and leaving the original run untouched.

### Flows

```csharp
var fulfil = new FlowBuilder("orders.fulfil", 1)
    .Step("reserve", Stock.Reserve)
        .BindInput((ReserveStock step) => step.OrderId, (Order order) => order.Id)
    .Step("ship", Shipping.Ship).After("reserve")
        .BindInput((ShipOrder step) => step.OrderId, (Order order) => order.Id)
        .WhenOutput("reserve", (Reservation reservation) => reservation.Reserved, true)
    .Build();

var run = await flows.StartAsync(fulfil, new Order("42"), new FlowStart(IdempotencyKey: "fulfil:42"), ct);
```

- **Structure:** steps bind input properties from the flow input or earlier outputs, with typed selectors that resolve to JSON names when the definition is built. Flows support equality conditions, parallel branches and joins; `WaitForSignal` steps wait for an external event without holding a worker slot.
- **Fan-out:** `ForEach` runs a step once per element of an array in an earlier output, up to 1,000 items. Steps after it receive `{"items": [...]}` with every item's output, in order. A failed item follows the flow's failure policy and is recovered like any step. `MaxParallelSteps` bounds how many items run at once.

  ```csharp
  .ForEach("ship", Shipping.Ship.Identity, "pick", (PickList list) => list.Parcels, (ShipParcel ship) => ship.Parcel)
  .Step("notify", Customers.Notify).After("ship")
  ```
- **Child flows:** `Child("ship", ShippingFlow.Definition, input)` runs another published flow as one step and waits for it without holding a worker. The step's output holds the child's final outputs; a failed child fails the step. `ForEach` with a child definition runs one child flow per item. Starting the parent publishes child definitions it carries ([ADR-023](docs/adr/0023-child-flows.md)).
- **Loops:** `Repeat("track", PollCarrier.Definition, "check", "status", "delivered", maximumIterations: 20)` runs a child flow again, passing `iteration` and the `previous` output, until a final step's output property matches ([ADR-026](docs/adr/0026-bounded-loops.md)).
- **Versioning:** every run pins an immutable definition version. Definitions move Draft → Published → Retired.
- **Compensation:** with `OnFailure(FlowFailurePolicy.Compensate)`, steps declared with `CompensateWith(...)` are undone in reverse dependency order when the flow fails, then the flow fails ([ADR-025](docs/adr/0025-compensation.md)).
- **Failure branches:** `.Step("apologise", Customers.Apologise).OnFailureOf("charge")` runs only when `charge` fails, is rejected or times out, and handles that failure: the run continues and can rejoin later steps ([ADR-028](docs/adr/0028-failure-branches.md)).
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
- **Payload protection:** `ProtectPayloads("2026-10", keys)` encrypts job inputs and outputs, flow inputs and signals at rest with AES-256-GCM and a rotating key ring. Handlers and the console see plain JSON; database readers, backups and archives see ciphertext. Schedule inputs, failure text and fingerprints are not encrypted ([ADR-027](docs/adr/0027-payload-protection.md)).
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
| Moving from Hangfire or Quartz.NET: concepts and the importers | [Migrating from Hangfire](docs/migrating-from-hangfire.md), [migrating from Quartz.NET](docs/migrating-from-quartz.md) |
| Design and invariants | [Architecture](docs/architecture.md), [decision records](docs/adr/README.md), [requirements](specs/durable-jobs.spec.md) |
| Storage providers and schema upgrades | [Providers](docs/providers.md) |
| Rolling upgrades, payload encryption | [ADR-021](docs/adr/0021-rolling-upgrades.md), [ADR-027](docs/adr/0027-payload-protection.md) |
| Scheduling | [Scheduling](docs/scheduling.md) |
| Address changes and aliases | [Identity evolution](docs/identity-evolution.md), [deployment preflight](docs/deployment-preflight.md) |
| Console, permissions, tenants | [Dashboard security](docs/dashboard-security.md) |
| Queries and MCP | [Execution queries](docs/execution-queries.md), [MCP](docs/mcp.md) |
| Operations and retention | [Operations and retention](docs/operations-and-retention.md) |
| Testing jobs and flows, `WorkThrough.Testing` | [Testing](docs/testing.md) |
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
- packs every library package and the `WorkThrough.Templates` package into `artifacts/packages`;
- creates and builds every template with every storage provider, runs the SQLite projects and the tests template;
- runs NuGet-only consumers on SDK 8 and 10;
- runs the Shop sample's smoke test and preflight, and the quick-start and Hangfire migration samples;
- runs process-kill and load smoke exercises.

Server providers are included when `DURABLEJOBS_POSTGRES`, `DURABLEJOBS_SQLSERVER`, `DURABLEJOBS_ORACLE`, `DURABLEJOBS_MYSQL` or `DURABLEJOBS_MARIADB` point at disposable databases ([providers](docs/providers.md)). Set `WORKTHROUGH_DOTNET8` when .NET 8 lives elsewhere. `scripts/format.sh` applies the code style that CI enforces.

## Release boundaries

- Delivery is at least once. Rewinding engine state cannot undo external side effects; compensation runs only the undo jobs an application declares.
- Tenant scoping governs WorkThrough's hosts, not direct database access. Archives are store-wide.
- OIDC sign-in, role permissions and tenant scoping are verified against a local Keycloak 26.4 (`scripts/check-oidc.py`), not yet against a hosted provider such as Entra ID or Auth0.
- Throughput per store, on the documented test host with near-instant handlers: about 800/s on PostgreSQL and SQLite without limits, and 500/s on PostgreSQL with 50 tenant quotas. SQL Server with `READ_COMMITTED_SNAPSHOT` reached 430/s and Oracle 280/s without limits; MySQL and MariaDB throughput is not yet measured. Claims under limits lock only the rows of the limits they touch, so tenants do not wait on each other, and limits stay exact ([ADR-020](docs/adr/0020-write-path-throughput.md), [ADR-022](docs/adr/0022-limit-locks.md)). A fleet-wide limit still serializes claims. WorkThrough suits business workflows; for high-volume event streams, use a message broker.
- There is no production certification and no Native AOT support. Performance figures describe the documented test host.
- Package publication and the project license are pending maintainer decisions.

Contributors: read [CONTRIBUTING.md](CONTRIBUTING.md) and [AGENTS.md](AGENTS.md). Security reports: [SECURITY.md](SECURITY.md). Branching and releases: [branching and release](docs/branching-and-release.md).
