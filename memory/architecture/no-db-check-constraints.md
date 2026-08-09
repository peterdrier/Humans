---
name: no CHECK constraints in the database
description: HARD RULE. Never add a CHECK constraint to a table — no `HasCheckConstraint`, no `AddCheckConstraint`, no raw-SQL `ALTER TABLE ... ADD CONSTRAINT ... CHECK`. Enforce the invariant in service code instead. Triggers and intra-section FKs are unaffected.
---

Invariants are enforced in service code, not by database CHECK constraints. Never write
`builder.ToTable(t => t.HasCheckConstraint(...))`, never hand-write `migrationBuilder.AddCheckConstraint`,
and never add one through raw SQL.

**Why:** Peter's call, 2026-08-09. CHECK constraints are disproportionately painful to manage
through EF: they are invisible to most of the tooling that keeps model and schema honest, they
survive in chain-built databases after the model stops declaring them, and their `Down` bodies
re-add constraints referencing columns that no longer exist. A live example — `google_resources`
carried `CK_google_resources_exactly_one_owner` (a `TeamId` XOR `UserId` rule) that
`RemoveUserIdFromGoogleResource` had to drop by hand alongside the column; it survives only in
that migration's `Down`, and it had to be individually audited during the
nobodies-collective/Humans#858 peels to prove the section could still be split. The same
invariant expressed in service code would have moved with the code and cost nothing. Service-level
enforcement also produces an error the user can read, instead of a Postgres constraint violation
surfacing as a 500.

**How to apply:** When you reach for a CHECK constraint, write the guard in the service that owns
the write path and cover it with a test. A singleton table is enforced by the repository addressing
one id; a "these two columns are mutually exclusive" rule is a validation branch before the save; a
temporal window (`ValidTo > ValidFrom`) is a guard clause in the service method that sets the dates.
If an EF migration scaffolds a CHECK constraint because a configuration still declares one, fix the
configuration and regenerate — do not hand-edit the migration
([`no-hand-edited-migrations`](no-hand-edited-migrations.md)).

**Scope — what this rule does NOT cover:**

- **plpgsql triggers stay.** `prevent_audit_log_modification` and
  `prevent_consent_record_modification` are deliberate and remain; immutability of audit and
  consent rows is worth enforcing where nothing can bypass it.
- **Foreign keys within a section stay.** Intra-section FKs are normal EF relationships. Only
  *cross-section* FKs are banned, and by a different rule —
  [`no-cross-section-ef-joins`](no-cross-section-ef-joins.md).
- Unique indexes, `NOT NULL`, and column types are not CHECK constraints and are unaffected.

**Related:** [`no-cross-section-ef-joins`](no-cross-section-ef-joins.md) ·
[`no-hand-edited-migrations`](no-hand-edited-migrations.md) ·
[`required-columns-need-approval`](required-columns-need-approval.md)
