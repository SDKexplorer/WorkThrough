# ADR 022: exact limits without the admission lock

Status: accepted, implemented in schema 7 and engine protocol 3.

## Context

ADR-020 moved settlements, and claims in stores without limits, off the store-wide admission lock. Stores with a fleet, per-type, keyed or rate limit, or a bounded flow, still serialized every claim. That keeps limits exact, but it means two tenants with separate quotas wait on each other. A typical SaaS store has limits, so it got none of the gain.

A limit counts admitted, unexpired leases. A claim must see every lease admitted before it under the same limit. A settlement only frees capacity: if a claim misses one, it is conservative, never wrong.

## Decision

Replace the store-wide mutex with one mutex per limit and key.

- **Lock rows.** Schema 7 adds `dj_limit_locks`, 4,096 pre-seeded rows. A limit scope, such as `fleet`, `type|<group>`, `tenant|<id>`, `partition|<key>`, `rate|<policy>|<scope>|<key>` or `flow|<run>`, maps to a row by a stable FNV-1a hash. Scopes that share a row only serialize their claims.
- **Claims.** A claim selects a page of ready jobs with `SKIP LOCKED`, as in ADR-020, narrowed by the usual admission predicates on a snapshot. It then locks the lock rows of every limit those candidates touch. For each candidate it repeats the exact admission check, which counts running leases as before. The attempt starts on a clock read after that check.
- **Exactness.** Two claims that could exceed the same limit lock the same row, so the second one counts after the first commits. Under read committed, PostgreSQL, Oracle and SQL Server with `READ_COMMITTED_SNAPSHOT` give each statement a fresh snapshot after the lock is granted.
- **Deadlock freedom.**
  - A claim waits for lock rows only while it holds none, and then takes them in ascending order in one statement. A later lock in the same transaction is taken with `SKIP LOCKED`; if any row is busy, the batch ends.
  - Claims never wait on job rows.
  - Settlements and recovery take no lock rows.
  - Claims for several queues first lock the queue-clock row, which serializes them among themselves and keeps weighted turns exact.
- **Sweeps.** Lease recovery, queued expiry, signal dispatch and rate-ledger pruning run in their own short admission transaction before the claim. Concurrent claims deleting the same ledger rows could otherwise deadlock.
- **Rollout.** Every claimant must take the lock rows, so the change is engine protocol 3 (ADR-021). Below protocol 3, claims under limits keep the serialized path. Schema 7 is additive: `reader_minimum` stays 6, and schema-6 hosts keep working until protocol 3 activates and they stop claiming.

## Consequences

- Claims for different tenants, partition keys, job types, rate keys and flow runs no longer serialize.
- A fleet-wide limit is one scope, so it still serializes every claim, as before. Settlements remain concurrent.
- Each admitted job under limits costs one more exact check and one clock read. The pre-filter keeps most rejected candidates out of the page.
- A policy created while claims are in flight applies to claims that start after it commits.
- SQLite keeps one writer; the locks are no-ops there.
