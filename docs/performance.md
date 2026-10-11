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

SQLite uses the provider's WAL journal with FULL synchronous durability and local disk, while PostgreSQL runs in the configured database (locally, a Docker instance). Record storage and hardware context alongside reports. A local workstation has other activity; compare repeated runs on a dedicated runner with the same database durability settings and retained-history size before setting performance budgets. CPU percentage uses one core as 100%; summed peak RSS values are per-process lifetime peaks, not synchronized fleet resident memory. An OS that does not supply a worker RSS/peak returns null in current reports. Older zero peak values mean unavailable, not zero memory consumption.

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

## Investigative contention

`scripts/investigation.sh --smoke` runs a short correctness check on SQLite and each configured server provider. An optional second argument selects one provider; a missing connection for an explicitly selected server is an error. Use only disposable databases. The script creates isolated stores, starts two real worker processes and hosts the actual MCP endpoint on a temporary loopback port. The official C# MCP client uses Streamable HTTP and a separate signed subject for each investigator.

Each repetition compares a fresh store without investigators against a fresh store with 20 clients. Both stores begin with the same synthetic terminal fixture: one to three attempts per job, 20% terminal failures and 16 correlation groups. These inserted rows represent retained history, not 100,000 jobs delivered by the engine. Metrics queries explicitly include the entire retained attempt history and assert its count; searches must find fixture records. This prevents measuring an accidentally empty time window.

Four producers submit batches of 24 real jobs while two processes execute them under a shared limit of six. Every live job must succeed once with the expected output. Completion polling and per-job attempt verification are included in measured job throughput. This is a closed-loop diagnostic workload, not a peak engine throughput or open-loop arrival-rate test. Baseline and investigation use identical producer/verification logic.

Clients rotate through execution search, lookup, attempts, metrics, capacity and retention status, pausing 250 ms after each response. The qualification host explicitly permits 32 concurrent HTTP requests and 10,000 requests/minute per subject, so this measures query contention without intentionally throttling the clients; these are not the product defaults. They have no payload permission; a secret marker in fixture input/errors must never appear in any result. Failures, redaction violations, incorrect outputs or duplicate attempts fail the run. Other tools, denied credentials, cancellation, throttling and fail-closed sinks are covered by the integration suite; this workload does not replace those tests.

Profiles:

| Profile | Retained jobs per store | Baseline | 20-client phase | Repetitions |
|---|---:|---:|---:|---:|
| `--smoke` | 1,000 | 5 s | 10 s | 1 |
| `--full` | 100,000 | 60 s | 120 s | 3 |
| `--soak` | 100,000 | 120 s | 2 h | 1 |

Reports under `artifacts/performance/*-investigation-*.json` record provider/runtime/revision, exact workload, worker/harness CPU, memory/allocation/GC samples, response sizes, request errors, redaction violations and latency distributions. Allocations cover the whole harness process, including producer verification and the MCP host/client; they are not isolated server allocations. Histograms use fixed memory with 1 ms buckets through 10 seconds; overflow count and exact maximum are explicit. A capped quantile with overflow is not evidence that the latency was below 10 seconds. Negative/non-finite durations fail the run. Each phase also reports acceptance, queue and end-to-end latency. For this immediate, single-attempt workload, queue wait is attempt start minus stored creation time; the mutable AvailableAt field is not a historical queue timestamp. A negative throughput-degradation percentage indicates run variation or higher observed throughput; it does not prove that MCP makes workers faster.

This host uses locally signed JWTs and an in-memory access sink. It does not qualify external IdP discovery, token refresh, a desktop AI application, external audit durability, automatic retention or database restoration. Execute the soak and inspect memory/connection growth and queue behavior on the intended deployment before marking those gates complete. Shared workstation/CI runs do not enforce the proposed latency/degradation budgets as a production SLA.

## Initial retained-history campaign — 2026-10-10

Each comparison used 100,000 synthetic retained jobs (200,000 attempts) per store, a 30-second baseline and a 60-second investigative phase. PostgreSQL was repeated three times with fresh stores; the other providers have one corrected comparison each. On macOS ARM64/.NET 8.0.31, SQLite ran locally; PostgreSQL 17.11 and Oracle Free 26ai ran in Docker, and SQL Server 2022 CU27 ran under x64 emulation. Server providers ran sequentially; Oracle and SQL Server were not running together. These shorter repetitions are distinct from the longer `--full` profile, and SQL Server's result is not a native capacity claim.

