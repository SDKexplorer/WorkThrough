# Transactions, operations, and retention

This document describes implemented operations. Automatic maintenance, retention holds and verified external-export receipts are proposed in [storage evolution](../specs/storage-evolution.spec.md); they are not available configuration options yet.

## Business writes and separate job databases

`RelationalJobStore.InTransactionAsync` supplies a connection, transaction, and scoped `TransactionalJobs` writer. Execute business SQL using that supplied transaction and call `EnqueueAsync(definition, input, options, ct)` before returning. Business data and job acceptance commit or roll back together. Keep the callback short; await all work and never commit/dispose the supplied transaction or make remote calls inside it. This is an explicit transaction boundary, not automatic enlistment in arbitrary ambient EF transactions.

When the destination job store is separate, use `TransactionalJobs.SendAsync`. It saves an outbox request in the business database. Initialize that source provider before starting its dispatcher. `.AddOutboxDispatcher(source)` delivers to the host's configured job store, or use `source.DispatchOutboxAsync(destination)` from your own service. Delivery occurs outside the source transaction. A destination commit with a lost response is reconciled by retrying the same envelope and key. The source retains its delivery receipt and clears the delivered payload. Caller keys must be unique for their meaning in the destination store; generated keys include the source store ID.

This does not create an atomic transaction across two databases. Monitor pending deliveries and dispatcher errors. Restoring a source backup can resend delivered requests; retained destination receipts and application effect keys are essential.

## Signals and timeouts

```csharp
var approval = new SignalDefinition<Approval>("orders.approved");
var graph = new FlowBuilder("orders.approval", 1)
    .WaitForSignal("approval", approval, TimeSpan.FromDays(2))
    .Build();
var runId = await flows.StartAsync(graph, order, cancellationToken: ct);
await flows.SignalAsync(runId, approval, decision, requestKey, ct);
```

A wait occupies no processor slot. Signals can arrive before their step becomes eligible; one signal name is one-shot within a run. Receipt keys deduplicate identical requests and reject conflicting reuse. A signal received by the persisted deadline wins. Timeout expires the step and applies the saved flow failure policy. Pause does not stop elapsed time. Recovering an expired signal step explicitly re-arms its deadline and can consume a retained late arrival. Run a worker or scheduler to advance waiting deadlines. Signal payload and input/output sizes are bounded.

## Operator controls

Queue pause prevents new claims and lets active attempts settle. Worker drain does the same for a stable worker ID; configure a persistent ID when controls should survive process replacement. Changes require expected revisions and attributed administrative commands. Job expiry applies before admission; it does not force-abort an already running processor. Cancellation and deadlines remain cooperative.

Retry/recover retains a job's logical identity and effect key. Replay intentionally creates a new standalone job and causation link; business effects can happen again. Original evidence stays unchanged. Replaying an archive requires explicitly exporting/reviewing its input and enqueueing new work; an archive is not silently reactivated.

## Archives and database growth

Start with one configured store per environment. `ArchiveCandidatesAsync` previews a bounded policy. `ArchiveCompletedAsync` moves at most ten units per call: a terminal standalone execution or a whole completed flow. Active, Paused, and Incident flows remain live. Failures/cancellations/rejections/expiry require a separate failure cutoff; the dashboard's initial archive action selects successful work only. The preview is advisory and candidates are reselected atomically when the command executes.

Archive packages contain job inputs/outputs, attempts, diagnostics, the pinned flow definition/input, signals, and event history. SHA-256 identifies the UTF-8 exported JSON. Bounds cap jobs, evidence counts, accumulated evidence size, and final package size. Oversized executions require separately designed streaming export rather than loading an unbounded package. An oversized oldest candidate blocks that batch and must be investigated.

Export and verify a copy before using `PurgeArchiveAsync(id, expectedHash, command)` to remove the stored body. Purge keeps the archive tombstone and hash. The SDK operation is intentionally explicit; the dashboard exports but does not purge. Export contains sensitive data and requires both metadata and payload permissions. Deployments may add payload-access logging at the host boundary.

Job/flow/signal request receipts, command receipts, audits, and definitions have separate lifetimes. Archival and body purge do not remove them. That preserves deduplication across retry, old producers, and restore windows, but these tables can still grow. There is no automatic destructive retention job. Event trimming is separately audited and blocked by lagging subscriptions or unarchived executions.

Separate hot/archive tables improve query isolation, not physical disk reclamation. PostgreSQL vacuum and SQLite checkpoint/vacuum require provider-specific maintenance windows. Do not split a live flow across databases. Cross-database/object-store archival uses export → verified external copy → explicit body purge, without a distributed transaction on the worker path.

## Restore and reconciliation

Stop writers before engine/schema upgrade or restore; retain a verified backup. Restore a consistent database snapshot including receipts, registry, definitions, active work, outbox, and subscription cursors. Deploy a matching engine schema and processor registry, run preflight for required queues/versions, and reconcile external effects before resuming producers and workers. A backup can precede already-completed external effects. Stable job keys and destination idempotency/fencing are required to prevent repeating those effects.

The executable fault harness kills a real worker after a separate business effect and checks lease recovery; SQLite additionally restores that crashed state into a different database file. PostgreSQL backup/PITR reconciliation and multi-host network-partition campaigns are deployment-specific and are not claimed by that test.
