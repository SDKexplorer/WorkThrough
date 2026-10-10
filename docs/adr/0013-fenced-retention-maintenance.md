# ADR-013: fenced retention maintenance and schema 4

Status: implemented, 2026-10-10. Load/restore qualification remains a release gate.

## Decision

Schema 4 adds retention policy revisions, whole-unit holds, maintenance ownership/batch descriptors, incidents and verified-export receipts. Upgrade from schemas 1–3 is transactional on SQLite/PostgreSQL. Stop **all older writers** before initialization; an already-running old writer does not acquire the new hold semantics. Schema-3 packages reject initialization of schema 4. Binary rollback requires restoring the pre-upgrade database and reconciling external effects.

Maintenance is doubly opt-in: register its hosted service and explicitly enable a persisted policy through an attributed administrative command. A short, independent database transaction acquires ownership and saves an immutable batch ID, original service actor, policy revision and database-time cutoffs. Replacement hosts reuse that descriptor with a new secret lease token. A policy revision change supersedes the pending batch, even when durations appear unchanged.

The batch transaction checks the unexpired token before and after its work. It archives at most ten whole terminal units, preserves held/active/incident work, optionally purges matching verified bodies, trims at most 1,000 eligible journal events, then commits its result, administrative receipt, audit and checkpoint together. Repeating a committed command returns its receipt; the old lease cannot perform new effects. The receipt fingerprint excludes the changing token but includes the immutable descriptor. Database/provider failures roll back the batch; they are never interpreted as successful empty cleanup.

Archive evidence bounds are checked before archive writes. An oversized unit is retained with a visible incident and excluded from subsequent automatic selection until an audited retry. Manual archival remains explicit and atomic. Holds also block manual purge and event trimming. Child execution holds resolve to the whole live flow; callers must retain the returned unit ID.

Exports happen outside database transactions. The first destination writes immutable, hash-addressed files to a trusted configured directory, flushes the file, reads it back and verifies length/hash. A conflicting/corrupted object is never overwritten. A trusted host records the destination/object version/hash/length and database verification time while it still owns the lease. Automatic purge requires a receipt for this batch, the current policy/destination/hash and a fresh same-transaction hold check. A takeover of that same batch can reconcile its recorded proof; a later batch must verify its copy again.

## Alternatives and limits

Deleting completed rows without receipts would weaken acceptance guarantees. Keeping ordinary cleanup ticks as jobs would create more history. A cross-store transaction is unnecessary: a failed copy/receipt leaves the body intact, and repeated copies reuse a verified immutable object.

Read-back verification proves the bytes observed at that time. Operators own destination durability, access control, encryption, backups and protection from later deletion. A local directory on the database's disk is not an independent disaster-recovery copy. This adapter does not certify power-loss behavior of arbitrary filesystems or mounts.

Worker writes and maintenance share the provider's existing serialization boundary. The host defaults to one unit per batch, a five-second cooperative budget and one minute between cycles. Large/contended deployments must measure this budget; SQLite synchronous calls cannot provide a hard cancellation deadline. Receipts, audit, batch descriptors and archive summaries remain retained and may grow. No fixed-size database or automatic shrinking claim is made.

## Verification

The provider suite covers competing connections, expired tokens, policy changes, hold/preview races, lost-response reconciliation, a forced lease loss **after archive writes** with full rollback, whole-flow evidence, oversized-candidate progress, subscriber watermarks, hash/destination mismatch, receipt preservation and file-copy reuse/corruption. Saved schema-1 and schema-3 fixtures upgrade without rewriting invocation identities. MCP uses read-only database connections and permission separation; the dashboard derives command actors from authenticated host context.

Large maintenance-versus-worker contention, long soak, multi-machine faults and PostgreSQL PITR remain separate qualification work. SQL Server/Oracle remain unimplemented; schema 4 does not imply their support.
