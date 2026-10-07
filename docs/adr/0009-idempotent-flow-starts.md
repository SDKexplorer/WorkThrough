# ADR-009: caller-idempotent flow starts

Status: implemented; verified against SQLite and PostgreSQL.

A producer can lose the response after storage accepts a run. Retrying without a caller request identity would create another graph and another set of external effects. The existing StartAsync overload remains available and always creates a new run; a second overload requires an explicit idempotencyKey as well as correlationId (which may be null). Correlation is never used as a uniqueness key.

Keys are case-sensitive, non-whitespace strings of at most 256 characters, unique across flow starts in one configured store. They occupy a separate namespace from single-job enqueue keys. The provider fingerprints the serialized definition, serialized input, and correlation with SHA-256. All graph options, queues, processor versions, and retry budgets are therefore part of request identity. This is serialization equality, not inferred business equivalence: callers must retain property/step order and serialization choices when retrying.

The provider checks a persisted receipt before validating the current definition lifecycle. An identical request returns its original run ID, including after completion, incident, pause, or retirement. A different request under the same key fails. A genuinely new request must still target a published or previously unseen code-first definition; a draft or retired definition cannot start new work.

The receipt, immutable definition registration, run, steps, and initial graph advancement share one provider write transaction. SQLite uses its immediate write transaction; PostgreSQL uses its transaction-scoped advisory lock. A failed receipt insert rolls everything back. A unique receipt key provides an additional database constraint. No handler executes within this transaction.

Initialization adds dj_flow_starts within schema 2. It changes no existing invocation identity or input. Existing runs without receipts cannot be automatically associated with new request keys. An older custom provider inherits an explicit NotSupportedException for the new overload rather than silently losing the guarantee. Providers advertising support must pass the concurrency, conflict, restart/retirement, and injected receipt-failure tests.

Receipts currently have no expiry. Future retention must preserve them for the advertised deduplication window and must not remove a receipt independently of its documented run-retention policy. Repeating a key does not authorize replaying a completed flow. Use a new key for deliberate new work.

This prevents duplicate logical starts; it does not change at-least-once attempt execution or make application business writes and enqueue atomic. A business outbox is a separate integration boundary.
