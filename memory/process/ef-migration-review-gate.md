---
name: Run the EF migration reviewer agent before committing any migration
description: MANDATORY for all DB schema changes. Run `.claude/agents/ef-migration-reviewer.md` before commit/PR. Don't proceed if it reports CRITICAL issues.
---

**Before committing any EF Core migration**, run the EF migration reviewer agent at `.claude/agents/ef-migration-reviewer.md`. Mandatory for all database changes — do not commit or create PRs until it passes with no CRITICAL issues.

**Why:** EF Core migrations have surprising failure modes (snapshot/Designer drift, ordering bugs, accidental column drops, missing nullable defaults) that don't surface in local builds but break production. The reviewer agent catches them before merge.

**How to apply:**

1. After running `dotnet ef migrations add <Name>` and reviewing the generated file, dispatch the EF migration reviewer agent.
2. If it reports CRITICAL issues, fix them by regenerating the migration (per [`no-hand-edited-migrations`](../architecture/no-hand-edited-migrations.md)) and re-run the reviewer.
3. Commit/PR only when the reviewer is clean.

**Related:** [`no-hand-edited-migrations`](../architecture/no-hand-edited-migrations.md), [`no-drops-until-prod-verified`](../architecture/no-drops-until-prod-verified.md).