| Provider / repetition | Baseline jobs/s | 20-client jobs/s | Throughput degradation | Jobs under investigation | MCP requests | Largest query p95 / p99 (ms) | Queue p95 under load (ms) |
|---|---:|---:|---:|---:|---:|---:|---:|
| sqlite | 29.2 | 32.2 | -10.5% | 1,944 | 4,626 | 73 / 76 | 271 |
| postgres 1 | 85.7 | 82.9 | 3.3% | 4,992 | 4,722 | 25 / 30 | 160 |
| postgres 2 | 87.5 | 77.3 | 11.7% | 4,656 | 4,705 | 28 / 38 | 171 |
| postgres 3 | 84.8 | 76.4 | 9.9% | 4,608 | 4,700 | 27 / 31 | 173 |
| sqlserver | 44.3 | 36.9 | 16.7% | 2,232 | 4,630 | 59 / 73 | 330 |
| oracle | 62.7 | 55.9 | 10.8% | 3,360 | 4,595 | 47 / 56 | 198 |

All 33,720 live jobs across the six baseline/investigative comparisons completed once with matching outputs. All 27,978 measured requests succeeded, with zero fixture-secret leaks. Query latency met the proposed 500 ms p95 / 2 s p99 targets. The proposed maximum 10% throughput-degradation target was **not met consistently**: one PostgreSQL repetition, Oracle and emulated SQL Server exceeded it. PostgreSQL varied from 3.3% to 11.7%, and SQLite's negative result also shows run variability. These observations require investigation on controlled hardware; they do not establish a stable performance budget or complete production qualification. Archive maintenance was disabled.

Raw version-2 reports: [SQLite](performance-results/2026-10-10-sqlite-investigation.json), [PostgreSQL 1](performance-results/2026-10-10-postgres-investigation.json), [PostgreSQL 2](performance-results/2026-10-10-postgres-repeat-2-investigation.json), [PostgreSQL 3](performance-results/2026-10-10-postgres-repeat-3-investigation.json), [SQL Server](performance-results/2026-10-10-sqlserver-investigation.json), [Oracle](performance-results/2026-10-10-oracle-investigation.json). Reports retain the pre-commit Git base plus modified working-tree status. Later changes only retained these files and updated documentation.

Two exploratory measurement defects were found and corrected. First, a default metrics window excluded the older fixture; current runs assert complete fixture coverage before and during measurement. Second, version-1 queue measurements subtracted the settled AvailableAt timestamp, which changes during settlement. Current reports use original storage acceptance time for these immediate, single-attempt jobs and reject negative/non-finite measurements. Version-1 reports are excluded from the table and retained under `artifacts/performance-exploratory/invalid-queue-metric-v1`; the current committed reports replace them. Resource counters unavailable from the OS are null, not zero. Six regression cases exercise rejection and explicit histogram overflow on both runtimes.

The longer full profile, repetitions on the other providers, SQL-plan analysis, a two-hour soak, connection-growth tracking, maintenance contention and production restore/partition campaigns remain uncompleted qualification work. Harness allocations and GC samples are recorded, but they include both clients and producers and do not isolate server allocation costs.

## Claim-path baseline — 2026-10-10

Baseline for the execution-control work (partition concurrency, rate limits, fairness), measured before any of it. Full profile: per provider, a crash proof plus three fresh-store repetitions of each workload; 6,000 measured jobs per provider, 24,000 in total. Revision `808e79c` with a clean working tree, .NET 8.0.31, macOS ARM64, 16 logical processors. SQLite ran locally with WAL/FULL. PostgreSQL 17.11 and Oracle Free 26ai (23.26.3) ran natively in Docker; SQL Server 2022 CU27 ran in Docker under x64 emulation, so its figures are not a native capacity claim. Providers ran one at a time. Unrelated containers were running on the same Docker host; treat small differences as noise.

