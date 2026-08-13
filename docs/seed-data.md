<!-- freshness:triggers
  src/Humans.Infrastructure/Data/Configurations/**
  src/Humans.Domain/Constants/**
  src/Sections/Humans.Development/**
  src/Humans.Infrastructure/Migrations/**
  src/Sections/**/Data/**
-->
<!-- freshness:flag-on-change
  Seed-data strategy (HasData, migration SQL, dev-only seeders). Review when EF configurations change HasData calls, when domain constants shift, or when dev seeder endpoints change.
-->

# Seed Data Strategy

## Decision Guide

| Scenario | Approach |
|----------|----------|
| Bootstrap rows every environment needs | EF Core `HasData` in entity configurations |
| One-time corrective insert/update during schema evolution | Migration SQL |
| Rich demo data for local dev or preview environments | Dev-only runtime seeder |
| Test-only records | Test fixtures, not app seed data |

## Existing Patterns

**`HasData`** — stable bootstrap rows with deterministic IDs, part of migrations. Since the DbContext split and the G5 section moves these configurations sit in two places:

- `src/Humans.Infrastructure/Data/Configurations/` — shift tags (`ShiftTagConfiguration`), sync settings (`SyncServiceSettingsConfiguration`), camp settings (`CampSettingsConfiguration`).
- `src/Sections/Humans.<Section>/Data/Configurations/` — system teams (`TeamConfiguration`, `Humans.Teams`), ticket sync state (`TicketSyncStateConfiguration`, `Humans.Tickets`), system settings (`SystemSettingConfiguration`, the `IsEmailSendingPaused` row), agent settings (`AgentSettingsConfiguration`), event-guide categories (`EventCategoryConfiguration`).

`HumansDbContext` was deleted outright (nobodies-collective/Humans#858; Peel 15, nobodies-collective/Humans#1273 folded Users and Profiles into the new `UsersDbContext`) — there is no shared root context left. A `HasData` row seeded from a section project lands in that section's own migration chain; every table belongs to exactly one section context, whether that context lives in a section's own project (e.g. `Humans.Teams`, `Humans.Tickets`) or, for sections peeled but not yet moved, in `src/Humans.Infrastructure/Data/` (`UsersDbContext`, `CampsDbContext`, `ShiftsDbContext`, `GoogleIntegrationDbContext`, `SystemDbContext`).

**Lazy singleton instead of `HasData`** — some single-row settings tables are deliberately *not* seeded and are created on first save instead (`GateSettingsConfiguration`, `HoldedSyncStateConfiguration` both carry a comment saying so). Prefer this when the row's shape is likely to change: it avoids a `HasData` value baked into an old migration drifting from the entity.

**Migration SQL** — one-off backfills tied to schema changes, written directly into a migration's `Up()`. These don't survive as standalone examples: a section move or DbContext peel squashes its migration chain into one baseline (see `memory/architecture/migration-regen-after-rebase.md`), absorbing any prior one-off backfill along with everything else.

**Well-known system accounts** — non-human accounts with a deterministic ID reserved in `Humans.Domain.Constants.SystemUserIds` (GUID block `0004`). The shared gate-terminal account (`SystemUserIds.GateTerminal`) is provisioned lazily — `GateTerminalAccountSeeder` creates the User + Stub→Active Profile through the canonical application-service path the first time a ticket admin sets its password from `/Tickets/Admin/Gate` — not via `HasData` or migration SQL. Idempotent; holds no roles and no email.

**Dev-only runtime seeders** — on-demand POST endpoints on `DevSeedController`, each behind `DevAuth:Enabled` + a non-production environment check + its own policy:

| Endpoint | Policy | Seeder | What it does |
|---|---|---|---|
| `/dev/seed/budget` | `FinanceAdminOrAdmin` | `IBudgetDemoSeeder` | Demo budget year with teams, categories, and line items |
| `/dev/seed/camp-roles` | `CampAdminOrAdmin` | `DevelopmentCampRoleSeeder` | Camp role definitions and assignments |
| `/dev/seed/dashboard` | `ShiftDashboardAccess` | `DevelopmentDashboardSeeder` | Teams, humans, shifts, and signups behind the shift dashboard |
| `/dev/seed/dashboard/reset` | `AdminOnly` | `DevelopmentDashboardSeeder` | Deletes the dashboard demo rows, then reseeds |

The two dashboard endpoints are stricter than the rest: they additionally require `ASPNETCORE_ENVIRONMENT=Development`, so they never run on QA or preview. Their buttons are on `Views/ShiftDashboard/Index.cshtml`.

The budget and camp-role endpoints are reached from the admin sidebar's **Dev** group (`AdminNavTree.cs`), whose two items carry `EnvironmentGate: env => !env.IsProduction()` — so they render on local and QA but never in production.

## Guardrails for Dev Seeders

1. Never run automatically at startup, in migrations, or from recurring jobs
2. Disabled in production (environment check + config flag)
3. Require authenticated privileged user
4. Idempotent — safe to run repeatedly
5. Use existing application services where possible, not raw DB inserts
6. No production secrets or real customer data
7. External-service-dependent features use a dev-only stub (e.g., `StubTicketVendorService`)
