# Security and operational boundaries

This alpha has not undergone an independent security assessment and is not production-certified.

- Reuse host authentication and identity-provider user management. Dashboard policies separate metadata, payloads, administrative actions, and audit reads. Anonymous access requires explicit development opt-in.
- Administrative endpoints validate permissions, CSRF tokens, trusted actor identity, reasons, command IDs, and state preconditions. Successful mutations, deduplication receipts, and attributed audits commit together. Database administrators can still change the underlying records; this is not a tamper-proof audit service.
- The Angular console is embedded in the Dashboard package and uses real APIs. Untrusted values render as text, assets use a restrictive content security policy, and database queries use parameters.
- Payloads, outputs, progress descriptions, and exception details may contain business data. Diagnostic access requires payload permission. Configure DescribeFailure to redact before storage, protect database access and backups, and consider `ProtectPayloads` to encrypt business data at rest ([ADR-027](docs/adr/0027-payload-protection.md)). Payload/archive size bounds, explicit archival and opt-in automatic retention with holds are implemented.
- The deployment boundary is one configured store/environment. Correlation IDs never grant access. Hosts sharing a store between tenants register an `ITenantAccessResolver` (ADR-019): tenant-scoped principals see only their tenants' executions through the dashboard and MCP, and store-wide surfaces are denied to them. This scoping does not cover direct database access or archives.
- Shared limits govern valid leases. Retries and process pauses require idempotency and, where applicable, resource-level fencing for external side effects.
- NuGet audit warnings are build errors. Do not silence vulnerability warnings to make CI pass.
- The OIDC sample signs in against a local Keycloak in `scripts/check-oidc.py`; hosted identity providers such as Entra ID or Auth0 are not yet verified. See [sign-in and permissions](docs/sign-in.md), [console security](docs/dashboard-security.md) and [ADR-007](docs/adr/0007-dashboard-access-and-audit.md).

There is no hosted disclosure contact yet. Before public release, maintainers must establish a private reporting channel, license, and supported-version policy.
