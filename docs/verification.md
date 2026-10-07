# Verification record

Date: 2026-10-07. Local macOS ARM64. Repository SDK 10.0.400; runtime packages target net8.0 and the Roslyn generator targets netstandard2.0. The compatibility gate also uses SDK 8.0.425 / runtime 8.0.31; the replacement worker runs on runtime 10.0.11.

## Completed

`WORKTHROUGH_DOTNET8=<SDK 8 executable> DURABLEJOBS_POSTGRES=<disposable connection> ./scripts/verify.sh` completed successfully. The retained local log is `artifacts/verification-latest.log` (ignored by Git).

| Check | Result |
|---|---|
| Angular formatting and production build | Passed; static console embedded for package consumers |
| Release solution build | Passed, zero warnings and errors |
| Automated suite on .NET 8 | 73 cases passed |
| Automated suite on .NET 10 | 73 cases passed |
| SQLite | Real file databases, independent connections and restarts |
| PostgreSQL | Real PostgreSQL 17 Alpine container; isolated schema per case |
| Published assembly replacement | Passed from producer runtime 8 to worker runtime 10 after removal of the original processor DLL |
| Relocation scenarios | Typed job, future-due raw-string processor, saved flow step; stored identities and inputs unchanged |
| Local NuGet packaging | Seven WorkThrough 0.1.0-alpha.1 packages produced |
| Package-only consumers | SDK 8 and SDK 10 passed with separate fresh caches, local package source mapping, and no project references |
| Generated registration | Typed handlers and direct raw-string processors discovered and executed from packages |
| Direct processor sample | Passed immediate processor execution and persisted recurring schedule setup |
| Repository skills | Both SKILL.md files previously passed the skill-creator validator |

The provider suite covers committed acceptance, enqueue deduplication/conflicts, shared total/type admission, queues, capabilities, scheduling, stale ownership, retries, cancellation, shutdown release, versioned graphs, conditional outputs, incident recovery, pause/resume, and draft/publication rules. Competing operations use separate store instances/connections. Schedule checks include concurrent dispatch, overlap coalescing, and a daylight-saving fallback occurrence.

A literal schema-1 fixture verifies additive upgrade, unchanged identity/input, preserved Stop flow semantics, published definition adoption, and rejection of an unknown schema. These checks do not permit old schema-1 workers to run alongside schema-2 workers; follow ADR-008.

Administrative tests cover duplicate/conflicting command IDs, revision/attempt preconditions, and audit persistence failure. An injected audit-write failure on each real provider rolls back both the mutation and command receipt; retry succeeds after removing the fault. Dashboard tests cover host authentication, permission separation, protected exception/payload data, CSRF validation, actor attribution, nested routes, and PathBase.

Worker regressions cover scoped lifetimes, dependency validation, interrupted setup, local capacity while revoked non-cooperative handlers still run, and cooperative/non-cooperative deadline behavior. Progress and error writes reject stale ownership.

## Live sample

The embedded Angular console was exercised through Demo.Host at `/operations/jobs` against a separate SQLite file. Approved and review branches completed. An intentional negative-amount failure created an Incident with waiting dependents; its inspector displayed the saved diagram, attempt evidence, and protected exception stack. An audited recovery retried the same logical step and retained prior attempts. A separate flow draft was saved without executing it. This is manual local UI evidence, not a browser accessibility certification.

## Limits of this evidence

- GitHub Actions is configured but has not yet run on a remote repository. Local packing is not package publication.
- This is an alpha correctness baseline, not an availability, performance, security, accessibility, or disaster-recovery certification.
- The oldest consumer SDK actually executed is 8.0.425. SDK 8.0.100, .NET 9, Native AOT, and full trimming have not been verified.
- SQL Server, MySQL, MongoDB, and further adapters are not implemented or tested.
- The standalone OIDC sample compiles; login against a real external identity provider still requires verification with deployment-specific credentials.
- Shared limits bound valid leases, not effects from a process resuming after losing its lease.
- Persisted restart, expiry, and targeted fault tests do not replace multi-machine partitions, exhaustive process-kill/ambiguous-commit campaigns, or production load tests.

See `plan.md` for remaining work rather than treating a green suite as a complete replacement for Hangfire or Wolverine.
