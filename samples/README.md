# Executable samples

All samples target net8.0. Repository builds use SDK 10; the verification script also compiles package consumers using SDK 8.

| Sample | Purpose |
|---|---|
| Demo.Processor | Typed `IProcessor<InvoiceInput>` with identity assigned at registration, named definitions/settings, immediate/delayed/cron jobs, progress and concurrency. `--once` smoke-tests execution. |
| Demo.Lifecycle | Business SQL plus outbox to a separate database, typed durable approval signal, persisted successor binding, and restart-safe receipt checks. Sends no real email. |
| Demo.Host | API + worker + scheduler + embedded Angular dashboard at `/operations/jobs`; development orders demonstrate success, branches, and recoverable failure. |
| Demo.Worker | Generic BackgroundService host with no HTTP listener; `--describe-worker` exports capabilities and `--preflight` checks an existing store without starting workers. |
| Demo.Dashboard | Standalone dashboard with no application DLL references; host OIDC/cookie authentication and server-controlled role permissions, with explicit anonymous Development fallback. |
| Demo.Contracts / Demo.Processors | Shared typed definitions, named step IDs and DTO property selectors, settings, and separate application implementations. |
| Refactor.Before / Refactor.MovedHandlers / Refactor.After | Separately published deployments: create typed, delayed string, and flow-step work, remove old assembly, and execute unchanged data through relocated implementations, then activate a deliberate address alias. |
| tests/PackageConsumer | Fresh local NuGet-only consumer for SDK/runtime 8 and 10, exercising generated typed handlers, typed DTO processors without message attributes, raw-string processors, and idempotent flow starts. |

The development sample uses a negative order amount to create an intentional failure; repeating that step without correcting its input will fail again and preserve both attempts. A real recovery requires fixing the underlying cause.

Use a common absolute SQLite path for separate local processes. Configure PostgreSQL for a multi-machine fleet. Only isolated Development hosts enable anonymous access. See `docs/dashboard-security.md` before configuring the standalone OIDC host; credentials and the real provider are supplied by the deployment.

Run `scripts/performance.sh --smoke` for independent worker processes, real-provider load checks, and crash recovery. Use `--full` for repeated measured runs; see [performance methodology](../docs/performance.md).
