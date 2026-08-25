# Adding a required column to an existing table needs Peter's approval

**Rule:** Never add a `NOT NULL` (required) column to an existing table without Peter's
per-instance approval. Default to a **nullable** column instead; treat "make it required"
as a separate, explicitly-approved step if it is ever actually needed.

**Why:** A required column on an existing table forces EF to scaffold a physical
`DEFAULT <clr-default>` so old rows can be backfilled — and that default is never dropped
and never declared in the model. The database and the model silently disagree from that
moment on. This exact pattern produced 31 stray physical defaults across 16 tables in four
months and walled off the Gate and Store DbContext peels (nobodies-collective/Humans#858
§5.1). Nullable columns add no default and no divergence.

**How to apply:**

- New column? Make it nullable. Handle "absent" in code (it already has to handle old rows).
- If Peter approves a required column: declare the default in the model too
  (`HasDefaultValue`/`HasDefaultValueSql` — mind the bool-sentinel rules in
  `.claude/agents/ef-migration-reviewer.md`), so model and database agree from day one.
- The backstop is `PhysicalDefaultParityTests` (integration): CI fails on any column whose
  default presence differs between the model and the chain-built schema.

**Related:** [`no-hand-edited-migrations`](no-hand-edited-migrations.md) ·
`.claude/agents/ef-migration-reviewer.md` § "AddColumn with defaults"
