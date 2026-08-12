---
name: Diff the snapshot after running an EF tool
description: After running any EF tool (`migrations add`, `migrations remove`, `database update`, `dbcontext optimize`) with `--context <C>`, always `git diff` the touched context's `*DbContextModelSnapshot.cs` before committing — empty migration body does NOT mean clean snapshot.
---

# Always `git diff` the snapshot after running an EF tool

`dotnet ef migrations add --context <C>` rewrites that context's snapshot, named after the context class — `src/Humans.Infrastructure/Migrations/Users/UsersDbContextModelSnapshot.cs` for `UsersDbContext`, `src/Sections/Humans.Holded/Data/Migrations/HoldedDbContextModelSnapshot.cs` for a G5-moved section (see [[ef-multi-context-commands]]) — to reflect EF's view of the current model. The migration **body** (`Up`/`Down` methods) shows the schema diff between the prior snapshot and the new one. The **snapshot file** is a full rewrite of the model state.

These can diverge. EF can produce an empty migration body (no `Up`/`Down` content) while still rewriting the snapshot file substantially. Causes include:
- Transient model-building soft failures (e.g., a custom `ValueConverter` / `ValueComparer` throws or warns during one specific invocation and the affected entity gets silently skipped from the produced model).
- EF Core API drift between runs — internal calls like `b.ToTable("X")` vs `b.ToTable("X", (string)null)` change with framework version or default-schema config.
- Snapshot writer quirks where unrelated entities reformat or reorder.

**The trap:** an "empty migration body" looks like "nothing changed" to the human running the tool. Then `dotnet ef migrations remove` cleans up the migration files — but does NOT roll back the snapshot rewrite. The corrupted snapshot is left staged for commit, and if you only `git status` (file count, not content), it slips through.

## How to apply

After every EF tool run, before staging anything:

```
# context still hosted in Infrastructure
git diff src/Humans.Infrastructure/Migrations/<Area>/<Context>ModelSnapshot.cs
# G5-moved section
git diff src/Sections/Humans.<Section>/Data/Migrations/<Context>ModelSnapshot.cs
```

If the diff touches entities you did NOT change in your code, **STOP** — your snapshot is corrupt. Restore from `origin/main` (or branch base) and figure out why EF's view of the model differs. Never commit unexplained snapshot diff. A diff on a *different* context's snapshot than the one you ran `--context` against is the same signal — something ran without it.

## Stronger principle: don't run EF tooling for code-only changes

Removing a nav property while keeping the FK column and `OnDelete` config is a pure C# refactor — the database schema cannot change. The same applies to:
- Renaming a nav property
- Changing nullability of a nav (the FK column is what matters)
- Reordering properties on an entity class

Running `migrations add` "to verify schema-neutrality" is the wrong tool. Read the change and reason about it directly: did the FK column change? Did `HasColumnName`/`HasColumnType`/`IsRequired`/index/constraint config change? If no, there is no schema change, end of story.

If you do run an EF tool for a real schema change, the snapshot diff IS expected and SHOULD be reviewed — but its scope should match the code change. A 50-line snapshot diff for a single-entity nav drop is the loud signal that something went wrong.

Origin: section-align AuditLog run, Phase 2A — commit `1c49fc078` corrupted the snapshot by running `dotnet ef migrations add` for a pure nav-property removal. The "empty migration body, removed" claim hid a 52-line snapshot rewrite that nuked `VolunteerBuildStatus` entity metadata and an unrelated `DataProtectionKeys` table call.

See also [[no-hand-edited-migrations]] for the broader migration discipline, and [[ef-multi-context-commands]] for the `--context`/`--project` forms per context.
