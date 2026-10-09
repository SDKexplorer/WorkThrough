# WorkThrough

[![Verify](https://github.com/SDKexplorer/WorkThrough/actions/workflows/verify.yml/badge.svg)](https://github.com/SDKexplorer/WorkThrough/actions/workflows/verify.yml)

**Move your code. Keep your work.**

A .NET 8 library for durable jobs, persisted flows, and recurring schedules, verified on .NET 8 and .NET 10. **Move a processor between classes, namespaces, projects, and assemblies without rewriting queued job metadata.**

WorkThrough is an alpha. SQLite, PostgreSQL and SQL Server are implemented; SQL Server qualification is tracked in [provider verification](docs/verification.md); packages are built locally, not published. Production maturity, final package/domain clearance, and a project license are separate release decisions.

The optional [read-only MCP endpoint](docs/mcp.md) supports bounded execution/attempt investigation, protected failure details, saved-flow inspection, audit/archive metadata, waiting/capacity explanations, windowed attempt metrics and retention status/preview. Opt-in [automatic retention](docs/operations-and-retention.md#opt-in-automatic-maintenance) includes holds and verified-copy purge. Controlled MCP actions, the Oracle provider and large load/restore qualification remain in the [plan](specs/storage-evolution.spec.md). The [remaining delivery order](docs/plan.md#remaining-delivery-order) makes those gaps explicit. Development uses `develop` with release promotion to `main`; see [branching and public-release preparation](docs/branching-and-release.md).

## Repository map

| Location | Purpose |
|---|---|
| `src/WorkThrough.Core` | Stable processor identities, flows, state rules, and persistence contracts |
| `src/WorkThrough.Hosting` | DI composition, background workers, and schedule dispatch |
| `src/WorkThrough.Storage.*` | SQLite/PostgreSQL/SQL Server adapters and shared transactional operations |
| `src/WorkThrough.Dashboard`, `dashboard-ui` | Protected HTTP APIs and the embedded Angular console |
| `src/WorkThrough.Generators` | Compile-time processor discovery and registration |
| `src/WorkThrough.Mcp` | Optional bearer-protected, read-only MCP endpoint without a dashboard dependency |
| `samples` | API, worker, dashboard-only, direct-processor, and relocation examples |
| `tests`, `scripts` | Real-provider conformance, package consumers, and deployment proofs |
| `docs`, `specs` | Architecture decisions, requirements, evidence, and the remaining plan |

Start with the [samples](samples/README.md). Contributors should read [CONTRIBUTING.md](CONTRIBUTING.md) and [AGENTS.md](AGENTS.md); security boundaries are in [SECURITY.md](SECURITY.md). CI runs the verification workflow on pushes and pull requests. A green build is evidence for the tested scenarios, not a production maturity claim.

## Processor identity survives refactoring

```csharp
public static class InvoiceJobs
{
    public static JobDefinition<InvoiceInput> Generate { get; } =
        new("invoices.generate", displayName: "Generate invoice");
}

[ManualProcessorRegistration]
public sealed class GenerateInvoice : IProcessor<InvoiceInput>
{
    public async Task<JobResult> ProcessAsync(
        InvoiceInput invoice, JobExecutionContext context, CancellationToken ct)
    {
        await context.ReportProgressAsync("Input validated", ct);
        // Call your service; deduplicate effects using context.IdempotencyKey.

        return JobResult.From(new InvoiceOutput(invoice.InvoiceId, Generated: true));
    }
}

public sealed record InvoiceInput(string InvoiceId);
public sealed record InvoiceOutput(string InvoiceId, bool Generated);
```

The database stores the permanent processor key, contract version, and input. It never dispatches using a stored CLR type, method, or assembly-qualified name. Several processors can accept the same input type, including `string`. Typed `[JobContract]` / `IJobHandler<T>` registration remains available through the same registry.

Keep `invoices.generate/v1` and its compatible input/output meaning when moving the class. Reference and deploy its new assembly; explicit or generated registration maps the existing identity to the new implementation. Existing and newly created jobs use that mapping. Historical attempt diagnostics retain the implementation actually used. A [deployment preflight](docs/deployment-preflight.md) checks whether the proposed worker fleet covers the processor versions and queues still required by saved work. Folder moves need no engine action unless they change the compiled namespace/assembly. Incompatible business or payload changes require a new version or an explicit adapter.

The [published relocation proof](scripts/check-refactor.sh) creates typed, future-due raw-string, and persisted flow-step invocations on .NET 8, removes the original assembly, and executes them through a renamed processor assembly on .NET 10. It checks unchanged processor identities and inputs.

## Setup and hosting

Nine local NuGet packages separate Core, Hosting, Dashboard, MCP, Generators, relational mechanics, SQLite, PostgreSQL, and SQL Server. Consumers do not need Node. The Angular console is embedded in the Dashboard package.

```csharp
builder.Services.AddWorkThrough()
    .UseSqlite("Data Source=jobs.db") // Or UsePostgreSql(connectionString) / UseSqlServer(connectionString).
    .AddProcessor<InvoiceInput, GenerateInvoice>(InvoiceJobs.Generate)
    .SeedClusterLimits(new(null, 20), new(InvoiceJobs.Generate.Identity.Key, 3))
    .AddScheduler()
    .AddWorker(options =>
    {
        options.MaxConcurrentJobs = 8;
        options.PerJobLimits[InvoiceJobs.Generate.Identity.Key] = 2;
        options.ExecutionTimeout = TimeSpan.FromMinutes(5);
        options.ProgressTimeout = TimeSpan.FromMinutes(1);
    });
```

Reference `WorkThrough.Generators` with `PrivateAssets="all"` in processor projects and the worker composition root, or register explicitly with `.AddProcessor<InvoiceInput, GenerateInvoice>(InvoiceJobs.Generate)`. Generated registration rejects inaccessible processors and missing stable IDs; conflicting implementations fail at catalog composition. Worker startup resolves handler dependencies before claiming work.

Inject `JobClient`:

```csharp
await jobs.EnqueueAsync(InvoiceJobs.Generate, invoice,
    new(CorrelationId: correlationId, IdempotencyKey: requestKey), ct);
await jobs.EnqueueAsync(InvoiceJobs.Generate, invoice,
    new(NotBefore: scheduledAt), ct);
```

The explicit key overload lets producer-only hosts enqueue without the processor DLL. Producer, scheduler, BackgroundService worker, API dashboard, and standalone dashboard roles can be deployed independently. The host controls port/address; `MapWorkThroughDashboard("/operations/jobs")` controls the mount path. Generic workers need no HTTP listener. SQLite requires a shared **absolute local file path** for separate local processes; use a qualified server database for a multi-machine fleet.

Local total/type concurrency and shared total/type limits all apply. Shared limits count valid admitted leases; lowering a limit does not terminate current handlers. A partitioned process can outlive its lease, so external effects still require idempotency/fencing.

## Flows and recovery

`FlowBuilder` supports sequences, parallel roots, joins, output property bindings, equality conditions, and raw/typed processor input. Every run pins an immutable definition version. `CorrelationId` groups executions; it is not an authorization, deduplication, or locking key.

Flow definitions have **Draft → Published → Retired** lifecycle. Draft edits require an expected revision. Publication validates and freezes the graph. Retiring prevents new starts while existing runs retain their graph. Create a new version to edit published content. The code-first `WorkflowClient.StartAsync(definition, ...)` convenience registers previously unseen graphs as published; it cannot bypass an existing Draft or Retired state.

New builders suspend on exhausted retries: the failed node creates an **Incident**, dependent steps remain waiting, and admission stops until recovery. `OnFailure(FlowFailurePolicy.Stop)` preserves terminal fail-and-skip behavior; schema-1 definitions retain that behavior. Recovery retries the failed step with its original logical job ID, attempts, and idempotency key. Pause/resume controls admission while allowing running attempts to settle.

Flow starts also accept a caller idempotency key:

```csharp
var runId = await flows.StartAsync(definition, input,
    correlationId: "order:123", idempotencyKey: "process-order:123", cancellationToken: ct);
```

Repeat the identical serialized definition/input/correlation and key to recover the accepted run ID after a lost response, even if that definition has since retired. Conflicting reuse fails. Keys are case-sensitive, scoped to the configured store, and distinct from individual-job keys. Without a key, every call creates a new run. This deduplicates logical starts; handlers still execute at least once. See [flow-start semantics](docs/adr/0009-idempotent-flow-starts.md).

Failed attempts retain exception type, message, and stack trace behind diagnostics permission. Configure `DescribeFailure` to redact before storage. Optional progress reporting and execution deadlines distinguish a live lease from useful progress. Deadlines request cooperative cancellation; non-cooperative handlers remain visibly suspected-stalled and occupy their local slot until they return. Expired leases are recovered; stale tokens cannot settle work.

[Scheduling](docs/scheduling.md) covers cron time zones, draft/publication, enable/pause, overlap, misfires, and transactional occurrence creation. Recurrence currently targets individual processors.

## Dashboard and security

The Angular console reads real storage data: correlation/status filters, saved flow diagrams, attempts and protected stack traces, waiting explanations, draft lifecycle, schedules, worker observations, limits, and an administrative audit trail. It does not load application processor DLLs.

[Bounded execution queries](docs/execution-queries.md) add metadata-only processor/queue/time filters and failed-attempt searches through `IExecutionQueries`, protected `/api/executions` routes, and the optional [MCP package](docs/mcp.md). They include recovered jobs and paginate without loading input/output. MCP uses dedicated bearer authentication, resource authorization, access events and request/query limits; it exposes no administrative actions.

Configure host authentication before mapping the dashboard. Default policies require `workthrough.permission` claims; hosts can override policies and inspect route resources. Metadata, payloads, recovery, publication, schedules, limits, and audit reads have separate permissions. Administrative actions require CSRF protection, a trusted actor, a reason, a command ID, and state preconditions. Mutation, command deduplication, and successful audit commit together.

The [standalone OIDC sample and security guide](docs/dashboard-security.md) reuse an identity provider for user management. The initial isolation boundary is one configured store/environment, not row-scoped multi-tenancy. `AllowAnonymous` is only for an explicitly isolated development host.

Subscribe to the `WorkThrough` ActivitySource and Meter with OpenTelemetry to export attempt traces/counts/durations. Job/correlation IDs are trace attributes, never metric labels. The console's counts describe stored state; they are not invented throughput or SLA measurements.

## Run and verify

Repository builds use SDK 10; SDK/runtime 8 is also required for the compatibility gate. Node 24 builds the Angular assets. NuGet consumers use .NET 8 or 10 without Node.

```sh
cd dashboard-ui && npm ci && npm run build && cd ..
dotnet restore WorkThrough.slnx --disable-parallel -m:1
dotnet build WorkThrough.slnx -m:1
ASPNETCORE_ENVIRONMENT=Development dotnet run --project samples/Demo.Host --urls http://127.0.0.1:5295
```

Open `/operations/jobs`. In Development, POST `{ "orderId": "123", "amount": 250 }` to `/demo/orders`; an amount above 1000 selects the review branch, and a negative amount creates an intentional incident. The [direct processor sample](samples/Demo.Processor) demonstrates typed definitions, configurable delayed and recurring work; `--once` performs a smoke test.

`./scripts/verify.sh` builds the frontend and libraries, tests both runtimes, proves cross-runtime assembly relocation, packs NuGet artifacts, and runs isolated package-only consumers with SDK 8 and SDK 10. Set `DURABLEJOBS_POSTGRES` or `DURABLEJOBS_SQLSERVER` to disposable test databases/servers to include those providers; see [provider instructions](docs/providers.md). If .NET 8 is installed separately, set `WORKTHROUGH_DOTNET8` to its absolute executable path. Local packages are written to `artifacts/packages`.

## Operational extensions and performance

Typed `JobDefinition<T>` addresses let producers and processors share contracts without sharing implementation DLLs. Assign identities at registration, add compatible historical aliases, and activate the reviewed mapping with fleet fencing. See [identity evolution](docs/identity-evolution.md).

The engine includes scoped middleware/validation/retry policies, rejection/deferral, local observers, durable event cursors, queue pause, worker drain, expiry, causation-aware replay, signals/timeouts, transaction/outbox acceptance, and bounded whole-execution archives. The [operations guide](docs/operations-and-retention.md) explains their boundaries; schema-4 upgrades require stopping old engine writers.

Run `scripts/format.sh` for braces, statement spacing, and the requested blank lines; CI enforces `--check`. `scripts/performance.sh --full` starts independent worker processes on disposable SQLite/PostgreSQL/SQL Server stores and records latency distributions, throughput, CPU/memory, environment, and correctness assertions. See [performance methodology and results](docs/performance.md).

## Release boundaries

Execution is at least once. Arbitrary side effects cannot be undone by rewinding engine state. Compensation/checkpoint forks, child flows, Oracle and further providers, optional interoperability/upcasting, partition/rate limits, and deployment-specific production soak/partition campaigns remain in the [plan](docs/plan.md). External OIDC login requires a configured identity provider and has not been verified against a real account. There is no production certification or Native AOT claim.

The [typed processor and lifecycle exploration](docs/processor-lifecycle-and-extensibility.md) distinguishes existing APIs from implemented middleware, retry policies, and durable subscriptions.
The [identity evolution design](docs/identity-evolution.md) covers defining a processor before assigning its key and coordinated old-ID aliases, including shared concurrency and rollout requirements.

See [architecture](docs/architecture.md), [decisions](docs/adr), [requirements](specs/durable-jobs.spec.md), [community research](docs/research.md), [verification](docs/verification.md), and [contributor guardrails](AGENTS.md).
