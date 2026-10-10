# MCP operations

Status: read-only investigation implemented, external client/load qualification and controlled actions pending, 2026-10-10. The maintainer selected **read-only diagnostics initially, administrative actions later**. The [current MCP package and hosting guide](../docs/mcp.md) define the implemented subset; this specification retains the broader target. Retention and additional databases are companion work in [storage evolution](storage-evolution.spec.md).

Implemented: bounded execution/attempt queries, additive protected dashboard routes, thirteen MCP tools (context/search/lookup/attempts/diagnostics/flow/audit/archive search/archive lookup/manual retention preview/capacity/metrics/retention status), dedicated JWT authentication and discovery, per-call resource checks, access evidence and query/result bounds. Diagnostics, audit and retention preview require distinct additional permissions. Templates, condition values, audit reasons/change contents and archive bodies are excluded from metadata tools. Deterministic wait/capacity observations, windowed attempt metrics and effective automatic-policy status are implemented with explicit coverage. Context lists unavailable capabilities; dashboard policy compatibility is preserved.

## Purpose

Let developers and operators investigate WorkThrough from an MCP client using the same evidence and permissions as the dashboard. The server supplies facts; the client's model can explain them and distinguish hypotheses from confirmed engine state. WorkThrough requires no embedded model, model subscription, or LLM API key.

Useful questions, in priority order:

| Operator question | Evidence to return |
|---|---|
| Why is order:123 stuck? | Correlated jobs/runs, pinned graph, blocked step, engine reason, attempts, timestamps and queue/worker observations |
| Which executions had failed attempts in the last day? | Failed-attempt time window, distinct execution IDs, current state and attempt outcomes; include jobs that subsequently recovered |
| Why is this processor queued after our deployment? | Stable processor key/version, registry mapping, required queue and observed worker capabilities |
| Are concurrency limits holding work back? | Shared limits, admitted lease counts, queue controls and recent worker capacity observations |
| Who retried or paused this run? | Authorized, redacted administrative audit with trusted actor and command ID |
| Where did this completed execution go? | Live/archive coverage, archive summary or tombstone, retention policy and unavailable-history explanation |
| What would cleanup archive today? | Bounded candidate preview, cutoff, exclusions and explicitly partial totals |

Stable processor identities remain the reference throughout. A namespace or assembly move must not change tool arguments or require the MCP host to load the processor DLL. Missing registrations can be diagnosed; input compatibility cannot be inferred from a class name or JSON resemblance.

## Proposed first tool set

The thirteen implemented tool names/schemas are documented in the hosting guide; names below for future tools remain proposals. Every implemented tool has typed input/output schemas. `workthrough_get_attempts` was split from lookup to keep attempt paging explicit; `workthrough_get_archive` permits archive-ID lookup outside the search window. Search supplies correlation, run and causation metadata; flow inspection supplies dependencies without condition expressions. It does not claim a complete cross-run correlation trace.

| Tool | Result | Permission |
|---|---|---|
| `workthrough_get_context` | Environment/store identity, engine/schema/tool versions, available capabilities and query limits | Authenticated endpoint access; authorized store |
| `workthrough_search_executions` | Paged metadata filtered by correlation, processor/version, queue, state and explicit time basis | `executions.read` |
| `workthrough_get_execution` | Current state, bounded attempt metadata, timing, engine wait reason and evidence references | `executions.read` |
| `workthrough_get_diagnostics` | Selected attempt's bounded, redacted exception/progress detail; payload only through an explicit permitted projection | `executions.read` + `payloads.read` |
| `workthrough_get_flow` | Pinned graph structure, step states and paged event timeline, without input templates or condition literals by default | `executions.read`; protected fields additionally need `payloads.read` |
| `workthrough_trace_correlation` | Paged related executions, runs and explicit causation/replay links | `executions.read` |
| `workthrough_get_capacity` | Queue controls, limits, worker heartbeat age, processor/queue coverage and observation timestamps | `executions.read` |
| `workthrough_get_audit` | Paged attributed operation metadata; reasons and change contents excluded | `executions.read` + `audit.read` |
| `workthrough_search_archives` | Paged archive summaries, original IDs where indexed and body availability | `executions.read` |
| `workthrough_preview_retention` | Supplied manual policy, bounded candidates and excluded categories; automatic policy/status is a separate `workthrough_get_retention` tool | `executions.read` + `retention.read` |

Archive search/lookup, supplied-policy preview and automatic-policy/hold/status reads are implemented. Original archived-outcome/step-ID indexes remain storage work. Dashboard preview still requires `archives.manage`; MCP preview's `retention.read` is an explicit new permission, not an implicit grant from `executions.read` or `archives.manage`.

