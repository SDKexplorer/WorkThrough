# ADR-010: Schema 3, coordinated aliases, durable extensions, and archival

Status: accepted for the local alpha, 2026-10-08.

Schema 3 adds expiry/causation/terminal timestamps, independent acceptance receipts, outbox delivery, coordinated processor registry/admission groups, queue/worker controls, durable signal waits and receipts, monotonic event cursors, and bounded archives. Initialization migrates schema 1 or 2 in the provider's serialized write transaction and rejects unknown future versions. Existing flow definitions are read using saved compatibility defaults; absent signal metadata is omitted during serialization so existing flow-start request fingerprints remain compatible.

Stop all schema-1/2 writers before migration. Schema-3 workers may coexist only with engines implementing these same persisted rules. Older engines cannot understand alias grouping, new terminal states, or intrinsic signal steps. Runtime compatibility (.NET 8/10) does not imply arbitrary engine-version coexistence. Rollback requires restoring the matching database backup and binaries; it is not an automatic downgrade script. Reconcile effects produced after the backup before resuming.

A registry revision is activated only without active valid leases. Every claim carries its startup revision; a change fences stale claimers. All versions of a processor key share an admission group; historical and canonical names share limits. Keep old identities in persisted requests and immutable graphs. Alias maps are explicit audited deployment changes, never a best-effort startup rewrite. Registry changes do not stop external effects in a partitioned process; idempotency/fencing remains required.

Business SQL and direct enqueue share the supplied local provider transaction. A separate destination uses source outbox → idempotent destination acceptance → source acknowledgment. Durable event handlers similarly acknowledge a persisted cursor after processing; both are at least once. Local observers are best effort after settlement.

Archival copies evidence and removes only terminal units from active tables in one attributed transaction. Whole flows stay together. Independent request receipts protect against duplicate acceptance after hot data or archive bodies are removed. Audits/definitions/receipts are retained independently and therefore need explicit future retention decisions. Archive and event-trim bounds favor correctness and predictable memory over unbounded cleanup throughput.

Alternatives rejected: persisting new CLR locations, alias-only dictionaries that split capacity, startup metadata rewrites, destructive cleanup of individual flow dependencies, cross-database transactions on claims, and claiming local callbacks are durable integrations.

SQLite initialization enables WAL by default for shared local-process deployments. `SqliteStorageOptions.UseWriteAheadLogging=false` preserves an existing journal mode. Engine connections retain FULL synchronous durability; there is no benchmark-only relaxation. Snapshot/backup guidance includes WAL state. The load harness uses the normal 30-second worker lease; only the explicit crash proof shortens recovery to two seconds.
