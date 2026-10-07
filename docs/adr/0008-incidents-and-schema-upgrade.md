# ADR 008: recoverable incidents and additive schema 2

The new builder defaults to suspending a flow when a step exhausts retries. The failed step keeps its attempts; dependent work remains waiting, and admission for the run stops. An operator can retry the failed step with an expected attempt number and an attributed, idempotent command. Successful siblings and their outputs are retained. This does not undo external effects.

`FlowFailurePolicy.Stop` retains the original terminal fail-and-skip behavior. Old serialized graphs that lack this field deserialize to Stop. New published graphs explicitly store the selected policy. Pausing stops admission; running attempts may settle, with advancement deferred until resume.

Schema 2 adds tables for drafts/lifecycle, command receipts/audit, attempt diagnostics, worker/progress observations, and execution events. It does not rewrite invocation identities or payloads. Existing immutable definitions are imported as Published. Initialization applies the additive upgrade transactionally under the provider write lock. Stop all schema-1 workers before upgrading: schema-1 and schema-2 binaries must not share an active store. New binaries reject unknown newer schemas. Rollback requires restoring a pre-upgrade backup and reconciling external effects; deleting the schema version is not a rollback.

Draft revision numbers protect concurrent edits; graph versions pin executions. Retiring a definition prevents new starts and leaves existing runs intact. The existing code-first StartAsync(definition, ...) method may register a previously unseen graph as published, but it cannot bypass an existing Draft or Retired lifecycle. Dashboard publication is explicit and audited.

Progress and deadlines are distinct from leases. A renewed lease proves ownership, not progress. Exceeding an optional deadline requests cooperative cancellation; non-cooperative code remains visibly suspected-stalled and occupies its local slot until it returns. Revocation fences storage settlement but cannot stop arbitrary external effects.
