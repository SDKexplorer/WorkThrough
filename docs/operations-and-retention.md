# Transactions, operations, and retention

This document describes implemented operations on SQLite/PostgreSQL. Automatic retention is opt-in and uses schema 4; see [ADR-013](adr/0013-fenced-retention-maintenance.md) for migration and failure boundaries. The broader [storage plan](../specs/storage-evolution.spec.md) still includes provider and load qualification work.

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

Retry/recover retains a job's logical identity and effect key. **Input correction** (`CorrectInputAsync`, or *Correct input* in the console) applies to work that failed, was rejected, expired or was cancelled. It replaces the input and runs the job again: a standalone job is queued, and a step of a flow in an incident is recovered. The first input is kept and shown to operators with `payloads.read`; the audit records only digests of the new input. Validation runs where the input types exist: register an `IJobValidator<T>` and the worker rejects an invalid correction without retrying. Like recovery, correction keeps the effect key. Replay intentionally creates a new standalone job and causation link; business effects can happen again. Original evidence stays unchanged. Replaying an archive requires explicitly exporting/reviewing its input and enqueueing new work; an archive is not silently reactivated.

## Archives and database growth

Start with one configured store per environment. `ArchiveCandidatesAsync` previews a bounded policy. `ArchiveCompletedAsync` moves at most ten units per call: a terminal standalone execution or a whole completed flow. Active, Paused, and Incident flows remain live. Failures/cancellations/rejections/expiry require a separate failure cutoff; the dashboard's initial archive action selects successful work only. The preview is advisory and candidates are reselected atomically when the command executes.

Archive packages contain job inputs/outputs, attempts, diagnostics, the pinned flow definition/input, signals, and event history. SHA-256 identifies the UTF-8 exported JSON. Bounds cap jobs, evidence counts, accumulated evidence size, and final package size. Oversized executions require separately designed streaming export rather than loading an unbounded package. An oversized candidate rejects a manual batch; automatic maintenance instead records an incident, preserves the unit and proceeds to other units on subsequent batches. `RetryRetentionIncidentAsync` removes that exclusion through an audited command after investigation.

Export and verify a copy before using `PurgeArchiveAsync(id, expectedHash, command)` to remove the stored body. Purge keeps the archive tombstone and hash. The SDK operation is intentionally explicit; the dashboard exports but does not purge. Export contains sensitive data and requires both metadata and payload permissions. Deployments may add payload-access logging at the host boundary.

Job/flow/signal request receipts, command receipts, audits, and definitions have separate lifetimes. Archival and body purge do not remove them. That preserves deduplication across retry, old producers, and restore windows, but these tables can still grow. Event trimming is separately enabled and blocked by lagging subscriptions, held evidence or unarchived executions.

Separate hot/archive tables improve query isolation, not physical disk reclamation. PostgreSQL vacuum and SQLite checkpoint/vacuum require provider-specific maintenance windows. Do not split a live flow across databases. Cross-database/object-store archival uses export → verified external copy → explicit body purge, without a distributed transaction on the worker path.

## Opt-in automatic maintenance

Register maintenance in a web host or generic background host independently of processors:

```csharp
builder.Services.AddWorkThrough()
    .UsePostgreSql(connectionString)
    .AddArchiveDirectory("operations-vault-v1", archiveDirectory)
    .AddRetentionMaintenance(options =>
    {
        options.Actor = new("my-service", "retention-worker");
        options.PollInterval = TimeSpan.FromMinutes(1);
        options.BatchBudget = TimeSpan.FromSeconds(5);
    });
```

`archiveDirectory` must be an absolute, operator-controlled path on suitable durable storage. Protect it as payload data. Changing its location/durability contract requires a new destination ID. Register only hosts that have access to the destination selected by the persisted policy. Missing or failed destinations produce incidents and retain bodies; they never fall back to another path.

Registration does **not** enable deletion. Configure the database once through `IRetentionAdministration` using the current revision and a trusted actor:

