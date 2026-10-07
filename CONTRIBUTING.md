# Contributing

Read `AGENTS.md` for dependency boundaries and correctness invariants. Use .NET SDK 10; `global.json` allows stable feature-band updates.

Run `./scripts/verify.sh`. For storage changes, include a disposable PostgreSQL database through `DURABLEJOBS_POSTGRES` so the shared suite runs against both implemented providers. Keep new regressions focused on observable behavior and meaningful failure boundaries.

Before changing persisted formats or public guarantees, add or amend an ADR. Do not renumber existing stable contract versions or rewrite historical attempt diagnostics. Update the plan and verification record when support claims change.

Commit messages should describe the changed behavior. Pull requests should explain the triggering problem, resulting behavior, relevant tests, and remaining limits. Avoid speculative framework abstractions and unrelated cleanup.

Packages are built locally under `artifacts/packages`. Public publishing, package-name ownership, and license selection are maintainer decisions and are not automated by this repository.