Default responses contain neither raw `JobRecord` nor archive JSON. Those contain payloads, output, errors and internal fields unsuitable for general metadata access. Never return lease tokens, connection strings, authentication tokens or effect/idempotency keys. Host-defined redaction also covers business identifiers and labels where necessary.

## Response and query contract

Each response identifies its store/environment, observation time, schema version and evidence IDs. Lists expose an opaque continuation cursor, actual coverage (`live`, `archive-summary`, or both), truncation/redaction indicators and returned-item count. Dashboard URLs come from validated host configuration. Unknown or unavailable information stays explicit.

Execution state, attempt outcome and storage location are separate dimensions. An execution can have failed yesterday and be successful now; an archived successful job remains successful. Failure-window searches use failed-attempt completion time, while creation-window searches use execution creation time. Use UTC, explicit inclusive/exclusive bounds and deterministic tie-breaking. Do not call a page length a total or count attempts as distinct jobs.

Use parameterized, indexed metadata projections and keyset pagination. Bound rows examined where feasible as well as rows returned; no `SELECT *` followed by in-memory filtering of payloads. Cursors bind the store and query; authorization is re-evaluated on every request. A cursor is not an access token. Each page documents its observation boundary: concurrent state changes may change membership between pages. Multi-call investigations are not a frozen database snapshot.

Engine wait reasons are deterministic evidence, including `RetryScheduled`, `FlowIncident`, `FlowPaused`, `AwaitingSignal`, `QueuePaused`, `NoRecentCapableWorker`, `SharedCapacity`, `LeaseExpired` and `ProgressOverdue`. Heartbeats are observations, not guarantees of future availability. An available retry operation does not prove that repeating external business effects is safe.

## Architecture and hosting

See [ADR-011](../docs/adr/0011-mcp-operations.md). Ship one optional `WorkThrough.Mcp` package initially, using the official C# SDK and ASP.NET Core Streamable HTTP. An API, dashboard host, separate operations host, or web-enabled worker can expose the endpoint at a host-selected path. An ordinary `BackgroundService` does not acquire a web listener automatically.

Keep operation read contracts, resource identities and projections independent of MCP/ASP.NET. Extend focused Core read ports where needed; keep transport authentication and protocol handling in adapters. Dashboard and MCP use the same permission vocabulary and state rules. Introduce shared authorization resource descriptors while preserving the existing `DashboardResource` policy extension through a compatibility adapter. Do not require the Angular dashboard package to host MCP.

Use one configured store/environment per endpoint first. Connection strings, arbitrary SQL, shell commands and target server URLs are not tool arguments. Adding SQL Server or Oracle does not change the MCP contract. Store-level isolation is supported; shared row-scoped multi-tenancy requires separate design. A correlation ID is never an authorization boundary.

The SDK's ASP.NET package currently targets .NET 8. Pin a reviewed version during implementation and execute .NET 8/10 consumers plus actual client interoperability tests before claiming support. Use stateless request handling where supported, and document the tested protocol/client matrix; do not implement custom protocol negotiation. See the [official SDK](https://github.com/modelcontextprotocol/csharp-sdk), [package framework support](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore) and [SDK HTTP guidance](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/stateless/stateless.html).

## Authorization, privacy and audit

Remote HTTP uses a dedicated host-configured bearer authentication scheme with issuer, audience and lifetime validation, protected-resource discovery, TLS and explicit allowed origins where applicable. Reuse the host identity provider for users and service accounts. Dashboard cookies and its development anonymous option do not grant MCP access. The protocol requires tokens issued for the intended resource; see [MCP authorization](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization).

Check permission and resource access at execution, including direct tool calls and any future resources. Filter tool discovery when useful, but discovery and `readOnlyHint` are not enforcement. Metadata-only users must not receive exception messages, stack traces, progress descriptions, payloads, condition constants, or sensitive audit changes. Apply redaction before serialization and before request/response logging. Treat job content as untrusted data, including text that resembles instructions; it never changes tool permissions or becomes an executable instruction.

Emit an access event to a host-configured security sink: trusted subject/issuer and client identity, tool, target IDs, store, outcome, request ID, duration and redaction status. Never record full payloads or bearer tokens. Sensitive-read deployments can require a durable access sink and fail closed when it is unavailable. This is distinct from the existing transactional mutation audit; diagnostic reads do not create engine command receipts or require a write-capable job-store credential.

## Functional requirements (EARS)

