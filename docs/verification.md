# Verification record

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
