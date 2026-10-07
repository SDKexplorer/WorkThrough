---
name: durablejobs-release
description: Validate local WorkThrough prerelease packages and deployment compatibility, including published assembly replacement and a package-only consumer. Use for packaging, compatibility changes, or release preparation in this repository.
---

Read `AGENTS.md`, `docs/plan.md`, and `docs/verification.md`. Run `scripts/verify.sh` from the repository root; provide a disposable `DURABLEJOBS_POSTGRES` database when validating that backend.

The assembly proof must use separately published applications, remove the original processor assembly from the old deployment, and execute unchanged persisted work in the replacement deployment. Do not replace it with a registry-only unit test.

The package consumer must restore the locally packed NuGet artifacts without project references and exercise generated registration. Check that the dashboard-only sample references no application processor or contract project.

Report the actual build, test, provider, refactor, and package-consumer results, including skipped checks. Keep package names, supported TFMs, migration policy, and license decisions explicit. Local packing does not authorize publishing to a feed or creating a release.
