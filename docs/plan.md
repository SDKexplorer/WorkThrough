# Implementation plan

This plan combines the user's requirements with a purposive review of 20 Hangfire/Wolverine discussions. It is not a ranking of market-wide complaint frequency. See `research.md` for evidence and fixed-versus-open distinctions.

## Remaining delivery order

The core engine and processor relocation guarantee are implemented. Completing an individual PR does not complete this roadmap. The maintainer prioritized MCP, retention, SQL Server/Oracle, operational UX, concurrency/isolation controls and release readiness. Import/export adapters for other engines are optional, not prerequisites. Protected attempt diagnostics and paged pinned-flow inspection are merged. The operations branch adds runtime evidence and opt-in retention; the current delivery status is:

| Order | Work | Completion evidence |
|---|---|---|
| 1 | Finish read-only MCP: external client/load verification; bounded wait/capacity explanations, attempt metrics, audit/archive metadata and manual retention preview are implemented | Permission/resource tests, explicit coverage, real-provider query bounds and client/package verification for every tool |
| 2 | Automatic retention implemented: opt-in maintenance, holds, fenced batches, verified file exports and separate purge/event policies; large contention/restore qualification remains | Crash/race tests proving active work, receipts and held evidence survive; restart-safe progress |
| 3 | SQL Server and Oracle implemented and native CI verified | Provider dialect/transaction implementation, real-database conformance, package consumers, relocation and process-kill recovery on .NET 8/10 |
| 4 | Implemented operational UX: windowed metrics, step display labels, worker acquisition health and retention status | Real measurements and saved-data inspection in the Angular console; no invented counts or missing-history claims |
| 5 | Implemented: partition-key and tenant concurrency, rate limits, bounded flow fan-out, weighted queue fairness (ADR-018) and tenant-scoped dashboard/MCP access (ADR-019) | Separate lock/tenant identities, resource-policy tests and multi-process admission/fairness evidence on all four providers |
| 6 | Controlled MCP actions, after the read-only release | Trusted approval, exact target/parameters, revocation/state preconditions and existing atomic command/audit reconciliation |
| 7 | Production/public release readiness; 100k-job/20-client correctness passed on all four providers; two-hour PostgreSQL soak passed (233k jobs, flat latency and CPU); proposed throughput budget missed in some runs | Soaks on the other providers and longer memory observation, external IdP/AI-client checks, multi-host/PITR evidence, license/package/visibility decisions and enforced branch policies |

Branch protection remains blocked by GitHub's current private-repository plan. Changing account plan or repository visibility is a maintainer decision. Public visibility, package publication and a project license are not authorized by completing implementation. Advanced architecture items remain design-and-implementation work, not hidden completed features.

Native recovery enhancements (validated input correction, checkpoint forks, compensation, child flows and contract upcasters) remain a separate backlog. Optional interoperability may later import from or export to Hangfire, Wolverine or other engines through explicit mappings of the supported subset. Different delivery, scheduling and flow semantics prevent assuming a lossless round trip. No such adapter is required for WorkThrough to operate or release, and none is implemented.

## P0 — identity and durable execution

- [x] Implement the direct processor identity and raw/typed input API in ADR-006. Permit multiple processors for the same input type; retain one shared dispatch path for all registration styles.
- [x] Extend the published relocation proof to a future-due raw-string processor invocation, preserving stored processor identity and input without metadata migration.
- [x] Require stable keys and contract versions; never dispatch through persisted CLR names.
- [x] Implement explicit and generated registration through one catalog.
- [x] Keep Core independent of hosts and databases; use concrete use cases and focused storage ports.
- [x] Implement durable enqueue, attempts, leases, retries, cancellation, and recovery.
- [x] Implement independent client, generic worker, web worker, and dashboard roles.
- [x] Implement SQLite and PostgreSQL storage adapters.
- [x] Initial verification gate: build, shared SQLite/PostgreSQL tests, package-consumer test, and published assembly-move proof all pass. See `verification.md`; this is not production certification.

## P1 — orchestration and operations

