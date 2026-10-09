# ADR-014: SQL Server storage and explicit identity bounds

Status: implementation under verification, 2026-10-10. Native x64 CI and the complete package/runtime gate must pass before the provider is listed as verified.

## Decision

Add `WorkThrough.Storage.SqlServer` using Microsoft.Data.SqlClient 7.1.0 and .NET 8 assets. The initial server target is SQL Server 2022 Developer on Linux x64, CU27 in the pinned CI image. Other server versions, Azure SQL and Windows deployments require their own qualification. Local execution through ARM emulation is debugging evidence only.

Use a dedicated database, the default `dbo` schema and a transaction-owned exclusive `sp_getapplock` resource named `WorkThrough:admission`. A negative lock result fails the transaction; cancellation and the five-second lock timeout never permit unfenced writes. SQL Server releases the application lock on commit/rollback/disconnection. All engine writes use this boundary; handlers and external archive uploads do not. `SYSUTCDATETIME` and `DATEDIFF_BIG` supply database UTC milliseconds. Current unexpired tokens still fence completion, renewal and retention batches.

Use snapshot transactions for multi-query inspection. A deployment administrator must enable `ALLOW_SNAPSHOT_ISOLATION`; initialization checks that it is active and does not alter database settings automatically. Snapshot inspection does not take the admission application lock. Runtime-only hosts disable initialization after a deployment credential has provisioned schema 4. Unknown versions fail initialization. SQL Server schema creation is transactional and repeatable; schemas 1–3 were never released for this provider and are not accepted as SQL Server migrations.

Keep state transitions shared. Small overridable SQL fragments own pagination, lengths, substring/casts and single-row upserts. Provider code owns DDL and JSON projections. SQL Server uses update/insert or insert-if-absent under the existing exclusive write lock, without MERGE or a general SQL-rewriting layer. Caller business SQL continues to use the original ADO.NET connection/transaction untouched.

## Accepted values and equality

Every stored text column uses `Latin1_General_100_BIN2`, independent of the database default collation. Unicode and case distinctions survive. Payloads remain Unicode JSON, so empty JSON strings and JSON null retain their meaning. Nullable job/outbox idempotency keys and flow-step pairs use filtered unique indexes, permitting multiple absent keys.

SQL Server pads string comparisons with spaces even with binary collations. This provider therefore rejects identity/filter strings ending in ASCII space instead of trimming or equating them. Parameter validation rejects recognized identity inputs; schema CHECK constraints are a second boundary. The failure rolls back the acceptance and its receipt. This is an explicit provider restriction, not a metadata migration and not a change to SQLite/PostgreSQL. Existing data containing such identities cannot be copied into this provider without a separately reviewed mapping.

SQL Server physical bounds are UTF-16 code units: processor/flow/schedule keys 120 (also validated by Core), queues 512, worker/step/subscription/idempotency keys 400, and archive destinations 100. Correlation text remains `nvarchar(max)` with the trailing-space restriction; existing inspection filters still allow 512 units and project longer saved metadata as clipped. Too-long indexed values fail atomically without truncation. Null and empty correlation values remain distinct. Payload, output, evidence and JSON body bounds remain those of the shared engine.

Length projections count UTF-16 units on SQL Server, while SQLite/PostgreSQL count Unicode characters. The projection limits are conservative on supplementary characters and may mark SQL Server text clipped earlier; neither identity nor payload is rewritten. Correlation history uses time indexes with a residual correlation predicate, because unbounded correlation text cannot be an ordinary SQL Server index key. Large-data query/maintenance contention qualification remains necessary.

## Verification and limits

Extend the same conformance suite, using isolated disposable databases and real SELECT-only SQL logins for MCP query hosts. Add case/Unicode/empty/null, nullable uniqueness, trailing-space rejection, length-boundary, repeat-initialization and unknown-version cases. Run generated package consumers on .NET 8 and 10, separately published processor relocation, two-process admission/performance smoke and killed-worker recovery. Record actual results in `verification.md`.

No exactly-once effects, live database switching, arbitrary engine-version mixing or production throughput certification is implied. Oracle still requires its own schema, parameter, LOB and interrupted-DDL implementation.
