# Samples

| Sample | Shows |
|---|---|
| [QuickStart](QuickStart/Program.cs) | The smallest program: one job, SQLite, an in-process worker. It runs the job and exits. |
| [QuickStart.Web](QuickStart.Web/Program.cs) | An API that enqueues jobs, with idempotent requests, a delayed job, the worker and the console in one process. |
| [HangfireMigration](HangfireMigration/Program.cs) | Moves a Hangfire application's queued, delayed and recurring jobs into WorkThrough with the importer, then retires them in Hangfire. |
| [QuartzMigration](QuartzMigration/Program.cs) | Moves Quartz.NET jobs with a holiday-calendar cron trigger and an interval trigger into WorkThrough schedules, then unschedules them in Quartz. |
| [Shop](#shop-sample) | A system split into deployable hosts, with a flow, tenants, limits, schedules, an OIDC console and MCP. |

```bash
dotnet run --project samples/QuickStart
dotnet run --project samples/HangfireMigration
dotnet run --project samples/QuartzMigration
ASPNETCORE_ENVIRONMENT=Development dotnet run --project samples/QuickStart.Web --urls http://127.0.0.1:5296
curl -X POST http://127.0.0.1:5296/signups -H 'Content-Type: application/json' -d '{"email":"ada@example.com"}'
```

Both quick starts register their jobs with `AddJobs(assembly)`, which finds every `IJob<T>` class whose input or class declares an address. Open <http://127.0.0.1:5296/jobs> to see the web sample's jobs. Repeating the same request returns the same job IDs, because each enqueue carries an idempotency key.

## Shop sample

One small shop, deployed the way a real system would be: shared definitions, jobs in their own project, an API that also runs work, a headless worker, a standalone operations console and an MCP endpoint. Every host points at the same store.

| Project | Role |
|---|---|
| `Shop.Shared` | Typed job addresses (`ShopJobs`) with their queues, the checkout flow, settings, storage choice, fleet-wide policies and the tenant-claim resolver. Producers need only this project. |
| `Shop.Jobs` | The `IJob<T>` implementations and `AddShopJobs`, which sets up each job in one `Job(...)` block: its class, limits and schedule. |
| `Shop.Api` | Order intake and review approval, a worker and scheduler, and the embedded dashboard. `--smoke` runs an end-to-end check. |
| `Shop.Worker` | Headless worker for scaling out. `--describe-worker` prints its capabilities; `--preflight` checks that it covers the stored work. |
| `Shop.Dashboard` | Standalone console with OIDC sign-in, role-based permissions and tenant-scoped users. References no job code. |
| `Shop.Mcp` | Read-only MCP endpoint for AI assistants, with bearer authentication and the same tenant scoping. |
| `Shop.Tests` | The checkout flow tested on the real engine with `WorkThrough.Testing`, with real jobs and with stubbed jobs. |

## Run it

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project samples/Shop.Api --urls http://127.0.0.1:5295
```

Open <http://127.0.0.1:5295/operations>. Place orders:

```bash
curl -X POST http://127.0.0.1:5295/orders -H 'Content-Type: application/json' -d '{"tenantId":"acme","customer":"ada","amount":40}'
curl -X POST http://127.0.0.1:5295/orders -H 'Content-Type: application/json' -d '{"tenantId":"acme","customer":"grace","amount":900}'
curl -X POST http://127.0.0.1:5295/orders -H 'Content-Type: application/json' -d '{"tenantId":"acme","customer":"alan","amount":-5}'
```

- The first order runs reserve → charge → ship → notify.
- The second needs manual review: the flow waits, holding no worker slot, until you approve it with `curl -X POST http://127.0.0.1:5295/orders/<orderId>/approve`.
- The third is rejected by the payment step without spending retries. The flow becomes an incident: the remaining steps wait, and the console shows the reason and the recovery actions.

Each order row and its "start checkout" job commit in one database transaction (`Orders.PlaceAsync`). That job starts the flow with an idempotency key, so a retry never starts a second checkout.

Scale out by starting workers against the same store. Separate local processes need the same absolute SQLite path; use PostgreSQL for several machines:

```bash
Shop__Sqlite="Data Source=$PWD/shop.db" dotnet run --project samples/Shop.Worker
Shop__PostgreSql="Host=...;Database=shop" dotnet run --project samples/Shop.Worker
```

## What to look at

- **Stable addresses** (`ShopJobs`, `AddShopJobs`): stored work names `shop.payment.charge` v1, never a CLR type. Move or rename a job class and existing work still runs.
- **Flow** (`CheckoutFlow`): steps pass outputs to inputs by property name. The review step runs only when the payment output says so, and shipping waits for it only then.
- **Policies**, created once and afterwards owned by operators in the console. Fleet-wide ones are in `ShopSetup.SeedShopPolicies`; each job's own sit with it in `AddShopJobs`:
  - at most 12 running attempts fleet-wide and 2 shipments at a time;
  - 4 slots per tenant, so one busy tenant cannot starve the others;
  - one payment at a time per customer;
  - 120 payment starts per tenant per minute;
  - a daily report at 06:00 UTC.
- **Tenants:** orders carry their tenant; partition keys are namespaced with it. Users whose identity token has `tenant` claims see only those tenants in `Shop.Dashboard` and `Shop.Mcp` (`TenantClaimResolver`).
- **Effects:** jobs receive `context.IdempotencyKey`, stable across retries; pass it to payment and shipping providers. Delivery is at least once.

## Configuration

Settings bind from the `Shop` section, with environment variables as `Shop__Name`:

- **Storage:** `Sqlite` (default `Data Source=shop.db`) or `PostgreSql`.
- **Capacity:** `WorkerSlots`, `FleetSlots`, `ShippingSlots`, `SlotsPerTenant`.
- **Rate:** `PaymentsPerTenantPerMinute`.
- **Report schedule:** `DailyReportCron`, `DailyReportTimeZone`.

`Shop.Dashboard` reads `Identity:Authority`, `ClientId` and `ClientSecret` from host configuration or a secret store; without an authority it runs anonymously only in Development. Its role-to-permission map is in `appsettings.json`; see [dashboard security](../docs/dashboard-security.md). `Shop.Mcp` needs `Mcp:Authority`, `Mcp:Resource` and `Mcp:Environment` and has no anonymous mode; see [MCP](../docs/mcp.md). Both use an already initialized store.

SQL Server, Oracle, MySQL and MariaDB work the same way through `UseSqlServer`, `UseOracle` and `UseMySql`; see [providers](../docs/providers.md). The sample keeps to SQLite and PostgreSQL so that its order table DDL stays portable.

## Verification

`scripts/verify.sh` runs `Shop.Api --smoke`: two orders in different tenants, one requiring review approval, both completing. It then runs `Shop.Worker --preflight` against the resulting store. The relocation proof (work created by one build, executed after its job classes moved to another assembly) lives in `tests/Relocation` and runs from `scripts/check-refactor.sh`.
