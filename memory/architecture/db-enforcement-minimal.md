---
name: DB enforcement is minimal — service is the contract
description: Don't argue from "DB constraints enforce X." At Humans the only doctrinal DB-level enforcement is the audit-log immutability trigger. Everything else is service logic.
---

Don't pitch designs by saying "DB-enforced uniqueness eliminates the race window" or "the unique index gives us free safety." That framing inverts the project's actual model.

**Why:** At Humans, the database is a storage layer, not a contract layer. Unique indexes happen incidentally via EF configuration, but they aren't load-bearing in the design — the service is the source of truth. The only doctrinal DB enforcement is the trigger that prevents UPDATE/DELETE on `consent_records`. At single-server, ~500-user scale, race windows that DB constraints "fix" don't actually occur in practice, and reaching for them as justification suggests a fix for a non-problem.

**How to apply:**

- When weighing storage shape, judge it on data-model clarity (what's the entity, what's the relationship), not on which DB constraints fire.
- If a service-layer pre-check is sufficient at this scale, the DB index isn't a design argument — it's an implementation detail.
- Don't confuse display concerns (slot 1, slot 2, slot 3) with storage concerns (a row per assignment, ordering done at render time).
- The one exception is `consent_records` immutability — that IS doctrinally enforced at the DB level (and listed in [design-rules.md §12](../../docs/architecture/design-rules.md#12-immutable-entity-rules)).

**Related:** [`audit-log-as-concurrency-safety-net`](audit-log-as-concurrency-safety-net.md), [design-rules.md §12](../../docs/architecture/design-rules.md#12-immutable-entity-rules).
