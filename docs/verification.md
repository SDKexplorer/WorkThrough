# Verification record

This is a chronological evidence log. Earlier package counts and pending states describe their recorded revision; current provider qualification and investigative contention results are in the 2026-10-10 sections below.

Date: 2026-10-08. Local macOS ARM64. Repository SDK 10.0.400; runtime packages target net8.0 and the Roslyn generator targets netstandard2.0. The compatibility gate also uses SDK 8.0.425 / runtime 8.0.31; the replacement worker runs on runtime 10.0.11.

## Completed

`WORKTHROUGH_DOTNET8=<SDK 8 executable> DURABLEJOBS_POSTGRES=<disposable connection> ./scripts/verify.sh` completed successfully after the bounded execution-query change. The retained local log is `artifacts/operations-query-verification.log` (ignored by Git).

| Check | Result |
|---|---|
| Angular formatting and production build | Passed; static console embedded for package consumers |
| Release solution build | Passed, zero warnings and errors |
| Repository readability gate | Passed; syntax-aware braces/spacing plus whitespace formatting; a second pass made no edits |
| Automated suite on .NET 8 | 141 cases passed |
| Automated suite on .NET 10 | 141 cases passed |
| SQLite | Real file databases, independent connections and restarts |
| PostgreSQL | Real PostgreSQL 17 Alpine container; isolated schema per case |
| Published assembly replacement | Passed from producer runtime 8 to worker runtime 10 after removal of the original processor DLL |
| Relocation scenarios | Typed job, future-due raw-string processor, saved flow step; stored identities and inputs unchanged |
| Coordinated identity rename | Old and new addresses executed through one canonical processor after alias activation; queued metadata unchanged |
| Local NuGet packaging | Seven WorkThrough 0.1.0-alpha.1 packages produced |
| Package-only consumers | SDK 8 and SDK 10 passed with separate fresh caches, local package source mapping, and no project references |
| Package registration paths | Generated typed/raw processors, manually assigned typed definitions, and idempotent flow starts executed from packages |
| Bounded execution query consumers | `IExecutionQueries` resolved through provider DI and found completed typed work from SDK 8 and 10 package-only consumers |
| Direct processor sample | Passed immediate processor execution and persisted recurring schedule setup |
| Lifecycle sample | Business outbox delivered into a separate job database; typed durable approval resumed the saved flow |
| Deployment preflight CLI | Rejected unsupported saved invoice work with exit code 2; no hosted worker started |
| Repository skills | Both SKILL.md files previously passed the skill-creator validator |
| Performance smoke | Independent worker processes, crash recovery, and three workloads passed on each provider |
| Full performance campaign | 12,000 measured jobs across 18 runs; output/attempt/admission assertions passed; raw reports committed |

The provider suite covers committed acceptance, enqueue deduplication/conflicts, shared total/type admission, queues, capabilities, scheduling, stale ownership, retries, cancellation, shutdown release, versioned graphs, conditional outputs, incident recovery, pause/resume, and draft/publication rules. Competing operations use separate store instances/connections. Schedule checks include concurrent dispatch, overlap coalescing, and a daylight-saving fallback occurrence.

Deployment tests on both providers cover exact versions, processor/queue matching on the same worker, recoverable incidents and waiting steps, future eligibility, enabled schedule targets, truncation, and payload exclusion. Waiting-to-ready activation records a fresh eligibility timestamp. The dashboard report requires metadata permission.

The relocation gate caught sub-millisecond early eligibility during this change. Explicit due times now round up to storage precision, immediate work uses the current millisecond, and PostgreSQL's clock uses floor rather than round-to-nearest. New provider regressions verify fractional and exact timestamps, pre-epoch conversion, and rejection beyond the representable range. The original relocation assertion remains intact. An execution-deadline race observed in hosted dependency CI was also corrected: elapsed monotonic budget is checked even if cancellation callback delivery is late. Existing cooperative/non-cooperative deadline tests pass on both runtimes in the final gate.

Flow-start tests race eight callers on independent connections, reject changed requests, return existing runs after restart/retirement, and inject receipt failures to verify rollback of definitions, runs, and steps. Correlation without an idempotency key still permits independent runs.

