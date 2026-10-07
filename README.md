# WorkThrough

A .NET 8 library for durable jobs, persisted flows, and recurring schedules, verified on .NET 8 and .NET 10. **Move a processor between classes, namespaces, projects, and assemblies without rewriting queued job metadata.**

WorkThrough is an alpha. SQLite and PostgreSQL are implemented; packages are built locally, not published. Production maturity, final package/domain clearance, and a project license are separate release decisions.

## Processor identity survives refactoring

```csharp
[Processor("invoices.generate", Version = 1, DisplayName = "Generate invoice")]
public sealed class GenerateInvoice : IProcessor<string>
{
    public async Task<JobResult> ProcessAsync(
        string message, JobExecutionContext context, CancellationToken ct)
    {
        var invoice = JsonSerializer.Deserialize<InvoiceInput>(message, JobJson.Options)!;
        await context.ReportProgressAsync("Input validated", ct);
        // Call your service; deduplicate external effects using context.IdempotencyKey.
        return JobResult.From(new { invoice.InvoiceId, generated = true });
    }
}
public sealed record InvoiceInput(string InvoiceId);
```

The database stores the permanent processor key, contract version, and input. It never dispatches using a stored CLR type, method, or assembly-qualified name. Several processors can accept the same input type, including `string`. Typed `[JobContract]` / `IJobHandler<T>` registration remains available through the same registry.

Keep `invoices.generate/v1` and its compatible input/output meaning when moving the class. Reference and deploy its new assembly; explicit or generated registration maps the existing identity to the new implementation. Existing and newly created jobs use that mapping. Historical attempt diagnostics retain the implementation actually used. Folder moves need no engine action unless they change the compiled namespace/assembly. Incompatible business or payload changes require a new version or an explicit adapter.

The [published relocation proof](scripts/check-refactor.sh) creates typed, future-due raw-string, and persisted flow-step invocations on .NET 8, removes the original assembly, and executes them through a renamed processor assembly on .NET 10. It checks unchanged processor identities and inputs.

## Setup and hosting

Seven local NuGet packages separate Core, Hosting, Dashboard, Generators, relational mechanics, SQLite, and PostgreSQL. Consumers do not need Node. The Angular console is embedded in the Dashboard package.

```csharp
builder.Services.AddWorkThrough()
    .UseSqlite("Data Source=jobs.db") // Or UsePostgreSql(connectionString).
    .AddGeneratedJobs(typeof(Program).Assembly)
    .SeedClusterLimits(new(null, 20), new("invoices.generate", 3))
    .AddScheduler()
    .AddWorker(options =>
    {
        options.MaxConcurrentJobs = 8;
        options.PerJobLimits["invoices.generate"] = 2;
        options.ExecutionTimeout = TimeSpan.FromMinutes(5);
        options.ProgressTimeout = TimeSpan.FromMinutes(1);
    });
```

Reference `WorkThrough.Generators` with `PrivateAssets="all"` in processor projects and the worker composition root, or use `.AddProcessor<string, GenerateInvoice>()`. Generated registration rejects inaccessible processors and missing stable IDs; conflicting implementations fail at catalog composition. Worker startup resolves handler dependencies before claiming work.

Inject `JobClient`:

```csharp
await jobs.EnqueueAsync(ProcessorId.For<GenerateInvoice>(), json,
    new(CorrelationId: "invoice:123", IdempotencyKey: "invoice:123"), ct);
await jobs.EnqueueAsync(new JobContractId("invoices.generate", 1), json,
    new(NotBefore: DateTimeOffset.UtcNow.AddHours(1)), ct);
```

The explicit key overload lets producer-only hosts enqueue without the processor DLL. Producer, scheduler, BackgroundService worker, API dashboard, and standalone dashboard roles can be deployed independently. The host controls port/address; `MapWorkThroughDashboard("/operations/jobs")` controls the mount path. Generic workers need no HTTP listener. SQLite requires a shared **absolute local file path** for separate local processes; use PostgreSQL for a multi-machine fleet.

Local total/type concurrency and shared total/type limits all apply. Shared limits count valid admitted leases; lowering a limit does not terminate current handlers. A partitioned process can outlive its lease, so external effects still require idempotency/fencing.

## Flows and recovery

`FlowBuilder` supports sequences, parallel roots, joins, output property bindings, equality conditions, and raw/typed processor input. Every run pins an immutable definition version. `CorrelationId` groups executions; it is not an authorization, deduplication, or locking key.

Flow definitions have **Draft → Published → Retired** lifecycle. Draft edits require an expected revision. Publication validates and freezes the graph. Retiring prevents new starts while existing runs retain their graph. Create a new version to edit published content. The code-first `WorkflowClient.StartAsync(definition, ...)` convenience registers previously unseen graphs as published; it cannot bypass an existing Draft or Retired state.

New builders suspend on exhausted retries: the failed node creates an **Incident**, dependent steps remain waiting, and admission stops until recovery. `OnFailure(FlowFailurePolicy.Stop)` preserves terminal fail-and-skip behavior; schema-1 definitions retain that behavior. Recovery retries the failed step with its original logical job ID, attempts, and idempotency key. Pause/resume controls admission while allowing running attempts to settle.

Failed attempts retain exception type, message, and stack trace behind diagnostics permission. Configure `DescribeFailure` to redact before storage. Optional progress reporting and execution deadlines distinguish a live lease from useful progress. Deadlines request cooperative cancellation; non-cooperative handlers remain visibly suspected-stalled and occupy their local slot until they return. Expired leases are recovered; stale tokens cannot settle work.

[Scheduling](docs/scheduling.md) covers cron time zones, draft/publication, enable/pause, overlap, misfires, and transactional occurrence creation. Recurrence currently targets individual processors.

## Dashboard and security

The Angular console reads real storage data: correlation/status filters, saved flow diagrams, attempts and protected stack traces, waiting explanations, draft lifecycle, schedules, worker observations, limits, and an administrative audit trail. It does not load application processor DLLs.

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

Open `/operations/jobs`. In Development, POST `{ "orderId": "123", "amount": 250 }` to `/demo/orders`; an amount above 1000 selects the review branch, and a negative amount creates an intentional incident. The [direct processor sample](samples/Demo.Processor) demonstrates raw-string, delayed, and recurring work; `--once` performs a smoke test.

`./scripts/verify.sh` builds the frontend and libraries, tests both runtimes, proves cross-runtime assembly relocation, packs NuGet artifacts, and runs isolated package-only consumers with SDK 8 and SDK 10. Set `DURABLEJOBS_POSTGRES` to a disposable database to include PostgreSQL; see [provider instructions](docs/providers.md). If .NET 8 is installed separately, set `WORKTHROUGH_DOTNET8` to its absolute executable path. Local packages are written to `artifacts/packages`.

## Release boundaries

Execution is at least once. Arbitrary side effects cannot be undone by rewinding engine state. Compensation/checkpoint forks, transactional business outboxes, durable external signals/child flows, SQL Server and further providers, legacy import/upcasting, retention automation, partition/rate limits, and production performance/fault campaigns remain in the [plan](docs/plan.md). External OIDC login requires a configured identity provider and has not been verified against a real account. There is no production certification or Native AOT claim.

See [architecture](docs/architecture.md), [decisions](docs/adr), [requirements](specs/durable-jobs.spec.md), [community research](docs/research.md), [verification](docs/verification.md), and [contributor guardrails](AGENTS.md).
