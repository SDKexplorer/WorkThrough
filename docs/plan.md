# Implementation plan

This plan combines the user's requirements with a purposive review of 20 Hangfire/Wolverine discussions. It is not a ranking of market-wide complaint frequency. See `research.md` for evidence and fixed-versus-open distinctions.

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

- [x] Implement explicit Draft → Published → Retired flow-definition lifecycle. Drafts are editable with optimistic concurrency and cannot execute; publishing validates an immutable revision; retiring prevents new runs without changing pinned active runs. Add corresponding draft/publish lifecycle for schedules, independently of enabled/paused admission, with permissions and audit.
- [x] Implement ADR-007: separate metadata/payload/action permissions, trusted user/service actors, atomic administrative audit and command deduplication, and embedded/standalone authentication samples. The OIDC sample builds; actual identity-provider login needs deployment-specific validation. Store/environment isolation is implemented; tenant row filtering remains outside the initial scope.
- [ ] Deliver the console direction in `dashboard-direction.md`: useful backlog/timing/capacity signals, saved-flow inspection, processor deployment visibility, and accessible dark/light themes. The Angular console is now embedded and uses real APIs. Deployment identity/queue coverage and oldest eligible timestamps are implemented; windowed timing/throughput metrics and advanced authoring remain incomplete.
- [x] Implement and verify the accepted .NET 8 minimum, multi-runtime package/SDK matrix, and durable format compatibility policy in ADR-005. Runtime packages use net8.0 assets; SDK/runtime 8 and 10 consumers are exercised. .NET 9 is not claimed tested.
- [ ] Define schema migration, engine-version coexistence, and rollback boundaries; add mixed-version consumer/worker tests and saved-data fixtures.
- [x] Add a deployment preflight report that compares declared contract/queue capabilities against queued work and active flow requirements. Flag removed support and unresolved mappings; do not claim to infer semantic compatibility of arbitrary handler code.
- [ ] Add human-readable job/step labels independently of permanent identity keys. A display-name change must not require data migration or contract revision.
- [ ] Add explicit identity-at-registration and coordinated compatibility aliases as explored in `identity-evolution.md`. Preserve old stored identities; aliases must share admission limits and have explicit rollout, idempotency, and retirement rules before being advertised as migrations.
- [x] Implement persisted graph versions, step dependencies, output bindings, equality branches, and atomic successor activation.
- [x] Implement local total/type limits and shared total/type admission policies.
- [x] Implement correlation filtering, attempt inspection, shared limit display, standalone retry, and cancellation in the dashboard.
- [ ] Complete worker operations: heartbeats, capability inventory, and per-job unsupported-contract/capacity explanations are implemented; oldest-ready timestamps are implemented in the coverage inventory; explicit unhealthy-acquisition indicators remain.
- [x] Add OpenTelemetry traces/metrics with correlation and causation metadata.
- [ ] Add audited policy changes, queue pause/resume, and configurable retry policies beyond the initial exponential worker default.
- [x] Add caller idempotency keys for starting flows, with atomic receipts, conflicting-reuse rejection, and recovery of the existing run after retirement. Individual-job keys remain separate.
- [ ] Add payload retention/size policies and cleanup that cannot remove active run dependencies.
- [x] Introduce recoverable flow incidents: exhausted technical retries block dependent work without confusing it with a deliberately skipped branch. Preserve explicit terminal-failure policy. ADR-008 and saved-schema tests preserve Stop semantics for old graphs; new builders default to Suspend.
- [x] Render the saved definition revision with execution states and node details; append an execution journal transactionally with state changes for a separate event timeline. Keep the dashboard independent of application DLLs.
- [ ] Complete recovery beyond the implemented audited retry and pause/resume commands: retry the failed step, validated input correction, and pause/resume admission. Show why a flow is waiting or blocked before offering an action.
- [x] Define execution deadlines and progress reporting separately from worker lease renewal; a live worker is not evidence that its handler is progressing.
- [ ] Add business-write/outbox integration before claiming atomic business operation plus enqueue. Specify supported local transaction boundaries and do not imply cross-database atomicity.
- [ ] Define tenant/environment isolation, viewer/operator permissions, payload redaction, and secret-reference handling before shared multi-tenant hosting. CorrelationId must never be an authorization boundary.
- [ ] Test process-kill, ambiguous commit, database disconnection, restoration, and resumption scenarios. Document restore reconciliation for external effects; retain the existing at-least-once guarantee.

