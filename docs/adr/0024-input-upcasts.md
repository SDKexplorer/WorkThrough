# ADR 024: input upcasts

Status: accepted, implemented.

## Context

ADR-001 makes stored work name a versioned address. When an input's meaning changes, a new version is required, and every queued job, delayed job, schedule and pinned flow step at the old version still needs a handler. Until now that meant keeping the old job class deployed until all of that work drained, which can be months for schedules and long flows. Other engines solve the same problem with patching or worker versioning (Temporal) or message upcasters (event-sourcing systems).

## Decision

A worker may serve an older version of a key through an upcast: a conversion from the stored input to the input of a registered job at a newer version of the same key.

- `JobCatalog.Upcast` and `WorkThroughBuilder.AddUpcast(from, to, convert)` register the older address. Executing it deserializes the stored input, converts it and invokes the target registration. Targets may themselves be upcasts, so conversions chain.
- Stored work never changes: the job, its attempts and its journal keep the older address and the original input. Operators see what was submitted.
- An upcast bridges versions of one key, so admission groups and limits are unchanged. Changing the key remains a registry alias ([identity evolution](../identity-evolution.md)), which fences the fleet.
- No registry entry or fencing is needed. Each worker serves the older version independently: one may run the old class, another an upcast, and both are correct for at-least-once execution.
- Deployment preflight already compares stored addresses with the addresses a proposed fleet registers; an upcast address counts as registered. Heartbeat descriptions name the version that runs it.

## Consequences

- An old job class can be removed as soon as an upcast covers its stored work, verified by preflight.
- Conversions run on every attempt. They must be pure and deterministic; a conversion that throws fails the attempt like a handler exception.
- An upcast cannot change what already happened: completed attempts keep their outputs, and replaying an old job runs the current conversion.
