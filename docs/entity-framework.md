# EF Core

`WorkThrough.EntityFrameworkCore` commits jobs with `SaveChangesAsync`: an order row and the job that confirms it are saved together, or neither is. The jobs are written in the DbContext's own transaction, on its own connection, so the job tables must live in the same database as the application's.

```csharp
builder.Services.AddWorkThrough().UsePostgreSql(connectionString).AddWorker();
builder.Services.AddDbContext<ShopDb>((services, options) => options
    .UseNpgsql(connectionString)
    .UseWorkThrough((RelationalJobStore)services.GetRequiredService<IJobStore>()));

// In a request handler:
db.Orders.Add(order);
var confirm = db.Enqueue(Orders.Confirm, new ConfirmOrder(order.Id));
await db.SaveChangesAsync();
// confirm.JobId now holds the accepted job's ID.
```

- **Without a transaction of yours,** SaveChanges opens one, writes the changes and the jobs, and commits.
- **Inside your transaction,** the jobs are written with the changes, and become visible when you commit. Rolling back removes both.
- **A failed save** writes nothing; the jobs stay recorded on the context, so a retried save includes them.
- **Defaults:** `Enqueue` takes the definition's queue and attempt budget. Pass `EnqueueOptions` for an idempotency key, delay, tenant or partition; a repeated idempotency key returns the existing job's ID.
- **Another store:** `db.Send(...)` saves an outbox request instead, for a job store in another database ([operations](operations-and-retention.md#business-writes-and-separate-job-databases)).
- **Asynchronous only:** with jobs recorded, the synchronous `SaveChanges` throws.
- **Locking:** the transaction takes the store's admission lock when the jobs are written, so keep it short.
- **Retrying execution strategies** cannot start their own transactions around a save. Run the whole unit in `strategy.ExecuteAsync` and begin the transaction yourself.

Verified with EF Core 8 on PostgreSQL (Npgsql), SQL Server, Oracle and MySQL/MariaDB (Pomelo). The package targets EF Core 8 and runs on later versions. `WorkThrough.Storage.PostgreSql` requires Npgsql 8.0.9 or later, so applications on EF Core 8 and its Npgsql 8 provider can use it.
