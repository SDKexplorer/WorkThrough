# Sign-in and permissions

WorkThrough has no user accounts or passwords. Your identity provider (Entra ID, Auth0, Keycloak, Okta and so on) signs people and programs in. WorkThrough reads the claims in what the provider issues and decides what the caller may do.

## Two ways in

| Who | Where | How they sign in | Package |
|---|---|---|---|
| People | The operations console, mapped with `MapWorkThroughDashboard` | In the browser, through the provider's login page (OpenID Connect authorization code flow with PKCE); the host keeps a session cookie | `WorkThrough.Dashboard` |
| AI assistants and scripts | The MCP endpoint, mapped with `MapWorkThroughMcp` | With an OAuth access token issued for the MCP endpoint, sent as a bearer token | `WorkThrough.Mcp` |

The console uses the host's ASP.NET Core authentication, whatever it is. The MCP endpoint has its own JWT bearer scheme, so a console cookie never authenticates an MCP call. [Shop.Dashboard](../samples/Shop.Dashboard/Program.cs) and [Shop.Mcp](../samples/Shop.Mcp/Program.cs) show both.

## What WorkThrough needs from a signed-in caller

1. **Who they are.** The `iss` and `sub` claims (the console falls back to `NameIdentifier`). Every command records them as its actor in the audit trail. A request without them cannot change anything.
2. **What they may do.** Claims of type `workthrough.permission`, one per permission below. Providers do not issue these. The host maps the provider's roles to permissions when the caller signs in, as both Shop samples do with `RolePermissions`. A role the host does not map grants nothing.
3. **Which tenants they see.** Only when the host registers an `ITenantAccessResolver` ([tenants](#tenants)).

## Permissions

| Permission | Allows |
|---|---|
| `executions.read` | Seeing jobs, flows, schedules, calendars, workers, queues, metrics and limits, without business data. The console shows nothing without it, so grant it with every other permission. |
| `payloads.read` | Seeing inputs, outputs, progress text, error messages and stack traces |
| `jobs.retry`, `jobs.cancel` | Retrying or cancelling one job |
| `jobs.replay` | Running a finished job again as a new job |
| `flows.recover` | Recovering a failed flow step, pausing and resuming a flow, forking a run from a step |
| `inputs.correct` | Replacing a failed job's input and retrying it; also needs `payloads.read` |
| `definitions.edit`, `definitions.publish`, `definitions.retire` | Saving, publishing and retiring flow definitions |
| `schedules.manage` | Saving, publishing, enabling and pausing schedules; saving and deleting calendars |
| `limits.manage` | Concurrency, keyed and rate limits |
| `queues.manage`, `workers.manage` | Pausing queues and setting their weights; draining workers |
| `audit.read` | The audit trail |
| `retention.read`, `retention.manage` | Retention status; the retention policy, holds and incidents |
| `archives.manage` | Creating archives and exporting their contents |

The server checks each permission on every request; hiding a button is not the check. MCP uses the same names: `executions.read` for its read tools, `payloads.read` for diagnostics, `audit.read` and `retention.read` for history, and the action permissions for [controlled actions](mcp.md#controlled-actions).

The samples map these roles, which you can copy or rename:

| Role | Permissions |
|---|---|
| `workthrough-viewer` | `executions.read`, for users scoped to their tenants |
| `workthrough-operator` | read, `tenants.all`, retry, cancel, replay, recover, queues, workers |
| `workthrough-designer` | read, `tenants.all`, definitions, schedules |
| `workthrough-administrator` | read, `tenants.all`, limits, audit, retention, archives |
| `workthrough-diagnostics` | read, `tenants.all`, payloads |
| `workthrough-data-steward` | read, `tenants.all`, payloads, input correction |
| `workthrough-auditor` | read, `tenants.all`, audit |

## Tenants

Without an `ITenantAccessResolver`, every signed-in caller sees the whole store. With one, a caller limited to some tenants sees only those tenants' executions; other work looks absent, and store-wide pages (statistics, audit, limits, queues, workers, schedules, definitions, retention) answer 403 ([ADR-019](adr/0019-tenant-access-isolation.md)).

The samples' [TenantClaimResolver](../samples/Shop.Shared/TenantClaimResolver.cs) reads tenants from a claim the provider issues (`tenant` by default, set by `TenantClaim`):

- a caller with tenant claims sees those tenants;
- a caller whose roles map to `tenants.all` sees the whole store;
- anyone else sees nothing.

So a provider that forgets to issue a tenant claim hides data rather than revealing it. Take tenants only from the provider, never from the request.

## Setting up a provider

The console needs a web application registration with a client secret and the redirect URI `https://<host>/signin-oidc`. The MCP endpoint needs an API registration whose identifier is the endpoint's public URI (`Mcp:Resource`). Shop.Dashboard reads `Identity:Authority`, `ClientId`, `ClientSecret`, `RoleClaim`, `TenantClaim` and `RolePermissions`. Shop.Mcp reads `Mcp:Authority`, `Resource`, `Environment`, `RoleClaim`, `TenantClaim` and `RolePermissions`. Keep secrets in environment variables (`Identity__ClientSecret`) or a secret store.

### Keycloak

[tests/Oidc/workthrough-realm.json](../tests/Oidc/workthrough-realm.json) is a complete realm. It has a console client, two MCP service-account clients, the `roles` and `tenant` mappers and two users. The authority is `https://<keycloak>/realms/<realm>`. `RoleClaim` is `roles`.

### Microsoft Entra ID

- **Console:** register a web application with the redirect URI and a client secret. Define app roles named like the roles above and assign them to users or groups; Entra puts assigned roles in the ID token's `roles` claim. The authority is `https://login.microsoftonline.com/<tenant-id>/v2.0`. `RoleClaim` is `roles`.
- **MCP:** register an API, set its application ID URI to `Mcp:Resource`, and define app roles that applications and users can hold. Set the manifest's `requestedAccessTokenVersion` (`accessTokenAcceptedVersion` in the older manifest) to 2, so the token issuer matches the v2.0 authority. Clients request the scope `<application ID URI>/.default`. `RoleClaim` is `roles`.
- **Tenants:** Entra has no per-user tenant claim. Issue one from a directory extension attribute through optional claims or a claims-mapping policy, or write a resolver that derives tenants from app roles or groups.

### Auth0

- **Console:** create a Regular Web Application with the callback URL. Auth0 keeps custom claims out of ID tokens unless they have a namespaced name, so add a post-login Action that sets, for example, `https://workthrough.example/roles` from `event.authorization.roles` and `https://workthrough.example/tenant` from the user's metadata. Set `RoleClaim` and `TenantClaim` to those names. The authority is `https://<domain>/`, with the trailing slash.
- **MCP:** create an API whose identifier is `Mcp:Resource`, enable RBAC and "Add Permissions in the Access Token", and give roles or machine-to-machine applications its permissions. `RoleClaim` is `permissions`, and `RolePermissions` maps each Auth0 permission name to WorkThrough permissions.

## What has been verified

| Check | Keycloak 26.4, local | Entra ID, Auth0 |
|---|---|---|
| Console sign-in, role mapping, tenant scoping (`scripts/check-oidc.py`) | Passed | Not run |
| MCP tokens: a mapped client reads, a forged signature gets 401, an unmapped client sees no tools (`scripts/check-hosted-idp.py`) | Passed (`--local-keycloak`) | Not run; the script runs against them once the provider is configured |

To check the console against a hosted provider by hand:

1. Sign in as a user holding an operator role. The console loads and shows every tenant.
2. Sign in as a user holding only `workthrough-viewer` and one tenant claim. Only that tenant's jobs are visible, and the audit page answers 403.
3. Retry a failed job as the operator. The audit entry shows the provider's issuer and the user's subject.
4. Remove the user's role in the provider, sign out and in again. Commands now answer 403.

## Before production

- Serve the console and MCP over HTTPS. Behind a TLS-terminating proxy, configure trusted forwarded headers before authentication.
- Persist and protect ASP.NET Data Protection keys when several console instances share sessions.
- The sample's session lasts 30 minutes without sliding. A role removed in the provider takes effect at the next sign-in; revoke sooner with host-specific session validation.
- Anonymous access exists only in the sample's Development mode without an authority. Never deploy it.
- Give the console and MCP read-only database credentials unless they run commands ([providers](providers.md#read-only-hosts)).
- Use tenant claims from the provider, and keep the resolver failing closed.
