# Decision records

Each record states a decision, why it was made and what it costs. Records are not rewritten when later work changes the picture; a later record or a dated amendment does that. Current behavior is described in the [architecture](../architecture.md) and the guides.

| Record | Decision | Status |
|---|---|---|
| [001](0001-stable-contracts.md) | Stable contract identities and generated registration | Implemented |
| [002](0002-durable-graphs.md) | Persisted workflow graphs and atomic transitions | Implemented |
| [003](0003-storage-and-admission.md) | Provider-owned atomicity and shared admission | Implemented; optimized by 020 and 022 |
| [004](0004-package-and-host-boundaries.md) | Optional hosts and small inward dependencies | Implemented |
| [005](0005-runtime-and-wire-compatibility.md) | .NET 8 minimum and durable format compatibility | Implemented |
| [006](0006-processors-jobs-and-schedules.md) | Processor identity, job executions and schedules | Implemented |
| [007](0007-dashboard-access-and-audit.md) | Dashboard authentication, permissions and audit | Implemented |
| [008](0008-incidents-and-schema-upgrade.md) | Recoverable incidents and schema 2 | Implemented |
| [009](0009-idempotent-flow-starts.md) | Caller-idempotent flow starts | Implemented |
| [010](0010-deployment-coverage.md) | Deployment capability checks | Implemented |
| [010](0010-schema3-operations-and-identity.md) | Schema 3: aliases, durable extensions and archival | Implemented |
| [011](0011-mcp-operations.md) | Optional MCP adapter, read-only first | Read-only implemented; actions planned |
| [012](0012-retention-and-provider-expansion.md) | Retention automation and provider expansion | Implemented |
| [013](0013-fenced-retention-maintenance.md) | Fenced retention maintenance and schema 4 | Implemented |
| [014](0014-sql-server-storage.md) | SQL Server storage | Implemented |
| [015](0015-oracle-storage.md) | Oracle storage and resumable DDL | Implemented |
| [016](0016-immediate-acceptance-clock.md) | Immediate jobs use the accepting database's clock | Implemented |
| [017](0017-operator-command-results.md) | Operator commands return results | Implemented |
| [018](0018-execution-admission-controls.md) | Keyed, tenant and rate limits, bounded fan-out, weighted queues; schema 5 | Implemented |
| [019](0019-tenant-access-isolation.md) | Tenant access isolation | Implemented |
| [020](0020-write-path-throughput.md) | Write-path throughput without weaker admission | Implemented |
| [021](0021-rolling-upgrades.md) | Rolling upgrades: schema reader ranges and engine protocols; schema 6 | Implemented |
| [022](0022-limit-locks.md) | Exact limits without the admission lock; schema 7, protocol 3 | Implemented |
| [023](0023-child-flows.md) | Child flows; schema 8, protocol 4 | Implemented |
| [024](0024-input-upcasts.md) | Input upcasts between contract versions | Implemented |
| [025](0025-compensation.md) | Compensation; protocol 5 | Implemented |
| [026](0026-bounded-loops.md) | Bounded loops; protocol 6 | Implemented |
| [027](0027-payload-protection.md) | Payload encryption at rest; protocol 7 | Implemented |
| [028](0028-failure-branches.md) | Failure branches; protocol 8 | Implemented |
| [029](0029-mysql-storage.md) | MySQL and MariaDB storage | Implemented |

Two records share the number 010; both are kept as written because other documents link to them.
