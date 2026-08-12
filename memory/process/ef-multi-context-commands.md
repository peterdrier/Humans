---
name: Every dotnet ef command needs --context since the per-section split
description: More than one DbContext is in play (HumansDbContext + one per peeled section, nobodies-collective/Humans#858). Every `dotnet ef` invocation MUST pass `--context <C>`, and `--project` is per-context once a section owns its own project (nobodies-collective/Humans#866).
---

Since the per-section DbContext split (nobodies-collective/Humans#858), more than one DbContext
is in play. `dotnet ef` errors out ("More than one DbContext was found") unless every invocation
names its context. Since the section-project split (nobodies-collective/Humans#866), `--project`
also varies: a section at G5 owns its migrations, so they are generated into and read from the
section's own project.

**Main pile (HumansDbContext)** — commands unchanged except the explicit context:

```bash
dotnet ef migrations add <Name> --context HumansDbContext \
  --project src/Humans.Infrastructure --startup-project src/Humans.Web
```

**Peeled section, still in Humans.Infrastructure** — context AND output dir (the section's
migrations live in their own folder with their own `<Section>DbContextModelSnapshot.cs`):

```bash
dotnet ef migrations add <Name> --context SystemSettingsDbContext \
  --output-dir Migrations/SystemSettings \
  --project src/Humans.Infrastructure --startup-project src/Humans.Web
```

**Section at G5, in its own project** — `--project` is the section, and the output dir is the
section-local `Data/Migrations` (no per-section subfolder: the project already scopes it):

```bash
dotnet ef migrations add <Name> --context StoreDbContext \
  --output-dir Data/Migrations \
  --project src/Sections/Humans.Store --startup-project src/Humans.Web
```

**Verification** (CI runs this per context; do the same locally before any migration commit):

```bash
dotnet ef migrations has-pending-model-changes --context <C> \
  --project <the context's project> --startup-project src/Humans.Web
```

**How to apply:**

- Never run a bare `dotnet ef migrations add` / `remove` / `database update` — pick the context
  first. Which context owns the table is the section boundary question; see the design doc
  `docs/superpowers/specs/2026-07-15-per-section-dbcontext-design.md` §3 for the partition map.
- `--startup-project src/Humans.Web` never changes. `--project` is the project that *contains
  the context*: `src/Humans.Infrastructure` for everything not yet at G5, `src/Sections/Humans.<Section>`
  after. The runtime and design-time `MigrationsAssembly` both derive from the context's own
  assembly, so they cannot disagree with where you generated.
- A schema change in a peeled section touches ONLY that section's migration folder and snapshot.
  If `git status` shows `HumansDbContextModelSnapshot.cs` changed after a section migration,
  something is wrong — stop and investigate ([[diff-snapshot-after-ef-tool]]).
- Section baselines are never edited or removed; rollback of a peel is a PR revert
  ([[no-hand-edited-migrations]] still applies in full — the one-time hand-emptied peel
  removal migrations were a Peter-authorized exception scoped to the #858 stack).
- When a new section context lands, add its `context:project` pair to the `SECTION_DB_CONTEXTS`
  workflow-level `env` var in `.github/workflows/build.yml` — one list, consumed by all three
  loops (Layer 1, Layer 2 per-section apply, post-apply). Nowhere else in that file names a
  context. A G5 move edits that pair's project and nothing else.
