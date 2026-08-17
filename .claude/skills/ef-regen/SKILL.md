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

## Determine the touched context(s)

Since the per-context DbContext split (nobodies-collective/Humans#858, #866) there is no shared context — `HumansDbContext` was deleted. Migrations live under one of two shapes — see `memory/process/ef-multi-context-commands.md` for the full partition:

| Location | `--project` | `--output-dir` |
|---|---|---|
| `src/Humans.Web/Migrations/<Area>/*.cs` (the platform context) | `src/Humans.Web` | `Migrations/<Area>` |
| `src/Sections/Humans.<Section>/Data/Migrations/*.cs` (moved, G5) | `src/Sections/Humans.<Section>` | `Data/Migrations` |

**Do not synthesize the context name from the folder.** A G5 section's project name and its context often differ: `src/Sections/Humans.Consent` is `LegalDbContext`, `src/Sections/Humans.Events` is `EventGuideDbContext`. Resolve the real class from either:

- the snapshot file already in the folder — it is named `<Context>ModelSnapshot.cs`; or
- the `SECTION_DB_CONTEXTS` map in `.github/workflows/build.yml`, whose entries are `<Context>:<project path>`.

Everything below refers to "the touched context" and "the migrations folder" — resolve them from the branch's in-flight migration files (step 1) before proceeding. If the branch touches more than one context, run steps 2–9 once per context; each context gets its own consolidated migration and its own commit.

## Hard preconditions

Confirm BEFORE deleting anything:

1. **Entity classes and EF configurations are in their final desired shape.** The regenerated migration captures whatever the model says NOW. If the entity still says `CampSeasonId` but you intended `CampId`, the regen bakes in the wrong column. Make all model changes first; regen second.
2. **`dotnet build Humans.slnx -v quiet` is green** with the model in its final shape. If the model doesn't compile, EF can't load it to compute the diff.
3. **Working tree clean except for the model/configuration changes.** No stray edits to migration files, no half-applied refactors. Stash or commit unrelated work first.
4. **Data loss is acceptable for any column being dropped.** This skill produces schema-only migrations. If old data needs to move into the new shape, that's a separate admin-button backfill (see `memory/process/no-data-backfills.md`) — design and ship it BEFORE this migration runs in any environment that has data.

If any of those is unmet, stop and ask the user.

## Steps

### 1. List the in-flight migrations

Migrations added on this branch since divergence from `origin/main`, across all migration folders:

```bash
git log --diff-filter=A --name-only origin/main..HEAD \
    -- 'src/Humans.Web/Migrations/*.cs' 'src/Sections/Humans.*/Data/Migrations/*.cs' \
  | grep -E '/(Migrations|Data/Migrations)(/[A-Za-z]+)?/[0-9]{14}_.*\.cs$' \
  | sort -u
```

This includes both `<timestamp>_<Name>.cs` and `<timestamp>_<Name>.Designer.cs` for each migration. Group the results by owning context per the table above. Show the list (grouped by context) to the user and confirm "yes, scrap all of these and consolidate" before proceeding — per context if more than one is touched.

**A relocated migration is not in-flight work.** If this branch moves a context between projects, every one of that context's migrations shows up above as an addition at the new path even though it is unchanged history from `origin/main`. Compare basenames against `origin/main` before treating any of them as scrappable:

```bash
git ls-tree -r --name-only origin/main \
  | grep -E '/[0-9]{14}_.*\.cs$' | xargs -n1 basename | sort -u
```

Anything whose basename is already on `origin/main` was relocated, not authored here — leave it alone.

### 2. Resolve the snapshot restore source — BEFORE deleting anything

Step 4 restores the context's snapshot from `origin/main`. That restore has no source in two cases, and step 4 is too late to find out: the migration files are already deleted by then, which strands the branch. Determine now which case you are in:

```bash
git cat-file -e origin/main:<migrations-folder>/<Context>ModelSnapshot.cs 2>/dev/null \
  && echo PRESENT || echo ABSENT
```

- **PRESENT** — ordinary case. Step 4 runs as written.
- **ABSENT, and the context exists on `origin/main` under a different path** — the branch is relocating the context. The pre-move snapshot is still at its old path on `origin/main`; that path is the restore source. Find it with:

  ```bash
  git ls-tree -r --name-only origin/main | grep '<Context>ModelSnapshot\.cs$'
  ```

- **ABSENT everywhere** — this branch introduces the context, so `origin/main` has no cumulative model state for it and there is nothing to restore. The correct baseline is *no snapshot*: delete the branch's snapshot file along with the migration files in step 3, and `migrations add` regenerates it from empty, producing a proper initial migration.

### 3. Delete the migration files

Delete the `.cs` and `.Designer.cs` for every migration in the list, from the touched context's migrations folder:

```bash
git rm <migrations-folder>/<timestamp>_<Name>.cs \
       <migrations-folder>/<timestamp>_<Name>.Designer.cs
```

Repeat for each in-flight migration in this context. Do NOT delete migrations from `origin/main`.

### 4. Reset the cumulative snapshot to main's view

Per the case established in step 2.

**PRESENT** — restore in place:

```bash
git checkout origin/main -- <migrations-folder>/<Context>ModelSnapshot.cs
```

**Relocating the context** — restore from the pre-move path, into the new one:

```bash
git show origin/main:<pre-move-migrations-folder>/<Context>ModelSnapshot.cs \
  > <migrations-folder>/<Context>ModelSnapshot.cs
```

**A verbatim copy does not compile — you must rewrite two lines before step 5.** The pre-move snapshot is written for its old project and carries a `using` for the context's old namespace plus an unqualified `typeof`:

```csharp
using Humans.Infrastructure.Data;                    // old context namespace
namespace Humans.Infrastructure.Migrations.<Area>    // old migrations namespace
    [DbContext(typeof(<Context>))]                   // resolved via that using
```

In the relocated project the context class has moved with it, so that `using` no longer names anything and `typeof(<Context>)` fails to resolve — and because section contexts are declared `internal sealed`, the type is not visible from outside its assembly anyway. Rewrite both to the new project, matching what EF itself emits for an already-moved section:

```csharp
using Humans.<Section>.Data;                         // new context namespace
namespace Humans.<Section>.Data.Migrations           // new migrations namespace
```

Cross-check against any section already moved — e.g. `src/Sections/Humans.Events/Data/Migrations/EventGuideDbContextModelSnapshot.cs` pairs `using Humans.Events.Data;` with `namespace Humans.Events.Data.Migrations`. `migrations add` rewrites the file's body in step 6, but it will not fix these two lines for you, and step 5 fails first if they are wrong.

**New context on this branch** — there is no `origin/main` state to restore. Delete the snapshot so EF starts from empty:

```bash
git rm <migrations-folder>/<Context>ModelSnapshot.cs
```

`migrations add` then regenerates it, and the consolidated migration is a full initial `CreateTable` set rather than a diff.

In the first two cases this puts the snapshot in a known-clean state matching what `origin/main` believes the model looks like — i.e. as if your branch's migrations had never existed. EF will then compute "model has all the new tables/columns; snapshot doesn't" and generate one fresh migration containing everything.

This reset is the one sanctioned exception to the "never touch the snapshot" rule in `memory/architecture/no-hand-edited-migrations.md`. It is sanctioned ONLY inside this workflow; never use it as a general escape hatch in other contexts.

### 5. Build to confirm the model loads

```bash
dotnet build Humans.slnx -v quiet
```

If this fails, stop — EF tooling can't run against a model that doesn't compile.

### 6. Generate the consolidated migration

`--output-dir` is **required**, not optional. Without it EF writes to the project's default `Migrations/` folder, which for both shapes is the wrong place: the replacement lands outside the chain its snapshot belongs to. `.claude/check-ef-output-dir.sh` cannot save you here — it only inspects commands that already carry the flag and exits 0 on any command without it.

The platform context, hosted in `Humans.Web`:

```bash
dotnet ef migrations add <MigrationName> \
  --context <Context> \
  --project src/Humans.Web \
  --output-dir Migrations/<Area> \
  --startup-project src/Humans.Web
```

Section moved to its own project (G5):

```bash
dotnet ef migrations add <MigrationName> \
  --context <Context> \
  --project src/Sections/Humans.<Section> \
  --output-dir Data/Migrations \
  --startup-project src/Humans.Web
```

`<Area>` / `<Section>` and `<Context>` per the table above — `<Context>` resolved from the snapshot filename or `SECTION_DB_CONTEXTS`, never guessed from the folder name. Use the `<MigrationName>` from `$ARGUMENTS`. The new migration gets the current UTC timestamp, which lands at the END of the chain — past all of main's interleaved migrations. See `memory/process/ef-multi-context-commands.md` for the canonical flag forms.

### 7. Inspect the generated migration

Read the new `<timestamp>_<MigrationName>.cs`. Verify:

- Only schema operations: `CreateTable`, `AddColumn`, `DropColumn`, `CreateIndex`, `AddForeignKey`, etc. NO `migrationBuilder.Sql(...)`.
- Operations match the net intended change. If you expected `CreateTable("widgets")` plus `DropColumn(camp_seasons.OldField)` and got something else, the model isn't in the shape you thought — go back to step 1's preconditions.
- No surprises (touching tables you didn't expect to change is a sign of model drift; investigate before committing).

### 8. Build and test

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
```

Both green.

### 9. Commit the regen as ONE standalone commit

The entire regen — deletions of the old migration files, the step 4 snapshot reset (restore from `origin/main`, restore from the pre-move path, or delete for a new context), the new consolidated migration `.cs` and `.Designer.cs`, and the regenerated `<Context>ModelSnapshot.cs` — must land as **one single commit**, separate from any other work. If more than one context was touched, one commit per context.

This matters for history: a reviewer or future archaeologist scrolling through `git log` should be able to point at one commit and say "that's where the migration stack was consolidated." If the regen is bundled with unrelated changes (entity refactors, controller edits, test fixes), the audit trail becomes muddy and it stops being obvious which file changes are the regen itself versus the surrounding work.

Workflow: stash or commit any unrelated in-flight edits BEFORE step 3; do the entire regen sequence in a clean working tree; commit the regen alone; then resume other work as separate commits.

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

- `memory/process/ef-multi-context-commands.md` — exact `--context`/`--project`/`--output-dir` forms per context shape; this skill's step 5 defers to it.
- `memory/architecture/no-hand-edited-migrations.md` — the broader "never hand-edit migrations or snapshots" rule. The snapshot restore in step 3 is the one carve-out, sanctioned only inside this skill.
- `memory/architecture/migration-regen-after-rebase.md` — describes the mid-chain failure mode. This skill is the canonical recovery action.
- `memory/process/no-data-backfills.md` — why this skill produces schema-only migrations and where data movement belongs instead.
