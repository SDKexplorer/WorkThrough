# WorkThrough compared with other .NET options

This compares design choices, not marketing claims. It reflects public documentation as of October 2026; check each project's current documentation and license before deciding. WorkThrough is an unpublished alpha. Every alternative below is more mature and has production users.

## At a glance

| | WorkThrough | Hangfire | Quartz.NET | Wolverine | MassTransit | Temporal | Durable Functions |
|---|---|---|---|---|---|---|---|
| What it is | Durable jobs, flows and schedules in your database | Background jobs in your database | Scheduler | Messaging with a durable inbox/outbox | Message bus over brokers | Durable execution platform | Durable orchestrations on Azure Functions |
| Infrastructure | SQLite, PostgreSQL, SQL Server, Oracle, MySQL or MariaDB | SQL Server or Redis, plus community storages | Memory or ADO.NET databases | PostgreSQL, SQL Server, EF Core, plus brokers | A broker (RabbitMQ, Azure Service Bus, SQS…) | Temporal cluster or Temporal Cloud | Azure Storage, MSSQL or a managed scheduler |
| Stored work names | A versioned address (`invoices.generate` v1) | The CLR type, method and arguments | The job's CLR type | The message type name (an alias can be configured) | The message type | Workflow and activity type names | Function names |
| Survives moving or renaming code | Yes, by design and tested; input upcasts for changed contracts | No: queued jobs break | No: the type name changes | Partly, through aliases | Partly, through message names | Yes, if names are kept | Yes, if names are kept |
| Workflows | Persisted graphs: bindings, conditions, joins, fan-out, child flows, loops, failure branches, compensation, signals, incidents | Continuations; batches in Pro | No | Sagas | Sagas and routing slips | Code-first workflows, deterministic replay | Code-first orchestrations, replay |
| Concurrency and tenant controls | Fleet, per-type, per-key, per-tenant and rate limits; weighted queues | Per-server workers; throttling in Pro | Thread pool and misfire rules | Per-endpoint parallelism | Concurrency and rate limits per endpoint | Task-queue rate limits | Per-function concurrency |
| Throughput | About 300–800 jobs/s per store; ~500/s with per-tenant quotas | High | High | Very high | Very high | Very high | High |
| Dashboard | Built in, audited actions, tenant-scoped | Built in, mature | Third-party | Commercial add-on | No | Built in | Azure portal and tools |
| AI assistant access | Read-only MCP endpoint | No | No | No | No | No | No |
| License | Not chosen yet | LGPL-3.0 core; Pro is paid | Apache-2.0 | MIT | Apache-2.0 up to v8; v9 is commercial | MIT server; Temporal Cloud paid | MIT |
| Maturity | Alpha, unpublished | Over 10 years | Over 15 years | Mature | Mature | Mature | Mature |

## When something else is the better choice

- **Hangfire:** you want background jobs today with the least ceremony (`BackgroundJob.Enqueue(() => Send(id))`), a huge user base and a mature dashboard, and your code rarely moves. Plan for keeping old method signatures alive while jobs drain.
- **Quartz.NET:** scheduling is the problem: rich triggers, calendars and clustering, with little need for queues or workflows.
- **Wolverine:** you are building a messaging application, perhaps with Marten. You want handlers by convention, a transactional outbox and broker transports in one framework.
- **MassTransit:** you already run a broker and need publish/subscribe, routing and sagas across services at high volume. Check its current licensing for new projects.
- **Temporal:** long-running orchestration with real logic (loops, branching on data, child workflows, timers spanning months), written as ordinary code, at large scale. You accept running or paying for the Temporal service and its determinism rules.
- **Durable Functions:** you are on Azure Functions and want code-first orchestrations without operating infrastructure.

## When WorkThrough fits

- **You refactor often.** Queued jobs, schedules and flow steps keep running after their classes move between namespaces, projects or assemblies. Hangfire and Quartz.NET store CLR names, so they cannot offer this without care.
- **You run multi-tenant SaaS on your own database.** Per-tenant quotas, per-customer serialization, rate limits and fair queues are built in and exact across hosts. Operators can also be limited to their own tenants in the dashboard and MCP.
- **You need evidence for operators and auditors.**
  - Every administrative action is attributed, carries a reason, is audited and is deduplicated.
  - Retention supports holds and verified exports.
  - Payloads sit behind a separate permission.
  - AI assistants can investigate read-only through MCP.
- **You want explicit guarantees.**
  - Delivery is at least once.
  - Stale workers can never settle an attempt.
  - Business data and the jobs it causes can commit in one transaction.
  - Each of these is tested against all six databases, process kills and soaks.
- **Your flows are fixed business processes:** checkout, onboarding, approvals, document pipelines. They are defined as data, so they are inspectable and versioned, with operator recovery for failed steps.

## Compared with Quartz.NET in detail

Quartz.NET is a scheduler; WorkThrough is a job and workflow engine with a scheduler. Both run clustered on a shared database.

| | WorkThrough | Quartz.NET |
|---|---|---|
| Triggers | Cron with optional seconds, or fixed intervals, in an IANA or Windows time zone, with start and end bounds | Cron with seconds, simple intervals, daily and calendar intervals |
| Exclusions | Excluded dates per schedule (holidays) | Shared calendars (holidays, weekly, cron calendars) |
| Missed runs | Skip, coalesce or catch up; skip or coalesce overlapping runs | Misfire instructions per trigger; `[DisallowConcurrentExecution]` |
| Retries | Per-job attempts with backoff, rejection and deferral | None built in; the job reschedules itself |
| Queues and limits | Weighted queues; fleet, type, key, tenant and rate limits | Thread pool size and trigger priority |
| Ad hoc work | First-class: enqueue now or later, idempotently | A one-off trigger per job |
| Workflows | Persisted flows with recovery | Job chaining through listeners |
| Stored identity | A versioned address | The job's CLR type name |
| Operations | Built-in audited console, history, retention | Third-party dashboards |

Quartz.NET still has more trigger kinds: shared calendars, daily time windows and wall-clock calendar intervals. Choose WorkThrough when scheduled work is part of a larger set of jobs and flows that need retries, limits, history and safe refactoring; [the importer](migrating-from-quartz.md) moves existing Quartz jobs and triggers.

## Trade-offs to weigh

- **Throughput:** completions and claims use row locks, but every event still takes one counter row so the journal stays gap-free. On the documented test host that gives about 800 jobs/s per PostgreSQL store, and about 500 with 50 tenant quotas ([ADR-020](adr/0020-write-path-throughput.md), [ADR-022](adr/0022-limit-locks.md)). A fleet-wide limit serializes claims. That is ample for business workflows; use a broker for event streams.
- **Flow model:** flows are graphs, not code. Data-driven fan-out, child flows and bounded loops of child flows are built in. Arbitrary code-level control flow is not; Temporal and Durable Functions express it naturally.
- **Upgrades:** from schema 6, additive schema changes and new engine protocol levels roll out while workers run ([ADR-021](adr/0021-rolling-upgrades.md)). Upgrading a store from schema 5 or earlier still requires stopping its workers once.
- **Availability:** packages are not published and the license is undecided. Neither is true of the alternatives.
