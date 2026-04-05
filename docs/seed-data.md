# Seed Data Strategy

## Purpose

This document explains how seed data should be created in Humans, and which approach to use for which kind of data.

The short version:

- Use EF Core `HasData` for tiny, foundational, well-known rows the app must always have.
- Use migration SQL for one-off backfills or transformations of existing real data.
- Use explicit dev-only runtime seeders for rich local demo data and operational feature data.

## Existing Patterns In This Repository

Humans already uses three different seed patterns:

### 1. Foundational seed data via `HasData`

Use this for stable bootstrap rows with deterministic identities.

Examples:

- System teams in [TeamConfiguration.cs](../src/Humans.Infrastructure/Data/Configurations/TeamConfiguration.cs)
- Shift tags in [ShiftTagConfiguration.cs](../src/Humans.Infrastructure/Data/Configurations/ShiftTagConfiguration.cs)
- Sync service settings in [SyncServiceSettingsConfiguration.cs](../src/Humans.Infrastructure/Data/Configurations/SyncServiceSettingsConfiguration.cs)
- Camp settings in [CampSettingsConfiguration.cs](../src/Humans.Infrastructure/Data/Configurations/CampSettingsConfiguration.cs)
- System settings in [SystemSettingConfiguration.cs](../src/Humans.Infrastructure/Data/Configurations/SystemSettingConfiguration.cs)
- Ticket sync singleton state in [TicketSyncStateConfiguration.cs](../src/Humans.Infrastructure/Data/Configurations/TicketSyncStateConfiguration.cs)

These rows are part of the app's baseline shape. They belong in migrations and should exist in every environment.

### 2. One-off backfills via migration SQL

Use this when the app already has real data and a migration needs to populate or reshape it once.

Example:

- [20260311161510_SeedLeadRoleDefinitions.cs](../src/Humans.Infrastructure/Migrations/20260311161510_SeedLeadRoleDefinitions.cs)

This is not a local demo-data mechanism. It is a schema-evolution mechanism.

### 3. Dev-only runtime seeding

Use this for local or preview convenience data that should be created only on demand.

Existing example:

- [DevLoginController.cs](../src/Humans.Web/Controllers/DevLoginController.cs)

This creates development personas dynamically so local and preview environments can log in without real Google OAuth users.

## Default Rule Going Forward

For operational, realistic, feature-level demo data, the default approach should be:

**Use an explicit dev-only runtime seeder.**

This includes data like:

- budget years, categories, and line items
- ticket orders and attendees
- realistic department/team data for local UI work
- sample operational records that exist to make screens useful in development

The first dedicated example of this pattern is the budget demo seeder:

- [DevSeedController.cs](../src/Humans.Web/Controllers/DevSeedController.cs)
- [DevelopmentBudgetSeeder.cs](../src/Humans.Web/Infrastructure/DevelopmentBudgetSeeder.cs)

The second concrete example is the ticketing demo seeder:

- [DevSeedController.cs](../src/Humans.Web/Controllers/DevSeedController.cs)
- [DevelopmentTicketSeeder.cs](../src/Humans.Web/Infrastructure/DevelopmentTicketSeeder.cs)

## Why This Is The Preferred Approach For Operational Demo Data

Operational feature data is different from bootstrap data:

- it is larger
- it is more domain-specific
- it changes more often
- it is useful mainly for local development, previews, demos, and manual verification
- it should not be inserted into production automatically

Trying to model this kind of data with `HasData` or migrations creates the wrong coupling.

## Pros

- It is opt-in. Nothing happens unless a developer explicitly runs the seeder.
- It keeps production bootstrap data separate from local demo data.
- It can use real application services and workflows instead of bypassing business logic.
- It can be idempotent, so developers can run it repeatedly without duplicate records.
- It can evolve quickly as screens and workflows change.
- It can create richer cross-entity data than `HasData` comfortably supports.
- It can be paired with dev-only stubs when a feature normally depends on external infrastructure.

