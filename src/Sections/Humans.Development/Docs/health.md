# Development — health

## Target (derived 2026-09-28)

### 1. What the section does

Lets a running non-Production app be exercised without Google OAuth or hand-built fixture data:

- **Dev sign-in.** Anonymous links sign the visitor in as a named persona, or as any existing
  human. Persona identities are deterministic, so reruns reach the same user.
- **Fixture seeding.** Privileged buttons fill the app with demo data: a budget year, the
  system camp-role definitions, and a coordinator-dashboard event with teams, humans, shifts
  and signups (plus a reset that deletes the dashboard fixture).

Nothing is reachable in Production; nothing is owned.

### 2. The shapes

| Shape | Members | Auth gate | Post-gate action |
|---|---|---|---|
| Dev-login sign-in | `SignIn(persona)`, `SignInAsUser(id)` | dev-auth on, host non-Production; Admin persona/impersonation also needs `AdminSignInAllowed` | Seeder writes idempotently, then `SignInManager.SignInAsync` |
| Dev-login chooser | `Users()` | dev-auth on, host non-Production | Read via `DevPersonaSeeder.GetUsersForChooserAsync` |
| Dev-seed page | `Index()` | `AdminOnly` + dev-auth on, host non-Production | Renders the budget and camp-role seed buttons |
| Dev-seed request (default) | `SeedBudget`, `SeedCampRoles` | `[Authorize(policy)]` + dev-auth on, host non-Production | Seeder call, redirect to `/Admin` |
| Dev-seed strict | `SeedDashboard`, `ResetDashboard` | `[Authorize(policy)]` + dev-auth on, host `IsDevelopment()` only | Seeder call, redirect to `/Shifts/Dashboard` |
| Section boot | `Section.Register` | Non-Production env name only | Registers the three seeders (fail-closed on unknown env) |
| Nav contribution | `SectionAdminNav.Groups` | Environment gate: `env.IsDevelopment()` | Renders the "Development" admin group (one item, `/dev/seed`) |

Everything else in the section is a helper reachable from one of these shapes.

### 3. Structure

```
Humans.Development/
├── Section.cs                 # DI entry point (fail-closed env check)
├── SectionAdminNav.cs         # Admin sidebar contribution ("Development" group)
├── Controllers/
│   ├── DevLoginController.cs  # Dev sign-in surface
│   └── DevSeedController.cs   # Fixture seeding surface + seed page
├── Services/
│   ├── DevPersonaSeeder.cs           # Persona create + EnsureActive repair
│   ├── DevelopmentCampRoleSeeder.cs  # Camp-role definitions seeder
│   ├── DevelopmentDashboardSeeder.cs # Dashboard demo seeder + reset
│   └── AuditEntityTypes.cs           # Persisted "Profile" discriminator
├── Views/
│   ├── DevLogin/Users.cshtml
│   ├── DevSeed/Index.cshtml          # The seed page the admin nav links to
│   ├── Shared/_DevLoginPanel.cshtml  # Rendered by Shell's /Account/Login
│   └── _ViewImports.cshtml
├── Contracts/                 # Intentionally empty (no external consumers)
│   └── README.md
└── Docs/
    ├── Development.md         # Section invariants
    ├── authorization.md       # Route → policy table
    ├── debt.yml               # Section debt ledger
    └── health.md              # This file
```

### 4. Invariants

- Seeders are not registered on a Production or unreadable environment name —
  `src/Sections/Humans.Development/Section.cs:40`.
- `DevLoginController` is removed from the Production controller feature —
  `src/Humans.Web/Hosting/DevLoginControllerExclusionProvider.cs:62`.
- Every dev-login and dev-seed action 404s on Production or with `DevAuth:Enabled` off —
  `src/Sections/Humans.Development/Controllers/DevLoginController.cs:158`,
  `src/Sections/Humans.Development/Controllers/DevSeedController.cs:102`.
- The dashboard seed and reset also require `IsDevelopment()` —
  `src/Sections/Humans.Development/Controllers/DevSeedController.cs:131`.
