# Community evidence informing the plan

Reviewed 2026-10-07. This is a purposive sample of 20 public discussions/issues relevant to this project's requirements, not a statistical estimate of complaint frequency. Old reports and fixed bugs are design lessons, not assertions that current releases remain broken.

## Hangfire

| Thread / date | What the discussion establishes | Design response |
|---|---|---|
| [TypeLoadException after renaming](https://discuss.hangfire.io/t/any-way-to-treat-and-fix-typeloadexception/9529), Nov 2022 | Renamed classes/namespaces broke scheduled work; replies suggest resolver customization or recreation. No confirmed final resolution. | Mandatory stable identities; explicit legacy adapters later. |
| [Job versioning/evolution](https://discuss.hangfire.io/t/job-versioning-evolution-of-job-types/5775), Jan–Feb 2019 | Users distinguish moved types, changed arguments, and upcasting; replies discuss resolution hooks and retaining methods. | Separate implementation relocation from incompatible contract evolution. |
| [Standalone dashboard without DLLs #1340](https://github.com/HangfireIO/Hangfire/issues/1340), Jan 2019 | Report against 1.6.22: workers succeed while a shared dashboard cannot render their jobs without application DLLs. Page remained open when reviewed. | Dashboard reads plain stored metadata without application contracts. |
| [Concurrency per queue](https://discuss.hangfire.io/t/disable-concurrent-execution-per-queue/4008), Nov 2017 / Jun 2020 | Custom locks occupy worker threads; follow-up wants sequential work per customer with parallelism across customers. | Admission before execution slots; keyed serialization is a separate future control. |
| [Recurring overlap/backlog](https://discuss.hangfire.io/t/disableconcurrentexecution-and-prevent-queuing-of-new-job-instances-while-existing-job-is-in-progress/9636), Dec 2022–Jan 2024 | Preventing overlap does not prevent redundant scheduled work accumulating. Suggested custom approaches have caveats. | Treat allow/skip/coalesce as schedule policy, separate from concurrency. |
| [Concurrency/retry on multiple servers](https://discuss.hangfire.io/t/how-to-configure-concurrency-and-retry-with-multiple-server-instances/11126), Jan 2025 | User reports attributes seemingly ignored across dashboard and specialized workers. Unresolved report, not a verified defect. | Stable policy identity; visible local versus shared scope and effective policies. |
| [Continuations created on retries](https://discuss.hangfire.io/t/cancel-the-job-continuations-if-exception-occur/11216), Mar–May 2025 | Handler-created continuations accumulate across retries; the author reports a cleanup filter. | Persist graph edges once; atomically activate uniquely identified logical successors. |
| [Shutdown versus delete](https://discuss.hangfire.io/t/job-shutdown-vs-delete/10952), Oct 2024 | User reports confusing cancellation/success outcomes on 1.8.14; no confirmed diagnosis. | Distinct interruption, expiry, cancellation, failure, and success states. |
| [Heavy SQL usage](https://discuss.hangfire.io/t/heavy-sql-usage-reported-by-host/7078), Feb 2020–Mar 2021 | Later replies distinguish long-poll waiting in query tools from actual CPU use. The original CPU allegation is unconfirmed. | Measure idle traffic, latency, connections, and CPU separately. |
| [Duplicate batch continuations](https://discuss.hangfire.io/t/multiple-continuations-are-created-with-the-same-jobid-for-all-jobs-in-a-batch/3055), Mar 2017 | Maintainer diagnosed continuation insertion outside a failed state transaction. Fixes were released in 1.6.10/1.6.12. | Historical regression case for atomic, idempotent transitions. |

Hangfire already has type serializer/resolver extension points and concurrency/rate-limiting facilities. Its [background-method documentation](https://docs.hangfire.io/en/latest/background-methods/index.html) explains persisted invocation metadata, while its [throttling documentation](https://docs.hangfire.io/en/latest/background-processing/throttling.html) explicitly discusses best-effort limitations under failures. The opportunity is clearer defaults and compatibility boundaries, not pretending these existing capabilities are absent.

## Wolverine

| Thread / creation date | What the discussion establishes | Design response |
|---|---|---|
| [Old message aliases #2340](https://github.com/JasperFx/wolverine/discussions/2340), 2026-03-23 | Maintainer points to existing identities/forwarders; reporter acknowledges overlooking forwarding. Discoverability issue, not missing support. | Make stable identity mandatory and migration paths easy to find. |
| [Generated types in wrong assembly #4151](https://github.com/JasperFx/wolverine/issues/4151), 2026-08-26 | Handler-library versus generated-entry-assembly mismatch surfaced after a healthy startup; closed via #4155 with validation/settlement fixes. | Validate actual deployed manifests and dependencies before acquisition; published-artifact tests. |
| [Generated discovery #2906](https://github.com/JasperFx/wolverine/issues/2906), 2026-05-26 | Maintainer proposes manifests for discovery/AOT/startup; closed through #2928. | A small registration generator is useful; avoid a second runtime dispatch framework. |
| [Publish during DB outage #1683](https://github.com/JasperFx/wolverine/issues/1683), 2025-09-07 | Reporter on 4.5.3 describes successful return despite eventual persistence failure. Closed with `wontfix`; rationale was not visible in retrieved content. | Specify durable acceptance and caller idempotency. Do not extrapolate a universal current defect. |
| [Stranded exclusive inbox #3590](https://github.com/JasperFx/wolverine/issues/3590), 2026-07-23 | Listener/recovery assignment mismatch stranded unowned work; closed via #3598, documented fixed in 6.22. | Test recovery with heterogeneous worker capabilities and ownership. |
| [Shutdown settlement #4213](https://github.com/JasperFx/wolverine/issues/4213), 2026-08-31 | Follow-up diagnoses disposal/executor creation causing inappropriate dead-lettering; closed via #4218. | Keep handler scopes alive while draining; never mark unexecuted work as handled. |
| [EF outbox commit ordering #3744](https://github.com/JasperFx/wolverine/issues/3744), 2026-07-30 | 6.24.0 report of aggregate commit without durable envelope; [fix #3754](https://github.com/JasperFx/wolverine/pull/3754) merged Aug 1. | Verify database rows and restart behavior, not only in-memory publish events. |
| [Producer without durability agent #1875](https://github.com/JasperFx/wolverine/issues/1875), 2025-11-28 | Producer-only configuration unexpectedly disabled EF outbox integration in 5.4.0; [fix #1907](https://github.com/JasperFx/wolverine/pull/1907) merged Dec 6, milestone 5.7.0. | Storage/client role must work without a processing role. |
| [Partitioned sequential processing #1637](https://github.com/JasperFx/wolverine/issues/1637), 2025-08-12 | Requirement to parallelize across streams and serialize within one; completed Sep 15, milestone 5.0. | Distinguish type concurrency, business-key serialization, and correlation grouping. |
| [Inactive consumer appears healthy #2396](https://github.com/JasperFx/wolverine/discussions/2396), 2026-04-01 | Unanswered report of silent consumption stoppage; root cause unverified. | Health should measure acquisition/completion progress, not process existence alone. |

Cross-checks: [message identities and versioning](https://wolverinefx.net/guide/messages), [durable messaging](https://wolverinefx.net/guide/durability/), [partitioning](https://wolverinefx.net/guide/messaging/partitioning), and [changelog](https://github.com/JasperFx/wolverine/blob/main/CHANGELOG.md). Current docs require explicit forwarder registration in v6; automatic forwarder scanning was removed for trimming/AOT. A merged fix does not by itself establish the exact NuGet release containing it.

## Priority inference

Identity survives refactoring first. Next: durable acceptance, provider conformance, role independence, explicit concurrency scopes, atomic graph transitions, and operational explanations. Recurring overlap policies, upcasters, application outbox integration, and controlled checkpoint/compensation operations follow. These priorities reflect the user's needs and the reviewed evidence, not a claim of novelty or measured market prevalence.

## Processor relocation: what the evidence does and does not establish

Rechecked the primary threads and documentation on 2026-10-07 after the user's follow-up. Hangfire's documented invocation format includes a type/namespace/assembly, method, parameter types, and arguments. The renaming discussion above is a concrete example of that coupling causing deployment problems. Resolver hooks can remap some historical identities without editing database rows; moving code is not universally impossible in Hangfire.

Wolverine already separates message handling from message identity. Its default message identity uses the message type's full name, and `[MessageIdentity("stable-name")]` supplies an explicit alternative. A compatible handler-only move and a renamed message contract are different changes. The alias discussion ended with the reporter acknowledging an existing forwarding mechanism. It would be inaccurate to market stable logical identities as wholly absent from Wolverine.

The product opportunity is an enforced default and a complete operational experience: stable keys required at declaration, a build-generated mapping to current handlers, a dashboard that needs no processor DLLs, inspectable persisted flow state, and published-artifact regression tests. Stable identity itself is an established technique.

For WorkThrough, a compatible processor relocation must leave saved execution identity and payload untouched. Rebuild and deploy references to the new implementation. Historical attempt assembly names stay as diagnostics. The current published refactor proof tests this distinction; it does not establish arbitrary payload or behavior migration.

Two useful additions follow from this evidence: preflight reports for capability gaps before deployment, and mutable display labels separate from permanent keys. Explicit aliases/adapters remain appropriate when deliberately changing a logical identity or importing legacy invocations. No mechanism should guess that a newly discovered handler is semantically interchangeable with a removed one.

This research is sufficient to identify engineering priorities. It does not establish which feature the majority of users would pay for, or that these themes are the most frequent requests across either community.
