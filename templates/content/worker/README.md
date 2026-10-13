# WorkThroughWorker

A WorkThrough worker: it runs `DailyReportJob` every day at 06:00 UTC and any job enqueued to its database.

```bash
dotnet run
```

- **Add a job:** create a class that implements `IJob<TInput>` and give its input a `[JobContract("your.address")]`. `AddJobs` finds it at startup.
- **A small job:** `.AddJob(new JobDefinition<MyInput>("your.address"), job => ...)` registers a delegate instead of a class.
- **Tests:** `dotnet new workthrough-tests` creates xUnit tests that run flows with stubbed jobs.
- **Enqueue:** from any process that can reach the database, `await jobs.EnqueueAsync(new DailyReport(7))` with an injected `JobClient`.
- **Storage:** set `ConnectionStrings:WorkThrough`. On SQL Server, enable `ALLOW_SNAPSHOT_ISOLATION`, and `READ_COMMITTED_SNAPSHOT` for higher throughput. On MySQL or MariaDB, name a dedicated database.

Delivery is at least once: make jobs idempotent with `context.IdempotencyKey`.
