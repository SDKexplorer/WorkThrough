# WorkThroughWeb

An API that hands work to durable background jobs, runs them in the same process and shows them in the operations console.

```bash
dotnet run --environment Development
curl -X POST http://localhost:5000/signups -H 'Content-Type: application/json' -d '{"email":"ada@example.com"}'
```

Open `/jobs` for the console and `/health` for storage and worker health.

- **Add a job:** create a class that implements `IJob<TInput>` and give its input a `[JobContract("your.address")]`. `AddJobs` finds it at startup.
- **A small job:** `.AddJob(new JobDefinition<MyInput>("your.address"), job => ...)` registers a delegate instead of a class.
- **Tests:** `dotnet new workthrough-tests` creates xUnit tests that run flows with stubbed jobs.
- **Production:** the console is anonymous only in Development. Configure ASP.NET Core authentication and grant `DashboardPermissions` claims before deploying.
- **Storage:** set `ConnectionStrings:WorkThrough`. On SQL Server, enable `ALLOW_SNAPSHOT_ISOLATION`, and `READ_COMMITTED_SNAPSHOT` for higher throughput. On MySQL or MariaDB, name a dedicated database.

Delivery is at least once: make jobs idempotent with `context.IdempotencyKey`.
