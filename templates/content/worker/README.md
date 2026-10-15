# WorkThroughWorker

A WorkThrough worker: it runs `DailyReportJob` every day at 06:00 UTC and any job enqueued to its database.

```bash
dotnet run
```

- **Add a job:** give it an address (a `JobDefinition`, like `Reports.Daily`) and a class implementing `IJob<TInput>`, then set it up in one place in `Program.cs`: `.Job(definition, job => job.RunWith<MyJob>())`. Add `.Every(cron, input)` for a schedule and `.AtMostRunning(n)` for a limit.
- **A small job:** `.Job(definition, job => job.Run(call => ...))` runs a delegate instead of a class.
- **Tests:** `dotnet new workthrough-tests` creates xUnit tests that run flows with stubbed jobs.
- **Enqueue:** from any process that can reach the database, `await jobs.Enqueue(Reports.Daily, new DailyReport(7))` with an injected `JobClient`; add `.After(...)`, `.Idempotent(...)` or `.ForTenant(...)` as needed.
- **Storage:** set `ConnectionStrings:WorkThrough`. On SQL Server, enable `ALLOW_SNAPSHOT_ISOLATION`, and `READ_COMMITTED_SNAPSHOT` for higher throughput. On MySQL or MariaDB, name a dedicated database.

Delivery is at least once: make jobs idempotent with `context.IdempotencyKey`.
