# ADR-001: Stable contract identities and generated registration

Status: accepted for the initial implementation.

Context: a processor can move between folders, namespaces, classes, and assemblies while durable work remains queued. Serialized invocation metadata couples that work to historical code layout.

Decision: persist explicit `JobContractId` values and plain JSON. Resolve current handler implementations through a deployment-local catalog. Offer build-time manifests and explicit registration over the same catalog. Require deployment references; never guess replacement types from names.

Alternatives: rewriting stored CLR names on each release; reflection heuristics; keeping old assemblies forever. These obscure compatibility and turn routine refactoring into data migration.

Consequences: compatible moves require no job rewrites. Incompatible input/output or semantic changes still require versioning. The published-artifact refactor test is a release gate.
