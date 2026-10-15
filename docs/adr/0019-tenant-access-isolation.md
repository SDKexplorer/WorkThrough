# ADR 019: tenant access isolation

Status: accepted, implemented.

## Context

ADR-018 added `TenantId` for admission. Until now a dashboard or MCP principal was store-wide: authorized to read every execution in the configured store, with correlation explicitly not an access boundary. Shared hosting needs principals that see only their own tenants' work.

## Decision

- The host registers an `ITenantAccessResolver`, which maps the authenticated `ClaimsPrincipal` to a `TenantAccess`: either `AllTenants`, or `Only(tenants)` with at most 100 tenant IDs.
  - Tenants come from trusted claims or server-side lookups, never from request parameters.
  - Without a resolver every principal stays store-wide, so existing deployments are unchanged.
  - A resolver exception fails the request.
- **Searches:** a tenant-scoped principal's searches are filtered in storage (`ExecutionQuery.TenantIds`, `JobFilter.TenantIds`), intersected with any tenant filter the caller supplied. Paging stays consistent because the filter is part of the cursor fingerprint.
- **Lookups:** lookups of other tenants' executions, and of untenanted executions, return exactly what an absent execution returns: not found, a null execution, `executionExists=false`, or a not-live flow. Existence is not disclosed.
- **Flow runs:** decided by their steps' tenant. Every step of a run carries the run's tenant.
- **Readiness:** keeps the waiting reason but withholds store-wide capacity counts and worker evidence, which describe other tenants' load.
- **Commands:** retry, cancel, recover, replay, and flow pause/resume first check that the target is visible. Permissions still apply independently.
- **Store-wide surfaces are denied by default:**
  - Dashboard endpoints serve tenant-scoped principals only when marked tenant-capable, so a new endpoint is store-wide until reviewed.
  - MCP lists and allows only the seven per-execution tools.
  - Denied surfaces include statistics, metrics, audit, archives, retention, limits, queues, workers, schedules and definitions.
- The core enforcement is `TenantScoping` decorators over the query ports, shared by the dashboard and MCP.

## Consequences

- Isolation applies to metadata, payloads and diagnostics read through WorkThrough's hosts. It does not protect against principals with direct database access, and it does not encrypt per tenant.
- Archived executions are not tenant-indexed, so archives remain store-wide.
- Untenanted work is invisible to tenant-scoped principals. Producers serving shared hosting must set `TenantId`.
- Tenant-scoped principals do not see fleet statistics or limits. Operators keep store-wide access.
