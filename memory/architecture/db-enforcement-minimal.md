---
name: DB enforcement is minimal — service is the contract
description: Don't argue from "DB constraints enforce X." At Humans the only doctrinal DB-level enforcement is the immutability triggers on `audit_log` and `consent_records`. Everything else is service logic, CHECK constraints are banned outright, and NOT NULL is not an enforcement tool — nullable is fine.
---

Don't pitch designs by saying "DB-enforced uniqueness eliminates the race window" or "the unique index gives us free safety." That framing inverts the project's actual model.

**Why:** At Humans, the database is a storage layer, not a contract layer. Unique indexes happen incidentally via EF configuration, but they aren't load-bearing in the design — the service is the source of truth. The only doctrinal DB enforcement is the triggers that prevent UPDATE/DELETE on `audit_log` and `consent_records`. At our small single-server scale, race windows that DB constraints "fix" don't actually occur in practice, and reaching for them as justification suggests a fix for a non-problem.

**How to apply:**

- When weighing storage shape, judge it on data-model clarity (what's the entity, what's the relationship), not on which DB constraints fire.
- If a service-layer pre-check is sufficient at this scale, the DB index isn't a design argument — it's an implementation detail.
- Don't confuse display concerns (slot 1, slot 2, slot 3) with storage concerns (a row per assignment, ordering done at render time).
- The exceptions are the immutability triggers on `consent_records` and `audit_log` — those ARE doctrinally enforced at the DB level (and listed in [design-rules.md §12](../../docs/architecture/design-rules.md#12-immutable-entity-rules)).
- CHECK constraints are not merely non-doctrinal, they are banned outright — see [`no-db-check-constraints`](no-db-check-constraints.md).
- NOT NULL is not an enforcement tool either. Required-ness lives in service code; a nullable
  column with a null default is perfectly fine storage (Peter, 2026-08-18, peterdrier/Humans#1379).
  An unneeded NOT NULL turns every later migration touching the column — defaults, drops,
  rollbacks — into ceremony: #1379's `RawPayload jsonb NOT NULL` forced an invalid `DEFAULT ''`
  into the drop's `Down()` and burned a review cycle on a rollback nobody will ever run.

**Related:** [`no-db-check-constraints`](no-db-check-constraints.md), [`audit-log-as-concurrency-safety-net`](audit-log-as-concurrency-safety-net.md), [design-rules.md §12](../../docs/architecture/design-rules.md#12-immutable-entity-rules).
