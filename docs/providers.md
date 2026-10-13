# Storage providers

Six databases implement the storage port through five packages. A claim that a provider works refers to a test run recorded in [verification](verification.md); compiling is not evidence.

| Database | Package and registration | Tested release | Writes that admit work | Decision record |
|---|---|---|---|---|
| SQLite | `WorkThrough.Storage.Sqlite`, `UseSqlite` | bundled 3.53.4, WAL | One immediate transaction at a time | — |
| PostgreSQL | `WorkThrough.Storage.PostgreSql`, `UsePostgreSql` | 17 | Transaction-scoped advisory lock | — |
| SQL Server | `WorkThrough.Storage.SqlServer`, `UseSqlServer` | 2022 CU27 Developer, Linux x64 | Transaction-owned application lock | [ADR-014](adr/0014-sql-server-storage.md) |
| Oracle | `WorkThrough.Storage.Oracle`, `UseOracle` | Free 26ai 23.26.3.0 | Lock on a schema-local admission row | [ADR-015](adr/0015-oracle-storage.md) |
| MySQL | `WorkThrough.Storage.MySql`, `UseMySql` | 8.4.11 | Lock on an admission row | [ADR-029](adr/0029-mysql-storage.md) |
| MariaDB | `WorkThrough.Storage.MySql`, `UseMySql` | 11.4.13 | Lock on an admission row | [ADR-029](adr/0029-mysql-storage.md) |

Managed services built on these engines (Azure SQL, Amazon RDS and Aurora, Azure Database for PostgreSQL or MySQL, Neon) are likely compatible but not verified.

## Choosing a provider

