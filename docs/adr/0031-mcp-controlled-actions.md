# ADR 031: MCP controlled actions

Status: accepted, implemented.

## Context

The MCP endpoint was read-only (ADR-019 and the [MCP specification](../../specs/mcp-operations.spec.md)). Operators asked for the next step: let an assistant that has diagnosed a stuck job retry it, or pause a queue during an incident. A model must not be able to act on its own say-so, and text the model reads, such as a failure message, must never become authority.

## Decision

A host enrolls actions explicitly in `WorkThroughMcpOptions.Actions`: retry, cancel and flow-step recovery for one job, queue pause and resume, worker drain and resume. Nothing is enrolled by default, and the endpoint stays read-only. Replay, input correction, publishing, limits, bulk actions and archive purge stay out: they lack exact preconditions, or they repeat or reshape business effects.

Each action goes prepare → approve → execute:

1. **Prepare** checks the action's permission, reads the target's current attempt or control revision and returns a proposal. The proposal holds the exact action, target, precondition, reason, caller (issuer and subject), environment, expiry and a plain description of its effects. The host's `IMcpActionApprovals` receives it. Nothing in the engine changes.
2. **Approve** happens in the host's own workflow: a console page, a chat approval, a ticket. WorkThrough supplies no approval UI and accepts no approval from the model. A `confirm` argument, repeated user text or a tool annotation is not approval.
3. **Execute** takes only the proposal ID. It uses the approved copy the host returns, never arguments from the model. It requires the same caller and environment, an unexpired proposal and the action's permission, checked again at that moment. Then it runs the existing audited operation with the bound precondition.

The proposal ID is the administrative command ID. A repeated execute after a lost response replays the stored command receipt, so the action happens once and the audit has one entry. If the target changed after preparation, the store's precondition fails and nothing happens.

Tenant-restricted principals cannot use actions; the tools are store-wide. The endpoint needs write-capable `IJobAdministration`, `IExecutionOperations` and `IJobQueries` registrations. Without them, or without an approval service, the action tools are not listed and direct calls are refused.

## Consequences

- An assistant can carry an investigation through to a fix, and a person still decides each change.
- A host that enrolls actions runs the MCP endpoint with a write credential; a read-only MCP deployment stays possible and is the default.
- Approval durability is the host's: an in-memory approval store loses pending proposals on restart, which only means they must be prepared again.
- A retry after the proposal expired reports `expired` even if the first execute committed; the audit shows what happened.
