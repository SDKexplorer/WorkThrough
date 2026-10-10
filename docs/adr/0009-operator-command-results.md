# ADR 009: operator commands return results

Audited operator commands report expected failures as values. `IJobAdministration`, `IWorkflowDefinitions`, `IJobSchedules`, `IExecutionOperations`, `IRetentionAdministration`, `IProcessorRegistry.SetProcessorAliasesAsync`, the archive commands on `IExecutionArchives`, and `IExecutionEvents.TrimEventsAsync` return `Result` or `Result<T>` from `WorkThrough.Core`.

An `Error` has a type and an operator-facing message:

- `Validation`: the request or command envelope is invalid.
- `NotFound`: the job, execution or retention unit the command addresses is absent from the store.
- `Conflict`: stored state, a revision, a hold, an active lease or a reused command ID no longer permits the change.

A failed result commits nothing: no mutation, no deduplication receipt and no audit entry. A repeated successful command still replays its receipt. Receipts for commands without a value continue to store `true`, so receipts written by earlier releases replay unchanged. Provider and infrastructure faults are not results; they still throw and roll back.

The rest of the API keeps its existing failure style. Lease-fenced worker calls return `bool` because `false` (ownership lost) is the expected, frequent case on the hot path. Programmer errors outside operator commands throw `ArgumentException`. Non-command state conflicts throw `StateConflictException`, which is no longer sealed. Idempotency-key and immutable-definition conflicts now throw `StateConflictException` instead of a plain `InvalidOperationException`; it derives from `InvalidOperationException`, so existing catches still apply. A stale processor registry raises `ProcessorRegistryChangedException`, also a `StateConflictException`; the worker treats it as fatal instead of retrying every poll.

The dashboard maps results to HTTP: success to 200 with the value, `Validation` to 400, `NotFound` to 404 and `Conflict` to 409, each with an `error` message. A mutation whose caller has no trusted subject claim is refused with 403 before the command runs.

Flow status, attempt status and archive kind are now enums (`FlowStatus`, `AttemptStatus`, `ArchiveKind`). Their member names are the stored values and they serialize as strings, so the database, archive bodies and stored batch results are unchanged. Renaming a member is a storage compatibility change. Event kinds, readiness reasons, schedule occurrence outcomes, retention incident codes and similar open vocabularies stay `string` so older readers tolerate values introduced by newer writers. Their known values are published as constants (`ExecutionEventKinds`, `ReadinessReasons`, and similar).

This is a source-breaking change to package APIs. It is intended to change no persisted schema, stored format or delivery guarantee. Mixed deployments with the previous release have not been tested and are not claimed.
