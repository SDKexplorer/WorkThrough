# WorkThroughWorker

A WorkThrough worker: it runs `DailyReportJob` every day at 06:00 UTC and any job enqueued to its database.

```bash
dotnet run
```

- **Add a job:** create a class that implements `IJob<TInput>` and give its input a `[JobContract("your.address")]`. `AddJobs` finds it at startup.
- **Enqueue:** from any process that can reach the database, `await jobs.EnqueueAsync(new DailyReport(7))` with an injected `JobClient`.
- **Storage:** set `ConnectionStrings:WorkThrough`. On SQL Server, enable `ALLOW_SNAPSHOT_ISOLATION`, and `READ_COMMITTED_SNAPSHOT` for higher throughput.

Delivery is at least once: make jobs idempotent with `context.IdempotencyKey`.
