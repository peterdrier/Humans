---
name: Every new column is nullable
description: HARD RULE. Every new column is nullable, on new and existing tables — never propose or ask about NOT NULL; only Peter initiates a required column.
---

# Every new column is nullable

**Rule:** Every new column is nullable — on existing tables and new ones alike. Don't propose a
`NOT NULL` column, offer it as an option, or ask whether one is wanted. A required column happens
only when Peter asks for that specific column himself.

**Why:** A required column on an existing table forces EF to scaffold a physical
`DEFAULT <clr-default>` so old rows can be backfilled — and that default is never dropped
and never declared in the model. The database and the model silently disagree from that
moment on. This exact pattern produced 31 stray physical defaults across 16 tables in four
months and walled off the Gate and Store DbContext peels (nobodies-collective/Humans#858
§5.1). Nullable columns add no default and no divergence. Peter, 2026-10-22: "Don't even ask
about not null columns ever … Everything is nullable going forward, unless I specifically ask
for it otherwise."

**How to apply:**

- New column? Make it nullable. Handle "absent" in code (it already has to handle old rows).
- If Peter asks for a required column: declare the default in the model too
  (`HasDefaultValue`/`HasDefaultValueSql` — mind the bool-sentinel rules in
  `.claude/agents/ef-migration-reviewer.md`), so model and database agree from day one.
- The backstop is `PhysicalDefaultParityTests` (integration): CI fails on any column whose
  default presence differs between the model and the chain-built schema.

**Related:** [`no-hand-edited-migrations`](no-hand-edited-migrations.md) ·
`.claude/agents/ef-migration-reviewer.md` § "AddColumn with defaults"
