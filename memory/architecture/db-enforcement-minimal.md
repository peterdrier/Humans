---
name: The service is the contract, not the database
description: HARD RULE. Designing storage or reaching for a DB constraint: invariants live in service code. No CHECK constraints ever; NOT NULL and unique indexes are not enforcement.
---

The database is storage. The owning service enforces every invariant. Don't justify a design by what a constraint or index would catch; at this scale the races they "fix" don't occur.

- **CHECK constraints are banned** (Peter, 2026-08-09): no `HasCheckConstraint`, no `AddCheckConstraint`, no raw SQL. Write the guard in the service that owns the write and test it there. A CHECK surfaces as a Postgres 500 and is invisible to EF-InMemory tests.
- **NOT NULL is not enforcement.** Required-ness lives in service code; see [`required-columns-need-approval`](required-columns-need-approval.md).
- **Unique indexes are not load-bearing;** see [`unique-constraints-ids-only`](unique-constraints-ids-only.md).
- **The only doctrinal DB enforcement** is the immutability triggers on `audit_log` and `consent_records`. They stay.
- Foreign keys inside one section are ordinary EF relationships. Across sections they are banned by [`no-cross-section-ef-joins`](no-cross-section-ef-joins.md).

**Details:**

- If EF scaffolds a CHECK because a configuration still declares one, fix the configuration and regenerate ([`no-hand-edited-migrations`](no-hand-edited-migrations.md)). An EF-generated `AddCheckConstraint` in a `Down` body is the rollback of a removal: commit it verbatim.
- `ck_agent_settings_singleton` and `CK_role_assignments_valid_window` predate the rule. They are debt, not precedent.
- Judge a storage shape on data-model clarity (what is the entity, what is the relationship), and keep display concerns (slot 1, 2, 3) out of it.

**Related:** [`no-concurrency-tokens`](no-concurrency-tokens.md)
