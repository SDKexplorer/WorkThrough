# Bounded execution queries

`IExecutionQueries` supplies metadata-only investigation for operational clients. SQLite and PostgreSQL implement it, and their `UseSqlite` / `UsePostgreSql` registrations expose it through dependency injection. These reads are the first part of the MCP query foundation; **the MCP server, bearer authentication and remaining investigation tools are not implemented yet**.

```csharp
var queries = services.GetRequiredService<IExecutionQueries>();
var query = new ExecutionQuery(
    ProcessorKey: InvoiceJobs.Generate.Identity.Key,
    ContractVersion: InvoiceJobs.Generate.Identity.Version,
    TimeBasis: ExecutionTimeBasis.FailedAttempt,
    Limit: 25);

var page = await queries.SearchExecutionsAsync(query, cancellationToken);
foreach (var execution in page.Items)
{
    Console.WriteLine($"{execution.Id}: {execution.Status}");
}

if (page.NextCursor is { } cursor)
{
    var nextPage = await queries.SearchExecutionsAsync(
        query with { Cursor = cursor }, cancellationToken);
}
```

`InvoiceJobs.Generate` represents the application's shared typed job definition. The operations host needs its stable identity, not its processor implementation assembly. Hosts must authorize these reads; the Core interface is not an authentication mechanism.

## Semantics

- Search uses a default 24-hour window, or an explicit positive window no longer than seven days. `From` is inclusive and `Until` is exclusive. Missing `Until` uses database observation time plus one millisecond to include the current stored millisecond; missing `From` selects the preceding 24 hours.
- `Created` selects by execution creation time. `FailedAttempt` selects by completion time of attempts with outcome `Failed`, including an execution that is now successful. Rejection, interruption and abandonment are distinct outcomes; they are not silently counted as failed attempts.
- Processor key/version, queue, correlation, current job state and flow-run ID filters combine with AND. A version filter requires a processor key. Correlation values remain observations, not tenant boundaries.
- Results sort by creation time descending, then ID ascending, regardless of time basis. A job with several matching failed attempts appears once. Read its attempt metadata for individual outcomes.
- Pages contain 25 items by default, at most 100. There is no total-count scan. Queue/correlation/step strings are capped at 512 characters in SQL and clipping is explicit through `MetadataTruncated`. This is a row/text bound, not the future MCP serialized-byte budget.
- Cursors preserve the original window and position and reject accidental reuse with a different store, filter or page size. URL-encode a cursor when using HTTP. Cursors are opaque to consumers but **not signed credentials**; hosts must reauthorize every request, and must not use their contents to grant access.
- Each page has its own short read snapshot. Concurrent status changes, archive operations or backdated inserts can change membership across pages. A cursor does not preserve a historical snapshot.
- Coverage is explicitly `live`. Search omits archived executions; a null `FindExecutionAsync` result means absent from active tables, not proof that the ID never existed. Archive lookup/indexing remains separate planned work.

`FindExecutionAsync` reads a single ID without a time restriction. `ReadAttemptsAsync` returns at most 100 timing/outcome summaries in ascending attempt order. Continue using `NextAfterAttempt`; `ExecutionExists` distinguishes a live job with no attempts from a job absent from active tables. Attempts and execution existence are observed in the same read transaction.

The metadata types contain no input, output, exception text, CLR implementation names, worker identity, lease token or effect/idempotency key. SQL selects those metadata columns directly; it does not deserialize a full `JobRecord` and hide fields afterwards. Business identifiers can still be sensitive, so a host may need additional filtering/redaction before sharing results externally.

## Dashboard HTTP API

Authenticated dashboard hosts expose three additive routes under their configured dashboard path:

| Route | Behavior |
|---|---|
| `GET /api/executions` | Bounded search; query-string names match `ExecutionQuery` properties |
| `GET /api/executions/{id}` | Single metadata lookup with store, observation time and live coverage |
| `GET /api/executions/{id}/attempts` | Attempt summaries; optional `afterAttempt` and `limit` |

For example, under `/operations/jobs`:

```text
/operations/jobs/api/executions?timeBasis=FailedAttempt&queue=billing&limit=25
```

All routes require `executions.read`, use the existing host resource-policy hooks and return `Cache-Control: no-store`. Existing `/api/jobs` routes and Angular behavior remain unchanged. Invalid windows, filters, limits and cursors return 400. A host using a custom provider without the new port receives 503 for these routes; it is not silently routed through an unbounded legacy query. A provider cancellation caused by the query budget returns 503; request cancellation propagates. Other database failures remain errors, never empty success results.

## Storage and verification boundaries

Queries use SQLite deferred read transactions and PostgreSQL Repeatable Read. They do not acquire the write admission lock or mutate engine state. Connection initialization is separate from read access. Each operation has a five-second cancellation budget and five-second command timeout; synchronous SQLite internals and connection setup mean this is not a hard wall-clock SLA.

Initialization adds compatible indexes for creation ordering, filtered history and failed-attempt time. No persisted job identity, payload or schema-version meaning changes. Existing schema-3 stores acquire the indexes on initialization; index creation on a large store needs an appropriate deployment window. Existing schema-1 upgrade coverage still applies.

Provider regressions cover recovered failures, duplicate attempt matches, time boundaries, same-time pagination, changed query/store cursors, clipping, cancellation, archive coverage and unreadable payload JSON. HTTP tests cover permission checks, metadata projection and input validation on both providers. Package consumers exercise the interface on .NET 8 and 10. See [verification](verification.md) for actual completed runs. The dedicated 100,000-execution MCP contention/soak campaign remains a later release gate; existing performance smoke is not that measurement.
