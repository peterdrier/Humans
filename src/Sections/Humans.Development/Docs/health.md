# Development — health

## Target (derived 2026-08-24)

### 1. What the section does

Provides two dev-only surfaces so a running (non-Production) app can be exercised
without going through Google OAuth or hand-building fixture data:

- **Dev sign-in.** Anonymous URLs mint a signed-in session as a named persona
  (`/dev/login/{slug}`, `/dev/login/users`, `/dev/login/users/{id}`). Persona
  identities are deterministic so reruns produce the same user.
- **Fixture seeding.** Authorized POST endpoints seed realistic demo data —
  Budget demo (`/dev/seed/budget`), five system camp-role definitions
  (`/dev/seed/camp-roles`), the coordinator-dashboard fixture
  (`/dev/seed/dashboard` + `/dev/seed/dashboard/reset`).

Nothing is reachable in Production; nothing is owned (no tables, no DbContext).

### 2. The shapes

The section groups by "gate shape" and "what happens next":

| Shape | Members | Auth gate | Post-gate action |
|---|---|---|---|
| Dev-login sign-in | `SignIn(persona)`, `SignInAsUser(id)` | dev-auth on, host non-Production; Admin persona/impersonation also needs `AdminSignInAllowed` | Seeder writes idempotently, then `SignInManager.SignInAsync` |
| Dev-login chooser | `Users()` | dev-auth on, host non-Production | Read via `DevPersonaSeeder.GetUsersForChooserAsync` (users list) |
| Dev-seed request (default) | `SeedBudget`, `SeedCampRoles` | `[Authorize(policy)]` + dev-auth on, host non-Production | Seeder call, redirect to `/Admin` |
| Dev-seed strict | `SeedDashboard`, `ResetDashboard` | `[Authorize(policy)]` + dev-auth on, host `IsDevelopment()` only | Seeder call, redirect to `/Shifts/Dashboard` |
| Section boot | `Section.Register` | Non-Production env name only | Registers 3 seeders (fail-closed on unknown env) |
| Nav contribution | `SectionAdminNav.Groups` | Environment gate: `!env.IsProduction()` | Renders "Dev" admin group |

Everything else in the section is helpers reachable from one of the six shapes.

### 3. Structure

The layout those shapes imply:

```
Humans.Development/
├── Section.cs                 # DI entry point (fail-closed env check)
├── SectionAdminNav.cs         # Admin sidebar contribution ("Dev" group)
├── Controllers/
│   ├── DevLoginController.cs  # Dev sign-in surface
│   └── DevSeedController.cs   # Fixture seeding surface
├── Services/
│   ├── DevPersonaSeeder.cs           # Persona create + EnsureActive repair
│   ├── DevelopmentCampRoleSeeder.cs  # Camp-role definitions seeder
│   ├── DevelopmentDashboardSeeder.cs # Dashboard demo seeder + reset
│   └── AuditEntityTypes.cs           # Persisted "Profile" discriminator
├── Views/
│   ├── DevLogin/Users.cshtml
│   ├── Shared/_DevLoginPanel.cshtml  # Rendered by Shell's /Account/Login
│   └── _ViewImports.cshtml
├── Contracts/                 # Intentionally empty (no external consumers)
│   └── README.md
└── Docs/
    ├── Development.md         # Section invariants
    ├── authorization.md       # Route → policy table
    └── health.md              # This file
```

Every file above is claimed by at least one thread in §Threads.

### 4. Invariants

- Nothing here is reachable in Production. Three independent mechanisms:
  seeders not registered, `DevLoginController` removed by Shell's
  `DevLoginControllerExclusionProvider`, both controllers' own guards return
  `NotFound()`.
- `Section.Register` fails closed on unreadable/blank/Production env names.
- Dashboard seed is stricter than the rest: `IsDevelopment()` **and**
  `DevAuth:Enabled`. QA and preview cannot invoke it.
- Dev login never yields an Admin session outside a dev host (Development,
  Testing, or a host that opted in with `DevAuth:AllowAdmin`).
- Every write goes through the owning section's service — no `DbContext`,
  no repository, no cross-section table access.
- Persona seeding is idempotent and repairs (`EnsureActiveAsync`).
- The `no-name` persona is re-blanked on every sign-in; the `guest` persona
  is minted fresh on every click.
- Section carries no resource set — every string is English developer copy;
  no type binds `IStringLocalizer<T>` for any `T`.

### 5. Seams (specified-but-unbuilt)

- **`Contracts/` folder.** Reserved for future consumers of an
  `IDevelopmentX` interface; today's `README.md` explains why it stays empty.

### 6. Deliberately not done

- **No caching decorator.** The section owns no data.
- **No repository.** No tables to guard.
- **No resource set / `IStringLocalizer<T>` binding.** Dev copy stays English.
- **No `Humans.Infrastructure` reference.** All writes flow through
  application-service interfaces or contracts leaves.
- **No renames of `Development*Seeder` → `Dev*Seeder`.** Half the section
  already uses the shorter `Dev` prefix; renaming the other half leaves an
  inconsistent set and breaks the persisted `nameof(DevPersonaSeeder)` string
  in `consent_records.user_agent`.
- **No generalisation of the Production controller-exclusion provider to
  cover `DevSeedController`.** Deliberate G0-audit deviation (gap #4) —
  behavioural, out of scope for a maintenance run.

### Load-bearing weirdness

- **Two independent locks on the Admin persona.** The Admin persona check runs
  before any seeding (`SignIn`), and the chooser separately checks
  `IsUserAdminAsync` on the target id (`SignInAsUser`). The panel enumerates
  through the same `PersonasFor` predicate so button and route agree. Three
  places, one contract — deliberate belt-and-braces because the failure mode
  is anonymous Admin against real Google Workspace data.
- **The two `PascalToKebab` copies were merged this run** — see Strike 4.
- **`docker-entrypoint.sh` (Ops) is the only place that can distinguish a
  per-PR preview from QA.** Both run under `Staging`; the entrypoint derives
  `PR_ID` from Coolify's container name/FQDN and defaults
  `DevAuth__AllowAdmin=true` inside the same block that switches the
  connection string to the throwaway per-PR database.
- **`AuditEntityTypes.Profile = "Profile"` is a persisted-string data
  contract**, matched by exact equality on read. Never regenerate from
  `nameof` — the entity type went internal to Users at G5, so this section
  cannot spell it anyway.

## Reforge

Section-scoped reforge is not re-run inside health.md between runs — the
authoritative live figure is the selector table in each run file. Selector
recorded on 2026-08-24: `score=273, loc=1568`.

## Assessment history

| Date | Branch | PR | Reforge (loc) | Notes |
|---|---|---|---|---|
| 2026-08-24 | `section-doctor/2026-08-24T071255Z` | peterdrier/Humans#1480 | 273 (1568) | First doctor pass — target derived; Contracts README + Development.md doc drift fixed; mojibake em-dashes in DevPersonaSeeder.cs cleaned; `PascalToKebab` deduped across DevLoginController/DevPersonaSeeder. |