A literal schema-1 fixture verifies additive upgrade, unchanged identity/input, preserved Stop flow semantics, published definition adoption, and rejection of an unknown schema. These checks do not permit old schema-1 workers to run alongside schema-3 workers; follow ADR-010.

Administrative tests cover duplicate/conflicting command IDs, revision/attempt preconditions, and audit persistence failure. An injected audit-write failure on each real provider rolls back both the mutation and command receipt; retry succeeds after removing the fault. Dashboard tests cover host authentication, permission separation, protected exception/payload data, CSRF validation, actor attribution, nested routes, and PathBase.

Worker regressions cover scoped lifetimes, dependency validation, interrupted setup, local capacity while revoked non-cooperative handlers still run, and cooperative/non-cooperative deadline behavior. Progress and error writes reject stale ownership.

The first schema-3 hosted run exposed two verification portability issues: ripgrep is absent on the Ubuntu runner, and a test's 600 ms setup lease could expire under shared-runner contention before the intended settlement assertion. Formatting now uses Git's null-delimited source inventory when ripgrep is absent and fails if no files are found. The terminal-flow regression uses normal leases during setup, shortening only the sibling lease in its deliberate expiry branch. No production lease enforcement was relaxed. The fallback is also exercised locally with ripgrep excluded from PATH.

Schema-3 coverage adds coordinated aliases and registry-revision fencing, middleware continuation/lifetime rules, typed validation, permanent rejection, deferral, observer isolation, queue/worker controls with distinct pause/drain/resume audit actions, pre-admission expiry, replay causation, transaction/outbox rollback and lost destination commit acknowledgment, durable signals/timeouts, whole-flow archival with retained receipts, event subscription cursors, and safe journal trimming. SQLite tests verify both WAL opt-in default and preserved existing journal configuration, with FULL synchronous durability.

The process harness terminates a worker after an independently committed effect and resumes the expired attempt in a replacement process on both providers. SQLite also resumes from a consistent backup restored into a separate store. Integration tests terminate an owned PostgreSQL backend and close a SQLite transaction connection to verify rollback and recovery. See [performance methodology and raw measurements](performance.md) for the separate full campaign, environment, exploratory failures, and limits.

## Live sample

The embedded Angular console was exercised through Demo.Host at `/operations/jobs` against a separate SQLite file. Approved and review branches completed. An intentional negative-amount failure created an Incident with waiting dependents; its inspector displayed the saved diagram, attempt evidence, and protected exception stack. An audited recovery retried the same logical step and retained prior attempts. A separate flow draft was saved without executing it. A recurring schedule was saved as a draft and published through the live console while its trigger remained paused; both actions appeared with actor/reason in the audit trail. Narrow-screen checks at 375 CSS pixels found no document overflow, and keyboard skip-to-content remained visible on focus. These are manual local checks, not a browser accessibility certification.

On 8 October the rebuilt host rendered the new processor-coverage inventory against the existing SQLite incident data: all three required processor/queue pairs matched the recent worker heartbeat; waiting/incident steps had no ready count. Historical heartbeats were excluded from matching after 60 seconds. The new view had no document overflow at 375 CSS pixels and no observed browser warnings/errors. A screenshot is retained locally as `artifacts/workthrough-coverage.png`.

The schema-3 console was then checked against the same preserved demo database. Queue pause/resume visibly changed admission state; both commands appeared with trusted actor and reason. Provider tests separately verify that these controls govern claims. Archive preview selected three completed units while leaving the incident live; no demo executions were removed. The archive view had no document overflow at 375 CSS pixels. Desktop evidence is retained locally as `artifacts/dashboard-operations.png`.

## Hosted verification

