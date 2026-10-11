# Deployment preflight

The permanent processor key makes code relocation safe only when the replacement deployment actually registers a compatible implementation. Preflight compares stored requirements with the **complete proposed fleet**, without loading old processor assemblies or claiming work.

```csharp
var report = await preflight.CheckAsync([
    new WorkerCapability("billing", [new JobContractId("invoices.generate", 1)], ["billing"]),
    new WorkerCapability("shipping", [new JobContractId("orders.dispatch", 1)], ["shipping"])
], ct);

if (!report.AllRequirementsCovered)
    throw new InvalidOperationException("Review missing processor versions or queues before deployment.");
```

`DeploymentPreflight` and `IDeploymentQueries` are registered by normal WorkThrough/provider composition. Pass every worker profile you intend to keep after deployment. Checking a single replacement worker against a heterogeneous fleet can intentionally report requirements served by other workers; list those profiles too. Processor version and queue must match on the same worker. Matching a processor on one worker and its queue on another does not establish coverage.

The inventory includes queued (including future work), running, and waiting jobs; failed/cancelled steps in recoverable active flows; and enabled published schedule targets. Completed history, standalone terminal failures, paused schedules, drafts, and unused published flow definitions do not create requirements. A new producer or schedule activation after the snapshot can introduce additional requirements.

The report includes outstanding and currently eligible job counts and the earliest eligible timestamp. Waiting dependencies, future-due jobs, and paused/incident runs are excluded from ready counts. Capacity limits do not change eligibility. Flow successor activation now records its eligibility time when dependencies resolve; historical rows created before this change may retain their original creation-based available_at timestamp. Time spent behind capacity limits or an intervening pause is included in wall-clock backlog age; this is not an execution-duration metric.

SQLite reads under a deferred snapshot transaction; PostgreSQL uses Repeatable Read. Neither reader takes the admission advisory lock or changes jobs. Query output is bounded to 1,000 processor/queue groups and 1,000 enabled schedules. The report explicitly marks truncation and refuses complete-coverage confirmation. Grouped counts may still scan outstanding rows; measure store overhead before polling at high frequency.

The dashboard's **Workers & capacity → Processor coverage** view compares requirements with stored heartbeats seen within 60 seconds. That is an observation of the current fleet, not a declaration of the next deployment. It exposes metadata through `executions.read`, without payloads or implementation assemblies. Busy, paused-by-policy, and semantically incompatible workers can still advertise identity coverage: the report is not a throughput, code correctness, or transaction compatibility guarantee.

## Worker sample

```sh
# JSON describing the sample worker's registered processor identities and queues:
dotnet run --project samples/Shop.Worker -- --describe-worker

# Read an existing initialized store and exit 0 for complete coverage, 2 otherwise:
Shop__Sqlite='Data Source=/absolute/path/shop.db' \
  dotnet run --project samples/Shop.Worker -- --preflight
```

These command modes build the composition root but do not start hosted workers, seed limits, initialize storage, or process jobs. The sample checks its own profile; use the SDK to combine profiles. Connection/schema failures remain errors, never a successful empty report.
