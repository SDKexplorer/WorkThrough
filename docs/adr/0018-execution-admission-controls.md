# ADR 018: partition concurrency, rate limits and weighted queue fairness

Status: accepted, implemented (schema 5).

Admission stays inside the provider's existing serialized claim transaction, so every rule below is exact across hosts and counts only admitted, unexpired leases (ADR-003).

## Partition concurrency

- Jobs may carry an optional `PartitionKey` (1–256 characters), independent of `CorrelationId`. It can be set on enqueue, transactional/outbox enqueue, schedules, and flow runs (one key for every step of a run).
- `PartitionLimit(JobKey?, MaximumPerPartition)` caps running attempts per distinct key value, for all work or one admission group. A maximum of 1 serializes a key.
- No strict FIFO: a failing job must not block its whole key. Jobs of a key still start in availability order.

## Rate limits

- `RateLimit(JobKey?, Permits, Window, PerPartition)`: at most `Permits` attempt starts per sliding `Window`, for all work or one admission group, optionally per partition key.
- Counted from attempt history (`dj_attempts.started_at`, joined to jobs) using the database clock. Windows are 1 second to 1 day; archiving work inside an open window removes it from the count.

## Weighted fair queuing

- Stride scheduling across queues: each queue has a weight (default 1 = round-robin) and a pass value; a claim serves the ready queue with the lowest pass, then its oldest job. Its pass becomes max(pass, virtual time) + 1,000,000 / weight, so an idle queue gains no burst credit. No eligible queue starves and one queue's backlog cannot delay another's.

## Operations and compatibility

- Audited commands (ADR-017 results) set and remove partition and rate limits (`limits.manage`) and queue weights (`queues.manage`). Readiness reports `PartitionCapacity` and `RateLimited`.
- Schema 5 is additive on all four providers (partition column, policy tables, queue weight and fairness state, attempt start index); Oracle resumes from its existing migration checkpoint. Stop schema-4 writers before upgrading: they would ignore the new limits.
- The claim reads which policy kinds exist in its first query and leaves absent predicates out of the claim query, so stores without these policies keep their claim cost. PostgreSQL admission and read transactions set `jit = off`: JIT compilation of the admission predicates took ~250 ms per claim against sub-millisecond execution. The rerun claim-path baseline is in `docs/performance.md`.
