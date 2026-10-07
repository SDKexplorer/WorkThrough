# Durable schedules

Use `JobClient.EnqueueAsync(processorId, input, new(NotBefore: instant))` for a one-time scheduled invocation. The time is an eligibility instant, not a promised start time under load.

For recurrence, add `.AddScheduler()` to any host using the store; it does not require `.AddWorker()`. Multiple schedulers may run against the same store. They use the provider write lock and database clock to commit the occurrence record, job, and next due instant in one transaction. The unique occurrence identity is schedule key, version, and UTC due instant. Processor identity stays independent of schedule identity and CLR location.

`IJobSchedules` supports editable drafts with revision checks, validated publication, and independently audited enable/pause commands. Only one version of a schedule key can be enabled at a time; old deployments cannot silently overwrite a published definition. Pause the active version before enabling its replacement. Jobs already created continue when a schedule is paused.

The initial trigger format is five-field cron with an explicit installed time zone. [Cronos](https://github.com/HangfireIO/Cronos) supplies calendar/DST calculations; daily non-interval triggers are not repeated during fall-back, while its documented interval behavior is retained. Use the same time-zone database across scheduler hosts. UTC is simplest when wall-clock business timing is unnecessary.

Overlap policies:

- `Allow`: create every selected occurrence even if previous jobs are queued/running.
- `SkipIfActive`: retain a skipped occurrence when prior work for the same schedule key is queued/running.
- `Coalesce`: retain one pending due occurrence until prior work ends, then emit once and calculate the next future time. Fair batch scanning keeps blocked schedules from permanently hiding other due schedules.

An occurrence over 30 seconds late is a misfire. `Skip` records it as skipped and advances; `Coalesce` emits one and advances past the missed interval; `CatchUp` walks forward one due occurrence per schedule per dispatch batch. Coalesced overlap takes precedence over catch-up. Dispatch batches are bounded to 100 schedules by the hosted scheduler; the storage API permits 1–500. Occurrence history identifies emitted/skipped work, not a manufactured successful handler attempt.

See `samples/Demo.Processor` for immediate, delayed, and hourly invocations of the same raw-string processor. Retrying a job does not create another occurrence. Schedule mutation does not undo prior effects. This version schedules individual processor invocations; atomic recurring flow starts and trigger calendars beyond cron remain future extensions.