The schema-3 main baseline `dcb45ed` passed both GitHub Actions jobs on Ubuntu: [Verify run 37748849077](https://github.com/SDKexplorer/WorkThrough/actions/runs/37748849077). That run passed 121 tests on each runtime, package consumers, relocation/alias proofs, samples, and both providers' recovery/performance smoke exercises. Consult the [Verify workflow](https://github.com/SDKexplorer/WorkThrough/actions/workflows/verify.yml) for the exact pushed revision's hosted result; smoke JSON reports are retained as workflow artifacts.

## Dependency maintenance verification

On the same date, the combined compatible updates from PRs 1, 2, 3, 6, 7, 8, and 9 passed the entire local gate in an isolated checkout. Both runtimes passed 121 tests; the release build had zero warnings/errors. Fresh package-only consumers, published relocation and aliases, direct-processor/lifecycle samples, and both providers' process-kill/performance smoke checks passed. The native SQLite report identifies 3.53.4 with WAL and FULL durability; PostgreSQL was 17.11. The log is `artifacts/dependency-verification.log`. The retained 12,000-job full campaign predates these dependency upgrades and is not claimed rerun against them.

Angular build/compiler peer metadata was checked directly: both require TypeScript `>=6.0 <6.1`. The Roslyn 5.9 PR's SDK 8 consumer failed CS9057. These upgrades are excluded rather than weakening peer validation or compiler compatibility. See [dependency update policy](dependency-updates.md). YAML parsing passed for the workflow and Dependabot configuration; hosted validation remains specific to the maintenance PR's current head.

## Bounded execution-query verification

The expanded 141-case suite passed on each runtime against real SQLite files and PostgreSQL 17.11. New provider cases verify failed-attempt windows including recovered jobs, one result per logical execution, exact filters, half-open/fractional creation windows, stable pagination ties across connections, preserved default windows, mismatched/malformed cursors, cancellation, clipped long metadata and explicit live-only archive coverage. A fixture with invalid payload/output JSON still supports metadata reads, proving that the new path does not deserialize those fields. Protected HTTP routes were exercised on both providers; legacy dashboard composition without the new port returns an explicit unsupported response.

The first expanded run exhausted PostgreSQL's connection limit because separate test schemas retained separate idle pools after disposal. The harness now clears only the completed test's pool before dropping its schema, using [Npgsql's scoped pool cleanup](https://www.npgsql.org/doc/api/Npgsql.NpgsqlConnection.html#Npgsql_NpgsqlConnection_ClearPool_Npgsql_NpgsqlConnection_). The subsequent full gate passed without reducing test parallelism or increasing server limits. Production connection management is unchanged.

That query-foundation gate also passed the release/readability build, published relocation/alias proofs, both package consumers, examples, process-kill recovery and all six provider/workload smoke runs. The subsequent MCP verification is recorded below; the proposed 100,000-execution investigative-query contention campaign remains unrun.

## Read-only MCP verification

The full gate in `artifacts/mcp-verification.log` passed **149 tests on each of .NET 8 and .NET 10**, with real SQLite files and PostgreSQL 17.11. It built/formatted the new `WorkThrough.Mcp` package and standalone sample without warnings or errors. All eight local packages packed; fresh SDK 8/10 consumers exercised the packaged MCP server and official C# client through real loopback Kestrel with signed JWTs. Published relocation/alias proofs, examples, process-kill recovery and all six workload smoke runs passed.

Eight new cases use the official C# SDK 2.2.0 Streamable HTTP client (current 2026-07-28 negotiation) and an independent HTTP JSON-RPC client using 2025-11-25 requests. They verify typed discovery, metadata paging/failed-attempt searches, attempt reads, signed-token validation (wrong issuer/audience and expiration), dashboard-scheme isolation, direct permission/resource enforcement, rejected mutation calls, request/result limits, per-principal rates, concurrency admission, deadline/disconnect cancellation and access-sink failure. The real-provider MCP connections are read-only: SQLite `Mode=ReadOnly` and PostgreSQL `default_transaction_read_only=on`. Job state and engine mutation audit remain unchanged by MCP reads. These transport tests do not claim to verify deployment database-role grants.

That gate established the initial four-tool metadata subset. The following increment adds protected diagnostics and saved-flow inspection. Real external identity-provider discovery/login, setup with third-party AI applications, remaining tools and the planned 100,000-execution/two-hour contention campaign remain unverified or unimplemented. The existing execution smoke is not that performance campaign. See [MCP hosting and limits](mcp.md).

## MCP diagnostic and flow inspection verification

The final gate in `artifacts/mcp-inspection-verification.log` passed **165 tests on each of .NET 8 and .NET 10**, against real SQLite files and PostgreSQL 17.11. Angular formatting/build, the zero-warning Release build, readability checks, all eight package builds, fresh SDK 8/10 package consumers, published relocation/alias proofs, examples, process-kill recovery and all six provider/workload smoke runs passed. The package consumers now inspect a two-node saved flow and a selected attempt through the packaged MCP server and official client over loopback HTTP.

Sixteen added cases cover bounded diagnostic fields (including the legacy exception-type fallback), missing executions/attempts, pinned flow revisions after publication of a newer definition, independent node/dependency/event pagination, oversized-definition coverage, archived runs with retained events, trimmed journal ranges, invalid page positions and cancellation. Invalid input/output JSON fixtures prove that these inspection paths do not deserialize job payloads. Both real providers are exercised through the existing read-only MCP connections.

Authorization cases verify that diagnostics require both execution and payload permissions, direct calls cannot bypass discovery filtering, run-specific policies receive the actual flow ID, and legacy hosts without the inspection port return explicit unavailable capabilities. Access evidence records sensitive reads and resource IDs without stored diagnostic text. A failing host redactor suppresses results; a trace-level SDK log capture also verifies that its exception containing a secret is not logged. Flow responses exclude templates and condition literals. These checks establish the six-tool subset, not the remaining MCP tools or the planned investigative-load campaign.

## MCP history and manual retention preview verification

The full gate in `artifacts/mcp-history-verification.log` passed **180 tests on each of .NET 8 and .NET 10**, using real SQLite and PostgreSQL 17.11. Angular formatting/build, zero-warning Release build, readability checks, all eight packages, fresh package-only consumers, published relocation/alias proofs, examples, process-kill recovery and all six provider/workload smoke runs passed. Packaged MCP consumers now also call audit/archive search and manual retention preview through the official client over loopback Kestrel.

Fifteen added cases verify audit attribution without reasons/display names/change JSON, bounded or unavailable actors, timestamp ties and fractional half-open windows, store/filter/query-kind cursor rejection, purged-body summaries, unrestricted-age archive-ID lookup, and manual-policy preview parity without payload reads or mutation. Invalid archive-body/change JSON fixtures still support metadata queries. Preview retains failed work without a failure cutoff, excludes incident flow steps and reports clipping and additional candidates explicitly. The legacy schema fixture still upgrades after adding the terminal-selection index after its column migration.

Real-provider MCP cases use read-only connections and exercise independent audit/retention permissions, discovery filtering, direct calls, actual archive-ID resource authorization, access evidence and rejected archive/export/purge mutations. Missing history ports report explicit unavailable capabilities. The new queries have row/window/time budgets; the proposed 100,000-execution contention and two-hour soak gates have not been run. Manual preview is not evidence of automatic retention, holds, export verification or purge implementation.

## Limits of this evidence

- Local packing is not package publication. Hosted verification applies only to the exact commit shown by each workflow run.
- This is an alpha correctness baseline, not an availability, performance, security, accessibility, or disaster-recovery certification.
- The oldest consumer SDK actually executed is 8.0.425. SDK 8.0.100, .NET 9, Native AOT, and full trimming have not been verified.
- SQL Server, Oracle, MySQL, MongoDB, and further adapters are not implemented or tested.
- The standalone OIDC sample compiles; login against a real external identity provider still requires verification with deployment-specific credentials.
- Shared limits bound valid leases, not effects from a process resuming after losing its lease.
- The targeted process-kill, lost-acknowledgment, disconnection, and SQLite restore proofs do not replace multi-machine partitions, PostgreSQL PITR, exhaustive fault campaigns, or production soak/load tests.

See `plan.md` for remaining work rather than treating a green suite as a complete replacement for Hangfire or Wolverine.


## Runtime evidence and console verification — 2026-10-08

The runtime-query increment passes `scripts/verify.sh`: **190 tests on each of .NET 8 and .NET 10**, SQLite and PostgreSQL 17, zero build warnings/errors, eight local packages, fresh NuGet-only consumers on both runtimes, published relocation proof and both-provider independent-process/performance smoke exercises. New coverage includes exact worker/queue matching, aliases/shared limits, incomplete and stale heartbeats, draining/acquisition failures, progress/deadline observations, half-open metric windows, recovered failures, archival coverage, authorization and absent optional ports. Logs: `artifacts/runtime-verification.log`.

The Angular build and formatting checks pass. An isolated local host was inspected through the in-app browser: empty attempt windows show unavailable durations, worker acquisition observations are visible, and the page has no document overflow at a 375px viewport. Existing demo data was untouched. This is targeted UI verification, not a comprehensive accessibility audit. Large investigative contention, external IdP/client integration and the two-hour soak remain unexecuted gates.


## Opt-in retention verification — 2026-10-10

`scripts/verify.sh` passes with **214 tests on each of .NET 8 and .NET 10**, SQLite and real PostgreSQL 17, zero build warnings/errors, eight local NuGet packages, both fresh package consumers, the published processor-relocation proof and both providers' process-kill/performance smoke exercises. The initial fresh consumer exposed a missing JSON namespace import; it was fixed before the final successful full gate. Final log: `artifacts/retention-verification.log`.

New tests cover disabled defaults, audited policy/hold revisions, whole-flow holds and incident preservation, competing maintenance connections, takeover with an unchanged batch ID, stale tokens, lost-response reconciliation, policy supersession, forced lease loss after archive writes with full transactional rollback, schema-3 upgrade, oversized-candidate progress, subscriber/hold-aware event trimming, verified-copy destination/hash checks, body-purge receipt preservation, file-copy reuse/corruption and hosted-service shutdown. MCP status is tested through read-only connections with separate retention permissions; dashboard writes verify CSRF, trusted attribution and optimistic concurrency. NuGet-only consumers inspect the thirteenth MCP tool.

The Angular production build and formatting gate pass. An isolated local sample visibly reports disabled/unconfigured retention and current hold/incident/journal state; the Archives view has no document overflow at 375px. Existing demo data and retention settings were not modified. This was a focused dark-theme UI check, not a complete accessibility audit.

These checks are not the proposed large maintenance-versus-worker performance gate. The 100k retained-execution investigative campaign, two-hour soak, real external IdP/desktop AI-client integration, destination durability/restore checks, PostgreSQL PITR and multi-machine partitions remain pending. SQL Server and Oracle remain unimplemented.


## SQL Server implementation campaign — 2026-10-10

The SQL Server provider uses Microsoft.Data.SqlClient 7.1.0 and a pinned SQL Server 2022 CU27 Developer image (16.0.4295.3, Ubuntu 22.04 x64). Local Docker is ARM64 and runs that image through emulation; these local results are debugging evidence, not a supported platform claim. Native Linux x64 CI is being added before qualification.

The shared SQLite/PostgreSQL/SQL Server suite passed 299 tests on .NET 10, then 308 tests after adding identity-boundary and initialization cases. The first full gate passed all 308 on .NET 10 but had 26 .NET 8 failures involving immediate claims and default query windows on the network databases. An isolated .NET 8 rerun passed all 308. A 100-job per-runtime PostgreSQL timing probe reproduced no failures; measured idle host/database differences were approximately 1–2 ms after warmup. The initial failure remains recorded rather than being called fixed. Native CI and a complete rerun are required. No scheduled eligibility boundary was weakened to hide a failure.

Read-only MCP cases provision real SELECT-only SQL Server logins, with no storage initialization. Additional package/runtime, published relocation, process-kill and two-worker performance gates are wired into `scripts/verify.sh`; their results are pending below.


The complete local rerun passed: 308 tests on each of .NET 8.0.31 and .NET 10.0.11, zero build warnings/errors, all nine local NuGet packages, isolated SDK 8/10 consumers (including SQL Server generated handlers and hosted workers), published relocation/alias proofs on all three providers, and process-kill recovery plus the two-worker performance smoke. SQL Server's emulated smoke measured 37.5/25.6/29.1 jobs/s for mixed/flows/outbox, with 60/120/60 successful jobs checked. These small local workloads are correctness/regression evidence, not production performance or soak qualification. Logs: `artifacts/sqlserver-verification.log` and `artifacts/performance/sqlserver-smoke.json`. A final constructor guard rejects implicit/system databases; its targeted checks and native CI run follow this gate.


Native x64 qualification passed at commit `b383bac3d4ef51393b8b7c466e536f0604f79d9e`: [CI run 37995454966](https://github.com/SDKexplorer/WorkThrough/actions/runs/37995454966), verify 8m12s and console 23s. All 313 tests passed on each runtime, including the final configuration guard; package consumers, published relocation and killed-worker recovery passed. The native SQL Server smoke measured 18.6/16.0/16.3 jobs/s for mixed/flows/outbox with 60/120/60 jobs verified. The earlier local timing failure did not recur in the full local rerun or native CI; its cause is not established. Raw CI log and performance artifacts were retained locally under `artifacts/sqlserver-native-ci*`. SQL Server 2022 CU27 Developer/Linux x64 is now the verified initial server matrix. Azure SQL, Windows SQL Server, other releases, sustained load and backup/PITR remain outside that evidence.

## Oracle and immediate-acceptance qualification, 2026-10-10

The local full gate `artifacts/oracle-verification-final.log` passed on macOS ARM64 with real SQLite, PostgreSQL 17.11 and Oracle Free 26ai 23.26.3.0 (official lite ARM64 image, ODP.NET Core 23.26.301). Both .NET 8.0.31 and .NET 10.0.11 passed **323 tests** each. Ten packages packed; fresh SDK 8/10 consumers executed Oracle generated registration, hosted work and metadata/retention/metrics queries. Published typed, future-due raw-string, flow-step and alias relocation proofs passed on all three providers with the original handler DLL removed. Independent-process crash recovery retained abandoned/successful attempts and one idempotent external effect. Local Oracle smoke verified 60 mixed jobs, 120 flow jobs and 60 outbox jobs at 59.1/44.1/46.0 jobs/s, with end-to-end p95 600/1768/59 ms. Raw reports remain in `artifacts/performance/`; these short workloads are correctness evidence, not production capacity promises.

Oracle-specific checks cover concurrent initialization, interrupted DDL checkpoint resumption, refusal of incomplete schemas and missing admission locks, actual SELECT-only and DML-only logins, denied migration access for runtime credentials, and server-side session disconnection before commit. The earlier failures exposed deferred segment allocation, Oracle character-set binding, archive SELECT syntax and fixture session cleanup; these were corrected. An error trace located an intermittent ORA-08177 in Oracle's recursive `cdef$` dictionary query while other test schemas were being created/dropped. Independent fixtures now run sequentially; intentional claim/scheduler/initializer races inside each test and multi-process workloads remain concurrent. DDL must be coordinated in deployments, and a serialization error fails a read rather than returning partial results.

A separate .NET 8/PostgreSQL clock probe reproduced the previously unexplained immediate-claim failure: the producer clock was a few milliseconds ahead of the database. [ADR-016](adr/0016-immediate-acceptance-clock.md) fixes immediate acceptance using storage time and adds an injected one-hour-skew regression on every provider. Explicit NotBefore precision tests still pass. This supersedes the earlier unresolved local timing observation; it does not weaken scheduled eligibility boundaries.

Native x64 [CI run 38039024967](https://github.com/SDKexplorer/WorkThrough/actions/runs/38039024967) passed at commit `9b68dcff8a537c9c30fa01178a9595e9243515cb`. The SQLite/PostgreSQL/SQL Server job passed 320 tests on each runtime, with three Oracle-only tests explicitly skipped; the separate SQLite/Oracle job passed all 232 applicable tests on each runtime. Both jobs passed package consumers, published relocation, killed-worker recovery and two-worker smoke workloads. The Angular check also passed. Native Oracle verified 60/120/60 mixed/flow/outbox jobs at 41.8/33.7/30.6 jobs/s (end-to-end p95 865/2272/105 ms). SQL Server requalified the shared portability/clock changes at 17.9/16.5/15.8 jobs/s. Raw native logs and Oracle reports are retained under `artifacts/oracle-native-ci*`.

SQL Server 2022 CU27 and Oracle Free 26ai 23.26.3.0 on Linux x64 are the verified initial server matrix. Large investigative contention, two-hour soak, multi-machine network partitions and provider backup/PITR campaigns remain release-readiness work.

## Retained-history investigative contention, 2026-10-10

The corrected version-2 campaign completed six comparisons across SQLite, PostgreSQL (three repetitions), SQL Server and Oracle using .NET 8.0.31, two independent workers and 20 official MCP clients. All 33,720 live jobs completed once with matching outputs; 27,978 measured requests had zero failures or fixture-secret leaks. Full-window metrics explicitly assert coverage of all 200,000 synthetic retained attempts. Queue measurements use stored creation time for this immediate, first-attempt workload; settled AvailableAt is mutable and must not be used. Six timing/overflow regressions passed on both .NET 8 and 10, and the build/readability gates passed with zero warnings/errors.

[Raw reports and methodology](performance.md#initial-retained-history-campaign--2026-10-10) include the 30-second baseline, 60-second investigative phase, provider/runtime versions, workload, distributions and resource observations. Query latency targets were met, but the proposed maximum 10% throughput degradation was not consistent: PostgreSQL varied from 3.3% to 11.7%, Oracle measured 10.8% and emulated SQL Server 16.7%. These results leave the performance-budget gate open. Local SQL Server uses x64 emulation and is not a native capacity claim. Superseded version-1 queue measurements are explicitly excluded from evidence; unavailable memory counters are now null rather than zero.

`scripts/investigation.sh` supplies smoke, repeated full and two-hour soak profiles. `scripts/verify.sh` runs the bounded smoke after engine correctness/performance checks, with artifacts uploaded by both native provider CI jobs. Native [run 38040714240](https://github.com/SDKexplorer/WorkThrough/actions/runs/38040714240) passed all provider/package/relocation/recovery gates and the first investigative smoke at `fab54e2`; its version-1 queue fields are superseded by the correction above. The corrected harness must pass the next native run. Longer repetitions, SQL-plan/server-allocation analysis, two-hour soak, external IdP/desktop AI-client setup, maintenance contention and restore/PITR qualification remain open.

## Execution admission controls, 2026-10-10

ADR-018 (partition limits, rate limits, weighted queue fairness, schema 5) at `e2de671`, on .NET 10 test runs per provider: SQLite + PostgreSQL 17 270 passed / 3 skipped; SQL Server 2022 (emulated x64) 267 passed / 3 skipped; Oracle Free 26ai 270 passed. Eleven provider-theory admission tests cover partition limits across workers, type scope, expired-lease release, sliding-window and per-partition rate limits, readiness reasons, audited policy commands, backlog fairness, weights, idle-burst prevention and flow/query partition keys. `Schema_four_stores_upgrade_to_admission_controls_without_losing_work` upgrades a real schema-4 fixture. A separate process-level check ran schema-4 (`cab5d8a`) code against each of the four databases, then schema-5 code against the same stores: queued work was preserved, an in-flight lease settled, queues gained weight 1 and partition limits applied. The no-policy claim-path rerun is in [performance](performance.md). Both runtimes and NuGet consumers are checked by `scripts/verify.sh` in CI.

Tenant IDs, keyed limits, the rate ledger and `MaxParallelSteps` (2026-10-11), on .NET 10 test runs per provider:

| Databases | Passed | Skipped |
|---|---:|---:|
| SQLite + PostgreSQL 17 | 276 | 3 |
| SQL Server 2022 (emulated) | 273 | 3 |
| Oracle Free 26ai | 276 | 0 |

New provider theories cover tenant limits that are independent of partitions, per-tenant rate limits, and gradual fan-out release with `FlowParallelism` readiness. The multi-process admission exercise passed on all four databases ([performance](performance.md)). The schema-4 upgrade fixture passes on every provider with the final schema 5.

Tenant access isolation, schedule seeding, worker/dispatch resilience fixes and the Shop sample (2026-10-11), on .NET 10:

| Databases | Passed | Skipped |
|---|---:|---:|
| SQLite + PostgreSQL 17 | 285 | 3 |
| SQL Server 2022 (emulated) | 282 | 3 |
| Oracle Free 26ai | 285 | 0 |

New coverage:

- dashboard and MCP tenant scoping on every provider, including absent-looking lookups, store-wide denial and a scoped command;
- schedule seeding across three racing hosts that respects operator changes;
- renewal surviving a transient storage failure;
- outbox and event dispatch draining full batches.

The renewal and drain tests fail without their fixes. `Shop.Api --smoke` and `Shop.Worker --preflight` pass locally. The two-hour PostgreSQL soak passed ([performance](performance.md)).