- [ ] Complete the read-only MCP integration in [MCP operations](../specs/mcp-operations.spec.md) and [ADR-011](adr/0011-mcp-operations.md). The optional [MCP package](mcp.md) implements thirteen typed tools: context, execution search/lookup, attempts, diagnostics, saved-flow inspection, audit, archive search/lookup and manual retention preview, waiting/capacity, attempt metrics and automatic-retention status. Dedicated bearer authentication/discovery, resource policies, access evidence and request/result bounds are implemented. Real-IdP/AI-client setup and the large contention/soak gate remain. Controlled actions are a later phase.
- [x] Implement explicit Draft → Published → Retired flow-definition lifecycle. Drafts are editable with optimistic concurrency and cannot execute; publishing validates an immutable revision; retiring prevents new runs without changing pinned active runs. Add corresponding draft/publish lifecycle for schedules, independently of enabled/paused admission, with permissions and audit.
- [x] Implement ADR-007: separate metadata/payload/action permissions, trusted user/service actors, atomic administrative audit and command deduplication, and embedded/standalone authentication samples. The OIDC sample builds; actual identity-provider login needs deployment-specific validation. Store/environment isolation is implemented; tenant row filtering remains outside the initial scope.
- [ ] Deliver the console direction in `dashboard-direction.md`: useful backlog/timing/capacity signals, saved-flow inspection, processor deployment visibility, and accessible dark/light themes. The Angular console is now embedded and uses real APIs. Deployment identity/queue coverage and oldest eligible timestamps are implemented; windowed timing/throughput metrics are implemented; advanced authoring remains incomplete.
- [x] Implement and verify the accepted .NET 8 minimum, multi-runtime package/SDK matrix, and durable format compatibility policy in ADR-005. Runtime packages use net8.0 assets; SDK/runtime 8 and 10 consumers are exercised. .NET 9 is not claimed tested.
- [x] Define schema-3 migration boundaries in ADR-010 and schema-4 retention boundaries in ADR-013. Saved schema-1 data and cross-runtime deployments are tested; older engine writers must stop before upgrading. Arbitrary engine-version coexistence is deliberately unsupported.
- [x] Add a deployment preflight report that compares declared contract/queue capabilities against queued work and active flow requirements. Flag removed support and unresolved mappings; do not claim to infer semantic compatibility of arbitrary handler code.
- [x] Complete human-readable step labels independently of permanent identity keys. Typed processor definitions and worker descriptions already support display names without data migration or contract revision; per-step `.Named(...)` labels are pinned with the saved definition and fall back to step IDs for old data.
- [x] Add explicit identity-at-registration and coordinated compatibility aliases as explored in `identity-evolution.md`. Preserve old stored identities; aliases must share admission limits and have explicit rollout, idempotency, and retirement rules before being advertised as migrations.
- [x] Implement persisted graph versions, step dependencies, output bindings, equality branches, and atomic successor activation.
- [x] Implement local total/type limits and shared total/type admission policies.
- [x] Implement correlation filtering, attempt inspection, shared limit display, standalone retry, and cancellation in the dashboard.
- [x] Complete worker operations: heartbeats, capability inventory, and per-job unsupported-contract/capacity explanations are implemented; oldest-ready timestamps are implemented in the coverage inventory; explicit acquisition-failure and stale-heartbeat indicators are implemented.
- [x] Add OpenTelemetry traces/metrics with correlation and causation metadata.
- [x] Add audited policy changes, queue pause/resume, and configurable retry policies beyond the initial exponential worker default.
- [x] Add caller idempotency keys for starting flows, with atomic receipts, conflicting-reuse rejection, and recovery of the existing run after retirement. Individual-job keys remain separate.
- [x] Add bounded explicit archive/input/output size policies and cleanup that cannot remove active run dependencies.
- [x] Implement opt-in automatic retention with policies/holds, fenced bounded batches, status, verified file exports, explicit body purge and subscriber-safe bounded journal trimming. Preserve receipts and active flow dependencies. See [ADR-013](adr/0013-fenced-retention-maintenance.md). Large maintenance-versus-worker contention and restore qualification remain release gates.
- [x] Introduce recoverable flow incidents: exhausted technical retries block dependent work without confusing it with a deliberately skipped branch. Preserve explicit terminal-failure policy. ADR-008 and saved-schema tests preserve Stop semantics for old graphs; new builders default to Suspend.
- [x] Render the saved definition revision with execution states and node details; append an execution journal transactionally with state changes for a separate event timeline. Keep the dashboard independent of application DLLs.
- [ ] Complete recovery beyond the implemented audited retry and pause/resume commands: retry the failed step, validated input correction, and pause/resume admission. Show why a flow is waiting or blocked before offering an action.
- [x] Define execution deadlines and progress reporting separately from worker lease renewal; a live worker is not evidence that its handler is progressing.
- [x] Add business-write/outbox integration before claiming atomic business operation plus enqueue. Specify supported local transaction boundaries and do not imply cross-database atomicity.
- [x] Define tenant/environment isolation, viewer/operator permissions and payload redaction for shared hosting: tenant-scoped dashboard/MCP access (ADR-019), separate payload permission (ADR-007) and diagnostic redaction. CorrelationId is never an authorization boundary.
- [x] Test process-kill, ambiguous commit, database disconnection, SQLite restoration, and resumption scenarios. Document restore reconciliation for external effects; retain the existing at-least-once guarantee. PostgreSQL PITR and multi-machine partition campaigns remain deployment readiness work.

