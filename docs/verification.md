# Verification record

Date: 2026-10-08. Local macOS ARM64. Repository SDK 10.0.400; runtime packages target net8.0 and the Roslyn generator targets netstandard2.0. The compatibility gate also uses SDK 8.0.425 / runtime 8.0.31; the replacement worker runs on runtime 10.0.11.

## Completed

`WORKTHROUGH_DOTNET8=<SDK 8 executable> DURABLEJOBS_POSTGRES=<disposable connection> ./scripts/verify.sh` completed successfully. The retained local log is `artifacts/verification-latest.log` (ignored by Git).

| Check | Result |
|---|---|
| Angular formatting and production build | Passed; static console embedded for package consumers |
| Release solution build | Passed, zero warnings and errors |
| Automated suite on .NET 8 | 91 cases passed |
| Automated suite on .NET 10 | 91 cases passed |
| SQLite | Real file databases, independent connections and restarts |
| PostgreSQL | Real PostgreSQL 17 Alpine container; isolated schema per case |
| Published assembly replacement | Passed from producer runtime 8 to worker runtime 10 after removal of the original processor DLL |
| Relocation scenarios | Typed job, future-due raw-string processor, saved flow step; stored identities and inputs unchanged |
| Local NuGet packaging | Seven WorkThrough 0.1.0-alpha.1 packages produced |
| Package-only consumers | SDK 8 and SDK 10 passed with separate fresh caches, local package source mapping, and no project references |
| Generated registration | Typed handlers, direct DTO/raw-string processors, and idempotent flow starts executed from packages |
| Direct processor sample | Passed immediate processor execution and persisted recurring schedule setup |
| Deployment preflight CLI | Rejected unsupported saved invoice work with exit code 2; no hosted worker started |
| Repository skills | Both SKILL.md files previously passed the skill-creator validator |

The provider suite covers committed acceptance, enqueue deduplication/conflicts, shared total/type admission, queues, capabilities, scheduling, stale ownership, retries, cancellation, shutdown release, versioned graphs, conditional outputs, incident recovery, pause/resume, and draft/publication rules. Competing operations use separate store instances/connections. Schedule checks include concurrent dispatch, overlap coalescing, and a daylight-saving fallback occurrence.

Deployment tests on both providers cover exact versions, processor/queue matching on the same worker, recoverable incidents and waiting steps, future eligibility, enabled schedule targets, truncation, and payload exclusion. Waiting-to-ready activation records a fresh eligibility timestamp. The dashboard report requires metadata permission.

The relocation gate caught sub-millisecond early eligibility during this change. Explicit due times now round up to storage precision, immediate work uses the current millisecond, and PostgreSQL's clock uses floor rather than round-to-nearest. New provider regressions verify fractional and exact timestamps, pre-epoch conversion, and rejection beyond the representable range. The original relocation assertion remains intact. An execution-deadline race observed in hosted dependency CI was also corrected: elapsed monotonic budget is checked even if cancellation callback delivery is late. Existing cooperative/non-cooperative deadline tests pass on both runtimes in the final gate.

Flow-start tests race eight callers on independent connections, reject changed requests, return existing runs after restart/retirement, and inject receipt failures to verify rollback of definitions, runs, and steps. Correlation without an idempotency key still permits independent runs.

A literal schema-1 fixture verifies additive upgrade, unchanged identity/input, preserved Stop flow semantics, published definition adoption, and rejection of an unknown schema. These checks do not permit old schema-1 workers to run alongside schema-2 workers; follow ADR-008.

Administrative tests cover duplicate/conflicting command IDs, revision/attempt preconditions, and audit persistence failure. An injected audit-write failure on each real provider rolls back both the mutation and command receipt; retry succeeds after removing the fault. Dashboard tests cover host authentication, permission separation, protected exception/payload data, CSRF validation, actor attribution, nested routes, and PathBase.

Worker regressions cover scoped lifetimes, dependency validation, interrupted setup, local capacity while revoked non-cooperative handlers still run, and cooperative/non-cooperative deadline behavior. Progress and error writes reject stale ownership.

## Live sample

The embedded Angular console was exercised through Demo.Host at `/operations/jobs` against a separate SQLite file. Approved and review branches completed. An intentional negative-amount failure created an Incident with waiting dependents; its inspector displayed the saved diagram, attempt evidence, and protected exception stack. An audited recovery retried the same logical step and retained prior attempts. A separate flow draft was saved without executing it. A recurring schedule was saved as a draft and published through the live console while its trigger remained paused; both actions appeared with actor/reason in the audit trail. Narrow-screen checks at 375 CSS pixels found no document overflow, and keyboard skip-to-content remained visible on focus. These are manual local checks, not a browser accessibility certification.

On 8 October the rebuilt host rendered the new processor-coverage inventory against the existing SQLite incident data: all three required processor/queue pairs matched the recent worker heartbeat; waiting/incident steps had no ready count. Historical heartbeats were excluded from matching after 60 seconds. The new view had no document overflow at 375 CSS pixels and no observed browser warnings/errors. A screenshot is retained locally as `artifacts/workthrough-coverage.png`.

## Hosted verification

The initial pushed main commit `4529af9` passed both GitHub Actions jobs on Ubuntu: [Verify run 37734216323](https://github.com/SDKexplorer/WorkThrough/actions/runs/37734216323). That run predates deployment preflight and the fixes above. The 91-case evidence here is the completed local gate; consult the workflow for the latest pushed revision's hosted result.

## Limits of this evidence

- Local packing is not package publication. Hosted verification applies only to the exact commit shown by each workflow run.
- This is an alpha correctness baseline, not an availability, performance, security, accessibility, or disaster-recovery certification.
- The oldest consumer SDK actually executed is 8.0.425. SDK 8.0.100, .NET 9, Native AOT, and full trimming have not been verified.
- SQL Server, MySQL, MongoDB, and further adapters are not implemented or tested.
- The standalone OIDC sample compiles; login against a real external identity provider still requires verification with deployment-specific credentials.
- Shared limits bound valid leases, not effects from a process resuming after losing its lease.
- Persisted restart, expiry, and targeted fault tests do not replace multi-machine partitions, exhaustive process-kill/ambiguous-commit campaigns, or production load tests.

See `plan.md` for remaining work rather than treating a green suite as a complete replacement for Hangfire or Wolverine.
