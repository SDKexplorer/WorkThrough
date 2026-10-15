# Dashboard and product direction

Status: evolving product direction, updated 13 October 2026. The live Angular console is embedded in the Dashboard NuGet and reads real engine APIs. Saved diagrams, incidents, audited recovery, drafts, schedules, workers, permission-aware inspection, windowed attempt metrics and deployment coverage are implemented. The separate deployment-preflight API/CLI, dark and light themes that follow the system preference, engine-protocol rollout, parent/child runs, forks, input correction and failure-branch edges are implemented; richer graph authoring remains planned. The old fictional concept files are unreachable from the live entry point.

## Brand

**WorkThrough** is the accepted working brand. Suggested positioning: **Move your code. Keep your work.** Source, package, sample, and live UI names use WorkThrough. Registry, domain, and trademark availability remain unconfirmed. A search with no clear match is not name clearance.

The user also suggested NetFlow and .NETwork. NetFlow already has a strong association with [Cisco's network monitoring technology](https://docs.crossworkassurance.cisco.com/docs/netflow). .NETwork would be difficult to distinguish in searches. TraiNET itself has existing uses, including [Trainet World](https://apps.apple.com/ng/app/trainet-world/id1666176205) and historical [USAID TraiNet software](https://pdf.usaid.gov/pdf_docs/PNACH683.pdf); treat it as an exploration, not a cleared release name.

Earlier candidates from the user: **WorkBinder**, **WorkStack**, and **WorkNet**. [An existing Java WorkBinder](https://github.com/imilos/WorkBinder) allocates HPC/grid jobs, a close domain collision. [Workstack](https://workstack.io/features/) already provides task/project planning, and [Worknet](https://www.worknet.ai/product) is an existing automation/collaboration product.

The brand should cover individual jobs, schedules, and orchestration without implying that moving assemblies alone can fix incompatible business behavior. The practical promise remains a permanent processor identity mapped to the currently deployed implementation.

## Product priorities

The console should answer four questions quickly:

1. Is work progressing, and where is it backing up?
2. Why is this execution waiting, running slowly, or blocked?
3. Which processor, input, definition revision, and attempt produced this result?
4. What action is available, what will it change, and who performed it?

Use a compact operations layout with dark and light themes, restrained accent colors, readable tables, monospace identifiers, keyboard navigation, copyable identifiers, URL-based filters, and explicit refresh/freshness state. Status always includes text. Use no decorative motion in the graph. Keep an accessible step list alongside any canvas graph.

Primary views: Overview, Executions, Flows, Schedules, Processors, Workers, and Activity. Ship a view only when its underlying capability exists. A disabled or absent recovery action must explain its precondition, not appear to succeed.

The first flow view is an **execution inspector** of the saved definition revision. A visual authoring tool is a separate later product decision: it requires graph validation, preview, publication, revision selection, and permissions. Observing a graph must never silently edit its definition.

## The distinctive processor view

Show the stable job address prominently, supported input/output contract versions, deployment capability coverage, queues, and current implementation diagnostics. Implementation class and assembly names are observations, never execution identity.

Show old and new implementation names across attempt/deployment history without suggesting that old rows need migration. During a rolling deployment, multiple compatible implementations may exist at once: do not present one global class name as authoritative. Explain unsupported processor/version/queue combinations before a developer discovers a backlog.

The direct processor API in ADR-006 is implemented. Direct processor and typed-contract registration share one catalog.

## Metrics with operational meaning

Every panel includes scope, units, aggregation window, and freshness. Unknown/stale is distinct from zero. Historical charts must use recorded observations, not invented samples or repeated lifetime counts.

| Signal | Meaning | Decision it supports |
|---|---|---|
| Ready backlog and oldest-ready age | Work eligible by time and dependency; report separate future-scheduled, dependency-waiting, and retry-delay counts | Whether capacity or processor coverage needs attention |
| Admission delay | Claim time minus the persisted time an attempt became eligible; record the eligibility transition rather than infer it from a rewritten retry timestamp | Whether concurrency, polling, or coverage is slowing work |
| Logical throughput | Logical jobs reaching success in the selected interval, separate from attempt completions and terminal failures | Whether useful work is keeping pace |
| Attempt outcomes and retries | Success, handler failure, interruption, lease loss, and retry scheduling are separate | Whether instability is hidden behind eventual success |
| Duration p50/p95 | Histograms of settled attempt execution duration, with outcome and bounded processor dimensions; do not average worker percentiles | Which processors need investigation |
| Capacity | Active valid leases versus fleet and per-processor admission policies; local limits shown per worker | Which limit is restricting admission |
| Flow incidents and age | Recoverable incidents requiring attention, distinct from terminal business rejection | Which runs an operator must handle |
| Worker health and progress | Heartbeat, last successful store interaction/acquisition loop, advertised capabilities, and handler progress separately | Whether a healthy process is actually doing work |
| Schedule lag | Occurrence creation and claim lag against intended fire time; label DST/misfire policy | Whether schedules are keeping their promises |

Counts read from durable state are authoritative for the console at their snapshot time. Process-local counters are operational telemetry and can be lost across a crash; do not claim transactional equivalence. If exact historical state charts are required, derive aggregates from a transactionally appended journal with idempotent projection and bounded retention. The first built-in views can use indexed bounded queries over persisted timestamps and attempts. No expensive whole-history percentile query on every refresh.

Expose .NET `ActivitySource` and `Meter` instrumentation with optional OpenTelemetry integration. The consuming host owns exporters and destinations. Do not start an exporter or require Prometheus/Grafana to open the dashboard. Avoid making the engine depend on an observability vendor.

Use bounded dimensions such as processor, queue, outcome, and provider with documented limits. `JobId`, `CorrelationId`, tenant IDs, arbitrary exception messages, and payload values do not belong in metric labels. Keep identifiers in appropriately protected logs, traces, and indexed execution searches. Default logs and telemetry omit payloads. Preserve trace context explicitly across durable enqueue/attempt boundaries, using links where fan-in or long-lived work makes one parent misleading.

Worker-emitted store gauges must not multiply the same fleet count when aggregated. Read fleet gauges centrally or document their source/resource semantics. Polling uses configurable intervals, bounded queries, backoff, and freshness indicators; a disconnected dashboard must not appear live.

## Frontend and hosting

Authentication, operation permissions, protected diagnostics, and administrative audit implement [ADR-007](adr/0007-dashboard-access-and-audit.md). User management belongs to the host identity provider. OIDC login is verified against a local Keycloak; hosted providers still need deployment-specific verification.

Accepted full-console stack: **Angular + TypeScript**, matching the user's team. Use standalone components, signals for local view state, strict template checking, and focused query/command adapters. A general Angular graph renderer still needs evaluation for accessibility, license, layout, performance, and maintenance. Neither the frontend framework nor the graph renderer produces backend telemetry.

The current console uses Angular standalone components and signals. A bounded saved-DAG renderer uses native buttons and SVG connectors, with step states and a separate event timeline. It reads the pinned graph from storage without application assemblies; advanced graph authoring and client deep links remain planned.

Compile static assets during library development/release and embed them in the dashboard NuGet. Consumers install and host the package without Node, SSR, CDN fonts, or an external asset service. The host controls address, arbitrary dashboard mount path, authentication, and authorization. Test nested route groups, `PathBase`, trailing slashes, bookmarked client routes, and relative asset/API resolution.

The dashboard adapter reads versioned data models without application processor assemblies. It delegates commands to application use cases that validate authorization, expected revision, allowed transitions, and idempotency, then append durable audit records. Cookie-authenticated writes retain anti-forgery protection. Payload access and recovery permissions are separate from viewing aggregate status. Large lists/timelines are paginated; large graphs use bounded/lazy detail loading and measured rendering budgets.

## Original delivery sequence (track completion in plan.md)

1. Finish the processor identity API, delayed relocation proof, and .NET 8 compatibility work first. Dashboard styling cannot replace those guarantees.
2. Improve the existing console's visual system and inspect experience against real data. Add explicit freshness, useful filters, empty/error states, and deep links.
3. Add the saved graph query and read-only execution diagram, plus processor/worker capability inventory. Keep current fail-and-skip semantics visible until the incident model changes.
4. Add precise metrics and optional telemetry, bounded historical aggregates, and measured store overhead.
5. Implement the incident model, transactional journal, audit, and recovery preconditions; then expose recovery operations in the console.

## Run the live console

Build `dashboard-ui` with `npm ci && npm run build`, then run `samples/Shop.Api` in Development as described in the [sample guide](../samples/README.md). Open `/operations`. The .NET host serves the compiled bundle and engine APIs together; a standalone static server does not provide those APIs. The build output remains `artifacts/console-preview/browser` for embedding.

## Sources informing the recommendation

- [Angular version compatibility](https://angular.dev/reference/versions): keep Node, TypeScript, and Angular versions compatible independently of .NET runtime support.
- [Angular static deployment](https://angular.dev/tools/cli/deployment): Angular can also ship as static assets; routing/base-path behavior must be configured for the host.
- [OpenTelemetry .NET instrumentation](https://opentelemetry.io/docs/languages/dotnet/libraries/) and [metrics guidance](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/docs/metrics/README.md): host-composed telemetry and cardinality constraints.

These sources support technology capabilities. The product priorities, proposed architecture, and metric definitions above are our design choices.
