# ADR-002: Persist workflow graphs and atomic transitions

Status: accepted for the initial implementation.

Context: restarting between parent completion and successor dispatch must not strand a flow or create extra children.

Decision: persist immutable graph versions, per-run input, and logical step records. Apply the pure transition planner inside the provider's completion transaction. Record selected/skipped outcomes through step states.

Alternatives: create continuations inside handlers; serialize closures; introduce a general replay-based C# workflow interpreter immediately.

Consequences: common dependency/branch workflows are inspectable and restartable. Graph changes need a new version. Arbitrary code expressions and loops are not part of the initial grammar.
