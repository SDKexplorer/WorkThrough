# Branching and public-release preparation

## Branch model

- `main`: release baseline and default branch; changes arrive through pull requests.
- `develop`: integration branch created from `main` on 2026-10-08.
- Short-lived branches: start from `develop` and open a pull request back to it. Squash feature PRs when useful.
- Promote a tested `develop` revision to `main` with a release PR and a merge commit so the two long-lived branches retain shared ancestry. Merge any main-only hotfix back into `develop` through a PR.

An additional staging branch is unnecessary until a distinct deployment requires it. Use version tags and immutable package artifacts for a release. Do not publish NuGet packages automatically as part of ordinary branch CI.

The workflow change in this proposal verifies PRs and pushes to both long-lived branches. Its jobs are `console`, `verify` (PostgreSQL and SQL Server), `oracle` and `mysql` (MySQL and MariaDB). Dependabot version updates target `develop` after the configuration is promoted to the default branch; GitHub security updates still target the default branch. Existing bot PRs are not silently merged or closed. See [Dependabot target-branch semantics](https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-options-reference#target-branch).

## Protection policy and actual enforcement

**Blocked as of 2026-10-08:** GitHub returned HTTP 403 for this private repository's branch-protection API, stating that GitHub Pro or public visibility is required. The current plan does not permit enabling protection. The JSON file below is a prepared configuration, not enforcement. CI alone cannot stop direct pushes. Repository visibility remains private.

Prepared policy in [branch-protection.json](../.github/branch-protection.json):

- Require a pull request and resolved review conversations.
- Require successful `console`, `verify`, `oracle` and `mysql` checks from the verified GitHub Actions app (ID 15368).
- Require the source branch to be current with its target.
- Apply restrictions to administrators too; block force pushes and branch deletion.
- Allow merge commits for release promotion.
- Initially require zero external approvals: the repository currently has one human maintainer, who cannot approve their own PR. Raise this to one when another reviewer is available. PR/check requirements still apply.

After the account plan supports private protection, or after an explicitly approved public release, inspect existing protection/rulesets first and reconcile them with this template. Then apply it from the repository root:

```sh
gh api --method PUT repos/SDKexplorer/WorkThrough/branches/main/protection \
    --input .github/branch-protection.json
gh api --method PUT repos/SDKexplorer/WorkThrough/branches/develop/protection \
    --input .github/branch-protection.json
```

Read both settings back and verify required checks, PR requirement, admin enforcement and force-push/deletion restrictions. Do not treat a successful file commit as proof that GitHub settings changed. Review any existing rules before applying the template: the update API replaces supplied settings. GitHub documents plan availability under [protected branches](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches).

## Public alpha readiness

A public alpha can invite contributors and feedback while keeping the support claims explicit. Source visibility and publishing a stable NuGet release are separate decisions. No visibility change, license selection or package publication is authorized by this proposal.

Before changing visibility:

- [ ] Choose and add a maintainer-approved license; inspect third-party notices and assets.
- [ ] Review the complete reachable Git history, branches, release artifacts and examples for credentials/private data. Rotate any exposed real secret; deleting its latest copy is insufficient. This review has not been run by the planning task.
- [ ] Establish a private security-reporting channel and enable the applicable GitHub security controls; update `SECURITY.md`.
- [ ] Apply and verify branch protection, including the solo-maintainer approval policy.
- [x] Document the verified provider/runtime matrix and alpha limitations; distinguish read-only MCP and implemented providers from planned controlled actions and sustained qualification.
- [ ] Decide package ownership, release versioning and artifact provenance before publishing to NuGet.
- [ ] Verify clean public-consumer setup, examples and links; add contribution/issue guidance suitable for outside users.

Changing visibility makes repository content and history publicly accessible. The maintainer must explicitly decide that action after reviewing this checklist. Keeping the repository private and upgrading the account plan is a separate way to enable protection.