| Provider | Workload | Jobs/s median (range) | Queue wait p50, median | Run p95 end-to-end, median |
|---|---|---:|---:|---:|
| sqlite | mixed | 34.3 (25.1–46.1) | 2.70 s | 11.43 s |
| sqlite | flows | 14.4 (13.8–14.7) | 5.30 s | 62.70 s |
| sqlite | outbox | 84.6 (76.9–91.7) | 2.63 s | 5.33 s |
| postgres | mixed | 80.6 (79.7–82.0) | 1.62 s | 3.88 s |
| postgres | flows | 58.7 (56.4–59.1) | 5.04 s | 11.58 s |
| postgres | outbox | 48.0 (48.0–48.8) | 5.15 s | 9.50 s |
| sqlserver (emulated) | mixed | 36.7 (35.9–37.0) | 4.07 s | 8.20 s |
| sqlserver (emulated) | flows | 30.9 (29.8–31.1) | 9.83 s | 21.59 s |
| sqlserver (emulated) | outbox | 36.0 (30.6–36.6) | 7.00 s | 12.65 s |
| oracle | mixed | 64.8 (64.2–65.6) | 1.95 s | 4.57 s |
| oracle | flows | 46.9 (38.5–47.4) | 6.13 s | 14.37 s |
| oracle | outbox | 52.5 (30.1–55.0) | 4.38 s | 8.09 s |

Every measured job settled once with the expected output, within fleet and type admission limits, and each crash proof passed. This is the first complete SQL Server and Oracle run of this suite. Latencies include the burst backlog; flow throughput counts completed steps.

Observations:

- PostgreSQL is within 2–6% of the 2026-10-08 campaign. SQLite mixed again varied widely between repetitions (25–46 jobs/s); SQLite flows are the slowest path on every run.
- Oracle repetition 3 is an outlier for flows (38.5 vs ~47 jobs/s) and outbox (30.1 vs ~54 jobs/s), with 20–45% more worker CPU for the same work and unchanged mixed throughput. This points to host contention rather than a code path; repetitions 1 and 2 match each other.
- Outbox queue and end-to-end latency are measured from the source commit, as the report's `latencyOrigin` records. Destination stores assign creation time on delivery, so measuring from stored creation time would hide the outbox backlog. An intermediate run at `1e66a31`, before this harness change, reported ~45 ms PostgreSQL outbox p95 for that reason; it is superseded by this table.

**SQL Server read deadlock (fixed in `808e79c`).** The first attempt at `1e66a31` aborted during flows repetition 1: the harness completion poll, `IJobQueries.ListAsync`, was chosen as a deadlock victim (error 1205). Plain store reads ran as auto-commit statements at locking READ COMMITTED on SQL Server and deadlocked with settling workers; dashboard job lists share that path. They now run in the provider's read snapshot, like the bounded operations queries. `Job_reads_do_not_fail_while_workers_settle_flows` reproduced the deadlock on every SQL Server run before the change and passes after it, and this baseline completed all SQL Server repetitions without error.

Raw reports: [SQLite](performance-results/2026-10-10-sqlite-full.json), [PostgreSQL](performance-results/2026-10-10-postgres-full.json), [SQL Server](performance-results/2026-10-10-sqlserver-full.json), [Oracle](performance-results/2026-10-10-oracle-full.json).

## Execution controls with no policies — 2026-10-10

The claim-path baseline rerun after ADR-018 (partition limits, rate limits, weighted queue fairness), with no policies configured and one queue, as in the baseline. Same profile, host and harness; revision `e2de671`, clean working tree. Every measured job settled once with the expected output within the fleet (6) and type (2) admission limits, and each crash proof passed.

| Provider | Workload | Baseline jobs/s | Controls jobs/s median (range) | Change | Queue wait p50 | Run p95 end-to-end |
|---|---|---:|---:|---:|---:|---:|
| sqlite | mixed | 34.3 | 30.8 (28.2–33.3) | -10% | 2.61 s | 12.96 s |
| sqlite | flows | 14.4 | 12.7 (12.7–13.0) | -12% | 6.77 s | 70.90 s |
| sqlite | outbox | 84.6 | 68.5 (57.2–77.2) | -19% | 3.96 s | 6.88 s |
| postgres | mixed | 80.6 | 82.8 (82.2–83.4) | +3% | 1.56 s | 3.75 s |
| postgres | flows | 58.7 | 60.4 (53.4–61.4) | +3% | 5.00 s | 11.16 s |
| postgres | outbox | 48.0 | 49.4 (21.9–52.6) | +3% | 4.99 s | 9.20 s |
| sqlserver (emulated) | mixed | 36.7 | 36.3 (32.2–36.6) | -1% | 4.04 s | 8.29 s |
| sqlserver (emulated) | flows | 30.9 | 31.3 (30.1–32.0) | +1% | 9.40 s | 21.49 s |
| sqlserver (emulated) | outbox | 36.0 | 35.0 (31.9–35.9) | -3% | 7.12 s | 13.03 s |
| oracle | mixed | 64.8 | 67.7 (67.5–67.7) | +4% | 1.86 s | 4.27 s |
| oracle | flows | 46.9 | 47.8 (47.4–48.0) | +2% | 6.12 s | 13.95 s |
| oracle | outbox | 52.5 | 56.6 (55.1–56.8) | +8% | 4.10 s | 7.56 s |

