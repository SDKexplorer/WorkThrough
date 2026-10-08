# ADR-010: explicit deployment capability checks

Status: implemented.

Stable identities decouple durable work from CLR locations, but cannot ensure that deployment artifacts include the intended processor registration. A read-only use case compares persisted processor/version/queue requirements with explicitly declared worker profiles. The console separately shows coverage observed from recent heartbeats.

Core owns the data-only comparison and the read port. Relational providers aggregate outstanding jobs and enabled schedule targets under read snapshots. This preserves the inward dependency rule and avoids calling old application code. Exact identity/queue matching is deliberate: inferring compatibility from class names, namespaces, JSON similarity, or another version would reintroduce unsafe assumptions.

A partial inventory is never a passing deployment check. Counts and output are bounded, while large-store query costs still require measurement. Read snapshots do not freeze new producer traffic; the result is evidence at an observed time. Existing heartbeats cannot guarantee future fleet composition, so CI/deployment tools must supply the complete proposed set.

This implements identity coverage, not business compatibility, local concurrency availability, payload schema conversion, or engine protocol negotiation. Unsupported old/new engine combinations retain the separate ADR-005/008 rollout restrictions. See deployment-preflight.md for exact inclusion rules and verification scenarios.
