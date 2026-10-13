# ADR 018: keyed concurrency, tenant isolation, rate limits, bounded fan-out and weighted queue fairness

Status: accepted, implemented (schema 5).

Admission stays inside the provider's existing serialized claim transaction, so every rule below is exact across hosts and counts only admitted, unexpired leases (ADR-003).

## Admission keys

- Jobs may carry two optional keys of 1–256 characters, both independent of `CorrelationId` and neither an access boundary:
  - `PartitionKey` names a business resource to serialize or bound, such as an account or document.
  - `TenantId` names the customer that owns the work.
- Both can be set on enqueue, transactional/outbox enqueue, schedules and flow runs (one value for every step of a run). They are separate identities: a tenant has many partitions.
- Partition keys are global. Tenants that reuse business IDs namespace them (`tenant/invoice-1`); the engine does not do it implicitly.

## Keyed concurrency and tenant quotas

- `KeyedLimit(Scope, JobKey?, MaximumPerKey)` caps running attempts per distinct partition key or tenant ID. It applies to all work, or to one admission group.
- A partition maximum of 1 serializes a key. A tenant maximum bounds how many fleet slots one tenant's backlog can hold, so other tenants keep being admitted.
- Work without a key of the limit's scope is not limited by it. There is no strict FIFO: a failing job must not block its whole key. Jobs of a key still start in availability order.

## Rate limits

- `RateLimit(JobKey?, Permits, Window, PerKey?)` allows at most `Permits` attempt starts per sliding `Window`, measured by the database clock. It applies to all work or one admission group, optionally per partition key or per tenant. Windows are 1 second to 1 day.
- Admitted starts are recorded in a rate ledger keyed by policy and key value, inside the claim transaction. A check is an index range over one key's recent starts, bounded by the permits while the limit binds.
  - The first implementation counted all attempt history in the window and joined it to jobs. Under contention its cost grew with every start in the window (measured: −27% throughput with non-binding policies after a few thousand starts).
- A limit counts the starts it admitted: starts before it existed are not counted. Removing a limit drops its ledger. Ledger rows older than the maximum window are deleted as claims proceed. Archiving work does not change counts.

## Bounded fan-out

- `FlowStart.MaxParallelSteps` (1–500) caps how many of one flow run's steps hold admitted leases at once. A wide parallel stage is then released gradually instead of occupying every worker slot.
- Steps waiting for a slot report `FlowParallelism`.

## Weighted fair queuing

- Stride scheduling across queues: each queue has a weight (default 1, plain round-robin) and a pass value. A claim serves the ready queue with the lowest pass, then that queue's oldest job.
- The served queue's pass becomes max(pass, virtual time) + 1,000,000 / weight, so an idle queue gains no burst credit. No eligible queue starves, and one queue's backlog cannot delay another's.

## Operations and compatibility

- Audited commands (ADR-017 results):
  - keyed and rate limits, under `limits.manage`;
  - queue weights, under `queues.manage`.
- Readiness reasons: `PartitionCapacity`, `TenantCapacity`, `FlowParallelism` and `RateLimited`.
- Schema 5 is additive on all four providers. It adds:
  - partition, tenant and flow-bound columns;
  - the policy tables and the rate ledger;
  - queue weight and fairness state.
- Oracle resumes from its existing migration checkpoint. Stop schema-4 writers before upgrading: they would ignore the new limits.
- **Claim cost:** the claim's first query reads which policy kinds exist and whether any running flow is bounded. Absent kinds are left out of the claim query, so stores without these policies keep their claim cost.
- **Partial indexes:** partition, tenant and bounded-flow indexes cover only rows that carry a value. Work without keys never maintains them.
- **PostgreSQL JIT:** admission and read transactions set `jit = off`. JIT compilation of the admission predicates took ~250 ms per claim, against sub-millisecond execution.
- **Measurements:** the claim-path rerun and the multi-process admission campaign are in `docs/performance.md`.
