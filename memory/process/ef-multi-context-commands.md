---
name: Every dotnet ef command needs --context since the per-section split
description: Humans.Infrastructure hosts multiple DbContexts (HumansDbContext + one per peeled section, nobodies-collective/Humans#858). Every `dotnet ef` invocation MUST pass `--context <C>`; section migrations also need `--output-dir Migrations/<Section>`.
---

Since the per-section DbContext split (nobodies-collective/Humans#858), `Humans.Infrastructure`
contains more than one DbContext. `dotnet ef` errors out ("More than one DbContext was found")
unless every invocation names its context.

**Main pile (HumansDbContext)** — commands unchanged except the explicit context:

```bash
dotnet ef migrations add <Name> --context HumansDbContext \
  --project src/Humans.Infrastructure --startup-project src/Humans.Web
```

**Peeled section** — context AND output dir (the section's migrations live in their own folder
with their own `<Section>DbContextModelSnapshot.cs`):

```bash
dotnet ef migrations add <Name> --context SystemSettingsDbContext \
  --output-dir Migrations/SystemSettings \
  --project src/Humans.Infrastructure --startup-project src/Humans.Web
```

**Verification** (CI runs this per context; do the same locally before any migration commit):

```bash
dotnet ef migrations has-pending-model-changes --context <C> \
  --project src/Humans.Infrastructure --startup-project src/Humans.Web
```

**How to apply:**

- Never run a bare `dotnet ef migrations add` / `remove` / `database update` — pick the context
  first. Which context owns the table is the section boundary question; see the design doc
  `docs/superpowers/specs/2026-07-15-per-section-dbcontext-design.md` §3 for the partition map.
- A schema change in a peeled section touches ONLY that section's migration folder and snapshot.
  If `git status` shows `HumansDbContextModelSnapshot.cs` changed after a section migration,
  something is wrong — stop and investigate ([[diff-snapshot-after-ef-tool]]).
- Section baselines are never edited or removed; rollback of a peel is a PR revert
  ([[no-hand-edited-migrations]] still applies in full — the one-time hand-emptied peel
  removal migrations were a Peter-authorized exception scoped to the #858 stack).
- Keep the context lists in `.github/workflows/build.yml` (Layer 1 loop, Layer 2 per-section
  apply, post-apply loop) in sync when a new section context lands.
