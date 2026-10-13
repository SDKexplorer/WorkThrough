# ADR 021: rolling upgrades

Status: accepted, implemented from schema 6.

## Context

Until schema 5, every schema upgrade required stopping all workers first. A host refused any store newer than its own schema, and nothing stopped an older worker from misreading work that used newer semantics. Deployments that cannot pause background work had no supported path.

Two kinds of change need different handling:

- **Schema changes** add tables, columns and indexes. Most are additive: older code that ignores them stays correct.
- **Semantic changes** alter what every host must do while work runs, such as how a flow fans out or how a limit is counted. One old host acting on the old rules can corrupt shared state, even if the schema is compatible.

## Decision

Track the two separately in a single row, `dj_compatibility (reader_minimum, protocol)`.

### Schema reader range

- `reader_minimum` is the oldest code schema version that remains correct against the installed schema.
- A host whose schema version is lower than the store's uses the store without migrating when `reader_minimum` is at or below its version. Otherwise it refuses with an instruction to upgrade.
- An additive migration keeps `reader_minimum`. A migration that older code would misread raises it to its own version, which turns that upgrade back into a stop-the-fleet change. The migration's ADR must say which it is.
- Schema 6 introduces the row with `reader_minimum` 6. Hosts of schema 5 and earlier cannot read it, so the upgrade to schema 6 is the last one that requires stopping older writers.

### Engine protocol

- `EngineProtocol.Supported` is the highest level of shared semantics a build understands. Level 1 is the baseline of every release before protocols.
- Workers advertise their level in heartbeats.
- `protocol` is the store's active level. A new store starts at the initializing build's level. An upgraded store stays at the baseline.
- **Activation:** a worker tries to raise the store to its own level after every heartbeat. Activation runs under the admission lock and succeeds only when:
  - no worker with a live heartbeat advertises a lower level;
  - every unexpired lease belongs to a live worker at the new level.

  Expired leases cannot settle, so they never block.
- **Refusal:** claims read the active level with their other settings and refuse a level above their build with `EngineProtocolException`. Administrative writes check it too. The worker logs the reason, stops acquiring, lets running attempts finish, and its host stops, like a changed processor registry.
- The level never decreases. A feature that changes shared semantics keeps its old behaviour until its level is active. Activation can perform a one-time conversion in the same transaction, such as initializing counters from the current rows.

### Why activation can be safe without checking every write

- An older worker without leases cannot start work after activation, because claims check the level in their admission transaction.
- An older worker with leases blocks activation until those leases settle or expire.
- Settlement only follows a claim, so it needs no check.
- Administrative writes take the admission lock and check the level themselves.
- Enqueue changes no shared semantics and is not checked.

## Rolling upgrade procedure

1. Apply the new schema with the migration credential while old workers run. It must be additive for their schema, as its ADR states.
2. Replace workers and other hosts one at a time. During the overlap, new hosts keep the active protocol's behaviour.
3. When the last older worker stops, the next heartbeat of a new worker activates the new protocol. Workers log the blockers until then.

## Consequences

- From schema 6, additive schema changes and new protocol levels deploy without stopping work.
- A host that is offline during activation and returns later refuses to claim. It logs why and needs an upgrade before it can work again.
- Rollback below an active protocol is not supported; a previous build would refuse the store. Restore from backup if a release must be withdrawn after activation.
- Hosts that never heartbeat, such as a standalone dashboard, do not block activation. Their administrative writes are refused if they are older than the active level.
