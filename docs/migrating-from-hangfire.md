# Migrating from Hangfire

Move gradually: new work goes to WorkThrough while Hangfire drains, and an importer moves Hangfire's waiting and recurring jobs across in one step when you are ready.

## Concepts

| Hangfire | WorkThrough |
|---|---|
| `BackgroundJob.Enqueue<IMailer>(m => m.Send(id))` | `await jobs.Enqueue(Emails.Send, new SendEmail(id))` |
| `BackgroundJob.Schedule(..., TimeSpan.FromHours(1))` | `await jobs.Enqueue(...).After(TimeSpan.FromHours(1))` |
| `RecurringJob.AddOrUpdate("daily", ..., Cron.Daily())` | `.Job(Reports.Daily, job => job.RunWith<DailyReportJob>().Every("0 0 * * *", input))` |
| `ContinueJobWith` | A flow step `.After("previous")` |
| Batches (Pro) | A flow with parallel steps or `ForEach` |
| `[AutomaticRetry(Attempts = 5)]` | `definition.WithMaxAttempts(5)`, or `UseRetryPolicy<T>()` |
| `[Queue("critical")]` | `definition.InQueue("critical")`, with queue weights |
| `[DisableConcurrentExecution]` | `job.AtMostRunning(1)`, or `.AtMostPerPartition(1)` with a partition key |
| Throttling (Pro) | `AtMostRunning`, `AtMostPerTenant` and `RateLimit` on the job |
| `IBackgroundJobClient` in a transaction | `InTransactionAsync`, or the outbox |
| Dashboard | The WorkThrough console, with audited actions and permissions |

The main difference: Hangfire stores the method to call, so renaming or moving it breaks queued jobs. WorkThrough stores an address such as `emails.send` v1 and an input DTO. Give each job a [`JobDefinition`](../README.md#quick-start) once; the class behind it can then move freely.

## 1. Write the WorkThrough jobs

For each Hangfire method, add an input record and a job, usually calling the same service:

```csharp
public sealed record SendEmail(string UserId);

public static class Emails
{
    public static JobDefinition<SendEmail> Send { get; } = new("emails.send");
}

builder.Services.AddWorkThrough()
    .UseSqlServer(connectionString)
    .Job(Emails.Send, job => job.Run(call =>
    {
        call.Service<IMailer>().Send(call.Input.UserId);

        return Task.CompletedTask;
    }))
    .AddWorker();
```

Switch producers to `JobClient` as you go. Both systems can run side by side: Hangfire keeps executing what it already holds.

## 2. Plan the import

Add `WorkThrough.Migration.Hangfire` to a host where your Hangfire job types still load, usually the old application, and map each method:

```csharp
var migration = new HangfireMigration(JobStorage.Current)
    .Map((IMailer mailer) => mailer.Send(default!), Emails.Send, call => new SendEmail(call.Argument<string>(0)))
    .Map((ReportService reports) => reports.Daily(default), Reports.Daily, call => new DailyReport(call.Argument<int>(0)));

var plan = migration.Plan();
foreach (var problem in plan.Problems)
{
    Console.WriteLine($"{problem.Kind} {problem.HangfireId} {problem.Method}: {problem.Problem}");
}
```

`Plan()` reads Hangfire through its storage API and changes nothing. It covers recurring jobs, enqueued jobs and scheduled jobs. Each item says what it becomes, or why it cannot move:
- a method without a mapping;
- a type that no longer loads;
- a converter that failed;
- two recurring jobs whose names collide.

It also reports running Hangfire servers, and counts processing and failed jobs, which are not imported.

## 3. Stop Hangfire and import

Stop every Hangfire server so no job runs in both systems; `ImportAsync` refuses while servers are registered. Then plan again and import:

```csharp
var result = await migration.ImportAsync(migration.Plan(), store, store);
```

- Enqueued and scheduled jobs keep their queue and due time. Each uses the idempotency key `hangfire:<job id>`, so running the import again changes nothing.
- Recurring jobs become published, enabled schedules named `hangfire.<id>` (lowercased, other characters replaced by `-`), in their original time zone. Six-field crons keep their seconds, and need engine protocol 9; a seconds field of 0 is dropped. `NameSchedules(id => ...)` picks other names.
- Failed, processing, succeeded and deleted jobs stay in Hangfire.

## 4. Retire the imported jobs in Hangfire

Check the imported work in the WorkThrough console, then:

```csharp
migration.Retire(result);
```

This deletes the imported jobs in Hangfire and removes the imported recurring jobs, so restarting a Hangfire server cannot run them again. Skipped items stay, for you to move by hand or let finish in Hangfire.

## Limits

- Continuations, batches and job filters are not imported; rebuild them as flows and WorkThrough middleware.
- Converters see Hangfire's deserialized arguments. Arguments that need the old types must be converted where those types load.
- `WorkThrough.Migration.Hangfire` depends on Hangfire.Core, which is LGPL-3.0. Use it only for the migration; your application does not need it afterwards.
