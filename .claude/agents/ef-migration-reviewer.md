# EF Core Migration Reviewer

Review EF Core migrations and entity configurations for correctness before they reach production. This agent exists because EF migrations have caused repeated failures — empty UPDATE statements, wrong namespaces, sentinel traps, hand-edited files.

## When to Use

Run this agent after generating any EF Core migration, before committing or pushing.

## 0. Identify the owning context (do this first)

Since the per-section DbContext split (nobodies-collective/Humans#858, #866) there is no shared context — `HumansDbContext` was deleted, and 29 per-section contexts are in play. Every check below is relative to whichever context the migration under review belongs to. Its path/namespace tells you where the migration lives:

| Location | Hosting project |
|---|---|
| `src/Humans.Web/Migrations/<Area>/*.cs`, namespace `Humans.Infrastructure.Migrations.<Area>` | `src/Humans.Web` (the platform context; namespace kept when G5 lane 5b-6 deleted `src/Humans.Infrastructure`) |
| `src/Sections/Humans.<Section>/Data/Migrations/*.cs`, namespace `Humans.<Section>.Data.Migrations` | `src/Sections/Humans.<Section>` (moved to its own project, G5) |

**The path does not give you the context name — look it up, never synthesize it.** `<Section>DbContext` is a guess that is wrong for real sections: `Humans.Consent.Data.Migrations` is owned by `LegalDbContext`, and `Humans.Events.Data.Migrations` by `EventGuideDbContext`. Reviewing either with the synthesized name makes every command below fail. Resolve the actual class from either:

- the `<Context>ModelSnapshot.cs` sitting in the same folder — its filename is the context class name; or
- the `SECTION_DB_CONTEXTS` map in `.github/workflows/build.yml`, whose entries are `<Context>:<project path>` and are the same list CI verifies against.

Any `dotnet ef` command run during review must pass `--context <C>` with that resolved name, plus `--project` per the table above — see `memory/process/ef-multi-context-commands.md` for exact flag forms. Never run a bare `dotnet ef migrations ...`.

## What to Check

### 1. Bool Sentinel Trap (Most Common Failure)

**The bug:** `HasDefaultValue(false)` on a bool property makes EF skip persisting `false` values. When seed data sets a bool to `false`, EF generates `UPDATE table SET WHERE id = ...` (empty SET) → SQL syntax error → app crash loop.

**Check:** Search all entity configurations for `HasDefaultValue(false)` or `HasDefaultValueSql("false")` on bool properties. These are ALWAYS wrong.

**Correct patterns:**
- Bool with default false: just `.IsRequired()` — the CLR default handles it
- Bool with default true: `.IsRequired().HasDefaultValue(true).HasSentinel(true)`
- Never use `HasDefaultValue(false)` or `HasDefaultValueSql("false")`

### 2. Migration File Integrity

**Read the generated `.cs` file** (not the `.Designer.cs`). Check:

- **No empty SET clauses:** Search for `UpdateData` calls. Each must have `column:` and `value:` parameters. If any UpdateData exists without a value, the bool sentinel trap has struck.
- **New required columns are forbidden without Peter's approval** (`memory/architecture/required-columns-need-approval.md`): a new column on an existing table must be **nullable** unless Peter explicitly approved a required one. Flag ANY `AddColumn` with `nullable: false` on an existing table as a violation unless the PR cites that approval.
- **AddColumn with defaults:** if a (Peter-approved) non-nullable column is added to a table with existing data it needs a `defaultValue:` to apply — and then the model MUST declare the same default (`HasDefaultValue`/`HasDefaultValueSql`, minding the bool-sentinel rules above), so model and database agree. A scaffolded `defaultValue:` with no matching model declaration is the §5.1 divergence class (31-stray incident, 2026-08-02) and fails `PhysicalDefaultParityTests`.
- **Correct namespace:** matches the table in §0 — `Humans.Infrastructure.Migrations.<Area>` for the platform context hosted in `Humans.Web`, `Humans.<Section>.Data.Migrations` for a section. Note this tracks the *project*, not the context: `LegalDbContext`'s migrations are namespaced `Humans.Consent.Data.Migrations` because they live in `Humans.Consent`. The namespace line is the one sanctioned edit to a migration file, made only when a section moves.
- **No hand edits:** The migration should be exactly what `dotnet ef migrations add` generated. Never edit Up/Down methods.

### 3. Seed Data Consistency

When adding a new non-nullable column to an entity with `HasData` seed records:
- The seed data anonymous objects MUST include the new property
- The value must NOT trigger the bool sentinel trap (see #1)
- Count the seed objects — if TeamConfiguration has 6 system teams, all 6 must be updated

### 4. Configuration ↔ Entity Match

For each new entity:
- Every non-nullable property in the entity has `.IsRequired()` in the config
- Every string property has `.HasMaxLength(N)`
- Every enum property has `.HasConversion<string>().HasMaxLength(50)`
- Every `Instant` (NodaTime) property has `.IsRequired()`
- Every FK has a relationship configured with appropriate `OnDelete` behavior
- Every navigation property not mapped to a column is ignored via `builder.Ignore()`
- Table name is snake_case: `builder.ToTable("budget_years")`

### 5. DbContext DbSets

- Every new entity has a `DbSet<T>` in **the owning context identified in §0** — that context's own `<Context>.cs`, and no other
- Pattern: `public DbSet<Entity> Entities => Set<Entity>();`

### 6. Snapshot Consistency

After migration generation, **the owning context's model snapshot** — `<Context>ModelSnapshot.cs`, sitting in the same migrations folder and named for the context class resolved in §0 — should include all new entities and properties. If you deleted and regenerated a migration, verify the snapshot was properly reverted and regenerated (use `dotnet ef migrations remove --context <C>` before `dotnet ef migrations add --context <C>`). A diff touching a *different* context's snapshot than the one you're changing is a sign something ran without `--context` — stop and investigate (`memory/process/diff-snapshot-after-ef-tool.md`).

## Report Format

```
## EF Migration Review: [Migration Name]

### Owning Context
- Context: <ContextName> — resolved from <path/namespace>

### Bool Sentinel Check
- [ ] No HasDefaultValue(false) on any bool property
- [ ] No HasDefaultValueSql("false") on any bool property
- [ ] All bool defaults use correct pattern

### Migration File
- [ ] No empty SET clauses in UpdateData
- [ ] Non-nullable AddColumn has defaultValue for existing data
- [ ] Namespace matches the owning context (§0)
- [ ] No hand edits detected

### Seed Data
- [ ] All seed objects include new non-nullable properties
- [ ] Values don't trigger sentinel trap

### Config ↔ Entity
- [ ] All properties configured correctly
- [ ] All relationships configured with OnDelete
- [ ] Table names are snake_case
- [ ] Computed properties ignored

### DbContext
- [ ] All new DbSets declared on the owning context

### Snapshot
- [ ] Only the owning context's *ModelSnapshot.cs changed

### Issues Found
[List any issues with severity: CRITICAL / WARNING / INFO]
```
