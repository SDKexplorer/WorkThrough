# ADR-029: MySQL and MariaDB storage

Status: implemented, 2026-10-13. The tested targets are MySQL 8.4.11 and MariaDB 11.4.13 with MySqlConnector 2.6.2, locally on arm64; a CI job runs the same verification on x64. See [verification](../verification.md) for what ran. Other releases need their own qualification; initialization refuses MySQL before 8.0.21 and MariaDB before 10.6, which lack features the provider relies on.

## Context

MySQL and MariaDB were the main relational gap, and a common request from teams moving off Hangfire. Both differ from the existing providers in ways that matter for durable work: DDL commits implicitly, default collations ignore case, accents and trailing spaces, InnoDB locks the rows a locking read examines rather than the rows it returns, and the two products have drifted apart in JSON functions, collation names and session variables.

## Decision

`WorkThrough.Storage.MySql` supplies `.UseMySql(connectionString)` for both products. It uses the shared engine transitions, like the other providers.

### Transactions

- Writes that admit work start a Read Committed transaction and lock the singleton `dj_admission` row with `SELECT … FOR UPDATE`. Read Committed avoids InnoDB gap locks, which would block inserts next to rows a writer only read.
- Settlements, owned-row writes and unrestricted claims run without the admission row, ordered by flow and job row locks (ADR-020), as on PostgreSQL and Oracle.
- Claims read candidate IDs first and then lock them by identity with `FOR UPDATE SKIP LOCKED`. A row-limited, ordered locking read locked more jobs than its claim could lease, and under concurrent claims a shared limit was under-admitted; the conformance test found it.
- Reads use a read-only Repeatable Read transaction, InnoDB's consistent snapshot, without locks.
- The clock is `UTC_TIMESTAMP(6)`, which does not depend on the session time zone.
- Each session sets strict SQL mode, so an over-long value fails instead of being truncated into a different identity.

### Text

Tables use `utf8mb4` with a binary collation that does not pad: `utf8mb4_0900_bin` on MySQL and `utf8mb4_nopad_bin` on MariaDB. Identities compare byte for byte; case, accents, Unicode normalization and trailing spaces stay distinct, so no identity is rejected for them. Column bounds match Oracle's: 120 characters for keys, 512 for queues, 400 for worker, step and idempotency keys, 2,000 for correlation. Business payloads are `LONGTEXT`.

### Initialization

Initialization runs on an unpooled session holding a named `GET_LOCK` derived from the database name. `dj_schema` records the version, an Installing/Ready status, a step checkpoint and the SHA-256 of the step list, as on Oracle (ADR-015). Each step checks whether its object exists, runs if not, and checkpoints; seeds and Ready commit together. Runtime sessions refuse an incomplete or incompatible schema. Initialization needs DDL privileges; runtime hosts need only SELECT, INSERT, UPDATE and DELETE, and inspection hosts SELECT.

Schema 10 is the first MySQL schema, so there is no earlier MySQL format to migrate.

### Product differences

The provider uses only JSON functions both products share (`JSON_EXTRACT`, `JSON_CONTAINS`, `JSON_TYPE`, `JSON_TABLE`). Comparisons between JSON-derived text and identity columns use `BINARY`, so they never depend on a connection collation.

## Consequences

- One package serves both products; CI runs the full verification against both.
- Business callbacks in `InTransactionAsync` must not run DDL, which would commit the engine's transaction.
- Run initialization once, with a DDL credential, before starting workers that use DML-only credentials.
- No production throughput claim is made beyond the measurements in [performance](../performance.md).
