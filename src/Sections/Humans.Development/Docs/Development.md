<!-- freshness:triggers
  src/Sections/Humans.Development/**
  src/Humans.Web/Hosting/DevLoginControllerExclusionProvider.cs
  src/Humans.Web/Views/Account/Login.cshtml
-->

# Development - Section Invariants

Dev-only tooling: persona sign-in and fixture seeding. Never reachable in Production. Owns no tables.

The section existed as a decision before it existed as code - the 2026-08-03 inventory freeze ruled *"move DevLogin/DevSeed to a new Development section that is not loaded in prod"* and [`docs/plans/2026-08-03-g0-first-audit/Development.md`](../../../../docs/plans/2026-08-03-g0-first-audit/Development.md) scored it against files scattered across `Humans.Web.Controllers` and `Humans.Web.Infrastructure`. This doc is that audit's G1 gap #1, written at the G5 move (nobodies-collective/Humans#866).

## Concepts

- **Persona** - a named, deterministic dev user. `DevPersonaSeeder.PersonaGuid(slug)` is a SHA-256 of `dev-persona:{slug}`, so the same slug is the same user id across restarts and machines, and seeding is idempotent. Every `RoleNames` constant becomes a persona automatically (`PascalToKebab`), plus seven hand-written ones: `guest`, `no-name`, `volunteer`, `barrio-1-lead`, `barrio-2-lead`, `coordinator`, `city-planning`.
- **Fixture seeder** - a dev-only writer that builds realistic demo data through *other* sections' service interfaces. Three live here (personas, camp roles, the coordinator dashboard); a fourth, `DevelopmentBudgetSeeder`, lives in `Humans.Budget` behind `IBudgetDemoSeeder` because it drives Budget's whole write surface.
- **Dev-auth gate** - `DevAuth:Enabled` (a `ConfigurationRegistry` setting in the `Development` category) **and** a non-Production host environment. Both are required; the dashboard seed additionally requires `ASPNETCORE_ENVIRONMENT=Development` exactly.
- **Admin opt-in** - `DevAuth:AllowAdmin` (same category), which re-admits the Admin persona and Admin impersonation on a deployed host. Set only on per-PR previews, by `docker-entrypoint.sh`.

## Data Model

This section owns no entities and no tables. Everything it creates belongs to another section and is created through that section's service.

## Routing

| Route | Method | Auth | Purpose |
|-------|--------|------|---------|
| `/dev/login/{persona}` | GET | Anonymous | Seed (idempotently) and sign in as a named persona; `guest` mints a fresh profileless account per click. The `admin` persona 404s outside a dev host or a preview |
| `/dev/login/users` | GET | Anonymous | User chooser - first 100 humans by burner name, ephemeral guests filtered out |
| `/dev/login/users/{id}` | GET | Anonymous | Sign in as an existing user by id; 404s outside a dev host or a preview when that human holds an active Admin assignment |
| `/dev/seed/budget` | POST | `FinanceAdminOrAdmin` | Budget demo data, via `IBudgetDemoSeeder` on Budget's contracts leaf |
| `/dev/seed/camp-roles` | POST | `CampAdminOrAdmin` | Five system camp-role definitions |
| `/dev/seed/dashboard` | POST | `ShiftDashboardAccess` | Coordinator-dashboard demo: one event, 8 departments, 5 subteams, ~120 humans, rotas/shifts/signups |
| `/dev/seed/dashboard/reset` | POST | `AdminOnly` | Delete everything the dashboard seed created |

Every route 404s when the dev-auth gate is closed. `/dev/login/*` additionally does not exist at all in Production - see Negative Access Rules.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anyone (anonymous) | `/dev/login/*`, in dev/preview only. This is the point of the section: it exists to get *into* an account |
| Finance Admin / Admin | `/dev/seed/budget` |
| Camp Admin / Admin | `/dev/seed/camp-roles` |
| Volunteer Coordinator / NoInfo Admin / Admin | `/dev/seed/dashboard` |
| Admin | `/dev/seed/dashboard/reset` - destructive, deletes seeded humans and teams |
| Everyone, in Production | Nothing |

## Invariants

- **Nothing in this section is reachable in Production.** Three independent mechanisms, deliberately not one: the seeders are not registered (`Section.Register` returns early), `DevLoginController` is removed from MVC's controller feature by Shell's `DevLoginControllerExclusionProvider`, and both controllers' own `IsDevAuthEnabled()` / `IsDevSeedEnabled()` guards return `NotFound()`.
- **`Section.Register` fails closed.** `ISection.Register` takes no `IHostEnvironment`, so it reads `HostDefaults.EnvironmentKey` from the configuration Shell hands it. An environment name it cannot read registers *nothing* - the failure lands as dev login 404ing in the test host, not as dev seeders reaching Production. Pinned by `DevelopmentArchitectureTests.Register_binds_nothing_in_production_or_when_the_environment_is_unknown`.
- **The dashboard seed is stricter than the rest**: `IsDevelopment()` **and** `DevAuth:Enabled`. QA, preview and production cannot invoke it regardless of role. (`src/Sections/Humans.Shifts/Docs/Shifts.md` states the same invariant from the other side; `DevSeedControllerTests` covers both branches.)
- **Dev login never yields an Admin session outside a dev host.** A "dev host" is `Development`, the in-process `Testing` integration host, or any host that sets `DevAuth:AllowAdmin` - QA runs `Staging` with `DevAuth:Enabled` on and real Google Workspace data, so an anonymous URL that mints Admin there is a live privilege escalation. Both doors are shut: the `admin` persona (matched by resolving the slug back through `DevPersonaSeeder.RoleNameFromSlug`, before any seeding runs) and impersonation of any human with an active `RoleNames.Admin` assignment (`IRoleAssignmentService.IsUserAdminAsync`). `_DevLoginPanel` renders `DevLoginController.PersonasFor(env, config)` so the button and the route share one predicate. Covered by `DevLoginControllerTests`.
- **Per-PR previews are the exception, and only the entrypoint can name them.** A preview container runs the same `Staging` environment name as QA, so the discriminator lives where the difference is already known: `docker-entrypoint.sh` derives `PR_ID` from Coolify's container name/FQDN, points the connection string at `humans_pr_{N}`, and defaults `DevAuth__AllowAdmin=true` **inside that same block** - the flag means "this container is on a throwaway PR database", so it must follow the switch actually happening, not `PR_ID` alone. A container that could not switch (no `DB_PASSWORD`, or a hand-supplied connection string) is still on the inherited QA connection, and anonymous Admin over QA data is exactly the escalation this invariant closes. QA's `deploy-qa.sh` never sets the flag. It is only a *default*, so a deploy that wants a preview without Admin can set it to `false`.
- **Every write goes through the owning section's service.** No `DbContext`, no repository, no cross-section table access - held by the section's csproj (no `Humans.Infrastructure` reference, no EF Core package) and by every seeder taking only application-service dependencies. Not pinned by a test, deliberately - the section has no tables, and an absence has no behaviour to regress.
- **Persona seeding is idempotent and repairs.** `EnsurePersonaAsync` returns the existing user when there is one, and `EnsureActiveAsync` runs on *every* sign-in: it submits any missing required consents through the canonical consent path and lifts a consent suspension, because personas hold governance roles and the nightly `SuspendNonCompliantMembersJob` suspends them otherwise (nobodies-collective/Humans#867).
- **The `no-name` persona is re-blanked on every sign-in** so the onboarding name gate (#812) re-triggers each time.
- **The `guest` persona is never reused** - each click mints a new profileless account, so parallel testers do not collide.

## Negative Access Rules

- A non-privileged authenticated human cannot invoke any `/dev/seed/*` action: `302 -> /Account/AccessDenied` (cookie authentication's `AccessDeniedPath`, app-wide - not a bare `403`). Pinned by `DevelopmentPageRenderTests`.
- No type in the section may take `IStringLocalizer<T>` for **any** `T`. The section carries no resource set, no `Development_*` key and no `Enum_Development*` key: every string is English developer copy. Adding localized copy must start by carving a resource set. Not pinned by a test, deliberately - there is no resource set to bind against; grep for `IStringLocalizer<` in the section returns only the `_ViewImports.cshtml` comment.
- The seeders must not be reachable from production code paths. They are `internal` to this assembly and registered only outside Production.
- No anonymous visitor to QA or Production may reach an Admin session through `/dev/login/*`, with `DevAuth:Enabled` on or off: `GET /dev/login/admin` and `GET /dev/login/users/{id}` for an active Admin both `404`. Only `Development`, `Testing` and a host with `DevAuth:AllowAdmin` (per-PR previews) allow either. Consequence: the Playwright suite runs against QA (`Staging`, no opt-in) and so has **no admin persona** - `AdminOnly` routes are covered in-process by `Humans.Integration.Tests` (`DevPersona.Admin`, a `Testing` host) instead.

## Triggers

- `GET /dev/login/{persona}` seeds or repairs the persona, then signs in.
- `POST /dev/seed/*` seeds on demand; only `dashboard/reset` deletes.
- Nothing here runs on a schedule and nothing subscribes to another section's events.

## Cross-Section Dependencies

Development is a pure consumer and depends on more sections than any other:

| Section | Surface used |
|---------|--------------|
| Users / Profiles | `UserManager<User>` and `SignInManager<User>` (the §2a Identity exception, see [`Users.md`](../../Humans.Users/Docs/Users.md)), `IUserService`, `IUserServiceRead`, `IUserEmailService`, `IProfileEditorService`, `IContactFieldService`, `IUserInfoInvalidator`, `IHumanLifecycleService` |
| Auth | `IRoleAssignmentService` |
| Teams | `ITeamService`, `ITeamSeeding`, `ISystemTeamSync` |
| Camps | `ICampServiceRead`, `ICampSeeding`, `ICampRoleSeeding` |
| Shifts | `IShiftSeeding`, `IBurnSettingsService`, `IShiftSignupSeeding` |
| Audit Log | `IAuditLogService` |
| Consent | `IConsentSubmission` (contracts leaf) |
| Governance | `IMembershipCalculatorRead` (contracts leaf) |
| City Planning | `CityPlanningOptions` (contracts leaf) - the dev city-planning team slug |
| Budget | `IBudgetDemoSeeder` (contracts leaf) |

Nothing depends on Development in the other direction. Shell reaches it twice and neither is a type reference: `/Account/Login` renders `_DevLoginPanel` by partial name, and `DevLoginControllerExclusionProvider` resolves the controller by name through `SectionDiscoveryExtensions`.

## Architecture

**Owning services:** None - two controllers over three internal fixture seeders.
**Owned tables:** None.
**Status:** (G5) Own project at `src/Sections/Humans.Development` (nobodies-collective/Humans#866). No G4 gate - the section owns no tables, so there is no `DbContext`, no history table and no baseline.

- `DevLoginController` and `DevSeedController` are `internal sealed` in `Humans.Development.Controllers`, routed by Shell's `SectionControllerFeatureProvider`. The route prefixes (`dev/login`, `dev/seed`) are unchanged by the move.
- No `Humans.Infrastructure` reference (Scanner's and Cantina's shape, not Debug's): every write goes through a `Humans.Application` interface or a contracts leaf, and `UserManager<User>` is Identity's, not Infrastructure's.
- `Contracts/` is an empty folder. Folder-vs-project is decided by where the consumer lives, and there is no compile-time consumer at all.
- `DevPersonaSeeder` is on the `ApplicationServicesTakeNoMemoryCacheRule` allowlist. It does not *hold* a cache - it calls `MemoryCacheExtensions.InvalidateUserAccess(userId)` after changing a persona's roles or teams, the same call Shell's `GateTerminalAccountSeeder` makes. It entered that sweep at the move, because the rule scans `Humans.Application` plus the section assemblies and this code used to sit in `Humans.Web/Infrastructure`, which it covers neither before nor after.
- **Known deviation, carried from the G0 audit (gap #5):** `DevPersonaSeeder` and `DevelopmentDashboardSeeder` create Identity accounts through `UserManager<User>` rather than `IUserService`. This is the §2a framework exception applied to a dev fixture; it is invisible to the analyzers because `UserManager` is neither a repository nor a `DbContext`. Sanctioned here rather than left implicit.
- **Gap #6 from the G0 audit is closed.** Shifts and Teams carried out their read-splits: `DevelopmentDashboardSeeder` now creates/deactivates the seeded event through `IShiftSeeding.CreateBurnAsync`/`DeactivateActiveBurnAsync` (input records, not a mutated `EventSettings` entity), resolves it via `IBurnSettingsService.GetByIdAsync`, and resolves teams via `ITeamServiceRead.GetTeamBySlugAsync` (`TeamInfo`, not the `Team` entity). No baselined entity-returning read remains in this section.
- **Known deviation, carried from the G0 audit (gap #4):** the Production exclusion covers `DevLoginController` only. `DevSeedController` stays in the MVC graph in Production behind its action-level guards. Generalising the provider to the whole section's controller surface is behavioural and out of a G5 move's scope.
- **No renames.** `DevelopmentCampRoleSeeder` and `DevelopmentDashboardSeeder` duplicate the section name, which is normally step 5's collapse case - but `DevPersonaSeeder`, `DevLoginController` and `DevSeedController` all use the shorter `Dev` prefix, so stripping half of them would leave the section inconsistently named. `nameof(DevPersonaSeeder)` is also written to `consent_records.user_agent` on every seeded consent, which makes that one a persisted string.
- **Decorator decision - no caching decorator.** Owns no data.
- **Cross-domain navs:** N/A - owns no entities.