- MCP-001: When an authorized client searches executions, the server shall return bounded metadata within the configured store and requested filters, with explicit time basis, coverage and pagination.
- MCP-002: When a client inspects a job or flow, the server shall return saved identities, pinned graph/state and available evidence without loading application processor assemblies.
- MCP-003: When a client asks why work is waiting, the server shall return the engine's observed reason and supporting timestamps, and distinguish unknown facts from confirmed state.
- MCP-004: While the endpoint is in read-only mode, the server shall expose no administrative tools and shall reject direct attempts to invoke mutations without changing engine state.
- MCP-005: When any tool or resource is called, the server shall enforce authenticated store, operation and resource authorization independently of earlier discovery or calls.
- MCP-006: Where the caller lacks payload permission, the server shall omit sensitive diagnostic, graph, payload and audit fields before serialization or logging.
- MCP-007: When an archive or history segment is unavailable, the server shall identify the coverage limitation without reporting unavailable data as an empty successful history.
- MCP-008: When retention is previewed, the server shall return bounded candidates and exclusions without archiving, trimming, exporting or deleting data.
- MCP-009: When a query exceeds its configured bounds or time budget, the server shall return a typed incomplete/error result without silently issuing an unbounded retry.
- MCP-010: When an MCP read is handled, the server shall emit a redacted access event using trusted host identity and an independent request ID.
- MCP-011 (phase 2): When an administrative action is requested, the server shall require action/resource permission, trusted approval, reason, command ID and current state preconditions before invoking the existing transactional operation.
- MCP-012 (phase 2): If approval is expired, parameters change, authority is revoked or state preconditions fail, the server shall reject execution without a state change.

## Phase 2: controlled actions

Start with single-target retry/recover, cancel, queue pause/resume and worker drain. Each action needs explicit enrollment by the host and action-specific permission. Keep replay separate: it creates a new logical job and can repeat business effects. Publishing flows, changing registry mappings, payload correction, bulk actions and archive purge are outside the initial action release.

Use prepare → approve → execute. Preparation returns an exact proposed action, target/store, state preconditions, reason and effects, including cancellation's cooperative nature. Approval comes from a trusted host/operator workflow and binds subject, action, arguments, preconditions and expiry. A model-supplied `confirm: true`, repeated user text, or an MCP annotation is not proof of approval. A short-lived approved-action handle carries no broader authority.

Execution reauthorizes and uses the existing atomic change + command receipt + administrative audit path. Retry an ambiguous response with the same durable command ID; MCP request IDs remain separate. Strengthen store-side compare-and-set support before exposing commands that currently lack sufficient state preconditions, including flow pause, limit changes and replay. A preview cannot freeze the world.

Cleanup automation is a separate, explicitly configured maintenance service. Later MCP may prepare an archive action for review; broad time-based predicates must not allow approval to include unreviewed new candidates. Remote purge remains deferred until verified export receipts, holds and exact-batch approval are implemented.

## Non-functional requirements and validation

Initial tunable design budgets, not measured results: 25 rows by default, maximum 100 per page; 64 KiB total serialized response budget including any compatibility text copy; 16 KiB per diagnostic field within that budget; five-second query deadline; explicit bounds on graph nodes/events and nested data. Time-window searches default to 24 hours, with a seven-day maximum per request. Direct ID lookup can inspect older retained work. Paginate structural results rather than clipping JSON or silently dropping graph edges.

Add per-principal rate limits, endpoint concurrency limits and cancellation propagation. Avoid long read transactions and unbounded aggregate scans that starve workers. A disconnect must release connections; SQLite's synchronous driver internals need measured cancellation limits. Do not poll or retain a connection for the lifetime of a durable job.

Before release, benchmark 100,000 retained executions with varied attempt histories on real SQLite and PostgreSQL, then each added provider. Run 20 concurrent investigative clients alongside producers/workers; compare throughput and queue latency with the same workload without MCP. Proposed targets: metadata query p95 under 500 ms, p99 under two seconds and under 10% execution-throughput degradation on recorded hardware. Record SQL plans, allocation, response size, cancellation, throttling and a two-hour soak. These targets require validation; they are not a production SLA.

## Acceptance criteria

| Given | When | Then |
|---|---|---|
| A job whose processor assembly was removed after relocation | An MCP-only host inspects it | Stable key/version, saved state and observations are available without that assembly |
| A paused queue, retry delay, signal wait and flow incident | Each is investigated | The corresponding reason and evidence are distinct; none is called a running handler failure |
| A job failed within the window and later succeeded | Failed-attempt search runs | It appears once as an execution with failed-attempt evidence and its current success state |
| A metadata-only principal and secret-bearing exception/input/graph/audit data | Every tool is called directly | Protected values are absent from results and access logs |
| Instruction-like text inside a failure | Diagnostics returns it | It remains bounded data and cannot enable a tool or supply trusted approval |
| An archived flow or purged body | Correlation/archive lookup runs | Retained summary and coverage limitation are explicit; no false live-only completeness |
| A candidate preview races with recovery or a hold | Preview completes | It changes nothing and cannot guarantee later eligibility |
| Equal timestamps and concurrent transitions | Multiple pages are requested | Ordering is deterministic and documented observation/coverage rules hold |
| A missing/expired/wrong-audience token or revoked permission | Discovery or direct invocation runs | Access fails without a data leak or cookie fallback |
| Read-only endpoint with mutation-shaped input | A direct call is attempted | No job, control, archive, receipt or engine audit row is mutated |
| Phase-2 approval followed by a state change or changed arguments | Execution is requested | The command fails its bound approval/preconditions without another effect |
| A committed phase-2 action with a lost response | The same command ID is retried | The original result returns with one successful mutation audit |

