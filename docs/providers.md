# Storage providers

SQLite and PostgreSQL implement the initial storage port. Claims of verification must refer to the test run recorded in `verification.md`; a compiled provider is not enough.

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
- Support initialization idempotently and reject unknown schema versions. Destructive production migration is never implicit.
- Run the complete storage conformance suite against a real instance of the provider.

## PostgreSQL test database

`DURABLEJOBS_POSTGRES` must point to a disposable database whose user can create/drop schemas. Each test creates a unique `dj_test_*` schema and drops only that schema when finished. Test output does not print the connection string.

```sh
docker run --rm -d --name durablejobs-postgres-test \
  -e POSTGRES_PASSWORD=durablejobs-test -e POSTGRES_DB=durablejobs \
  -p 127.0.0.1:55432:5432 postgres:17-alpine
export DURABLEJOBS_POSTGRES='Host=127.0.0.1;Port=55432;Database=durablejobs;Username=postgres;Password=durablejobs-test'
dotnet test tests/WorkThrough.Tests --configuration Release
docker stop durablejobs-postgres-test
```

When the environment variable is absent, the shared suite enumerates SQLite cases only. CI supplies PostgreSQL explicitly. SQL Server, MySQL, and MongoDB are roadmap providers; they are not currently advertised as implemented.

SQLite initialization now enables WAL by default for local multi-process use, and every engine connection uses `synchronous=FULL`. `new SqliteStorageOptions(UseWriteAheadLogging: false)` preserves an existing journal mode; it does not convert WAL back to DELETE. All SQLite processes must share a local filesystem, not a network mount. Back up through SQLite's backup API or a consistent snapshot including WAL state. See [SQLite WAL](https://www.sqlite.org/wal.html) and [Microsoft.Data.Sqlite async limits](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async). The pinned native library was verified as SQLite 3.53.3 on macOS ARM64, including the WAL-reset correction.