- The Admin persona 404s off a dev host before any seeding —
  `src/Sections/Humans.Development/Controllers/DevLoginController.cs:58`; impersonating an
  active Admin 404s off a dev host —
  `src/Sections/Humans.Development/Controllers/DevLoginController.cs:140`; a dev host is
  `Development`, `Testing` or `DevAuth:AllowAdmin` —
  `src/Sections/Humans.Development/Controllers/DevLoginController.cs:191`.
- The login panel offers exactly the personas the route accepts —
  `src/Sections/Humans.Development/Views/Shared/_DevLoginPanel.cshtml:27`.
- An existing persona is repaired to Active on every sign-in —
  `src/Sections/Humans.Development/Services/DevPersonaSeeder.cs:66`.
- The `no-name` persona's legal names are re-blanked on every sign-in —
  `src/Sections/Humans.Development/Controllers/DevLoginController.cs:90`.
- Every `guest` click mints a new account —
  `src/Sections/Humans.Development/Services/DevPersonaSeeder.cs:302`.
- The seeded event's build start is no later than its first-crew boundary —
  `src/Sections/Humans.Development/Services/DevelopmentDashboardSeeder.cs:104`.
- Dashboard humans get their names through the profile path before any self-signup —
  `src/Sections/Humans.Development/Services/DevelopmentDashboardSeeder.cs:327`.

### 5. Seams (specified-but-unbuilt)

- **`Contracts/` folder.** Reserved for a future consumer of a Development interface; today's
  `README.md` explains why it stays empty.

### 6. Deliberately not done

- **No caching decorator.** The section owns no data.
- **No repository.** No tables to guard.
- **No resource set / `IStringLocalizer<T>` binding.** Dev copy stays English; not test-pinned
  (Peter declined the pinning tests on the first run).
- **No `Humans.Infrastructure` reference.** All writes flow through other sections' service
  interfaces or contracts leaves.
- **No renames of `Development*Seeder` → `Dev*Seeder`.** Half the section already uses the
  shorter `Dev` prefix, and `nameof(DevPersonaSeeder)` is persisted in
  `consent_records.user_agent`.
- **No generalisation of the Production controller-exclusion provider to cover
  `DevSeedController`.** Carried G0-audit deviation (gap #4); behavioural.
- **No second copy of the dependency list.** `Development.md`'s Cross-Section Dependencies table
  is the only one; `Contracts/README.md` and the csproj point at it.

### Load-bearing weirdness

- **Three places, one Admin contract.** The Admin persona check runs before any seeding, the
  chooser separately checks `IsUserAdminAsync`, and the panel enumerates through the same
  `PersonasFor` predicate. Deliberate belt-and-braces: the failure mode is anonymous Admin
  against real Google Workspace data.
- **The seed page answers on QA and previews, its nav link does not.** `/dev/seed` opens
  wherever dev-auth is on, but the "Development" admin nav group shows only on `Development`
  (Peter's choice in the admin-nav rework). On QA and previews the page is reached by URL.
- **`docker-entrypoint.sh` (Ops) is the only place that can distinguish a per-PR preview from
  QA.** Both run `Staging`; the entrypoint sets `DevAuth__AllowAdmin=true` inside the same block
  that switches the connection string to the throwaway per-PR database.
- **`AuditEntityTypes.Profile = "Profile"` is a persisted-string data contract**, matched by exact
  equality on read. Never regenerate from `nameof` — Users' `Profile` is internal, so this
  section cannot spell it anyway.
- **Persona slugs, the persona list and the slug reverse lookup share one list and one
  kebab-case helper** (`DevPersonaSeeder.RoleNameValues`, `DevPersonaSeeder.PascalToKebab`), so
  they cannot disagree.

## Assessment history

| Date | Branch | PR | Notes |
|---|---|---|---|
| 2026-08-24 | `section-doctor/2026-08-24T071255Z` | peterdrier/Humans#1480 | First doctor pass — target derived; Contracts README + Development.md doc drift fixed; mojibake em-dashes in DevPersonaSeeder.cs cleaned; `PascalToKebab` deduped across DevLoginController/DevPersonaSeeder. |
| 2026-09-28 | `section-doctor/2026-09-28T011647Z` | pending | Comments and docs that named absent tests, Shell-owned policies and retired interfaces made true; dependency list kept in one place; guest and no-name persona invariants pinned; RoleNames read from one list. |
