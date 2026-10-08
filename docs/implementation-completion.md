# Completion work

Requested on 2026-10-08. This tracks implementation of the concrete gaps discussed with the maintainer, rather than treating every possible future provider or workflow language feature as a release requirement.

- [x] Typed definitions, explicit processor registration, display names, compatibility fixtures.
- [x] Coordinated identity aliases with shared admission and stale-deployment fencing.
- [x] Scoped middleware, validation, retry classification, explicit rejection/deferral, post-settlement observers.
- [x] Business transaction enqueue/outbox integration with real-provider rollback checks.
- [x] Queue pause, worker drain, expiry, and intentional replay with causation.
- [x] Bounded archival of terminal executions/whole finished flows; retain deduplication receipts, definitions, and audits independently.
- [x] Durable flow signals and timeouts without an occupied processor slot.
- [x] Process termination, lost commit acknowledgment, connection failure, and restored-store recovery proofs.
- [x] Samples, dashboard operations, migration guidance, full local verification, and hosted verification workflow.
- [x] Repository-wide readability enforcement and typed samples with named configuration.
- [x] Repeatable performance scenarios with correctness checks, latency distributions, throughput, resource measurements, and recorded environment.

Local evidence is recorded in [verification.md](verification.md): 121 tests on each of .NET 8 and .NET 10, package-only consumers, published relocation/alias proofs, runnable samples, dashboard checks, and the independent-process smoke campaign. The [full performance campaign](performance.md) verified 12,000 measured jobs on real SQLite/PostgreSQL stores. The broader [roadmap](plan.md) deliberately retains future features and production-readiness work.

After pushing, the [Verify workflow](https://github.com/SDKexplorer/WorkThrough/actions/workflows/verify.yml) is the source of hosted evidence for that exact commit; local results do not substitute for its status.

Use one store per environment initially. Active and archived records have separate tables and query paths. Cross-database/object-storage archival is an export-and-verify operation followed by explicitly authorized removal, never a distributed transaction on the worker path. Moving tables alone does not reduce database disk usage; provider maintenance and archive retention are distinct operations.

Public package publication and the project license remain maintainer release decisions. Arbitrary SQL dialects, general BPMN, unrestricted expression execution, and exactly-once external effects are not promised.
