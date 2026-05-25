# Debug — Section Invariants

Developer/diagnostics section. Admin-only pages exposing operational insight that no domain section owns. Owns no tables.

## Concepts

- The **Debug** section is the developer/diagnostics area: admin-only pages surfacing operational insight (client demographics, request health, and — as they migrate — logs and cache/db stats) that belongs to no domain section. **Client stats** is its first page.
- It is the forward home for "any tool a developer wants." The legacy equivalents on `/Admin/*` (Logs, DB stats, Cache stats, Configuration, Maintenance) are frozen and move here only when that refactor is separately scoped — not as part of unrelated work.
- The section owns no domain data. The figures it shows come from process-local, in-memory trackers that reset on every restart/redeploy.

## Data Model

This section owns no entities. Displayed data comes from in-memory, process-local singletons (`IClientStatsTracker`, `IHttpStatusTracker`) — not persisted, lost on restart.

## Routing

All pages live under `/Debug` on `DebugController`, every action `AdminOnly`. Pages sit at `/Debug/<Page>` directly (e.g. `/Debug/ClientStats`), **not** `/Debug/Admin/*`: the whole section is admin-gated with no user-facing pages, so there is no public-vs-admin split to disambiguate. (The `/<Section>/Admin/*` shape in [`../../memory/architecture/no-admin-url-section.md`](../../memory/architecture/no-admin-url-section.md) exists to separate admin actions from public ones inside a *mixed* section; Debug is admin-only end to end.)

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Admin | Full access to every Debug page |
| All other roles | None — no access to any `/Debug/*` page |

## Invariants

- Every Debug page requires `PolicyNames.AdminOnly` (class-level `[Authorize]` on `DebugController`).
- Debug owns no domain data; its state is the in-memory trackers, which are process-local and reset on restart/redeploy.
- New developer/diagnostics pages are added here (`/Debug/*`), never under the legacy `/Admin/*` (see [`../../memory/architecture/debug-section.md`](../../memory/architecture/debug-section.md)).
- Debug pages are read-only — they never mutate the database or domain state.

## Negative Access Rules

- A non-Admin user **cannot** reach any `/Debug/*` page — `[Authorize(Policy = PolicyNames.AdminOnly)]` on `DebugController` rejects them.
- Debug pages **cannot** write to the database or change domain state — they are pure read/diagnostics surfaces.

## Triggers

None — Debug pages are read-only. The trackers they display are fed passively by `ClientStatsMiddleware` (page views) and a `MeterListener` over the ASP.NET Core hosting meter (status codes); Debug actions write nothing.

## Cross-Section Dependencies

None — Debug consumes only the in-memory telemetry trackers (`IClientStatsTracker`, `IHttpStatusTracker`, registered in `TelemetryInfrastructureExtensions`), not any domain section's service or tables.

## Architecture

**Owning services:** None — controller-only over in-memory telemetry trackers.
**Owned tables:** None.
**Status:** (A) Migrated — greenfield (PR #708 / `browser-stats`, 2026-05-22).

- `DebugController` lives in `Humans.Web/Controllers`; it consumes `IClientStatsTracker` and `IHttpStatusTracker`.
- **Decorator decision — no caching decorator.** Owns no data; the trackers are already in-memory singletons.
- **Cross-domain navs:** N/A — owns no entities.
- **Architecture test:** N/A — no service/repository layer to pin. Page access is class-level `[Authorize(Policy = PolicyNames.AdminOnly)]`.
