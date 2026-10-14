# MCP hosting

`WorkThrough.Mcp` is an optional NuGet package over `IExecutionQueries` and the optional `IExecutionInspectionQueries` / `IOperationHistoryQueries` / `IRuntimeQueries` / `IRetentionQueries`, using the official C# MCP SDK 2.2.0 and ASP.NET Core Streamable HTTP in stateless mode. It references neither Dashboard/Angular nor application processors. No model account or API key is needed on the server.

| Implemented tool | Evidence |
|---|---|
| `workthrough_get_context` | Actual store ID/database observation time, configured environment/resource, tool schema version, limits and unavailable capabilities |
| `workthrough_search_executions` | Paged live metadata by correlation, processor/version, queue, current status, flow-run ID, partition key, tenant and creation/failed-attempt time |
| `workthrough_get_execution` | One live execution's metadata by ID, including older retained work |
| `workthrough_get_attempts` | Paged attempt outcomes/timestamps with an exclusive attempt-number continuation |
| `workthrough_get_diagnostics` | One attempt's bounded exception type/message/stack after host redaction; requires both execution and payload permissions |
| `workthrough_get_flow` | Pinned run metadata, independently paged node states/dependency IDs and retained event history; no templates or condition values |
| `workthrough_get_audit` | Paged attributed operation metadata; excludes reasons and change contents |
| `workthrough_search_archives` | Paged archive summaries by archival time, correlation and kind, including purged-body summaries |
| `workthrough_get_archive` | One summary by standalone job ID or whole-flow run ID, with no age restriction |
| `workthrough_preview_retention` | Advisory preview of a caller-supplied manual archive policy; never deletes data |
| `workthrough_get_capacity` | Snapshot of waiting reasons (shared, partition, tenant, rate and flow-parallelism limits), admitted leases and bounded recent worker evidence |
| `workthrough_get_metrics` | Windowed live attempt counts, successful attempt throughput and mean/max attempt duration |
| `workthrough_get_retention` | Persisted automatic policy, pending/last batch, holds/incidents and journal/subscriber positions |

Search accepts a `query` object matching `ExecutionQuery`; enums use names such as `FailedAttempt`. Lookup tools take `id`; attempts also accept `afterAttempt` and `limit`. Discovery includes typed input/output schemas and read-only annotations. Direct calls enforce authorization independently.

Search can investigate failed attempts even after recovery, and follow correlation or flow IDs. Results include stable processor identities and causation/run references. They contain no input/output, exception/progress text, implementation names, effect keys or lease tokens. Stored labels remain untrusted business data. Metadata can itself be sensitive; grant access only to principals allowed to read all metadata in this store.

