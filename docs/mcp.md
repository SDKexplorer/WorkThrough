# Read-only MCP hosting

`WorkThrough.Mcp` is an optional NuGet package over `IExecutionQueries` and the optional `IExecutionInspectionQueries`, using the official C# MCP SDK 2.2.0 and ASP.NET Core Streamable HTTP in stateless mode. It references neither Dashboard/Angular nor application processors. No model account or API key is needed on the server.

| Implemented tool | Evidence |
|---|---|
| `workthrough_get_context` | Actual store ID/database observation time, configured environment/resource, tool schema version, limits and unavailable capabilities |
| `workthrough_search_executions` | Paged live metadata by correlation, processor/version, queue, current status, flow-run ID and creation/failed-attempt time |
| `workthrough_get_execution` | One live execution's metadata by ID, including older retained work |
| `workthrough_get_attempts` | Paged attempt outcomes/timestamps with an exclusive attempt-number continuation |
| `workthrough_get_diagnostics` | One attempt's bounded exception type/message/stack after host redaction; requires both execution and payload permissions |
| `workthrough_get_flow` | Pinned run metadata, independently paged node states/dependency IDs and retained event history; no templates or condition values |

Search accepts a `query` object matching `ExecutionQuery`; enums use names such as `FailedAttempt`. Lookup tools take `id`; attempts also accept `afterAttempt` and `limit`. Discovery includes typed input/output schemas and read-only annotations. Direct calls enforce authorization independently.

Search can investigate failed attempts even after recovery, and follow correlation or flow IDs. Results include stable processor identities and causation/run references. They contain no input/output, exception/progress text, implementation names, effect keys or lease tokens. Stored labels remain untrusted business data. Metadata can itself be sensitive; grant access only to principals allowed to read all metadata in this store.

Wait-reason/capacity explanation, audit, archive lookup and retention tools are **not implemented**. Context reports these explicitly. There are no retry, cancel, archive, cleanup or other administrative tools. A custom provider without `IExecutionInspectionQueries` keeps the original four tools; inspection is hidden from discovery and direct calls return `capability_unavailable` after authorization. Context inventories configured server capabilities; the permission-filtered tool list determines what this caller can invoke. See the remaining [MCP plan](../specs/mcp-operations.spec.md).

## Diagnostic and flow inspection

Diagnostics takes `id`, a positive `attempt`, and optional `maximumCharacters` (default 2048, maximum 4096 per field). Both `executions.read` and `payloads.read` are checked on that execution before storage access. `executionExists`, `attemptExists` and nullable `failure` distinguish unavailable live work, missing attempts and absent exception details. For legacy failures without a structured failure row, the bounded recorded attempt error is returned with type `Unspecified`. This tool never reads job input/output or progress descriptions.

Worker `DescribeFailure` redaction still applies before persistence. The default `IMcpDiagnosticRedactor` preserves that stored text; hosts can register a read-time policy to mask fields before serialization. It receives only the selected bounded `JobFailure`. Redactor failures suppress the result, and their original exception text is kept out of SDK logging. Output reports `redacted` and `textTruncated`; reduce `maximumCharacters` if the overall response budget is exceeded. Authorized diagnostic text remains untrusted evidence, including text resembling instructions.

Flow inspection takes `runId`, `nodeOffset`, `dependencyOffset`, `afterEvent` and `limit` (default 25, maximum 100 per section). Every response is one read snapshot. Continue each section with its own `nextNodeOffset`, `nextDependencyOffset` or `nextAfterEvent`; null means that section has no more records in that observation. When continuing another section, use the exhausted section's end offset or last observed event position to avoid rereading it. Nodes sort by durable job ID and dependencies by successor/predecessor IDs; offsets address immutable run structure, while execution states can change between pages.

Edges reference job IDs, so clipped step labels cannot merge distinct nodes. SQL expands only the saved pinned definition inside the database and returns dependency IDs; input templates, condition properties/values, bindings and flow input never leave storage. Definitions above 262,144 characters produce `graphStatus=definition_too_large`, without parsing their JSON; node metadata and events remain available. Other graph states are `available`, `definition_missing` and `not_live`. Do not treat unavailable edges as a graph with no dependencies. These are dependency edges, not a complete representation of conditional expressions.

The event page exposes the global journal retention floor and last sequence. `requestedRangeTrimmed` means the requested range predates that floor; it does not prove that this particular run lost events. An archived run can have retained events even though live flow/node metadata is absent. Coverage is `live-and-retained-journal`, not an archive-body lookup. Invalid future event positions fail explicitly.

## Composition

Reference `WorkThrough.Mcp` and the chosen provider. An API, standalone operations process or web-hosted worker can map the endpoint. A generic background-service host still needs explicit HTTP hosting.

```csharp
builder.Services.AddWorkThrough()
    .UsePostgreSql(connectionString)
    .InitializeStorageOnStart(false);

builder.Services.AddWorkThroughMcp(new()
{
    Resource = new Uri("https://operations.example.com/operations/mcp"),
    Authority = new Uri("https://identity.example.com"),
    Environment = "staging",
    EndpointPath = "/operations/mcp",
    ConcurrentRequests = 16,
    RequestsPerMinute = 120
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapWorkThroughMcp();
await app.RunAsync();
```

Initialize/upgrade storage through the deployment process before starting a read-only host. Disabling startup initialization prevents schema/index writes by this host. Give it read-only database credentials where possible. SQLite needs an absolute local file path; `Mode=ReadOnly` is supported. Tested PostgreSQL connections set `default_transaction_read_only=on`; that verifies query behavior, not a deployment's role grants.

