---
name: Every new column on an existing table is nullable
description: HARD RULE. Every new column on an existing table is nullable — never propose or ask about NOT NULL there; only Peter initiates one. New tables may have required columns.
---

# Every new column on an existing table is nullable

**Rule:** Every new column on an existing table is nullable. Don't propose a `NOT NULL` column
there, offer it as an option, or ask whether one is wanted; a required column happens only when
Peter asks for that specific column himself. Completely new tables can have required columns from
the start.

**Why:** A required column on an existing table forces EF to scaffold a physical
`DEFAULT <clr-default>` so old rows can be backfilled — and that default is never dropped
and never declared in the model. The database and the model silently disagree from that
moment on. This exact pattern produced 31 stray physical defaults across 16 tables in four
months and walled off the Gate and Store DbContext peels (nobodies-collective/Humans#858
§5.1). Nullable columns add no default and no divergence. Peter, 2026-10-22: "Don't even ask
about not null columns ever … Everything is nullable going forward, unless I specifically ask
for it otherwise." He narrowed it the same day: completely new tables may have required columns.

**How to apply:**

- New column? Make it nullable. Handle "absent" in code (it already has to handle old rows).
- If Peter asks for a required column: declare the default in the model too
  (`HasDefaultValue`/`HasDefaultValueSql` — mind the bool-sentinel rules in
  `.claude/agents/ef-migration-reviewer.md`), so model and database agree from day one.
- The backstop is `PhysicalDefaultParityTests` (integration): CI fails on any column whose
  default presence differs between the model and the chain-built schema.

**Related:** [`no-hand-edited-migrations`](no-hand-edited-migrations.md) ·
`.claude/agents/ef-migration-reviewer.md` § "AddColumn with defaults"