The first rerun, at `92b855f`, was 14–18% slower on PostgreSQL in every workload. Paired, interleaved runs against `cab5d8a` (develop) confirmed a real cost of 5–10%, while run-to-run drift on this host reached 30%. `EXPLAIN ANALYZE` showed the cause: claims are not prepared statements, so PostgreSQL re-plans every claim, and the partition and rate predicates raised planning time from 0.25 ms to 0.60 ms inside the global advisory lock (execution rose only 0.07 ms). A separate policy check fixed planning but added a round trip, which costs as much on this Docker host. The final claim reads the registry revision, drain flag, global maximum and configured policy kinds in one query, leaves absent policy predicates out of the claim query, and indexes only partitioned jobs. Two further paired PostgreSQL passes then measured the branch at or above develop (mixed 83.1–87.5 vs 80.6–84.1 jobs/s, outbox 49.5–54.9 vs 46.9–50.2). That combined query is one round trip fewer than before, which explains the small gains.

SQLite's lower medians are host drift, not the change: a paired SQLite A/B (six runs per side) measured develop at 26.9 / 13.2 / 63.7 jobs/s and this revision at 30.2 / 12.9 / 68.9 jobs/s (mixed / flows / outbox). Develop itself ran 25% below its own baseline that hour. PostgreSQL outbox repetition 1 (21.9 jobs/s) matches isolated collapses also seen on develop during the A/B runs. Throughput with policies configured depends on policy shape and is covered for correctness, not performance, by `AdmissionControlTests`.

Raw reports: [SQLite](performance-results/2026-10-10-sqlite-controls.json), [PostgreSQL](performance-results/2026-10-10-postgres-controls.json), [SQL Server](performance-results/2026-10-10-sqlserver-controls.json), [Oracle](performance-results/2026-10-10-oracle-controls.json).

## Multi-process admission campaign — 2026-10-11

`WorkThrough.Exercises admission <provider>` runs three separate worker processes (four slots each, so a 12-slot fleet). The workers serve three queues of one fresh store, through five phases:

1. **Overhead:** the same 400-job keyed workload runs alternately without policies and with non-binding partition, tenant and per-tenant rate policies (10,000 each). The difference prices predicate evaluation under contention.
2. **Keyed limits:** partition maximum 1 and tenant maximum 3. One busy tenant has 340 jobs over 24 partitions; three quiet tenants have 20 jobs each.
3. **Rate:** 10 starts per second on one processor type, over 50 jobs.
4. **Fan-out:** six flow runs of 16 parallel steps, each started with `MaxParallelSteps: 3`.
5. **Fairness:** two queues of 90 jobs each, released together, with weights 3 and 1.

Each limit is checked from the database's recorded attempt intervals, and a breach fails the run. Worker resident memory is sampled every 250 ms. The runs used the same host as above, on the working tree committed as the next revision after `6255d8b`; only documentation changed after the runs.

| Provider | No policies, jobs/s | Non-binding policies, jobs/s | Partition / tenant peak | Quiet / busy tenant wait p95 | Busiest 1 s window | Flow run peak | Bulk share | Worker RSS start → peak |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| sqlite | 129.6 / 81.3 | 98.1 / 86.6 | 1 / 3 | 1.1 s / 32.1 s | 10 | 3 | 0.750 | 172 → 279 MiB |
| postgres | 92.2 / 101.9 | 88.0 / 85.1 | 1 / 3 | 0.5 s / 5.8 s | 10 | 3 | 0.750 | 239 → 319 MiB |
| sqlserver (emulated) | 52.1 / 53.4 | 50.7 / 47.9 | 1 / 3 | 1.3 s / 14.1 s | 10 | 3 | 0.750 | 260 → 357 MiB |
| oracle | 73.2 / 85.1 | 80.1 / 78.4 | 1 / 3 | 0.5 s / 5.9 s | 10 | 3 | 0.750 | 339 → 438 MiB |

