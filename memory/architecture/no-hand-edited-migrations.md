---
name: EF migrations are generated, never hand-written
description: HARD RULE. Any schema change, `dotnet ef` command, or migration/snapshot file: EF generates every line and it is committed verbatim. This is the one migration rule.
---

**Only `dotnet ef migrations add` writes a migration.** Every migration `.cs`, `.Designer.cs` and `*DbContextModelSnapshot.cs` is committed exactly as EF generated it. Never write, edit, reorder, rename, comment or delete one by hand, and never restore a snapshot with `git checkout`. This covers `migrationBuilder.Sql(...)`, inserted `AddColumn`/`DropColumn`, changed timestamps and any snapshot edit "so the next regen comes out right". No exceptions, no "just this once".

**Why:** a hand-touched migration or snapshot applies cleanly and passes tests, then breaks `migrations remove` and every later `migrations add` built on it. Peter, 2026-05-04: "you are never fucking allowed to hand write a migration again."

**If EF generated the wrong thing** (a `RenameColumn` where you meant drop + add, a CHECK constraint, anything): fix the model or configuration, then regenerate. While your migration is still the newest in its context:

```bash
dotnet ef migrations remove --context <C> --project <P> --startup-project src/Humans.Web
# fix the model, then rebuild
dotnet build src/Humans.Web -v quiet
dotnet ef migrations add <Name> --context <C> --output-dir <D> --project <P> --startup-project src/Humans.Web
```

**Every `dotnet ef` command names its context.** One DbContext per section plus the platform context; there is no shared one.

| Context | `--project` | `--output-dir` |
|---|---|---|
| A section's (`StoreDbContext`, …) | `src/Sections/Humans.<Section>` | `Data/Migrations` |
| Platform (`SystemDbContext`) | `src/Humans.Web` | `Migrations/System` |

The context class name is the snapshot's file name minus `ModelSnapshot` (it is not always the section name: Consent is `LegalDbContext`). A new context's design-time factory must be `Data/<Context>Factory.cs`, or CI never checks it. Verify before committing: `dotnet ef migrations has-pending-model-changes --context <C> --project <P> --startup-project src/Humans.Web`.

**After every EF run, `git diff` the snapshot.** It must change only what your model change explains, and only in the context you named. An empty migration body does not mean a clean snapshot, and `migrations remove` does not undo a snapshot rewrite. Unexplained diff: stop, do not commit it. Don't run EF at all for a code-only change (renaming or removing a nav property while the column and its config stay).

**Once main's migrations sit after yours (you merged main mid-PR), `migrations remove` is unsafe.** It walks back through main's migrations and corrupts the snapshot. Run [`/ef-regen`](../../.claude/skills/ef-regen/SKILL.md), which rebuilds the branch's migrations as one consolidated migration. Never improvise around it: no snapshot edits, no timestamp renames, no removing main's migrations, no `git reset --hard`. Cheaper: regenerate before merging main, while yours is still last.

**A migration merged to `main` is frozen.** QA has applied it. Never edit, delete, rename or regenerate it; a wrong one is fixed by a new generated migration on top. (A whole deleted section is the one case: [`removed-section-migrations`](removed-section-migrations.md).)

**Before committing a migration,** run the [`ef-migration-reviewer`](../../.claude/agents/ef-migration-reviewer.md) agent and resolve every CRITICAL finding by regenerating.

**Migrations are schema only.** Data changes never go in one: [`no-data-backfills`](../process/no-data-backfills.md). Bug-fix, refactor and debt work may include a section-owned migration when the fix needs it; a large ownership transition is its own planned PR.

**If the tools cannot produce the migration you need, stop and ask Peter.** Hand-touched migrations already in history (the #858 peel removals, trigger DDL) were Peter's own exceptions and are not precedent.

**Related:** [`required-columns-need-approval`](required-columns-need-approval.md) · [`no-drops-until-prod-verified`](no-drops-until-prod-verified.md) · [`no-manual-db-writes`](../process/no-manual-db-writes.md) · [`event-deploy-freeze`](../process/event-deploy-freeze.md)