- [x] Implement scoped attempt middleware, typed validation, configurable retry classification, and post-settlement observers as defined in `processor-lifecycle-and-extensibility.md`. Durable lifecycle subscriptions require a separate persisted delivery/outbox contract; local callbacks must not masquerade as reliable delivery.

## P2 — scheduling and compatibility

- [x] Add recurring schedules with explicit overlap policies: allow, skip-if-active, and coalesce. Keep schedule identity separate from processor identity.
- [x] Define named schedule lifecycle, update ownership, pause/resume, and atomic/idempotent occurrence creation. Reuse one processor across immediate jobs, one-time schedules, recurring schedules, and flow steps.
- [x] Deliver the executable sample in `samples/README.md`: one Shop system (shared definitions, processors, intake API, headless worker, OIDC console, MCP) smoke-tested by `verify.sh`. Relocation fixtures live in `tests/Relocation`.
- [x] Seed recurring schedules from code (`SeedSchedules`), through the audited lifecycle and without overriding operators.
- [ ] Optional: add import/export adapters for other engines, such as Hangfire or Wolverine, with explicit identity/payload mappings, supported version shapes and semantic-loss reports. Nice to have; not a release prerequisite.
- [ ] Add contract upcasters and rolling-deployment capability checks. Preserve pending old versions until compatible consumers exist.
- [ ] Evaluate automatic canonical flow revisions with optional human labels; pin executions to the chosen revision and make activation/migration explicit. Mandatory integer versions remain the current API.
- [x] Add partition-key concurrency and rate limits. Correlation must remain independent from lock identity (ADR-018, schema 5).
- [x] Add SQL Server, then Oracle, following the portability refactor and real-provider gates in [storage evolution](../specs/storage-evolution.spec.md) and [ADR-012](adr/0012-retention-and-provider-expansion.md). Both pass native Linux x64 qualification on their documented initial server matrix. SQLite/PostgreSQL retain their existing verification. Sustained contention and provider restore campaigns remain release-readiness work.
- [x] Add durable signals with deduplication, storage of signals arriving before their wait step, and race-tested timeout behavior; never park a worker thread while waiting.
- [x] Define cron time zones, daylight-saving transitions, and missed-schedule policy alongside overlap controls.
- [x] Add weighted queue fairness (ADR-018).
- [x] Add bounded fan-out (`MaxParallelSteps`), per-tenant quotas and a multi-process admission load exercise that measures policy cost under contention and worker memory (ADR-018). Longer soaks remain release-readiness work.

## P3 — controlled recovery and release

- [ ] Add explicitly enabled MCP recovery/control actions only after trusted approval integration, state preconditions and command/audit reconciliation are verified. Keep bulk replay, payload changes and deletion separately scoped.
- [ ] Apply and verify GitHub branch protections using [branching and release guidance](branching-and-release.md). `develop` exists; enforcement is blocked by the current private-repository account plan. Public visibility requires a separate maintainer decision and readiness review.
- [ ] Fork an execution from a persisted checkpoint, with a new run ID and causation link; preserve the original audit trail.
- [ ] Add explicit compensation handlers for reversible external effects. Never label state rewind as undoing arbitrary side effects.
- [ ] Add child flows, durable timers/signals, failure branches, and a documented expression/versioning model.
- [x] Benchmark idle polling, admission contention, and completion throughput before replacing conservative locking. Independent worker processes, three repeated workloads, raw latency/resource reports, and correctness assertions are in `performance.md`; conservative transaction serialization remains in place.
- [ ] Define supported platforms/TFMs, compatibility policy, license, final package name, and public release process.
- [x] Apply the accepted WorkThrough working brand consistently to source/API/package/sample/UI names, with build and package-consumer verification. Package/domain clearance and publication remain separate; persisted processor keys must not change during the rename.

