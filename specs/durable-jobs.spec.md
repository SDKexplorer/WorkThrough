# WorkThrough requirements

## User value

Developers can persist background work, reorganize their .NET solution, and continue executing queued jobs. Applications compose storage, processing, and monitoring independently. Workflows preserve state and group related executions by correlation ID.

Requirements derive from the conversation: assembly relocation first; extensible database support; NuGet delivery; background services; dashboard on API or worker; configurable total/type concurrency; conditional persisted workflows; correlation grouping; later recovery/compensation. Clean dependency boundaries are required; CQRS/MediatR are not mandatory.

## Functional requirements (EARS)

- FR-001: When a compatible handler moves between classes, namespaces, or referenced assemblies, the engine shall execute existing work using the current registered implementation without rewriting job identity.
- FR-002: When no stable contract key is declared, registration/enqueue shall fail with a diagnostic.
- FR-003: When two distinct implementations claim the same contract in one worker, startup shall reject the conflict before claiming work.
- FR-004: When enqueue reports success, the provider shall have durably committed the job; a failed or ambiguous commit shall not be reported as confirmed success.
- FR-005: Where a caller idempotency key is supplied, repeated equivalent enqueue requests shall identify one logical job and conflicting requests shall fail.
- FR-006: Where processing is enabled, the engine shall run through the .NET host lifecycle and create an independent DI scope per attempt.
- FR-007: Where a dashboard is hosted, it shall use stored metadata and queries without requiring processor assemblies.
- FR-008: When a host maps the dashboard, the host shall choose its URL path and authorization policy.
- FR-009: While an applicable local or shared concurrency limit is full, the engine shall leave work queued without occupying a handler slot.
- FR-010: When ownership expires or changes, stale attempts shall be unable to commit completion or failure.
- FR-011: When a flow starts, the engine shall persist its definition version, input, correlation, and logical steps atomically.
- FR-012: When a predecessor completes, completion and the activation/skipping of eligible successors shall commit atomically.
- FR-013: When an existing flow version is republished with different content, publication shall fail and request a new version.
- FR-014: When executions share a correlation ID, queries shall return them as a group without treating correlation as uniqueness or concurrency ownership.
- FR-015 (planned): When a checkpoint fork is requested, the engine shall create a new causally linked run while preserving the original history.
- FR-016 (planned): Where a compensation handler is declared, the engine shall record its execution independently from restoring workflow state.
- FR-017: The minimum modern runtime shall be .NET 8. Where a runtime and consumer SDK are advertised as supported, packages and generated registration shall be verified using that runtime and SDK; ADR-005 records the matrix and tooling decisions.
- FR-018 (planned): When supported producer and worker versions differ, the engine shall preserve the documented envelope, contract, and flow semantics; incompatible schema/protocol combinations shall be rejected before work is claimed.
- FR-019: When a recoverable technical failure exhausts retries, the engine shall retain an incident and block dependent steps while preserving the distinction from deliberately skipped branches.
- FR-020: When an operator inspects a run diagram, the dashboard shall display that run's saved definition revision and execution states without loading application assemblies.
- FR-021: When an operator performs a recovery action, the engine shall validate authorization and current state, apply the transition atomically, and retain an audit record without rewriting prior attempt history.
- FR-022: When a processor is declared, its permanent logical identity shall be independently expressible from its CLR input type; multiple processors may accept the same type, including string.
- FR-023: When an immediate job, one-time schedule, recurring schedule, or flow step targets a processor, the engine shall resolve the same logical processor identity through the deployed registry without persisting a CLR invocation dependency.
- FR-024: When recurring schedules are registered by multiple hosts or recovered after a crash, a due occurrence shall create at most one logical execution within the documented schedule-update policy; attempts retain at-least-once execution semantics.
- FR-025: When dashboard access is evaluated, the host shall supply authenticated identity and the server shall enforce separate operation and resource permissions, including distinct metadata and payload access, without requiring an engine-owned user database.
- FR-026: When an administrative state change succeeds, its trusted actor, command ID, reason, redacted change summary, and outcome shall commit atomically with the change; inability to persist the successful-action audit shall roll back that change.
- FR-027: When the same authorized administrative command is repeated, its persisted result shall be returned without a second state change; conflicting reuse of its command ID shall be rejected.
- FR-028: When the dashboard displays action history, it shall distinguish human/service administration from worker attempts, filter within authorized scope, and preserve stable actor identity independently of display-name changes.

- FR-029: Flow and schedule definitions shall support draft authoring before publication. Drafts cannot execute; publication validates and freezes a revision; retiring a published flow revision prevents new starts while active runs retain their pinned definition. Editing published content creates a draft and never mutates active execution history. Publication, retirement, and schedule activation changes require authorization and audit.

## Non-functional requirements

- Correctness: run the provider conformance suite on every advertised backend; physically test replacement deployment artifacts.
- Portability: Core has no host/web/database references. A provider must not weaken guarantees silently.
- Security: dashboard defaults to host authorization; mutations require anti-forgery validation; queries are parameterized; UI uses text rendering for untrusted values.
- Performance: no production SLA is claimed. Record idle database traffic and admission/completion throughput before choosing production defaults. Initial PostgreSQL writes serialize deliberately.
- Maintainability: deterministic warning-clean builds, nullable references, reviewed schema/API changes, actual package-consumer validation, and preserved exception evidence without default payload logging.
- Operations: cancellation is cooperative; delivery is at least once; interruption, failure, expiry, and success have distinct attempt outcomes.

## Error handling

| Condition | Behavior |
|---|---|
| Missing or conflicting contract | Registration/startup error; no claim |
| No compatible worker | Remains queued; worker capabilities explain missing coverage |
| Storage unavailable | Enqueue throws; worker acquisition backs off and logs; unsettled attempts recover by lease |
| Handler throws | Persist failure attempt; retry until budget is exhausted |
| Lost lease | Request cancellation; reject stale settlement |
| Invalid flow binding | Retain parent success; record the binding failure under the saved flow failure policy |
| Unauthorized dashboard request | Host authentication/authorization handles challenge/forbid |
| Invalid dashboard mutation token | HTTP 400, no mutation |
| Unsafe standalone retry of flow node | HTTP 409; use the explicit audited flow recovery operation |

## Acceptance examples

- Given persisted work from an old assembly, when a replacement deployment contains only the renamed processor, then the same job ID and payload execute successfully.
- Given two workers and a shared per-type maximum of one, when both claim that type, then at most one receives a valid lease at a time.
- Given a parent's persisted approved result, when a new process resumes its flow, then the approved child executes, the alternative is skipped, and repeated completion cannot create another logical child.
- Given a dashboard host with no processor references, when an authorized operator filters by correlation, then stored executions and attempts render correctly.

Implementation checklist and current scope are maintained once in `docs/plan.md`.
