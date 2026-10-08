# Dependency updates

Dependency PRs must preserve the consumer SDK/compiler matrix, Angular peer constraints, native database behavior, and stored-format guarantees. A newer package version alone does not establish compatibility. Keep peer validation and warnings-as-errors enabled.

## Review on 2026-10-08

Nine initial Dependabot PRs were opened against an older baseline. The maintenance branch combines the compatible candidates so their interaction is verified together:

| PR | Update | Assessment |
|---|---|---|
| [1](https://github.com/SDKexplorer/WorkThrough/pull/1) | setup-dotnet 6 | Include; retain SDK 8 and 10 matrix |
| [2](https://github.com/SDKexplorer/WorkThrough/pull/2) | setup-node 6 | Include; npm cache settings are explicit |
| [3](https://github.com/SDKexplorer/WorkThrough/pull/3) | checkout 7 | Include; read-only token and disabled credential persistence remain |
| [4](https://github.com/SDKexplorer/WorkThrough/pull/4) | TypeScript 7.0.2 | Exclude: Angular 22.2 declares `>=6.0 <6.1`; npm correctly rejects the combination |
| [5](https://github.com/SDKexplorer/WorkThrough/pull/5) | Roslyn 5.9.0 | Exclude: package-only SDK 8 consumer fails CS9057 loading a generator built against a newer compiler |
| [6](https://github.com/SDKexplorer/WorkThrough/pull/6) | Hosting 10.0.12 | Include; exercise actual .NET 8/10 hosts |
| [7](https://github.com/SDKexplorer/WorkThrough/pull/7) | Test SDK 18.10.1 | Include; execute tests on both runtimes |
| [8](https://github.com/SDKexplorer/WorkThrough/pull/8) | Npgsql 10.0.3 | Include; use real PostgreSQL conformance and fault tests |
| [9](https://github.com/SDKexplorer/WorkThrough/pull/9) | SQLitePCLRaw bundle 3.0.5 | Include; verify native loading, WAL/FULL, package consumers, recovery and archives |

The old setup-node and SQLite failures were in the non-cooperative execution-deadline regression, rather than dependency restore or native loading. Current main includes the monotonic deadline fix. Refreshing onto that baseline and running the complete gate is required; historical green checks are insufficient for the current implementation.

## Automation boundaries

Dependabot groups routine minor/patch updates by ecosystem, keeps Angular packages together, and limits each ecosystem to three open version-update PRs. Major upgrades remain separate review decisions. TypeScript versions outside the current Angular range and Roslyn minor/major upgrades are excluded explicitly. Revisit those constraints when intentionally changing Angular or the minimum supported compiler, including any security-driven upgrade; these are compatibility boundaries, not permanent exemptions from maintenance.

The Verify workflow runs for every PR and pushes to main. This avoids duplicate push-plus-PR checks on dependency branches. A new revision cancels superseded runs for the same PR; different PRs retain independent checks. Manual runs remain available.

Run `WORKTHROUGH_DOTNET8=<SDK 8 executable> DURABLEJOBS_POSTGRES=<disposable connection> ./scripts/verify.sh`. This includes both runtime suites, published relocation/alias proofs, fresh package-only consumers, samples, readability enforcement, and independent-process performance/recovery smoke tests. Changes to native database packages need the real-provider gate, not just a successful restore.

Configuration semantics follow the [Dependabot options reference](https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-options-reference). The [SQLitePCLRaw v3 migration notes](https://github.com/ericsink/SQLitePCL.raw/blob/master/v3.md) describe continued support for the bundle API; execution on supported platforms remains the verification requirement.
