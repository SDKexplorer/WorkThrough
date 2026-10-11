# ADR 023: child flows

Status: accepted, implemented in schema 8 and engine protocol 4.

## Context

Flows are persisted graphs. Large processes need composition: an order flow runs a shipping flow, possibly once per parcel. Each part should be versioned, inspectable and recoverable on its own. Copying the shipping steps into every flow that ships would duplicate definitions and their maintenance.

## Decision

A child step runs another published flow and waits for it.

- **Declaring:** `FlowBuilder.Child(id, childDefinition, input, timeout)` stores the child's key, version and a timeout. `ForEach(id, childDefinition, …)` runs one child per array element. Starting a parent publishes any child definition it carries that the store lacks, with the same immutability check as the parent. A definition loaded from storage names its children only by key and version.
- **Starting:** when the step becomes ready, the transaction that makes it ready starts the child run. The child receives:
  - the step's bound input;
  - the step's correlation, tenant, partition key and trace;
  - a link to the parent run and step.

  The step waits like a signal wait: it holds no worker and has a deadline.
- **Completing:** when the child succeeds or fails, its final transaction records a signal named after the child run on the parent run. Dispatching signals then advances the parent under the parent's own flow lock.
  - No transaction locks a child flow and then its parent, so this adds no lock-ordering hazard.
  - A child in an incident is not finished; the parent keeps waiting while operators recover it.
- **Outcome:**
  - On success the step's output is `{"stepId": output}` for each of the child's final steps, those that no other step depends on.
  - On failure the step fails with the child's error and follows the parent's failure policy.
  - A missing or unpublished child, or nesting deeper than 8, fails the step. A deadline that passes expires it.
- **Recovery:** recovering a failed child step plans it again, which starts a new child run. The failed run stays for inspection.
- **Inspection:**
  - Runs record `parent_run_id`, `parent_job_id` and `depth` (schema 8).
  - `IJobQueries.ChildRunsAsync` lists a run's children.
  - The console links parent and child runs.
- **Rollout:** older workers would treat a child step as a job no one runs, so definitions with child steps need engine protocol 4 before a run can start (ADR-021). Schema 8 is additive.

## Consequences

- A parent advances after its child finishes, on the next signal dispatch. Workers dispatch signals as part of their regular claims, so the delay is about one poll interval.
- Cancelling a parent step does not cancel its child run. The child finishes on its own, and its outcome no longer matches a waiting step. Cascading cancellation is planned.
- Archives move runs independently; a child's parent link may refer to a run that was archived first.
