# ADR-003: Provider-owned atomicity and shared admission

Status: accepted as a conservative baseline; optimize after measurement.

Context: local semaphores do not limit a fleet. Generic CRUD does not establish claim or transition guarantees across databases.

Decision: providers implement atomic operations, using their database clock and short serialized admission transactions. SQLite uses an immediate write transaction. PostgreSQL uses a transaction-scoped advisory lock. Handlers execute outside those transactions. Conformance tests are shared across real database providers.

Alternatives: process-local locks for shared limits; long database transactions around handlers; one untested SQL dialect for every database; distributed locks in a second store.

Consequences: an understandable correctness baseline and extra serialization under load. Shared limits bound admitted leases, not physical effects after partitions. Database or effect-level idempotency/fencing remains necessary. No production throughput claim is made yet.
