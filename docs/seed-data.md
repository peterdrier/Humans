# Seed Data Strategy

## Decision Guide

| Scenario | Approach |
|----------|----------|
| Bootstrap rows every environment needs | EF Core `HasData` in entity configurations |
| One-time corrective insert/update during schema evolution | Migration SQL |
| Rich demo data for local dev or preview environments | Dev-only runtime seeder |
| Test-only records | Test fixtures, not app seed data |

## Existing Patterns

**`HasData`** — system teams, shift tags, sync settings, camp settings, ticket sync state. Stable bootstrap rows with deterministic IDs, part of migrations.

**Migration SQL** — e.g., `20260311161510_SeedLeadRoleDefinitions.cs`. One-off backfills tied to schema changes.

**Dev-only runtime seeders** — on-demand endpoints behind `DevAuth:Enabled` + non-production environment check:
- `/dev/seed/budget` — creates demo budget year with teams, categories, and line items via `IBudgetService`
- `/dev/seed/tickets` — triggers a sync cycle against `StubTicketVendorService`, which returns canned sample data processed through the real `TicketSyncService` pipeline

Buttons for both are on the Dev Login page.

## Guardrails for Dev Seeders

1. Never run automatically at startup, in migrations, or from recurring jobs
2. Disabled in production (environment check + config flag)
3. Require authenticated privileged user
4. Idempotent — safe to run repeatedly
5. Use existing application services where possible, not raw DB inserts
6. No production secrets or real customer data
7. External-service-dependent features use a dev-only stub (e.g., `StubTicketVendorService`)
