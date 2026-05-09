---
name: ef-regen
description: "Scrap and regenerate the in-flight EF migrations on the current branch as one consolidated migration. Use when migrations have accumulated mid-development cruft (added/removed columns, stacked add-column-to-existing-table fixes, hand-edited SQL that needs to come out, schema changes that should have been one migration but ended up as five), or when the branch has merged main and its migrations are now stuck mid-chain so `dotnet ef migrations remove` is unsafe. Triggers on phrases like 'regen the migrations', 'redo these migrations', 'scrap and regen migrations', 'consolidate the in-flight migrations', 'redo the migration stack', or any time an agent hits the mid-chain situation in `memory/architecture/migration-regen-after-rebase.md`."
argument-hint: "<MigrationName>  (e.g. AddContainers — name of the consolidated migration)"
---

# Regenerate EF Migrations

Deterministic recovery: throw away the in-flight migrations on this branch and let `dotnet ef migrations add` produce one clean consolidated migration against `origin/main`'s snapshot.

`$ARGUMENTS`: the name of the consolidated migration to generate (e.g. `AddContainers`). Should describe the net effect, not the development history.

## When this skill applies

- Multiple migrations on the branch make incremental changes that, taken together, are one logical schema change (created table → added columns → renamed → dropped columns → re-added).
- A migration on the branch contains hand-edited SQL (`migrationBuilder.Sql(...)`) that needs to come out — see `memory/architecture/no-hand-edited-migrations.md`.
- Branch's migrations are now mid-chain because main raced ahead with later-timestamped migrations during the PR's life — see `memory/architecture/migration-regen-after-rebase.md`. `dotnet ef migrations remove` is unsafe in this state; this skill is the canonical alternative.

## When this skill does NOT apply

- You want to keep the in-flight migrations as discrete steps (this skill consolidates everything into one).
- The migration you want to redo is already in production. Production migrations are frozen — write a new corrective migration on top, do not regen.
- Only one migration is in flight and it's still end-of-chain. In that case use `dotnet ef migrations remove` directly — that's the canonical EF tool for the simple case.

## Hard preconditions

Confirm BEFORE deleting anything:

1. **Entity classes and EF configurations are in their final desired shape.** The regenerated migration captures whatever the model says NOW. If the entity still says `CampSeasonId` but you intended `CampId`, the regen bakes in the wrong column. Make all model changes first; regen second.
2. **`dotnet build Humans.slnx -v quiet` is green** with the model in its final shape. If the model doesn't compile, EF can't load it to compute the diff.
3. **Working tree clean except for the model/configuration changes.** No stray edits to migration files, no half-applied refactors. Stash or commit unrelated work first.
4. **Data loss is acceptable for any column being dropped.** This skill produces schema-only migrations. If old data needs to move into the new shape, that's a separate admin-button backfill (see `memory/process/no-data-backfills.md`) — design and ship it BEFORE this migration runs in any environment that has data.

If any of those is unmet, stop and ask the user.

## Steps

### 1. List the in-flight migrations

Migrations added on this branch since divergence from `origin/main`:

```bash
git log --diff-filter=A --name-only origin/main..HEAD -- 'src/Humans.Infrastructure/Migrations/*.cs' \
  | grep -E '^src/Humans.Infrastructure/Migrations/[0-9]{14}_.*\.cs$' \
  | sort -u
```

This includes both `<timestamp>_<Name>.cs` and `<timestamp>_<Name>.Designer.cs` for each migration. Show the list to the user and confirm "yes, scrap all of these and consolidate" before proceeding.

### 2. Delete the migration files

Delete the `.cs` and `.Designer.cs` for every migration in the list:

```bash
git rm src/Humans.Infrastructure/Migrations/<timestamp>_<Name>.cs \
       src/Humans.Infrastructure/Migrations/<timestamp>_<Name>.Designer.cs
```

Repeat for each in-flight migration. Do NOT delete migrations from `origin/main`.

### 3. Restore the cumulative snapshot from main

```bash
git checkout origin/main -- src/Humans.Infrastructure/Migrations/HumansDbContextModelSnapshot.cs
```

