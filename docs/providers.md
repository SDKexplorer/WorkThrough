# Storage providers

SQLite, PostgreSQL and SQL Server implement the storage port. Claims of verification must refer to the test run recorded in `verification.md`; a compiled provider is not enough.

SQLite uses a local file and an immediate write transaction. Use a local durable volume. Network filesystems and multi-machine shared SQLite files are not supported. SQLite driver operations execute synchronously internally despite its async API, so use PostgreSQL for a networked worker fleet.

PostgreSQL uses ordinary tables and a short transaction-scoped advisory lock. The current table prefix is `dj_`; use a separate database or PostgreSQL search path for isolation. The lock serializes writes within the database, including separate schemas; this conservative baseline requires benchmarking before large deployments.

## Provider contract

- Persist before successful enqueue acknowledgment. Duplicate idempotency keys are atomic, and conflicting work is rejected.
- Use database time for leases and shared admission. Claims recover expired attempts, check total/type capacity, and issue fresh tokens atomically.
- Persist eligibility at millisecond precision, rounding fractional `NotBefore` instants up. Database clocks round down to milliseconds so conversion never admits work before its saved eligibility. This does not remove application/database clock skew; synchronize deployment clocks.
- Never accept stale or expired settlement tokens.
- Commit parent completion and successor activation together, including branch outcomes.
- Commit idempotent flow-start receipts with the run and all steps. Identical retries return the accepted run, even after retirement; conflicting requests fail without writes.
- Keep queries independent of handler assemblies and bound list reads. Deployment inventory uses SQLite deferred / PostgreSQL Repeatable Read snapshots, returns grouped metadata only, and refuses a complete coverage verdict when truncated.
- Implement `IExecutionQueries` with direct metadata projections, bounded pages/time windows and deterministic continuation. SQLite/PostgreSQL use read snapshots without the admission write lock; the new indexes are additive. See [execution-query semantics](execution-queries.md).
- Support initialization idempotently and reject unknown schema versions. Destructive production migration is never implicit.
- Run the complete storage conformance suite against a real instance of the provider.

## PostgreSQL test database

`IOperationHistoryQueries` provides snapshot audit/archive searches and a supplied-policy archive preview. Searches share five-second query budgets, 1–100 row pages and seven-day maximum windows. Audit SQL projects only bounded actor issuer/subject and operation metadata; JSON above 16,384 characters is not parsed. Archive SQL never selects the body. Preview shares the actual manual archive eligibility selector and returns at most ten units plus a `HasMoreCandidates` flag. It does not test archive byte/evidence limits or configure automatic cleanup.

Additive indexes cover audit time/action/target, archive time/correlation and terminal job selection. The terminal-job index is created after legacy upgrades add `terminal_at`, preserving the schema-1 migration order. These indexes do not change schema version or dispatch/settlement semantics. Initialize storage with a deployment credential before starting a read-only operations host. A result limit bounds returned rows; SQL plans and the five-second budget also matter when scanning a large retained dataset. The planned contention campaign remains necessary.

`DURABLEJOBS_POSTGRES` must point to a disposable database whose user can create/drop schemas. Each test creates a unique `dj_test_*` schema and drops only that schema when finished. Test output does not print the connection string.

```sh
docker run --rm -d --name durablejobs-postgres-test \
  -e POSTGRES_PASSWORD=durablejobs-test -e POSTGRES_DB=durablejobs \
  -p 127.0.0.1:55432:5432 postgres:17-alpine
export DURABLEJOBS_POSTGRES='Host=127.0.0.1;Port=55432;Database=durablejobs;Username=postgres;Password=durablejobs-test'
dotnet test tests/WorkThrough.Tests --configuration Release
docker stop durablejobs-postgres-test
```

When server environment variables are absent, the shared suite enumerates SQLite cases only. CI supplies PostgreSQL and SQL Server explicitly. SQL Server is implemented with native qualification recorded separately; Oracle remains planned. See the [portability, migration and verification plan](../specs/storage-evolution.spec.md). MySQL and MongoDB remain unprioritized possibilities.