- [ ] Review and implement scoped attempt middleware, typed validation, configurable retry classification, and post-settlement observers as defined in `processor-lifecycle-and-extensibility.md`. Durable lifecycle subscriptions require a separate persisted delivery/outbox contract; local callbacks must not masquerade as reliable delivery.

## P2 — scheduling and compatibility

- [x] Add recurring schedules with explicit overlap policies: allow, skip-if-active, and coalesce. Keep schedule identity separate from processor identity.
- [x] Define named schedule lifecycle, update ownership, pause/resume, and atomic/idempotent occurrence creation. Reuse one processor across immediate jobs, one-time schedules, recurring schedules, and flow steps.
- [x] Deliver the executable sample set in `samples/README.md`, starting with the .NET 8 direct-processor quickstart and scheduling example. Build and smoke-test runnable samples in CI.
- [ ] Add legacy invocation import with explicit old identity/signature mappings and payload conversion. Start with one verified Hangfire storage/version shape.
- [ ] Add contract upcasters and rolling-deployment capability checks. Preserve pending old versions until compatible consumers exist.
- [ ] Evaluate automatic canonical flow revisions with optional human labels; pin executions to the chosen revision and make activation/migration explicit. Mandatory integer versions remain the current API.
- [ ] Add partition-key concurrency and rate limits. Correlation must remain independent from lock identity.
- [ ] Add SQL Server, then prioritize further providers using real adoption needs; all must pass the same guarantees.
- [ ] Add durable signals with deduplication, storage of signals arriving before their wait step, and race-tested timeout behavior; never park a worker thread while waiting.
- [x] Define cron time zones, daylight-saving transitions, and missed-schedule policy alongside overlap controls.
- [ ] Add bounded fan-out, queue fairness, per-tenant quotas where applicable, and load tests that measure both database contention and memory growth.

## P3 — controlled recovery and release

- [ ] Fork an execution from a persisted checkpoint, with a new run ID and causation link; preserve the original audit trail.
- [ ] Add explicit compensation handlers for reversible external effects. Never label state rewind as undoing arbitrary side effects.
- [ ] Add child flows, durable timers/signals, failure branches, and a documented expression/versioning model.
- [ ] Benchmark idle polling, admission contention, and completion throughput before replacing conservative locking.
- [ ] Define supported platforms/TFMs, compatibility policy, license, final package name, and public release process.
- [x] Apply the accepted WorkThrough working brand consistently to source/API/package/sample/UI names, with build and package-consumer verification. Package/domain clearance and publication remain separate; persisted processor keys must not change during the rename.

## Exit criteria for the initial repository

1. Given an enqueued job from build A, when its contract/handler classes are renamed and the old handler assembly is removed, build B executes the unchanged persisted job.
2. Given competing independent stores/workers, valid admitted leases never exceed the configured shared total/type limits.
3. Given a completed predecessor, a restart sees its output and exactly one logical successor per selected step.
4. Given a dashboard-only deployment, it renders and filters unknown job contracts without their application DLLs.
5. Given storage failure, enqueue does not falsely acknowledge success.
6. Given shutdown or stale ownership, incomplete work never becomes successful solely because cancellation occurred.

The implementation is an alpha with verified runtime, storage, lifecycle, recovery, and dashboard paths. Completion of these exit criteria is not a claim of production maturity or completion of P1–P3.