## Throughput — 2026-10-11

- [x] Raise write-path throughput without weakening admission: a lock-free claim pre-check, batched claims and completions, batched statements, owned writes outside the admission lock, and the SQLite busy timeout (ADR-020). Drain throughput rose 2.4–3.6× on every provider.
- [x] Settlement outside the admission lock with flow-first row locks, journal events appended at commit, and `SKIP LOCKED`/`READPAST` claims while no limit exists (ADR-020 amendment). PostgreSQL drain rose 3.3×, SQL Server with versioned reads 2.1×, Oracle 1.5×.
- [x] Concurrent claims while limits exist: per-limit lock rows under engine protocol 3 (ADR-022). Tenant-limited PostgreSQL drain rose from about 140 to 500 jobs/s.

## Product review findings — 2026-10-11

Gaps found when reviewing WorkThrough as an adopter would. None blocks the current guarantees; each needs a maintainer priority.

- [x] Propagate W3C trace context from enqueue, flow start and outbox delivery to attempt activities, so a producer's trace continues into job execution.
- [x] ASP.NET Core health checks for storage reachability and worker acquisition failures (`AddHealthChecks().AddWorkThrough()`).
- [x] Dynamic fan-out: `ForEach` expands a step into one item step per element of an earlier output, joined afterwards, under engine protocol 2 (ADR-021).
- [ ] Payload protection: encryption at rest or a supported secret-reference pattern for sensitive inputs. Today inputs are stored as JSON, protected by database access and the payload permission.
- [ ] MySQL/MariaDB provider, a common request from Hangfire users.
- [x] One programming model: `IJob<TInput>` with `ExecuteAsync`, replacing `IProcessor<T>` and `IJobHandler<T>`. Addresses come from a `JobDefinition`, `[Job]` on the class, or `[JobContract]` on the input type; `AddJob` registers all three.
- [x] Typed flow bindings and conditions (`BindInput((Target t) => t.X, (Source s) => s.Y)`) and a [testing guide](testing.md).
- [x] An easier first job: `AddJobs(assembly)` registers attributed jobs without the generator, storage registration needs no extra `using`, and `QuickStart` and `QuickStart.Web` samples sit beside Shop.
- [x] `dotnet new workthrough-worker` and `workthrough-web` templates for every storage provider, checked in `verify.sh`.
- [x] Rolling upgrades from schema 6: schema reader ranges and engine protocol levels (ADR-021).

## Adoption priorities — 2026-10-11

Positioning: relocation alone does not win adoption. Hangfire has compatibility hooks, Quartz has rename mappings, and Wolverine separates handlers from message identities. The advantage must be how consistently the whole product handles deployment, diagnosis and recovery.

1. **Release confidence (critical):** sustained soaks on SQL Server and Oracle, a backup/restore drill, and real OIDC against a local identity provider. Package publication and the license come last.
2. **Execution controls:** done. Per-key serialization, tenant quotas, rate limits and weighted fair queues are exact across hosts (ADR-018, ADR-022).
3. **Contract evolution (high):** input upcasters between contract versions, and rollout checks that understand them. A saved flow revision pins its graph and processor versions, not its handler binaries.
4. **Consumer testing tools (high):** a `WorkThrough.Testing` package with a real store, a controllable clock, job fakes and flow assertions.
5. **Recovery and composition (medium):** child flows are done (ADR-023). Next: validated input correction, compensation and checkpoint forks. Bounded fan-out, retry and pause/resume exist.
6. **Operational breadth (medium):** wider fleet investigation in the console. MCP actions remain phase two.

## Exit criteria for the initial repository

1. Given an enqueued job from build A, when its contract/handler classes are renamed and the old handler assembly is removed, build B executes the unchanged persisted job.
2. Given competing independent stores/workers, valid admitted leases never exceed the configured shared total/type limits.
3. Given a completed predecessor, a restart sees its output and exactly one logical successor per selected step.
4. Given a dashboard-only deployment, it renders and filters unknown job contracts without their application DLLs.
5. Given storage failure, enqueue does not falsely acknowledge success.
6. Given shutdown or stale ownership, incomplete work never becomes successful solely because cancellation occurred.

The implementation is an alpha with verified runtime, storage, lifecycle, recovery, and dashboard paths. Completion of these exit criteria is not a claim of production maturity or completion of P1–P3.