- **Limits:** every one held across the separate processes. The weighted queue received exactly its 3/4 share of the 120 starts made while both queues had work.
- **Tenant isolation:** quiet tenants waited at most 0.5–1.3 s (p95) while the busy tenant's backlog queued for 6–32 s.
- **Policy cost:** non-binding policies cost 3–16% on the server databases.
  - This is the price of evaluating all three policy kinds on every claim, including a per-tenant rate count over thousands of in-window starts. Binding limits count at most their permits.
  - The SQLite and Oracle no-policy pairs varied 13–37% between themselves, so their differences are within noise.
- **Rate ledger:** the first implementation counted rate limits from attempt history and cost PostgreSQL 27% in the second non-binding run. The rate ledger (ADR-018) reduced that to 16%.
- **Memory:** worker memory rose during warm-up (the first phase), then grew 3–4% across the remaining four phases. This short campaign does not rule out slow growth; the two-hour soak remains release-readiness work.
- **No-policy claim path:** paired, interleaved PostgreSQL runs against `e2de671` (six repetitions each) measured medians of 83.6 / 61.1 / 50.6 jobs/s before and 83.8 / 60.7 / 50.6 after (mixed / flows / outbox). Reading the extra policy kinds in the claim's first query costs nothing measurable.

Raw reports: [SQLite](performance-results/2026-10-11-sqlite-admission.json), [PostgreSQL](performance-results/2026-10-11-postgres-admission.json), [SQL Server](performance-results/2026-10-11-sqlserver-admission.json), [Oracle](performance-results/2026-10-11-oracle-admission.json).

## Two-hour soak — 2026-10-11

`WorkThrough.Exercises soak postgres 120` ran for 120 minutes on PostgreSQL 17 at revision `f8f9a7c` (clean tree), on the host above, with retention disabled so history accumulated.

- **Load:** three worker processes (12 slots) and an open-loop producer offering 25 operations per second. The actual rate was about 24.9/s, because ticks the producer could not keep up with were skipped and recorded.
- **Mix:** 10% four-step flow runs with `MaxParallelSteps: 2`, 10% rate-limited jobs, and the rest keyed jobs across 10 tenants and 200 partitions.
- **Policies:** partition maximum 1, tenant maximum 4, and 5 starts per second for the rate-limited type.
- **Checks:** every minute, every limit was checked against the database's recorded attempt intervals. After the run, the store had to drain and every job had to have exactly one successful attempt.

| Result | Value |
|---|---|
| Jobs | 233,469 (179,592 operations, of which 17,959 were flow starts); all succeeded once, zero retries |
| Highest per-minute peaks | partition 1 / 1, tenant 3 / 4, flow run 2 / 2, rate window 5 / 5 |
| Completed per minute | 1,779–1,956, matching the offered load |
| Backlog | 0–33 throughout; 0 at the end, so no drain was needed |
| Queue wait p95 | 426 ms average over the first 10 minutes, 244 ms over the last 10; single highest minute 1.36 s |
| Worker resident memory (sum of 3) | 318 MiB at the start, 325 MiB at minute 10, 331 MiB at the end |
| Worker CPU | 72.8 s/min over the first 10 minutes, 68.9 s/min over the last 10 |
| Stored jobs at the end | 233,469 |

Throughput, latency and CPU did not degrade as history grew to 233k jobs. Worker memory rose 6 MiB between minute 10 and minute 120. That rate is too small to call a leak or to rule one out, so longer soaks remain advisable before production. The early latency was higher while workloads in this session (builds and tests) shared the host. This was one provider on one machine; it is not a multi-host or failover campaign.

Raw report: [PostgreSQL soak](performance-results/2026-10-11-postgres-soak.json).

## Write-path throughput (ADR-020) — 2026-10-11

[ADR-020](adr/0020-write-path-throughput.md) changed the write path while keeping the single admission lock and every guarantee. Binaries were built at `5aa2a7a`; the working tree had documentation edits only. This is the same host and full profile as the claim-path baseline. The Oracle run was repeated after its first attempt started before the database service had registered with its listener; it was an environment race, not a test failure.

