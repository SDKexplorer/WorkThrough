# ADR-016: Immediate jobs use the acceptance database clock

Status: implemented and verified on all four providers through native CI, 2026-10-10. See [verification](../verification.md#oracle-and-immediate-acceptance-qualification-2026-10-10).

An immediate JobClient request previously used the producer's UTC clock for `CreatedAt` and `AvailableAt`. A producer a few milliseconds ahead of the database could commit work that the next claim treated as future work. This was reproduced on the .NET 8/PostgreSQL local gate; explicit scheduling was not involved.

JobClient now marks envelopes without `NotBefore` with the additive `JobRecord.UseStorageTime` property. The destination store assigns both times from its own clock inside the acceptance transaction and clears the flag. The outbox preserves the flag until destination acceptance. A successful immediate enqueue therefore has an eligibility instant no later than its database acceptance, independently of producer clock skew. Existing deduplication receipts still return their original job, without changing timestamps.

Explicit `NotBefore` instants keep their existing rounding-up rule and are never brought forward. Directly constructed records and historical serialized envelopes without the flag default to false and preserve their times. Saved job tables do not need a schema change; the additive property only communicates intent in acceptance/outbox envelopes. Older engine writers must not coexist arbitrarily with an upgraded engine, as already required by the deployment policy.

The conformance suite injects a producer clock one hour ahead and verifies immediate eligibility and stored timestamps on every configured provider. Existing submillisecond scheduling tests continue to verify that explicit boundaries are not weakened. Cross-runtime package, outbox and published relocation gates cover the additive envelope contract. Hosts still need synchronized clocks for explicit deadlines, human observations and authentication.
