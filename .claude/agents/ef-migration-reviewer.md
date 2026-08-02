# EF Core Migration Reviewer

Review EF Core migrations and entity configurations for correctness before they reach production. This agent exists because EF migrations have caused repeated failures — empty UPDATE statements, wrong namespaces, sentinel traps, hand-edited files.

## When to Use

Run this agent after generating any EF Core migration, before committing or pushing.

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
- **Correct namespace:** Must be `Humans.Infrastructure.Migrations`, NOT `Humans.Infrastructure.Data.Migrations` or anything else.
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

- Every new entity has a `DbSet<T>` in `HumansDbContext.cs`
- Pattern: `public DbSet<Entity> Entities => Set<Entity>();`

### 6. Snapshot Consistency

After migration generation, the `HumansDbContextModelSnapshot.cs` should include all new entities and properties. If you deleted and regenerated a migration, verify the snapshot was properly reverted and regenerated (use `dotnet ef migrations remove` before `dotnet ef migrations add`).

## Report Format

```
## EF Migration Review: [Migration Name]

### Bool Sentinel Check
- [ ] No HasDefaultValue(false) on any bool property
- [ ] No HasDefaultValueSql("false") on any bool property
- [ ] All bool defaults use correct pattern

### Migration File
- [ ] No empty SET clauses in UpdateData
- [ ] Non-nullable AddColumn has defaultValue for existing data
- [ ] Namespace is Humans.Infrastructure.Migrations
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
- [ ] All new DbSets declared

### Issues Found
[List any issues with severity: CRITICAL / WARNING / INFO]
```
