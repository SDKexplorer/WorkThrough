# ADR-012: retention automation and provider expansion

Status: retention implemented on SQLite/PostgreSQL, 2026-10-10; provider expansion remains proposed. [ADR-013](0013-fenced-retention-maintenance.md) records schema-4 guarantees and remaining qualification.

## Context

Manual archival already preserves terminal execution evidence and deduplication receipts. It does not provide automatic retention or bound every growing table. The maintainer also requests SQL Server and Oracle. Existing relational SQL and initialization assume SQLite/PostgreSQL behavior, so a driver replacement is insufficient.

## Decision

Add opt-in maintenance hosting over the existing administrative operations. Persist policy revisions, holds, fenced coordination and batch receipts. Keep completed flows together, preserve active dependencies and receipt lifetimes, and make blocked/oversized candidates visible. Default maintenance to disabled. Separate archive creation, verified external export, body purge and provider-specific physical maintenance.

Add SQL Server before Oracle, through optional provider packages and focused internal seams for SQL, schema migration, binding, clocks and locking. Preserve the same transactional execution contract. Prove each provider against real instances before advertising support. Keep the existing conservative write-serialization baseline until correctness and workload evidence justify a separate optimization.

Oracle requires a resumable migration lifecycle rather than assuming schema DDL can share the ordinary rollback model. All providers need explicit identity equality, null/empty-value behavior and unique-key semantics. Separate runtime and migration privileges. Adding a provider does not authorize live cross-provider store migration.

## Alternatives and trade-offs

- Automatic row deletion is simpler but can break flow evidence, subscriptions and deduplication. Archive whole terminal units and retain receipts instead.
- Same-database archives reduce active-table pressure but keep substantial storage. Optional verified external export permits later body purge at the cost of another failure/reconciliation path.
- Putting all providers behind an ORM does not establish equivalent leases, locks or schema transitions. Use focused provider code and shared conformance tests.
- Implementing SQL Server and Oracle simultaneously increases the portability change surface. Deliver sequentially, with SQLite/PostgreSQL regression gates after extraction.
- Global write serialization is conservative and measurable. Preserve it initially rather than promising backend-specific concurrency optimizations before fault tests.

## Consequences

Automatic retention gains policy, status, audit and coordination state. Receipts still grow; no fixed-size database claim is made. Large archives require explicit bounds and eventual streaming/export design. External-copy verification cannot make deletion reversible; holds and policy remain necessary.

New providers add driver/server/OS matrices, CI infrastructure, migration tests and operational runbooks. Keep .NET 8 compatibility separate from database support. Failure, contention and restore evidence govern support claims, as described in [storage evolution](../../specs/storage-evolution.spec.md).