## Error handling

| Condition | Contract |
|---|---|
| Missing/invalid authentication | HTTP authentication challenge; no tool result containing data |
| Insufficient permission | Forbid/typed authorization failure appropriate to the protocol; no existence leak |
| Invalid filter/cursor or unsupported capability | Structured argument/capability error; no fallback to broader access |
| Missing live record, archived record, or purged body | Distinct authorized coverage result where evidence exists; otherwise scoped not-found |
| Query timeout, cancellation, storage failure | Typed unavailable/incomplete result with request ID; never a fabricated empty list |
| Rate/concurrency limit | Retry guidance with a bounded backoff; no internal request amplification |
| Partial inventory or graph | Explicit continuation/coverage; no complete deployment verdict or fabricated edges |
| Stale phase-2 preconditions/approval | Conflict; refresh and obtain approval for the changed action |

## Implementation checklist

### M0 — shared foundation

- [x] Freeze the initial context/search/lookup/attempt schemas and permission/resource mapping with a client spike. Richer tool schemas remain to design.
- [x] Add bounded live execution/attempt metadata reads, keyset pagination, explicit failed-attempt windows, compatible provider indexes and protected HTTP routes. Legacy `ListAsync` remains unchanged; the new port never loads payloads.
- [x] Add bounded audit/archive metadata queries, store/query-bound pagination and additive history indexes. Individual archived step/outcome indexing and the planned-scale query-plan/performance gates remain.
- [x] Extract shared execution projections, permission vocabulary and a framework-independent MCP operation resource; preserve `DashboardResource` policy compatibility. Richer projections remain pending.
- [x] Add dedicated bearer authentication, discovery, per-call authorization and metadata-only access events. Real external IdP discovery/login remains deployment validation.

### M1 — read-only package

- [x] Add selected-attempt diagnostics with dual permissions/read-time redaction and bounded pinned-flow node/dependency/event inspection, including missing/trimmed coverage and optional provider capability.
- [x] Add permission-protected audit metadata, archive search/lookup and supplied manual-policy preview without loading sensitive evidence or mutating storage.
- [x] Add metadata-only wait/capacity explanations, windowed attempt metrics and permission-protected persisted retention status; all use bounded snapshots and explicit coverage.
- [x] Add a standalone authenticated sample and composable endpoint for an API/web worker; no processor or Angular dependency.
- [x] Exercise the official C# 2.2.0 client and an independent HTTP JSON-RPC client over Streamable HTTP; document their protocol coverage. These are not two third-party AI applications.
- [x] Test .NET 8/10 NuGet consumers, permission/resource bypass attempts, metadata projections, paging, cancellation, limits and access-sink failure for the shipped tools.
- [ ] Finish contention/performance qualification with real providers. The initial 100,000-retained-job/20-client campaign passed correctness checks on SQLite, PostgreSQL, SQL Server and Oracle, but missed the proposed throughput budget in some runs; raw reports and repeatable smoke/full/soak profiles are in [performance](../docs/performance.md). Repetitions, SQL-plan/allocation analysis and the two-hour soak remain.

### M1 follow-up — richer investigation

- [ ] Add schedule/misfire explanations and deterministic failure grouping. Bounded attempt completion-window aggregates are implemented.
- [ ] Add explicit declared-fleet deployment preflight and retained-history lookup without full archive export.
- [ ] Consider authorized runbook resources/prompts and dashboard diagram links. Inline MCP Apps diagrams and local stdio hosting are optional later integrations.

### M2 — approved operations

- [ ] Define trusted approval integration, action handles, expiry, permissions and revocation behavior.
- [ ] Add missing state preconditions and atomic command/audit tests before registering each action.
- [ ] Implement single-target recovery/control tools; document external-effect risk and retry versus replay.
- [ ] Evaluate bounded archive actions after the retention/export prerequisites. Keep deletion disabled until explicitly supported.

Automatic incident repair, unrestricted payload editing, SQL execution, a new user directory and distributed transactions are outside this plan. MCP protocol tasks do not replace WorkThrough job/flow lifecycles. Alerting and scheduled investigation need a separately configured scheduler/notification integration; an MCP connection alone is not a monitoring service.