**The standard benchmark** caps the fleet at six leases, uses 10 ms handlers and runs a closed loop, so it is bounded by those limits as well as by the write path:

| Provider | Workload | Baseline jobs/s | ADR-020 jobs/s median (range) | Change | Queue wait p50 | Run p95 end-to-end |
|---|---|---:|---:|---:|---:|---:|
| sqlite | mixed | 34.3 | 298.8 (298.7–298.8) | +771% | 0.54 s | 1.43 s |
| sqlite | flows | 14.4 | 215.2 (209.7–218.4) | +1398% | 1.19 s | 4.11 s |
| sqlite | outbox | 84.6 | 391.9 (384.6–410.8) | +363% | 0.66 s | 1.15 s |
| postgres | mixed | 80.6 | 162.0 (159.6–165.3) | +101% | 0.58 s | 1.62 s |
| postgres | flows | 58.7 | 108.7 (104.6–110.7) | +85% | 2.28 s | 5.14 s |
| postgres | outbox | 48.0 | 108.6 (105.3–111.0) | +126% | 2.29 s | 4.08 s |
| sqlserver (emulated) | mixed | 36.7 | 87.3 (75.8–92.3) | +138% | 0.92 s | 2.61 s |
| sqlserver (emulated) | flows | 30.9 | 61.8 (57.3–62.4) | +100% | 3.84 s | 8.77 s |
| sqlserver (emulated) | outbox | 36.0 | 58.1 (58.1–59.1) | +61% | 4.01 s | 7.61 s |
| oracle | mixed | 64.8 | 108.6 (99.9–108.9) | +68% | 0.86 s | 2.42 s |
| oracle | flows | 46.9 | 77.7 (72.1–78.4) | +66% | 3.02 s | 6.96 s |
| oracle | outbox | 52.5 | 73.6 (65.6–73.9) | +40% | 3.19 s | 5.63 s |

Every job settled once with the expected output, within the fleet and type limits, and each crash proof passed. SQLite's largest gains come from its busy timeout: without it, processes contending for the single writer spent most of their time in the driver's fixed retry sleeps.

**The throughput exercise** (`WorkThrough.Exercises throughput <provider> <jobs>`) measures the ceiling with no limits. A backlog is preloaded, then three workers with 16 slots each drain it with 1 ms handlers:

| Provider | Drain before (jobs/s) | Drain after (jobs/s) | Change |
|---|---:|---:|---:|
| PostgreSQL | 87–105 | 221–275 | about 2.5× |
| SQL Server (emulated) | 63–64 | 230–231 | 3.6× |
| Oracle | 88 | 211–212 | 2.4× |
| SQLite | 265 | 695–725 | 2.7× |

Enqueue ran at roughly 500–4,000/s throughout and was never the bottleneck. These runs used one machine with Docker databases; absolute numbers will differ on production hardware, and the per-store ceiling remains a design limit (ADR-020).

### Row-locked settlement and unrestricted claims (ADR-020 amendment)

The amendment moved settlement, and claims while no limit exists, out of the admission lock, and appends journal events at commit. Same exercise, same host, alternating the previous build (`a2f23a9`) and this change, two runs each:

| Provider | Previous build (jobs/s) | This change (jobs/s) | Change |
|---|---:|---:|---:|
| PostgreSQL | 227–254 | 811–829 | about 3.3× |
| SQL Server (emulated), `READ_COMMITTED_SNAPSHOT` on | 195–206 | 434 | about 2.1× |
| SQL Server (emulated), off | 195–206 | 201–211 | unchanged: serialized |
| Oracle | 179–183 | 278–279 | about 1.5× |
| SQLite | 729–736 | 723–813 | unchanged: single writer |

The SQL Server variant is selected with `WT_EXERCISE_SQLSERVER_VERSIONED_READS=1`. Stores with limits keep the serialized claim path; their throughput is unchanged. The multi-process admission exercise still held every limit on PostgreSQL and SQLite.

Raw reports for the first decision: [SQLite](performance-results/2026-10-11-sqlite-throughput.json), [PostgreSQL](performance-results/2026-10-11-postgres-throughput.json), [SQL Server](performance-results/2026-10-11-sqlserver-throughput.json), [Oracle](performance-results/2026-10-11-oracle-throughput.json).
