# Contributing

Read `AGENTS.md` for dependency boundaries and correctness invariants. Use .NET SDK 10; `global.json` allows stable feature-band updates.

Start feature branches from `develop` and target it with pull requests. Promote tested integration work to `main` through a release PR. See [branching and release guidance](docs/branching-and-release.md) for CI, merge strategy and the current GitHub protection limitation.

Run `./scripts/verify.sh`. For storage changes, point `DURABLEJOBS_POSTGRES`, `DURABLEJOBS_SQLSERVER`, `DURABLEJOBS_ORACLE`, `DURABLEJOBS_MYSQL` and `DURABLEJOBS_MARIADB` at disposable databases so the shared suite runs against every provider ([running the provider tests](docs/providers.md#running-the-provider-tests)); CI runs all six. Keep new regressions focused on observable behavior and meaningful failure boundaries.

Before changing persisted formats or public guarantees, add or amend an ADR. Do not renumber existing stable contract versions or rewrite historical attempt diagnostics. Update the plan and verification record when support claims change.

Commit messages should describe the changed behavior. Pull requests should explain the triggering problem, resulting behavior, relevant tests, and remaining limits. Avoid speculative framework abstractions and unrelated cleanup.

Packages are built locally under `artifacts/packages`. Public publishing, package-name ownership, and license selection are maintainer decisions and are not automated by this repository.