- **One machine:** SQLite. Several processes on that machine can share the file.
- **Several machines:** any server database. Pick the one you already operate; the engine behaves the same on each.
- **Throughput:** PostgreSQL is the fastest measured server provider; see [throughput](#throughput) below.

## Provider contract

Every provider must:

- persist before acknowledging an enqueue. Duplicate idempotency keys are atomic, and conflicting work is rejected;
- use database time for leases and shared admission. Claims recover expired attempts, check every limit and issue fresh tokens atomically;
- persist eligibility at millisecond precision, rounding fractional `NotBefore` instants up. Database clocks round down, so conversion never admits work before its saved eligibility. Immediate envelopes use the accepting database's clock ([ADR-016](adr/0016-immediate-acceptance-clock.md));
- never accept stale or expired settlement tokens;
- commit a step's completion and its successors' activation together, including branch outcomes;
- commit idempotent flow-start receipts with the run and all its steps. Identical retries return the accepted run, even after retirement; conflicting requests fail without writes;
- keep queries independent of handler assemblies and bound every list read. Deployment inventory refuses a complete coverage verdict when truncated;
- implement the metadata queries (`IExecutionQueries`, `IExecutionInspectionQueries`, `IOperationHistoryQueries`, `IRuntimeQueries`, `IRetentionQueries`) with bounded pages, time windows and deterministic continuation, on read snapshots that take no admission lock ([execution queries](execution-queries.md));
- initialize idempotently and reject unknown schema versions. A destructive migration is never implicit;
- pass the complete shared test suite against a real instance.

## SQLite

Uses a local file. Writes that admit work take one immediate transaction at a time.

- **Filesystem:** use a local durable volume. Network filesystems and shared files across machines are not supported.
- **Journal:** initialization enables WAL for local multi-process use, and every connection sets `synchronous=FULL`. `new SqliteStorageOptions(UseWriteAheadLogging: false)` keeps an existing journal mode; it does not convert WAL back.
- **Contention:** connections set `busy_timeout` to the connection string's `DefaultTimeout`, so contending processes wait inside SQLite instead of in long driver retries.
- **Async:** the driver runs operations synchronously behind its async API ([Microsoft.Data.Sqlite async limits](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)), so use a server database for a networked fleet.
- **Backups:** use SQLite's backup API or a consistent snapshot that includes WAL state ([SQLite WAL](https://www.sqlite.org/wal.html)).

## PostgreSQL

Uses ordinary `dj_` tables. Writes that admit work take a short transaction-scoped advisory lock. The lock is database-wide, so stores in different schemas of one database share it; use a separate database for independent stores that must not contend.

- **Isolation:** use a separate database or search path per store.
- **Reads:** Repeatable Read snapshots.
- **JIT:** admission and read transactions set `jit = off`, because compiling the admission predicates cost far more than running them.

## SQL Server

Use a dedicated database with `dbo` as the user's default schema. An administrator enables snapshot isolation once:

```sql
ALTER DATABASE [WorkThrough] SET ALLOW_SNAPSHOT_ISOLATION ON;
ALTER DATABASE [WorkThrough] SET READ_COMMITTED_SNAPSHOT ON; -- recommended; see Throughput
```

- **Initialization:** creates the schema transactionally and checks that snapshot isolation is on.
- **Credentials:** runtime hosts can use a DML credential with `.InitializeStorageOnStart(false)` after deployment initialization; dashboard and MCP hosts need SELECT only. The library creates no production logins.
- **Locking:** writes that admit work take a transaction-owned application lock, failing after five seconds. Multi-query inspection uses snapshot isolation.
- **Text:** identities use a binary Unicode collation. Trailing ASCII spaces in identities and filters are rejected, because SQL equality would otherwise merge distinct keys.
- **Bounds:** 120 UTF-16 units for processor, flow and schedule keys; 512 for queues; 400 for worker, step, subscription and idempotency keys; 100 for archive destinations. Oversized values fail without truncation. Correlation text can be longer; searches on it use bounded history indexes plus filtering.
- **Not verified:** Azure SQL, other editions, versions and operating systems. Local ARM-emulated tests are not a supported deployment.

## Oracle

`.UseOracle(connectionString, new OracleStorageOptions(Schema: "WORKTHROUGH"))`. The schema defaults to the connection user.

- **Database:** a dedicated, unquoted schema with AL32UTF8 encoding.
- **Initialization:** run once as the schema owner with CREATE TABLE, tablespace quota and EXECUTE on `SYS.DBMS_LOCK`. Oracle commits DDL on its own, so initialization is resumable and runtime hosts refuse an incomplete schema. `MigrationConnectionString` selects a separate deployment credential.
- **Credentials:** runtime hosts use `.InitializeStorageOnStart(false)` and a DML credential; dashboard and MCP hosts can use SELECT only, with `Schema` naming the owner.
- **Transactions:** business callbacks in `InTransactionAsync` must contain DML only: no DDL, explicit commit or autonomous transaction.
- **Text:** empty identity and filter values, including an empty correlation, are rejected; use null for absence. Unicode JSON, empty JSON strings and trailing-space identities stay intact. Correlation is limited to 2,000 characters and Oracle's 4,000-byte VARCHAR2 ceiling.
- **Reads:** retry a transient ORA-08177 with a fresh snapshot, up to three times; writes are never retried.
- **Pools:** budget all replicas' pools against the database's session and process limits.

## MySQL and MariaDB

`.UseMySql(connectionString)` serves both. Name a dedicated database in the connection string; system databases are refused.

- **Text:** initialization creates InnoDB tables with an exact binary collation that does not pad (`utf8mb4_0900_bin` on MySQL, `utf8mb4_nopad_bin` on MariaDB), so case, accents and trailing spaces in identities stay distinct. Bounds match Oracle's.
- **Versions:** initialization refuses MySQL before 8.0.21 and MariaDB before 10.6.
- **Initialization:** run once with a credential that can create tables. It holds a named lock and resumes from its last checkpoint if interrupted, because MySQL commits DDL on its own; runtime hosts refuse an incomplete schema.
- **Credentials:** runtime hosts need SELECT, INSERT, UPDATE and DELETE and can disable initialization; dashboard and MCP hosts can use SELECT only.
- **Sessions:** strict SQL mode, so an over-long value fails instead of being truncated.
- **Transactions:** business callbacks in `InTransactionAsync` must not run DDL.
- **Pools:** budget all replicas' pools against `max_connections`.

## Schema versions and upgrades

| Schema | Adds |
|---|---|
| 1 | Jobs, attempts, flows and limits |
| 2 | Definition lifecycle, commands and audit, diagnostics and progress, workers, events and schedules ([ADR-008](adr/0008-incidents-and-schema-upgrade.md)) |
| 3 | Aliases, outbox, signals, queue controls, archives and subscriptions ([ADR-010](adr/0010-schema3-operations-and-identity.md)) |
| 4 | Retention policies, holds, maintenance and verified exports ([ADR-013](adr/0013-fenced-retention-maintenance.md)) |
| 5 | Partition and tenant keys, keyed and rate limits, bounded flows and queue weights ([ADR-018](adr/0018-execution-admission-controls.md)) |
| 6 | The compatibility record for rolling upgrades ([ADR-021](adr/0021-rolling-upgrades.md)) |
| 7 | Limit lock rows ([ADR-022](adr/0022-limit-locks.md)) |
| 8 | Parent links and depth for child flows ([ADR-023](adr/0023-child-flows.md)) |
| 9 | The original input of a corrected job |
| 10 | The source run of a forked run |

Every upgrade is additive: queued work and live leases survive. Upgrading from schema 5 or earlier requires stopping older writers once, because those releases cannot read the compatibility record. From schema 6, a host accepts a newer schema whose `reader_minimum` still includes its own version, so later migrations roll out while older workers run. New shared semantics wait for an [engine protocol level](architecture.md#engine-protocol-and-payload-protection) that every live worker supports, and older hosts refuse to claim from a store above their level. MySQL and MariaDB start at schema 10.

## Throughput

Writes follow [ADR-020](adr/0020-write-path-throughput.md) and [ADR-022](adr/0022-limit-locks.md):

- **Outside the admission lock:** owned-row writes and settlements run on row locks, ordered by flow and job, on PostgreSQL, Oracle, MySQL and MariaDB. Claims do too while the store has no limit, rate or bounded flow and the worker serves one queue.
- **SQL Server** takes those paths only with `READ_COMMITTED_SNAPSHOT` on; otherwise it serializes them, because plain reads would wait on row locks. Azure SQL Database enables the setting by default.
- **SQLite** has one writer.
- **Under limits:** from engine protocol 3, claims lock per-limit rows instead of the admission lock, so tenants, keys, types and flows do not wait on each other and limits stay exact. A fleet-wide limit still serializes claims.

With a preloaded backlog, no limits and near-instant handlers, three 16-slot workers drained about 810–830 jobs/s on PostgreSQL, 430 on emulated SQL Server with versioned reads (200 without), 280 on Oracle and 720–810 on local SQLite on the documented test host. With 50 tenant quotas, PostgreSQL drained about 500 jobs/s. MySQL and MariaDB are not yet measured. See [performance](performance.md).

## Read-only hosts

Dashboard and MCP hosts can run on read-only connections without initializing storage: SQLite `Mode=ReadOnly`, PostgreSQL `default_transaction_read_only=on`, or SELECT-only logins on SQL Server, Oracle, MySQL and MariaDB. Production read-only roles still need deployment-specific grants. See [MCP hosting](mcp.md).

- **History searches** (`IOperationHistoryQueries`): five-second budgets, 1–100 row pages and seven-day windows. Audit SQL projects only the actor's issuer and subject; actor JSON above 16,384 characters is not parsed. Archive SQL never selects the body. Archive preview uses the manual archive selector and returns at most ten units.
- **Flow inspection** (`IExecutionInspectionQueries`): selected-attempt text is clipped in SQL, and payloads, outputs and full job records are never selected. Each provider projects saved dependencies with its own JSON functions, inside a 262,144-character definition bound.

## Running the provider tests

Without server environment variables, the shared suite runs SQLite only. Each variable names a **disposable** server; never point these gates at production data.

| Variable | Login needs to | Fixtures create |
|---|---|---|
| `DURABLEJOBS_POSTGRES` | create and drop schemas | `dj_test_*` schemas |
| `DURABLEJOBS_SQLSERVER` | create and drop databases and logins | `dj_test_*`, `wt_exercise_*`, `wt_package_*` databases |
| `DURABLEJOBS_ORACLE` | as a PDB administrator, create, grant and drop users | `WT_TEST_*`, `WT_PROOF_*` schemas |
| `DURABLEJOBS_MYSQL`, `DURABLEJOBS_MARIADB` | create and drop databases and users | `dj_test_*` databases |

```sh
docker run --rm -d --name workthrough-postgres-test \
  -e POSTGRES_PASSWORD=workthrough-test -e POSTGRES_DB=workthrough \
  -p 127.0.0.1:55432:5432 postgres:17-alpine
export DURABLEJOBS_POSTGRES='Host=127.0.0.1;Port=55432;Database=workthrough;Username=postgres;Password=workthrough-test'
dotnet test tests/WorkThrough.Tests --configuration Release
docker stop workthrough-postgres-test
```

- **Oracle:** the official lite container lacks the USERS tablespace; CI creates a test tablespace and grants SYSTEM `EXECUTE` on `SYS.DBMS_LOCK` with grant option. Oracle fixtures run one at a time, because concurrent schema DDL conflicts with dictionary reads inside Serializable transactions; connections inside a test still compete.
- **MySQL and MariaDB:** fixtures bound their pools to 16 connections and run in parallel, so raise `max_connections` (2,000 in CI).
- **CI:** PostgreSQL and SQL Server run on one runner; Oracle and MySQL/MariaDB each run on their own, with images pinned by digest. `scripts/verify.sh` includes each provider when its variable is set.