The MCP verification gate also exercises metadata/context queries through SQLite `Mode=ReadOnly` and PostgreSQL connections with `default_transaction_read_only=on`, without storage initialization. Both passed on .NET 8 and 10. Production read-only roles still need deployment-specific grants; this is query compatibility evidence, not a role provisioning test. See [MCP hosting](mcp.md) and [verification](verification.md).

`IExecutionInspectionQueries` uses the same bounded read snapshots and timeouts. Selected-attempt text is clipped in SQL; payload/output and full job records are never selected. Saved dependency projection uses SQLite `json_each` and PostgreSQL lateral `jsonb_array_elements` inside a 262,144-character definition bound, returning only job-ID pairs. Node, edge and journal pages are separate sections of the same snapshot; the global journal floor is explicit. No schema or settlement semantics changed. Additional providers must implement the dependency projection before advertising flow inspection.

SQLite initialization now enables WAL by default for local multi-process use, and every engine connection uses `synchronous=FULL`. `new SqliteStorageOptions(UseWriteAheadLogging: false)` preserves an existing journal mode; it does not convert WAL back to DELETE. All SQLite processes must share a local filesystem, not a network mount. Back up through SQLite's backup API or a consistent snapshot including WAL state. See [SQLite WAL](https://www.sqlite.org/wal.html) and [Microsoft.Data.Sqlite async limits](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async). The dependency-maintenance gate verified native SQLite 3.53.4 from bundle 3.0.5 on macOS ARM64 with WAL/FULL. The earlier full performance campaign used 3.53.3; its retained results remain historical measurements of that version.


Schema 4 adds whole-unit retention holds, policy revisions, maintenance leases/batches/incidents and verified-export receipts on SQLite/PostgreSQL. Stop older writers before upgrading; see [ADR-013](adr/0013-fenced-retention-maintenance.md). `IRetentionQueries` is metadata-only and works through read-only connections. Maintenance uses the existing serialized write transaction, checks its lease before and after every batch, and keeps copy verification outside that transaction. Schema-1/schema-3 upgrades and partial-batch rollback are covered by the shared suite.

## SQL Server setup

`WorkThrough.Storage.SqlServer` supplies `.UseSqlServer(connectionString)`. The implementation targets SQL Server 2022 CU27 Developer on Linux x64 with Microsoft.Data.SqlClient 7.1.0. The native CI qualification and current evidence are tracked in [verification](verification.md). Local ARM-emulated tests are not a supported deployment matrix. Azure SQL and other editions/OS/server versions remain unverified.

Use a dedicated database with `dbo` as the user's default schema. A deployment administrator enables snapshot isolation once:

```sql
ALTER DATABASE [WorkThrough] SET ALLOW_SNAPSHOT_ISOLATION ON;
```

Initialization creates schema 4 transactionally and checks that snapshot isolation is active. Runtime hosts can use a DML credential with `.InitializeStorageOnStart(false)` after deployment initialization. Read-only dashboard/MCP hosts need SELECT grants and disable initialization. Production identities/passwords belong in host configuration or a secret store; the library does not create production logins.

Every engine write takes a transaction-owned application lock for at most five seconds before failing. Multi-query inspection uses snapshot isolation. Locks, lease checks, administrative receipts/audit and successor activation retain the shared transaction boundary. See [ADR-014](adr/0014-sql-server-storage.md).

Identities use binary Unicode collation. SQL Server explicitly rejects trailing ASCII spaces in identities and filters, because SQL equality would otherwise collapse distinct keys. Physical indexed-value bounds are 120 UTF-16 units for processor/flow/schedule keys, 512 for queues, 400 for worker/step/subscription/idempotency keys and 100 for archive destinations. Oversized values fail without truncation or partial acceptance. Correlation text can be longer and is clipped by metadata views; correlation searches use bounded history indexes plus filtering. These restrictions must be checked before a separately planned import from another store.

For tests, set `DURABLEJOBS_SQLSERVER` to a **disposable server** login that can create/drop databases and test logins. The suite creates unique `dj_test_*`, `wt_exercise_*` and `wt_package_*` databases, and removes only its own resources. Never use production credentials/data. CI supplies a pinned x64 Developer container and disposable credentials; SELECT-only MCP login tests verify database-enforced read access. `scripts/verify.sh` includes this provider only when the variable is present.