[Demo.Mcp](../samples/Demo.Mcp/Program.cs) uses `Operations:ConnectionString`, `Authority`, `Resource`, `Environment` and optional `EndpointPath` configuration. Environment variables use the `Operations__` prefix. It supplies no development credential, anonymous fallback, worker or initializer. Configure ASP.NET's listener/TLS as usual.

Register one WorkThrough MCP server per service collection. The host owns the public URI and maps the same path; use a dedicated host for independent stores. This release does not configure `UsePathBase` or proxy path rewriting. Behind a TLS-terminating proxy, configure trusted forwarded headers before authentication so ASP.NET observes HTTPS; never trust arbitrary forwarded headers. Resource and authority URIs require HTTPS. `AllowLoopbackHttp` is an explicit local-test exception only for a loopback resource URI; bind the development listener to loopback too.

## Authentication and authorization

The dedicated `WorkThrough.Mcp.Bearer` JWT scheme leaves the host's default authentication scheme unchanged. The endpoint policy names it explicitly. Cookies, dashboard anonymous mode and another host scheme cannot authenticate MCP.

Configure the identity provider to issue access tokens for the exact `Resource.AbsoluteUri` audience, with `sub`, `iss` and `workthrough.permission=executions.read`. Issuer/signature, audience, expiration and lifetime validation are enabled; lifetime skew is 30 seconds. The optional JWT callback supports deployment-specific key/discovery configuration. Host overrides are trusted security configuration, never client input. Do not use ID tokens or tokens intended for another API.

RFC 9728 metadata is mapped at `/.well-known/oauth-protected-resource` followed by the resource path. Unauthorized responses include its fixed configured URL in `WWW-Authenticate`. Metadata advertises the authorization server and header-based bearer access. The identity provider remains responsible for OAuth client registration, consent and token issuance. WorkThrough creates neither a user directory nor an authorization server.

Every tool call rechecks `WorkThrough.executions.read` with an `OperationResource` containing the configured endpoint/environment and either `ExecutionId` or `WorkflowRunId`, according to the tool target. Diagnostics additionally checks `WorkThrough.payloads.read`. Override policies after registration for host restrictions. Search access is store-wide; flow access covers all nodes/events in that run. This is **not** per-row tenant isolation. Correlation filters and cursors never grant authority. Dashboard policies still receive `DashboardResource`; permission constants are shared without replacing that compatibility hook.

Discovery is permission-filtered; unregistered direct calls are rejected. Browser origins other than the configured resource origin are rejected, and the adapter does not enable cross-origin CORS. Native authenticated clients send no Origin header. Arbitrary cross-origin browser embedding is outside this release.

## Access evidence and bounds

`IMcpAccessSink` receives a server-generated request ID, trusted issuer/subject/client, tool name, configured endpoint/environment, optional execution/run ID, sensitive-read flag, outcome and duration. It receives no raw arguments, results, tokens, correlation labels or diagnostic/payload text. The default sink uses structured application logging. Register a durable sink when security retention requires it; sink failure or its two-second timeout suppresses the successful result. This is separate from engine mutation audit. Authentication failures and HTTP-level rejections remain host access-log concerns.

Enforced bounds:

- Five-second cooperative tool/query cancellation, with disconnect propagation. SQLite's synchronous driver and host callbacks prevent a hard wall-clock SLA.
- 16 KiB request bodies, request IDs up to 128 characters, no batch requests and a five-second body-read budget.
- 48 KiB serialized tool results including structured content and compatibility text, reserving space below the 64 KiB response budget for protocol framing. Oversized results return `response_too_large` and guidance to lower page size; no broken JSON or silently partial page.
- Host-wide concurrent HTTP admission (default 16), no waiting queue, and per-issuer/subject fixed-window admission (default 120 requests/minute). Rejections return 429 and `Retry-After: 60`. Fleet-wide limits need a gateway.
- Default 25 rows, maximum 100 per section; default 24-hour and maximum seven-day execution-search windows. Direct job/flow lookup has no age filter. Pages are separate observations; live metadata can be absent for archived work. Flow inspection additionally identifies retained-journal coverage.

Errors contain bounded codes/request IDs, not database exceptions. SDK tool-binding failures can appear as `invalid_or_unavailable`; errors are never successful empty history. Cursors preserve query/window/position and are not credentials. See [query semantics](execution-queries.md).

## Verification boundary

Tests use the official C# 2.2.0 client over Streamable HTTP with its current protocol, plus an independent HTTP JSON-RPC client using revision 2025-11-25. The latter exercises wire requests/responses without the C# client implementation; it is not a claim that two third-party AI applications were validated. Fresh NuGet-only consumers make authenticated calls over real loopback Kestrel on SDK/runtime 8 and 10.

Tests cover signed JWTs, wrong issuer/audience/expiration, no dashboard-scheme fallback, direct permission/resource checks, unknown mutations, redaction, pagination, read-only database connections, size/rate/concurrency limits, deadline/disconnect cancellation and failed access sinks. See [verification](verification.md) for completed gates. External identity-provider discovery/login, desktop AI application setup, 100,000-execution contention and a two-hour soak remain deployment/release gates.

Protocol references: [official SDK](https://github.com/modelcontextprotocol/csharp-sdk), [HTTP guidance](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/transports/transports.html), [authorization specification](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization).