This puts the snapshot in a known-clean state matching what `origin/main` believes the model looks like — i.e. as if your branch's migrations had never existed. EF will then compute "model has all the new tables/columns; snapshot doesn't" and generate one fresh migration containing everything.

This restore is the one sanctioned exception to the "never touch the snapshot" rule in `memory/architecture/no-hand-edited-migrations.md`. It is sanctioned ONLY inside this workflow; never use it as a general escape hatch in other contexts.

### 4. Build to confirm the model loads

```bash
dotnet build Humans.slnx -v quiet
```

If this fails, stop — EF tooling can't run against a model that doesn't compile.

### 5. Generate the consolidated migration

```bash
dotnet ef migrations add <MigrationName> \
  --project src/Humans.Infrastructure \
  --startup-project src/Humans.Web
```

Use the `<MigrationName>` from `$ARGUMENTS`. The new migration gets the current UTC timestamp, which lands at the END of the chain — past all of main's interleaved migrations.

### 6. Inspect the generated migration

Read the new `<timestamp>_<MigrationName>.cs`. Verify:

- Only schema operations: `CreateTable`, `AddColumn`, `DropColumn`, `CreateIndex`, `AddForeignKey`, etc. NO `migrationBuilder.Sql(...)`.
- Operations match the net intended change. If you expected `CreateTable("widgets")` plus `DropColumn(camp_seasons.OldField)` and got something else, the model isn't in the shape you thought — go back to step 1's preconditions.
- No surprises (touching tables you didn't expect to change is a sign of model drift; investigate before committing).

### 7. Build and test

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
```

Both green.

### 8. Commit the regen as ONE standalone commit

The entire regen — deletions of the old migration files, snapshot restore from `origin/main`, the new consolidated migration `.cs` and `.Designer.cs`, and the updated `HumansDbContextModelSnapshot.cs` — must land as **one single commit**, separate from any other work.

This matters for history: a reviewer or future archaeologist scrolling through `git log` should be able to point at one commit and say "that's where the migration stack was consolidated." If the regen is bundled with unrelated changes (entity refactors, controller edits, test fixes), the audit trail becomes muddy and it stops being obvious which file changes are the regen itself versus the surrounding work.

Workflow: stash or commit any unrelated in-flight edits BEFORE step 2; do the entire regen sequence in a clean working tree; commit the regen alone; then resume other work as separate commits.

The commit message should:
- Summarize what the consolidated migration does (table created, columns added/dropped).
- List the migrations being replaced, by name.
- Note that the snapshot was restored from `origin/main` as the authorized first step of an `ef-regen` consolidation, so future archaeology shows the restore was deliberate, not an accidental `git checkout`.

Example:

```
migrations: consolidate Containers in-flight stack into single AddContainers

Replaces 5 incremental migrations (AddContainers + RemoveContainerSortOrder
+ AddContainerPlacementPhase + AddContainerPlacement + AddContainerPlacementNotes)
with one regenerated migration. Snapshot was restored from origin/main as
the authorized first step of /ef-regen — see .claude/skills/ef-regen/SKILL.md.

Net schema change:
- containers table created with final shape (incl. placement + placement-notes)
- city_planning_settings: 3 placement-phase columns added
- camp_seasons: ContainerCount + ContainerNotes dropped (data loss accepted —
  not in production)
```

Push to the branch as normal.

## Why this works

EF Core's diff engine computes "what migration to generate" by comparing the live model (entities + configurations) against the cumulative snapshot. By deleting the in-flight migrations and resetting the snapshot to `origin/main`'s view, we're telling EF "pretend the branch's migrations never happened — what would you generate now to get from main's schema to the current model?" The answer is one consolidated migration that captures the net effect.

This bypasses the broken state of `dotnet ef migrations remove` after main's migrations interleave with the branch's, which is the failure mode `migration-regen-after-rebase.md` describes.

## Cross-references

- `memory/architecture/no-hand-edited-migrations.md` — the broader "never hand-edit migrations or snapshots" rule. The snapshot restore in step 3 is the one carve-out, sanctioned only inside this skill.
- `memory/architecture/migration-regen-after-rebase.md` — describes the mid-chain failure mode. This skill is the canonical recovery action.
- `memory/process/no-data-backfills.md` — why this skill produces schema-only migrations and where data movement belongs instead.