## Cons

- It adds extra code paths that need guardrails.
- If misconfigured, a non-production app pointed at a real database could still write demo data.
- It is not automatically available in tests or migrations.
- It can drift from production-like reality if nobody maintains it.
- It needs explicit documentation and discoverability or people will not know it exists.
- It may need companion dev-only infrastructure so the seeded data is actually visible in the UI.

## Required Guardrails For Dev Seeders

All new dev seeders should follow these rules:

1. They must never run automatically at startup, during migration, or from recurring jobs.
2. They must be disabled in `Production`.
3. They must require an explicit enablement flag.
4. They must require an authenticated privileged user, not anonymous access.
5. They should be idempotent.
6. They should be deterministic where that improves reruns and cleanup.
7. They should call application/infrastructure services where possible, instead of duplicating business rules with raw inserts.
8. They should avoid production secrets, real customer data, or environment-specific identifiers.
9. If a feature normally depends on an external provider, the seeder may be paired with a dev-only stub service, but that stub must also be disabled in `Production`.

## Preferred Implementation Shape

For local/demo operational data, prefer this structure:

- A thin dev-only controller or command entry point in `Humans.Web`
- A dedicated seeder service that owns the data creation workflow
- Business writes delegated to existing services where practical
- Direct `DbContext` usage only for setup glue that is not yet exposed through a proper service

The budget seeder follows this shape:

- [DevSeedController.cs](../src/Humans.Web/Controllers/DevSeedController.cs) exposes the explicit endpoint
- [DevelopmentBudgetSeeder.cs](../src/Humans.Web/Infrastructure/DevelopmentBudgetSeeder.cs) owns the orchestration

The ticketing seeder follows the same shape, with one extra piece:

- [DevelopmentTicketSeeder.cs](../src/Humans.Web/Infrastructure/DevelopmentTicketSeeder.cs) seeds orders and attendees
- [StubTicketVendorService.cs](../src/Humans.Infrastructure/Services/StubTicketVendorService.cs) is a dev-only companion that lets the seeded local data drive `/Tickets` without real TicketTailor credentials

Operational seeders may also encode realistic business assumptions for local development, such as:

- sales cadence over time
- release timing patterns, such as an initial burst followed by a taper
- ticket type mix and price points
- donation and discount behavior
- VAT rules, thresholds, and other reporting-sensitive calculations

When they do, keep those assumptions aligned with the relevant feature docs and shared constants rather than scattering duplicate rules.

For example, the ticket seeder is allowed to model a plausible launch curve for local development, while still taking VAT behavior from `TicketConstants` and the ticketing feature documentation rather than hard-coding disconnected rules in multiple places.

## Decision Guide

Use this table when adding new seed data:

| Scenario | Default approach |
|----------|------------------|
| Well-known app bootstrap rows that every environment needs | EF Core `HasData` |
| Existing production data needs a one-time corrective insert/update during schema evolution | Migration SQL |
| Rich local demo data for feature development or manual QA | Dev-only runtime seeder |
| Test-only records for unit/integration tests | Test fixtures/factories, not app seed data |

## Budget Seeder Decision

The budget demo seeder was intentionally implemented as a dev-only runtime seeder rather than `HasData` or a migration because:

- budgets are operational data, not app bootstrap data
- the data is useful for local browsing and demos, not for every environment
- it should never run implicitly in production
- it benefits from using the real budget services and app behavior

## Future Guidance

Future operational demo datasets should follow the same pattern as the budget seeder unless there is a strong reason not to.

Examples:

- ticket sales demo data
- realistic attendee datasets
- richer department or finance walkthrough datasets

If a future seeder would be dangerous to expose over HTTP, use the same dev-only principles behind a local command or other explicit development-only trigger instead.

If a future operational dataset depends on an external API to render correctly, prefer a local stub companion over weakening production-facing configuration rules.
