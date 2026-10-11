# ADR 020: write-path throughput without weakening admission

Status: accepted, implemented.

## Context

Each store serializes admission writes through one lock: a PostgreSQL advisory lock, a SQL Server application lock, an Oracle admission row, or SQLite's single writer (ADR-003). With no limits and near-instant handlers, three 16-slot workers drained only about 60–100 jobs per second on the server databases. Enqueue managed ten times that rate under the same lock.

Statement logs showed no slow query. The cost was sequential round trips while holding the lock, and diagnostic writes that took the lock they did not need:

- about 13 statements per job across its claim and its completion;
- separate locked transactions to record the handler and to start progress tracking;
- empty claims from idle or saturated workers;
- lease renewals.

SQLite connections had no busy timeout, so contending processes fell back to the driver's long fixed retry sleeps.

## Decision

Keep the single admission lock and every guarantee that depends on it. Shorten and avoid the work done under it:

1. **Lock-free pre-check:** before locking, a snapshot read checks whether a sweep is due, or whether ready work exists in the requested queues with fleet capacity left. If neither, the claim returns without locking. A stale answer only delays work by one poll; admission is still decided under the lock.
2. **Batch claims:** `ClaimBatchAsync` leases up to the worker's free slots in one transaction. Before each lease it re-evaluates every shared limit, the worker's local per-type budgets and the queue's fair-share turn, so a batch admits exactly what separate claims would. Recovery, expiry and signal sweeps run only when the pre-check saw something to sweep.
3. **Batch completions:** a worker's completions that finish while the previous batch commits go into one transaction (`CompleteBatchAsync`). Each item keeps its own lease check, and a lone completion is not delayed.
4. **Statement batching:** consecutive statements of a lease, a settlement or a journal entry are sent in one round trip. Oracle wraps them in an anonymous block. Parameter names get a suffix that providers strip when they type parameters by name.
5. **Owned writes:** renewals, handler and progress records, and heartbeats change only rows that the attempt's lease token or the worker's ID owns. PostgreSQL, SQL Server and Oracle run them without the admission lock; SQLite keeps its single writer.
   - Lease recovery now revokes a lease only if it is still expired and its token is unchanged, so a renewal racing a sweep cannot be overwritten.
   - Custom providers inherit the locked path unless they opt in.
6. **SQLite busy timeout:** connections set `busy_timeout` to the connection's command timeout, so writers wait in SQLite's busy handler.

## Not done: relaxing the lock for claims and settlements

Running claims concurrently (`SKIP LOCKED` / `READPAST`), or settling without the lock, was analysed and deferred:

- **Flow joins:** two parallel steps of one flow completing concurrently could each miss the other's success and never activate their join. Avoiding that needs per-flow row locks.
- **Lock ordering:** those row locks, together with the single event-sequence row that keeps the journal gap-free and ordered, create lock-ordering hazards across claims, signals and settlements.
- **Exact limits:** global, keyed and rate limits would need per-limit counter rows to stay exact.

Each needs its own design and fault testing. The event-sequence row also stays a serialization point while the journal promises gap-free order to subscribers.

## Consequences

- **Throughput** (preloaded backlog, 3 workers × 16 slots, 1 ms handlers, drain jobs/s):

  | Provider | Before | After |
  |---|---:|---:|
  | PostgreSQL | ~95 | 221–275 |
  | SQL Server (emulated) | 63 | 230 |
  | Oracle | 88 | 212 |
  | SQLite | 265 | 695–725 |

  The standard benchmark's limited workloads also improved; see `docs/performance.md`.
- Delivery stays at least once. Admission stays exact across hosts. A stale worker still cannot settle an attempt.
- A database error while committing a completion batch fails every completion in it. Those attempts remain recoverable through lease expiry, the same outcome as a failed single settlement.
- `IJobStore` gains `ClaimBatchAsync` and `CompleteBatchAsync` with default implementations, so existing providers keep working unchanged.
