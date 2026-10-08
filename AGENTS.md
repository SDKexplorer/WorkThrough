# WorkThrough contributor and agent guide

Read `README.md`, `docs/architecture.md`, and `docs/plan.md` before changing behavior. This is an early .NET library, not a production-certified engine.

## Priorities

1. Persisted work must survive handler namespace, class, and assembly moves.
2. A successful enqueue means storage committed the job.
3. Crashes must not silently lose work or manufacture successful attempts.
4. Configuration and operational state must be explainable.

## Architecture

- `WorkThrough.Core` owns contracts, state rules, use cases, and storage ports. It references no hosting, web, ORM, or database package.
- Hosting, storage, and dashboard depend inward on Core. The dashboard must never depend on application handlers.
- Use small concrete use cases. Separate reads and writes where useful. Do not introduce MediatR, generic repositories, event sourcing, or an interface for every class without a demonstrated need.
- Keep public contract identities explicit. Never persist CLR names, assembly-qualified names, method names, delegates, or closures as execution identity.
- Generated registration is a convenience over the same explicit registry API. It must not introduce a second dispatch system.
- Treat package APIs, consumer compilers, runtime targets, stored formats, and database schema compatibility as separate contracts. Follow ADR-005 when implementing multi-version support; the executed runtime/SDK matrix is recorded in docs/verification.md.

## Reliability invariants

- Delivery is at least once. Do not claim exactly-once external effects.
- Global limits apply to admitted, unexpired leases. A partitioned or stalled process can outlive its lease; handlers need idempotency and resources may need fencing.
- Claim, lease ownership, concurrency admission, and completion must be atomic at the provider boundary.
- State changes require the current unexpired lease token. Stale workers cannot settle attempts.
- Shutdown is not job success. Stop acquisition before disposing handler scopes.
- Workflow completion and successor activation must share a transaction.
- Correlation groups observations; it is not an automatic lock or deduplication key.

## Change discipline

- Enable nullable references and forward cancellation tokens through I/O.
- Keep persisted schema and API changes additive where practical. Write an ADR for changes to compatibility or delivery guarantees.
- Add tests for observable failure modes, especially restarts, multiple independent hosts, expired ownership, and published artifact composition.
- Never claim a provider is supported based only on compilation or an in-memory substitute.
- Never claim a runtime is supported based only on adding a TFM. Execute package consumers on the advertised runtime and check the oldest supported compiler for generators.
- Framework-specific code must preserve persisted semantics and validation. Test saved data and mixed supported deployments before claiming backward compatibility.
- Do not log full payloads by default. HTML-encode untrusted dashboard text and protect dashboard endpoints through host authorization.
- Follow ADR-007 for dashboard security work: reuse host identity, derive actors from trusted server context, enforce action/resource permissions on the server, and commit administrative changes with their audit records atomically. Preserve permission separation and the same-transaction audit/command receipt invariant.
- Keep README/plan status honest: implemented, verified, experimental, and planned are different.
- Angular is the accepted dashboard frontend. The live Angular entry point must only display real API data. Keep archived concept fixtures isolated and unreachable from the production entry point. Build static assets before .NET packaging so NuGet consumers do not need Node.
- Do not publish packages or choose a project license without an explicit maintainer decision.

## Verification

Run `./scripts/verify.sh` for build, tests, refactor survival, and local packages. PostgreSQL tests require `DURABLEJOBS_POSTGRES` pointing to a disposable test database; see `docs/providers.md`. Do not point integration tests at production data.

Repository skills live under `.agents/skills/`. Use the provider skill for persistence changes and the release skill for packaging or release verification.

## Readability

Use four-space indentation and consistent Allman braces. Expand multi-statement blocks. Separate a completed control-flow block from the next statement with a blank line; do not add a blank line merely before a closing brace. Put a blank line before a return that follows other statements in the same scope. A scope containing only a return needs no extra blank line. Keep related statements together, prefer named application definitions/configuration to repeated string literals, and extract focused methods when a lifecycle operation becomes difficult to read.

Run `scripts/format.sh` to apply Roslyn brace/whitespace formatting and the syntax-aware spacing rules, and `scripts/format.sh --check` to enforce them. Strings, raw SQL, comments, and generated output are not edited as executable statements.
