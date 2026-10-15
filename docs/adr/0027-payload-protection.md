# ADR 027: payload protection

Status: accepted, implemented in engine protocol 7.

## Context

Job inputs, outputs, flow inputs and signal payloads often carry personal or payment data. Until now they were stored as plain JSON, readable by anyone with database access, backups or exports. Database encryption at rest (TDE) protects disks, not people who can query the database.

## Decision

Stores encrypt business data before writing it and decrypt it after reading it, so the engine, handlers and the console still see the original JSON.

- `WorkThroughBuilder.ProtectPayloads(currentKey, keys)` sets an AES-256-GCM protector with a key ring. `ProtectPayloads(IPayloadProtector)` accepts a custom one, for example one backed by a key management service.
- A protected value is stored as an envelope: `{"$wt":"aes-gcm-v1","key":"…","nonce":"…","data":"…"}`. Each value gets a fresh 96-bit nonce. The key ID is authenticated data, so an envelope cannot be relabelled to another key.
- New values use the current key. Older keys stay in the ring to read what they wrote, so keys rotate without rewriting stored work. Remove a key only when no retained work uses it.
- Covered: job inputs (including the original input kept by input correction), job outputs, flow inputs, signal payloads, child-flow outcomes, and outbox requests.
- Not covered: schedule inputs, failure messages and stack traces, audit records, correlation IDs, tenant and partition keys, and idempotency fingerprints. Fingerprints are SHA-256 hashes of plain JSON, so a low-entropy input can be confirmed by guessing.
- Archives keep envelopes as stored. Reading an archive's business data needs the key that wrote it.

### Rollout

An older worker would hand an envelope to its handler as if it were the input. So stores write envelopes only once engine protocol 7 is active (ADR-021); before that they write plain JSON. A store reads the protocol at most every five seconds while it is below 7, and never again once it is 7. Plain JSON written earlier stays readable.

### Hosts without the key

- A host without a protector, or without a value's key, reads the envelope unchanged.
- A worker never runs a handler on an envelope. It fails the attempt with `PayloadKeyMissingException` and schedules a retry, even when the retry policy would stop. The attempt counts, so a worker that never gets the key ends in Failed, which an operator can retry.
- Advancing a flow, resolving a child outcome or dispatching an outbox request needs the value itself. A host without the key throws instead of planning from ciphertext; nothing commits, and a host with the key completes the work.
- The console shows envelopes when its host lacks the key. That is a valid deployment for operators who must not read business data.

## Consequences

- Every host that runs or advances work needs the full key ring. Key distribution is the application's job.
- Envelopes are about a third larger than the JSON they protect. The 1 MiB input limit applies to the plain JSON.
- Losing a key loses the data it protects. There is no recovery.
- Queries on payload content are not offered, so encryption removes no feature. Database-side inspection of payloads stops working, which is the point.