Automatic-retention status is read-only. Administrative tools exist only when the host enrolls [controlled actions](#controlled-actions). A custom provider without any optional ports keeps the original four tools; unavailable inspection/history tools are hidden from discovery and direct calls return `capability_unavailable` after authorization. Context inventories configured capabilities; the permission-filtered tool list determines what this caller can invoke. See the remaining [MCP plan](../specs/mcp-operations.spec.md).

## Diagnostic and flow inspection

Diagnostics takes `id`, a positive `attempt`, and optional `maximumCharacters` (default 2048, maximum 4096 per field). Both `executions.read` and `payloads.read` are checked on that execution before storage access. `executionExists`, `attemptExists` and nullable `failure` distinguish unavailable live work, missing attempts and absent exception details. For legacy failures without a structured failure row, the bounded recorded attempt error is returned with type `Unspecified`. This tool never reads job input/output or progress descriptions.

Worker `DescribeFailure` redaction still applies before persistence. The default `IMcpDiagnosticRedactor` preserves that stored text; hosts can register a read-time policy to mask fields before serialization. It receives only the selected bounded `JobFailure`. Redactor failures suppress the result, and their original exception text is kept out of SDK logging. Output reports `redacted` and `textTruncated`; reduce `maximumCharacters` if the overall response budget is exceeded. Authorized diagnostic text remains untrusted evidence, including text resembling instructions.

Flow inspection takes `runId`, `nodeOffset`, `dependencyOffset`, `afterEvent` and `limit` (default 25, maximum 100 per section). Every response is one read snapshot. Continue each section with its own `nextNodeOffset`, `nextDependencyOffset` or `nextAfterEvent`; null means that section has no more records in that observation. When continuing another section, use the exhausted section's end offset or last observed event position to avoid rereading it. Nodes sort by durable job ID and dependencies by successor/predecessor IDs; offsets address immutable run structure, while execution states can change between pages.

Edges reference job IDs, so clipped step labels cannot merge distinct nodes. SQL expands only the saved pinned definition inside the database and returns dependency IDs; input templates, condition properties/values, bindings and flow input never leave storage. Definitions above 262,144 characters produce `graphStatus=definition_too_large`, without parsing their JSON; node metadata and events remain available. Other graph states are `available`, `definition_missing` and `not_live`. Do not treat unavailable edges as a graph with no dependencies. These are dependency edges, not a complete representation of conditional expressions.

The event page exposes the global journal retention floor and last sequence. `requestedRangeTrimmed` means the requested range predates that floor; it does not prove that this particular run lost events. An archived run can have retained events even though live flow/node metadata is absent. Coverage is `live-and-retained-journal`, not an archive-body lookup. Invalid future event positions fail explicitly.

## Runtime evidence

`workthrough_get_capacity` takes an execution `id` and `workerExamples` (default 10, maximum 100). The database snapshot checks shared total/type admission (including processor aliases), partition and tenant limits, rate limits, a flow run's `MaxParallelSteps`, queue and flow state, delayed/retrying work, execution deadlines and last progress. Tenant-scoped callers receive the reason without fleet-wide counts or worker examples. Limits count unexpired admitted leases. They cannot prove that a handler or its external effects stopped after lease expiry.

Recent worker evidence requires the exact processor version and queue on the same heartbeat, no older than 60 seconds. At most 501 workers are examined; more than 500 or an oversized/incomplete heartbeat reports `workerCoverageIncomplete`. Missing or stale evidence never proves no worker exists. Draining, exhausted local total capacity and consecutive acquisition failures have distinct reasons. Free per-type local slots and future acquisition success cannot be inferred. Worker examples are bounded metadata without progress descriptions or lease tokens.

`workthrough_get_metrics` takes `query` with optional `from`/`until`: a half-open completion-time window, default one hour, maximum seven days. Counts and timing describe finished attempts, including failures subsequently recovered; successful attempts per second is averaged over the whole requested window. Mean/max duration is null when empty. Coverage is `live-attempts`: archival removes attempts from these aggregates, so they are not a permanent historical telemetry store. Both tools require `executions.read`; capacity checks the actual execution resource.

The Angular console uses the same port for its one-hour metrics and detailed waiting reason. Worker rows show acquisition failures and stale heartbeat observations. `.Named("Send invoice")` labels a flow step independently of its permanent ID; labels are pinned with the saved definition and changing a published label requires a new definition version. Existing definitions without labels keep their IDs as display text.

## Audit, archives and cleanup preview

`workthrough_get_audit` accepts an `AuditQuery` in `query`, with exact action/target filters, time window, limit and cursor. It returns command ID, timestamp, trusted actor issuer/subject, action, target and outcome. Reasons, actor display names, command arguments/results and change JSON are never selected. Metadata is clipped to 512 characters with an explicit flag. Actor JSON over 16,384 characters is not parsed; `actorAvailable=false` reports missing attribution. Both `executions.read` and `audit.read` are required. Target filters narrow store-wide access; they do not impose per-row authorization.

`workthrough_search_archives` accepts an `ArchiveQuery` in `query`. Time means archival time, not execution/completion time. Audit/archive searches default to the previous 24 hours, allow at most seven days and 100 rows, and order by timestamp descending then ID ascending. Reuse the same filters and limit with `nextCursor`; cursors bind query kind, store and frozen window. Pages are fresh snapshots and returned counts are not totals.

Archive summaries expose kind, unit ID, correlation, archival time, job count and `bodyAvailable`. They never select the body or its hash. The existing summary format does not record original execution outcome, so no outcome is inferred. `workthrough_get_archive` takes `archiveId` without a time restriction: use a standalone job ID or whole-flow run ID. Individual archived step IDs are not indexed; a missing lookup does not prove a step never existed. Purged bodies leave searchable summaries. Lookup enforces `OperationResource.ArchiveId`; archive search requires store-wide metadata access.

`workthrough_preview_retention` accepts `policy` with `succeededBefore`, optional `failedBefore` and `batchSize` (default/max 10). It shares the manual archive selector in a bounded read snapshot. Missing failure cutoff retains failed/cancelled/rejected/expired work. Active work, paused/incident flows, held units and individual flow steps are excluded. A whole flow is one unit. `hasMoreCandidates` reports an observed extra candidate, not an estimated total. No payload is loaded; byte/evidence limits are not preflighted (`evidenceSizeVerified=false`). Eligibility can change before an actual archive transaction.

The preview describes the supplied manual policy (`policySource=caller-supplied`), not a persisted automatic-retention configuration. Automatic policies, holds, fenced maintenance and verified-copy purge are described in the [operations guide](operations-and-retention.md#opt-in-automatic-maintenance). This preview does not evaluate the automatic policy or its incident exclusions. It requires `retention.read` in addition to `executions.read`; `archives.manage` does not implicitly grant it. `workthrough_get_retention` requires the same two read permissions and returns the stored policy (null when unconfigured), pending/last batch, hold/incident counts, up to 100 incident examples, bounded failure codes and journal/subscriber positions. It omits archive bodies, hold reasons, owner identities and secret lease tokens. Status does not enable maintenance or grant control access. Existing dashboard preview authorization is unchanged. History tools use the same result budgets, cancellation and access evidence as other tools. Audit reads set the sensitive-read flag; archive lookups also record the archive ID without recording query arguments or results.

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

Initialize/upgrade storage through the deployment process before starting a read-only host. Disabling startup initialization prevents schema/index writes by this host. Give it read-only database credentials where possible. Tested PostgreSQL connections set `default_transaction_read_only=on`; that verifies query behavior, not a deployment's role grants.

[Shop.Mcp](../samples/Shop.Mcp/Program.cs) uses `Mcp:Authority`, `Resource`, `Environment` and optional `EndpointPath`, plus the shop's `Shop:PostgreSql` store; environment variables use the `Mcp__` and `Shop__` prefixes. It registers the tenant-claim resolver and supplies no development credential, anonymous fallback, worker or initializer. Configure ASP.NET's listener/TLS as usual.

Register one WorkThrough MCP server per service collection. The host owns the public URI and maps the same path; use a dedicated host for independent stores. This release does not configure `UsePathBase` or proxy path rewriting. Behind a TLS-terminating proxy, configure trusted forwarded headers before authentication so ASP.NET observes HTTPS; never trust arbitrary forwarded headers. Resource and authority URIs require HTTPS. `AllowLoopbackHttp` is an explicit local-test exception only for a loopback resource URI; bind the development listener to loopback too.

## Authentication and authorization

The dedicated `WorkThrough.Mcp.Bearer` JWT scheme leaves the host's default authentication scheme unchanged. The endpoint policy names it explicitly. Cookies, dashboard anonymous mode and another host scheme cannot authenticate MCP.

Configure the identity provider to issue access tokens for the exact `Resource.AbsoluteUri` audience, with `sub`, `iss` and `workthrough.permission=executions.read`. Issuer/signature, audience, expiration and lifetime validation are enabled; lifetime skew is 30 seconds. The optional JWT callback supports deployment-specific key/discovery configuration. Host overrides are trusted security configuration, never client input. Do not use ID tokens or tokens intended for another API.

RFC 9728 metadata is mapped at `/.well-known/oauth-protected-resource` followed by the resource path. Unauthorized responses include its fixed configured URL in `WWW-Authenticate`. Metadata advertises the authorization server and header-based bearer access. The identity provider remains responsible for OAuth client registration, consent and token issuance. WorkThrough creates neither a user directory nor an authorization server.

Every tool call rechecks `WorkThrough.executions.read` with an `OperationResource` containing the configured endpoint/environment and the appropriate `ExecutionId`, `WorkflowRunId` or `ArchiveId`. Diagnostics additionally checks `payloads.read`; audit and cleanup preview additionally check `audit.read` and `retention.read`. Override policies after registration for host restrictions. Search access is store-wide unless the host registers an `ITenantAccessResolver`. Tenant-scoped principals list and call only the seven per-execution tools, see their tenants' executions only, and receive absent results for anything else ([ADR-019](adr/0019-tenant-access-isolation.md)). Flow access covers all nodes/events in that run. Correlation filters and cursors never grant authority. Dashboard policies still receive `DashboardResource`; permission constants are shared without replacing that compatibility hook.

### Hosted identity providers

Hosted providers do not issue `workthrough.permission` claims. Entra ID puts app roles in `roles`; Auth0 with RBAC puts API permissions in `permissions`. Map them in the host's JWT callback, as [Shop.Mcp](../samples/Shop.Mcp/Program.cs) does with `Mcp:RoleClaim` and `Mcp:RolePermissions` in [appsettings.json](../samples/Shop.Mcp/appsettings.json). Only mapped values grant anything.

- **Entra ID:** register an API with an app ID URI equal to `Resource`. Define app roles and assign them to users or client applications. Set `requestedAccessTokenVersion` to 2 in the manifest, so the token issuer matches the `https://login.microsoftonline.com/<tenant>/v2.0` authority. Clients request `<app ID URI>/.default`. `RoleClaim` is `roles`.
- **Auth0:** create an API whose identifier equals `Resource`, enable RBAC and "Add Permissions in the Access Token", and grant permissions to roles or machine-to-machine applications. The authority is `https://<domain>/`, and token requests pass the audience. `RoleClaim` is `permissions`.

`scripts/check-hosted-idp.py` checks a deployment's provider with client-credentials tokens:
- a role-mapped client sees and calls the read tools;
- a token with a broken signature gets 401 with the resource metadata challenge;
- a valid token without a mapped role sees no tools.

It reads the provider settings from the environment (see its header) and creates nothing in the provider. `--local-keycloak` runs the same checks against Keycloak over HTTPS; that validates the script, not a hosted provider. Interactive dashboard sign-in through a hosted provider is still checked by hand.

Discovery is permission-filtered; unregistered direct calls are rejected. Browser origins other than the configured resource origin are rejected, and the adapter does not enable cross-origin CORS. Native authenticated clients send no Origin header. Arbitrary cross-origin browser embedding is outside this release.

## Controlled actions

The endpoint is read-only unless the host enrolls actions ([ADR-031](adr/0031-mcp-controlled-actions.md)):

```csharp
builder.Services.AddSingleton<IMcpActionApprovals, ConsoleApprovals>(); // the host's approval workflow
builder.Services.AddWorkThroughMcp(new()
{
    Authority = authority, Resource = resource, Environment = "production",
    Actions = new HashSet<McpAction> { McpAction.RetryJob, McpAction.PauseQueue, McpAction.ResumeQueue },
    ApprovalLifetime = TimeSpan.FromMinutes(15)
});
```

`workthrough_prepare_action` takes an action, a target (job ID, queue or worker ID) and a reason. It checks the action's permission (`jobs.retry`, `jobs.cancel`, `flows.recover`, `queues.manage` or `workers.manage`) and binds the target's current attempt or revision. It hands the proposal to `IMcpActionApprovals.SubmitAsync` and returns it with status `awaiting-approval`. A person approves it in the host's workflow. `workthrough_execute_action` then takes only the proposal ID. It runs the approved copy through the existing audited operation, with the proposal ID as the command ID.

| Status | Meaning |
|---|---|
| `succeeded` | The action ran, or a repeat returned the first result |
| `not-approved` | No approval, or the proposal belongs to another caller or environment |
| `expired` | Past `ApprovalLifetime` |
| `forbidden` | The caller lacks the action's permission now |
| `precondition-failed` | The target changed after preparation; nothing happened |
| `not-found` | The job does not exist |

Only `succeeded` changed engine state. Tenant-restricted principals cannot use actions. The endpoint needs write-capable `IJobAdministration`, `IExecutionOperations` and `IJobQueries`; without them, or without `IMcpActionApprovals`, the action tools are not listed.

## Access evidence and bounds

`IMcpAccessSink` receives a server-generated request ID, trusted issuer/subject/client, tool name, configured endpoint/environment, optional execution/run ID, sensitive-read flag, outcome and duration. It receives no raw arguments, results, tokens, correlation labels or diagnostic/payload text. The default sink uses structured application logging. Register a durable sink when security retention requires it; sink failure or its two-second timeout suppresses the successful result. This is separate from engine mutation audit. Authentication failures and HTTP-level rejections remain host access-log concerns.

Enforced bounds:

- Five-second cooperative tool/query cancellation, with disconnect propagation. Host callbacks prevent a hard wall-clock SLA.
- 16 KiB request bodies, request IDs up to 128 characters, no batch requests and a five-second body-read budget.
- 48 KiB serialized tool results including structured content and compatibility text, reserving space below the 64 KiB response budget for protocol framing. Oversized results return `response_too_large` and guidance to lower page size; no broken JSON or silently partial page.
- Host-wide concurrent HTTP admission (default 16), no waiting queue, and per-issuer/subject fixed-window admission (default 120 requests/minute). Rejections return 429 and `Retry-After: 60`. Fleet-wide limits need a gateway.
- Default 25 rows, maximum 100 per section; default 24-hour and maximum seven-day execution/audit/archive search windows. Cleanup preview returns at most ten units. Direct job/flow/archive lookup has no age filter. Pages are separate observations with explicit coverage.

Errors contain bounded codes/request IDs, not database exceptions. SDK tool-binding failures can appear as `invalid_or_unavailable`; errors are never successful empty history. Cursors preserve query/window/position and are not credentials. See [query semantics](execution-queries.md).

## Verification boundary

Tests use the official C# 2.2.0 client over Streamable HTTP with its current protocol, plus an independent HTTP JSON-RPC client using revision 2025-11-25. The latter exercises wire requests/responses without the C# client implementation; it is not a claim that two third-party AI applications were validated. Fresh NuGet-only consumers make authenticated calls over real loopback Kestrel on SDK/runtime 8 and 10.

Tests cover signed JWTs, wrong issuer/audience/expiration, no dashboard-scheme fallback, direct permission/resource checks, unknown mutations, redaction, pagination, read-only database connections, size/rate/concurrency limits, deadline/disconnect cancellation and failed access sinks. A [short 100,000-job contention campaign](performance.md#initial-retained-history-campaign--2026-10-10) completed on SQLite, PostgreSQL, SQL Server and Oracle with 20 official C# clients (a smoke version also passed on MySQL and MariaDB) and two worker processes, with no correctness failures; the proposed throughput budget was missed in some runs. The repeatable harness and CI smoke exercise those paths. See [verification](verification.md) for completed gates. Hosted identity-provider discovery/login, desktop AI application setup, repeated contention analysis and a two-hour soak with investigators remain deployment/release gates.

Protocol references: [official SDK](https://github.com/modelcontextprotocol/csharp-sdk), [HTTP guidance](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/transports/transports.html), [authorization specification](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization).
