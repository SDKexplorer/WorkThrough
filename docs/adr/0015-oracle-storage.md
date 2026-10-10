# ADR-015: Oracle storage and resumable DDL initialization

Status: implemented; qualification in progress, 2026-10-10. See `verification.md` for completed evidence. The initial target is Oracle Database Free 26ai, release 23.26.3.0, with ODP.NET Core 23.26.301 on .NET 8 and 10. Other editions/releases require separate qualification.

## Transactions and inspection

`WorkThrough.Storage.Oracle` uses the shared engine transitions. Every write starts a Read Committed transaction and locks the dedicated schema's singleton `dj_admission` row with `FOR UPDATE WAIT 5`. A missing row or lock timeout refuses the write. Oracle releases the row lock on commit, rollback or session loss. Database UTC milliseconds come from `SYS_EXTRACT_UTC(SYSTIMESTAMP)`. Claim, ownership fencing, attempt settlement, successor activation and administrative receipt/audit retain one transaction. Handlers and external archive uploads execute outside that transaction.

Inspection uses Serializable transactions for a consistent multi-query snapshot without the admission lock. Tables allocate segments immediately during initialization: Oracle deferred segment creation conflicts with Serializable access to fresh tables. SELECT-only credentials are exercised independently from schema owners. The provider does not retry arbitrary business transactions after errors; callers reconcile ambiguous acceptance using stable receipts. A diagnostic trace of concurrent test-schema DDL identified a recursive `cdef$` dictionary read failing with ORA-08177, so independent fixtures create/drop schemas sequentially. This does not serialize the deliberate competing connections/processes within a test. Deployment DDL must be coordinated; a serialization error fails the read instead of returning a partial snapshot.

SQL fragments own pagination, casts, quoted attempt number and JSON projections. Library bind markers are translated outside string literals to named Oracle parameters. Parameters use the database's required AL32UTF8 encoding and CLOBs for large text. Single-row MERGE operations run under the admission row lock. Shared batches become separate commands in the same existing transaction. Application business SQL remains native Oracle SQL, untouched by the adapter.

`InTransactionAsync` callbacks must perform DML only. Oracle DDL commits implicitly and would break the engine's atomic boundary. Provision business tables separately, before starting workers; do not run DDL, explicit commits or autonomous transactions in callbacks. This is a caller contract, not something an arbitrary ADO.NET callback can be sandboxed to enforce.

## Initialization

Use one dedicated, unquoted schema. SYS/SYSTEM are not valid storage schemas. A migration credential initializes it before runtime hosts start; runtime hosts disable initialization. `OracleStorageOptions.Schema` selects the owner when connecting with a different DML/SELECT-only login. `MigrationConnectionString` can select the schema owner's deployment credential separately from runtime credentials. Credentials remain in host configuration/secret management.

Oracle DDL cannot be rolled back as one transaction. Initialization therefore takes a schema-specific DBMS_LOCK session lock on an **unpooled** connection, with `release_on_commit = false`. This lock survives DDL commits and releases when the connection closes. Only the migration credential needs EXECUTE on SYS.DBMS_LOCK, plus the required DDL privileges and tablespace quota. Runtime writers need SELECT/INSERT/UPDATE/DELETE on engine tables, and inspection hosts need SELECT only. Runtime hosts do not require DBMS_LOCK.

`dj_schema` records version 4, Installing/Ready status, an ordered checkpoint and the SHA-256 of the exact DDL definition. Each DDL step checks whether its named object exists, executes if needed, then commits its checkpoint. A restart after DDL but before its checkpoint recognizes the existing object and continues. Final singleton seeds and Ready status commit together. Runtime connection opening and acquisition both refuse incomplete/incompatible schemas. Unknown versions, changed migration definitions and invalid checkpoints fail closed.

This is recovery of this provider's interrupted installation, not arbitrary schema drift repair. The schema owner must keep the schema dedicated to WorkThrough; initialization does not certify independently modified objects merely because their names exist. Stop all older writers before future migrations. Oracle has no released schema-1/2/3 format to migrate.

## Text and physical bounds

Use AL32UTF8 with binary comparison sessions. Case, composed/decomposed Unicode and trailing spaces remain distinct. Oracle collapses empty VARCHAR2 strings to null; the provider explicitly rejects empty identity/filter values, including empty CorrelationId, rather than silently changing their meaning. Use null for absent correlation. JSON empty strings are serialized as JSON and remain supported. Empty diagnostic text is stored as EMPTY_CLOB().

Physical VARCHAR2 limits are 120 characters for processor/flow/schedule keys, 512 for queues, 400 for worker/step/subscription/idempotency keys, 100 for export destinations and 2,000 for correlation. Oracle's standard 4,000-byte VARCHAR2 limit also applies, so supplementary Unicode can hit the byte limit first. Oversized values fail atomically; they are never truncated into an identity. Correlation filtering uses bounded history indexes and a residual predicate. Cross-provider data transfer requires checking these restrictions and remains a separate feature.

## Qualification

Run shared real-provider conformance, audit rollback, scheduling, signals, archive/retention, metadata/MCP and Unicode identity tests. Additional tests cover concurrent initialization, an interrupted DDL checkpoint, missing admission rows and actual database-enforced SELECT-only/DML-only credentials. Package consumers, published assembly relocation and killed-worker/two-process workloads must pass before advertising the tested matrix. The x64 CI job runs separately from SQL Server to avoid memory contention between database services. No production throughput or arbitrary engine-version coexistence claim is implied.

References: [Oracle transactions](https://docs.oracle.com/en/database/oracle/oracle-database/26/sqlrf/SET-TRANSACTION.html), [DBMS_LOCK](https://docs.oracle.com/en/database/oracle/oracle-database/26/arpls/DBMS_LOCK.html), [ODP.NET requirements](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/InstallSystemRequirements.html).

Migration fingerprints normalize line endings so builds from LF and CRLF source checkouts identify the same DDL sequence. This normalization changes no stored processor identity or application data.
