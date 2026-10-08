# Performance and failure exercises

Run `scripts/performance.sh --smoke` for the CI profile or `scripts/performance.sh --full` for repeated measurements. Set `DURABLEJOBS_POSTGRES` to a disposable database to exercise PostgreSQL. `WORKTHROUGH_DOTNET8` selects the actual runtime executable. Never point the harness at production; it creates isolated schemas/files and deletes its own test data afterward.

## What is measured

The harness starts two separate OS processes running actual `BackgroundService` workers. Four concurrent producers submit a finite burst after warmup. Each worker has four local slots, the fleet has six admitted leases, and the limited processor type has a shared limit of two. Load workers use 30-second leases; the crash proof uses two seconds for bounded recovery. Processors perform a cancellable 10 ms delay and return a value checked against their accepted input.

Three workloads are measured:

- Mixed standalone jobs, half targeting the constrained processor type.
- Persisted two-step flows, including predecessor settlement and successor activation.
- Business-store outbox acceptance, delivery into a separate job store, and processor settlement.

The full profile uses 100 warmup jobs and three fresh-database repetitions per workload, with 500 measured operations per repetition (1,000 jobs for flows). The smoke profile uses 12 warmup jobs, 60 operations, and one repetition. No results are fabricated or derived from in-memory storage.

JSON reports in `artifacts/performance` include database version, runtime, OS/architecture, logical processors, Git revision/dirty state, exact workload parameters, committed-acceptance throughput, end-to-end job throughput, p50/p95/p99 and min/max acceptance/queue/end-to-end/attempt latencies, worker and harness CPU, summed worker peak RSS, idle worker CPU, and observed peak global/type attempt intervals. Durations use monotonic local clocks for API calls and database timestamps for durable lifecycle intervals.

Each run must settle exactly the expected jobs with matching outputs and one successful attempt per job, and must stay within fleet/type admission limits. Failure ends the campaign rather than reporting a misleading successful throughput. Reported peaks use millisecond database timestamps with completion ordered before admission at equal timestamps; provider conformance tests separately race atomic admission. This is evidence about valid leases, not a claim that a partitioned process physically stops.

## Failure campaign

Before load measurement for each provider, kill a worker after an independently committed business effect and before job settlement. A replacement worker must recover the lease, preserve an Abandoned attempt followed by Succeeded, and leave exactly one idempotent effect. SQLite also restores a consistent backup of that crashed store into another file before resumption.

Integration tests additionally lose the destination response after a real outbox commit, terminate an owned PostgreSQL backend/close a SQLite transaction connection, verify rollback and subsequent recovery, retain whole-flow/signal archive evidence and deduplication, and exercise subscription cursor/trim races. CI runs both providers and publishes its performance smoke JSON as an artifact.

## Interpretation and limits

These are finite-burst engine measurements, not an open-loop HTTP service SLA or a Hangfire/Wolverine comparison. Queue wait includes the burst backlog; flow successors also include predecessor time. Outbox acceptance measures the source commit, and end-to-end includes delivery. Throughput includes producer time, dispatch, drain, and up to 50 ms completion polling. Small smoke samples have weak tail estimates; use full reports for comparison.

SQLite uses the provider's WAL journal with FULL synchronous durability and local disk, while PostgreSQL runs in the configured database (locally, a Docker instance). Record storage and hardware context alongside reports. A local workstation has other activity; compare repeated runs on a dedicated runner with the same database durability settings and retained-history size before setting performance budgets. CPU percentage uses one core as 100%; summed peak RSS values are per-process lifetime peaks, not synchronized fleet resident memory.

No fixed throughput threshold is imposed on shared CI hardware. CI gates correctness and bounded completion, and retains measurements for trend analysis. A production readiness campaign still needs realistic payload sizes/business work, sustained open-loop arrival rates, large retained histories, long-running memory/connection tracking, PostgreSQL restore/PITR, multi-machine partitions, and deployment-specific latency/error budgets. Conservative serialized admission remains deliberate until those measurements justify a proven change.

## Exploratory failures retained

The first local rollback-journal run completed 500 mixed jobs at 18.2 jobs/s and 1,000 flow jobs at 5.0 jobs/s, then was deliberately stopped for investigation. A subsequent admission-query revision still encountered abandoned attempts under the harness's two-second crash-test lease. That campaign failed its correctness gate and is not a successful benchmark. Logs remain in `artifacts/performance-exploratory-aborted.log` and `artifacts/performance-short-lease-failed.log`. The final campaign uses normal load leases and the SQLite WAL/FULL provider configuration; do not treat these differently configured exploratory runs as a controlled speedup comparison.

## Local campaign — 2026-10-08

macOS ARM64, .NET 8.0.31, two worker processes; PostgreSQL 17 in Docker. Each provider completed 6,000 measured jobs across nine runs, plus warmup and its crash proof. Every measured job had exactly one successful attempt and the expected output. These local results are not a production SLA.

| Provider | Workload | Jobs/s median (range) | Run p95 end-to-end, median |
|---|---|---:|---:|
| sqlite | mixed | 56.9 (31.9–81.4) | 5.54 s |
| sqlite | flows | 14.0 (13.6–14.6) | 65.14 s |
| sqlite | outbox | 71.9 (70.2–80.3) | 6.20 s |
| postgres | mixed | 85.9 (83.6–85.9) | 3.78 s |
| postgres | flows | 62.5 (61.5–62.8) | 10.71 s |
| postgres | outbox | 52.2 (51.5–52.5) | 8.72 s |

Flow throughput counts completed steps; each measured flow contains two steps. End-to-end latency includes queue backlog from the burst. PostgreSQL showed more consistent results; SQLite was substantially slower for flows and more variable for mixed jobs. Retain this limitation when planning deployment capacity.

Raw reports: [SQLite](performance-results/2026-10-08-sqlite.json), [PostgreSQL](performance-results/2026-10-08-postgres.json). They record the pre-commit Git base and a modified working tree because measurements were run before committing the implementation. Later changes were formatting, archive read-method extraction, sample/docs, reserved-address validation, and administrative audit labels; the measured claim/settlement path is unchanged.
