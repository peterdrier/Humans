---
name: no CHECK constraints in the database
description: HARD RULE. Never put a CHECK constraint into the forward model — no `HasCheckConstraint` in an entity configuration, no hand-written `AddCheckConstraint`, no raw-SQL `ALTER TABLE ... ADD CONSTRAINT ... CHECK`. Enforce the invariant in service code instead. An EF-generated `AddCheckConstraint` in a migration's `Down`, triggers, and intra-section FKs are unaffected.
---

Invariants are enforced in service code, not by database CHECK constraints. Never write
`builder.ToTable(t => t.HasCheckConstraint(...))`, never hand-write `migrationBuilder.AddCheckConstraint`
into a migration's `Up`, and never add one through raw SQL.

**Why:** Peter's call, 2026-08-09. A CHECK constraint puts an invariant where the service that
owns the write path can't see it, and enforces it as a Postgres error — the user gets a 500
naming a constraint instead of a message, and the rule stays invisible to the service tests,
which run on EF-InMemory where no constraint fires. It also pins a business rule to the storage
layer:
`google_resources` carried `CK_google_resources_exactly_one_owner`, a `TeamId` XOR `UserId` rule
written across two sections' owner columns — exactly the DB-level cross-section coupling
[`no-cross-section-ef-joins`](no-cross-section-ef-joins.md) bans in its own form. The same
invariant in service code sits next to the code it governs and moves with it.

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
- **An EF-generated `AddCheckConstraint` in a `Down` body is fine — commit it verbatim.** Removing
  a constraint from the model makes EF scaffold the restore into `Down`; that is the rollback of
  a removal, not a new constraint, and stripping it would be a hand-edit
  ([`no-hand-edited-migrations`](no-hand-edited-migrations.md)).
  `20260216162630_RemoveUserIdFromGoogleResource.cs` is the shape.
- **The two constraints already in the model are debt, not precedent.**
  `ck_agent_settings_singleton` (`AgentSettingsConfiguration.cs`) and
  `CK_role_assignments_valid_window` (`RoleAssignmentConfiguration.cs`) predate this rule; the
  comment in `AgentSettingsConfiguration` explaining how to declare one properly documents the
  old pattern — don't copy it. Removing them is a schema change for its own PR.

**Related:** [`no-cross-section-ef-joins`](no-cross-section-ef-joins.md) ·
[`no-hand-edited-migrations`](no-hand-edited-migrations.md) ·
[`required-columns-need-approval`](required-columns-need-approval.md)
