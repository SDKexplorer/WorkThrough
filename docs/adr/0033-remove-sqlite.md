# ADR 033: remove the SQLite provider

Status: accepted, implemented.

## Context

SQLite was the default for the quick start, templates, the test host and the shared suite, because it needs no server. It was never a production target: one writer at a time, a driver that runs synchronously behind its async API, and a file that cannot be shared across machines. Under the suite's parallel claim races it produced failures the server providers did not: a pooled connection left inside a transaction ("SQL logic error") and intermittent under-admission of a bounded flow. Hardening SQLite for loads it is not meant for took effort away from the production providers, and its presence suggested a deployment option we would not support.

## Decision

`WorkThrough.Storage.Sqlite` and its tests are removed. PostgreSQL takes its place wherever a store is needed without a choice:

- **Test host:** `WorkThroughTestHost` runs in a throwaway schema of the database named by `WORKTHROUGH_TEST_POSTGRES`, creating it at start and dropping it on dispose. `PostgresStorageOptions.ClockOffset` gives it the movable clock SQLite had.
- **Initialization:** `PostgresJobStore.InitializeAsync` creates the schema named first in the connection's `Search Path`, when missing and a plain identifier, so samples, templates and verification runs can each use their own schema in one database.
- **Samples and templates:** use PostgreSQL by default, from `WORKTHROUGH_POSTGRES` or the local database in `samples/compose.yaml`. The templates' default storage is `postgresql`.
- **Suite and scripts:** the shared suite needs `DURABLEJOBS_POSTGRES` and runs every other provider whose variable is set. The relocation proof, exercises and CI jobs use PostgreSQL where they used SQLite.

## Consequences

- First runs, consumer tests and our own suite need a PostgreSQL server, usually one Docker container. There is no zero-setup path.
- Every remaining provider is a server database that is qualified with soaks, process kills and multi-host runs.
- The SQLite-only failures are gone with the provider. Evidence recorded for SQLite before this decision stays in the verification log as history.
