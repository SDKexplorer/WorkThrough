# ADR 020: write-path throughput without weakening admission

Status: accepted, implemented; amended with row-locked settlement and unrestricted claims.

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

## Amendment: row-locked settlement and unrestricted claims

The first decision left settlements and claims under the admission lock. Measurements then showed that lock, together with the event-sequence row, capped every provider at a few hundred jobs per second. Three further changes remove it from the common path without weakening a guarantee:

7. **Journal last:** a transaction's events are buffered and appended just before commit, in one round trip. The event-sequence row, which every journaled transaction increments, is then locked only for that final moment. The journal stays gap-free and ordered by commit.
8. **Row-locked settlement:** on PostgreSQL and Oracle, and on SQL Server when `READ_COMMITTED_SNAPSHOT` is on, settlement takes no admission lock. Ordering comes from row locks instead:
   - A transaction that changes a flow's steps locks the flow row first, then step rows, then the sequence row at commit. Settlement batches lock their flows in one global order.
   - The job row is read with a lock and its lease rechecked, so a concurrent recovery or cancellation cannot be overwritten.
   - Sweeps inside a claim (lease recovery, queued expiry, signal dispatch) skip a flow or job that another transaction holds instead of waiting; the next sweep handles it. A sweep therefore never waits while holding the admission lock.
   - Leases and flow changes are conditional on the status that was read. A claim and a failing flow that race for the same queued step agree: the step is either leased or skipped, never both.
   - A serialized claim reads the database clock again after its admission counts and starts the attempt on it. A settlement those counts no longer saw has committed, so its recorded finish is earlier. Recorded attempt intervals therefore never show more overlap than a limit allows. Without this, the multi-process admission exercise observed five overlapping attempts against a tenant limit of three, although no claim had admitted beyond it.
   - Without versioned reads, SQL Server's plain reads wait on row locks, which can deadlock a claim with a settlement; it keeps the serialized path there. SQLite always has one writer.
9. **Unrestricted claims:** when the store has no cluster, per-type, keyed or rate limit and no bounded flow, no sweep is due, and the worker asks for one queue, a claim takes no admission lock. It locks a page of ready jobs with `FOR UPDATE SKIP LOCKED` (`UPDLOCK, READPAST` on SQL Server), so concurrent claims lease disjoint jobs. The claim rereads the policies in its transaction and falls back to the serialized path if any appeared. Workers serving several queues keep the serialized path, which owns the fair-share turn.

Limits stay exact: whenever a limit, a rate or a bounded flow exists, claims serialize as before. A policy created while unrestricted claims are in flight applies to claims that start after it commits, as it would to claims that committed just before it.

## Consequences

- **Throughput** (preloaded backlog, 3 workers × 16 slots, 1 ms handlers, drain jobs/s):

  | Provider | Before | After first decision | After amendment |
  |---|---:|---:|---:|
  | PostgreSQL | ~95 | 221–275 | 811–829 |
  | SQL Server (emulated), `READ_COMMITTED_SNAPSHOT` on | 63 | 230 | 434 |
  | SQL Server (emulated), off | 63 | 230 | 201–211 (serialized) |
  | Oracle | 88 | 212 | 278–279 |
  | SQLite | 265 | 695–725 | 723–813 (single writer) |

  The standard benchmark's limited workloads also improved; see `docs/performance.md`.
- Delivery stays at least once. Admission stays exact across hosts. A stale worker still cannot settle an attempt.
- A database error while committing a completion batch fails every completion in it. Those attempts remain recoverable through lease expiry, the same outcome as a failed single settlement.
- `IJobStore` gains `ClaimBatchAsync` and `CompleteBatchAsync` with default implementations, so existing providers keep working unchanged.
- Custom relational providers keep the serialized path unless they override `BeginRowLockedWriteAsync`, `LockingSelectSql` and `SkipLockedJobsSql` together.
- A store with any limit, rate or bounded flow, or a worker serving several queues, still claims through the admission lock here. [ADR-022](0022-limit-locks.md) removes that from engine protocol 3.
- The event-sequence row remains the last serialization point. Removing it would mean giving up the gap-free, commit-ordered journal.