```csharp
var policy = new RetentionPolicy(
    SucceededAfter: TimeSpan.FromDays(7),
    FailedAfter: TimeSpan.FromDays(30),
    Enabled: true,
    BatchSize: 1,
    PurgeAfter: TimeSpan.FromDays(90),
    ExportDestination: "operations-vault-v1");

var result = await retention.ConfigureRetentionAsync(policy, expectedRevision,
    new AdministrativeCommand(commandId, operatorActor, "Apply the approved retention policy"), ct);
if (!result.IsSuccess)
{
    // Validation, NotFound or Conflict. Nothing was committed; refresh the revision before retrying a conflict.
    throw new InvalidOperationException(result.Error.Message);
}
```

Administrative commands return `Result`/`Result<T>` instead of throwing for expected failures (ADR 017).

From deployment code or scripts, build a command in one line: `AdministrativeCommand.New(OperationActor.Service("billing-deploy"), "Raise invoice concurrency")`. Each command is audited under that service name. To retry after a lost response, resend the same command instance: its ID makes the retry return the original result instead of applying the change twice.

These durations are an example, not defaults. Omit `FailedAfter` to retain unsuccessful executions indefinitely. Omit both `PurgeAfter` and `ExportDestination` to keep archive bodies. `EventsAfter` is a separate opt-in age for journal cleanup, additionally bounded by every subscriber and live/held evidence. Maintenance does not expire acceptance receipts or audit records. Policy revisions survive deployments; do not overwrite them on every application startup.

`SetRetentionHoldAsync(unitId, held, expectedRevision, command)` protects a whole unit from manual/automatic archival, body purge and event trimming. For a live flow step, use the returned flow ID for future hold reads/changes. Holds do not restore previously removed evidence. Hold reasons remain in protected audit data. Release holds and retry incidents only after reviewing why evidence was retained.

Each cycle handles one persisted batch, then yields for the configured interval. A crashed host's lease expires and another host can resume its batch. Shutdown never means cleanup succeeded. Partial writes, checkpoint changes and audit roll back together. A lost response can be reconciled by rerunning the same batch handle; finished batches return their durable result. An export can succeed before a crash: the same immutable file is verified and reused. Automatic purge only follows recorded verification for that batch and rechecks current policy/hold state.

`IRetentionQueries.ReadRetentionStatusAsync` exposes policy, pending ownership, last result, bounded failure codes, hold/incident counts, up to 100 incident examples and journal/subscriber positions without bodies, reasons or lease tokens. The Archives tab and read-only `workthrough_get_retention` tool use it. Dashboard reads require `executions.read` plus `retention.read`; configuration, holds and incident retry require `retention.manage`, CSRF protection and an audited command. APIs are `/api/retention`, `/api/retention/policy`, `/api/retention/holds/{unitId}` and `/api/retention/incidents/{unitId}/retry` beneath the configured dashboard path. The console currently inspects maintenance; configure it through these APIs or the SDK.

Timeout/failure keeps a pending batch; failure evidence is saved when the store and lease remain available. Host logs are still necessary when the database itself is unreachable. The cancellation budget is cooperative, particularly with SQLite. Measure maintenance impact before enabling it under production load; the full contention/soak and restore campaigns remain pending.

## Restore and reconciliation

Stop writers before a restore, and before upgrading a store from schema 5 or earlier. From schema 6, follow the rolling procedure in [ADR-021](adr/0021-rolling-upgrades.md); retain a verified backup either way. Restore a consistent database snapshot including receipts, registry, definitions, active work, outbox, and subscription cursors. Deploy a matching engine schema and processor registry, run preflight for required queues/versions, and reconcile external effects before resuming producers and workers. A backup can precede already-completed external effects. Stable job keys and destination idempotency/fencing are required to prevent repeating those effects.

The executable fault harness kills a real worker after a separate business effect and checks lease recovery; SQLite additionally restores that crashed state into a different database file. PostgreSQL backup/PITR reconciliation and multi-host network-partition campaigns are deployment-specific and are not claimed by that test.
