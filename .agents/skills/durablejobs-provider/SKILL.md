---
name: durablejobs-provider
description: Add or change a WorkThrough persistence provider, including atomic admission, ownership fencing, workflow transitions, and backend conformance verification. Use for this repository's storage adapters, not ordinary application queries.
---

Read `AGENTS.md`, `docs/providers.md`, and ADR-003 from the repository root. Preserve the guarantees of `IJobStore`; a generic repository or an EF provider is not evidence of equivalent behavior.

Explain the backend's actual claim transaction, database clock, lock lifetime, and completion fencing. Keep handler execution outside storage transactions. Persist predecessor completion and successor activation together. Check that unsupported contracts and throttled jobs do not consume attempts.

Extend `StorageConformanceTests` with a real disposable instance of the provider. Exercise separate connections, stale tokens, competing claims, restart recovery, idempotent enqueue, immutable flows, and branch transitions. Never substitute an in-memory database for backend locking tests. Do not use production data for verification.

Update `docs/providers.md` and `docs/verification.md` with exactly what ran. If the backend cannot be run, label it unverified and keep that limitation visible in the plan. Read `docs/architecture.md` when changing delivery or compatibility semantics and record that decision in an ADR.
