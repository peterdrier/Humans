# Debug — G0 First Audit

Kind: horizontal · Audited 2026-08-03 @ 5a9bbe198

Scope note: Debug is **not** a distinct entry in `reforge.surface-score.json` — it lives inside the "Platform" catch-all. No `docs/sections/Debug.md` exists. Scope taken as `DebugController.cs` (the `/Debug/*` diagnostics pages) as the section's core, plus `DevLoginController.cs`/`DevSeedController.cs` (dev-only tooling under `/DevLogin`, `/DevSeed`) as adjacent dev-tools controllers surfaced by the same Platform bucket — audited separately below since they have materially different crosscut characteristics.

## `DebugController` — the diagnostics/`/Debug/*` core

### G1 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository in-section | **N/A — owns zero tables** | `DebugController` (`src/Humans.Web/Controllers/DebugController.cs`) has no repository dependency at all. Its data sources are in-process singletons: `InMemoryLogSink.Instance`, `OperationTimingRegistry.Instance`, `QueryStatistics`, `ICacheStatsProvider`, `IClientStatsTracker`, `IHttpStatusTracker`, `ConfigurationRegistry`. This is itself the G1 finding, not a blocker: Debug is correctly a pure read-only diagnostics surface over other horizontals' in-memory telemetry, never touching a DB table directly (its one DB-adjacent action, `DbVersion`, delegates to `IAdminDatabaseDiagnosticsService`, which is Platform's own migration-status reader, not a section table). |
| 2 | One writer-service per table | N/A | No owned tables (see #1). The one write action (`ClearHangfireLocks`) delegates entirely to `IAdminDatabaseDiagnosticsService.ClearHangfireLocksAsync`, a Platform-owned diagnostic op, not a vertical-section table write. |
| 3 | No EF entity leaks across the boundary | PASS | All view models are Web-layer DTOs (`HttpErrorsViewModel`, `DbStatsViewModel`, `CacheStatsViewModel`, `ClientStatsViewModel`, `TimingsViewModel`) — no domain entities returned. |
| 4 | No cross-section EF joins (zero baseline entries) | PASS | Grepped all 5 baseline files under `tests/Humans.Application.Tests/Architecture/Baselines/` for `Debug` — zero hits. |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]` / baseline rows | PASS | Grep of `DebugController.cs` for `Grandfathered`/`[Obsolete]` — zero matches. |
| 6 | Controllers thin — no HUM0031 grandfathers | PASS | Same grep — zero matches. Note: several methods build moderately complex view-model projections inline (e.g. `CacheStats`, `ClientStats`) but stay within formatting/shaping, no business logic. |
| 7 | `docs/sections/Debug.md` exists and matches reality | **FAIL** | No such file exists. Given Debug is a real horizontal with its own routes, actors (`AdminOnly` policy), and crosscut constraints, it warrants a minimal section doc — even the horizontal sections (AuditLog, Auth) are expected to have one per the gate ladder's "per-section ladder" including horizontals explicitly listed in the tracker table. |

**Crosscut check (horizontal-specific, per `peters-hard-rules.md` — horizontals may not reach into vertical sections' logic):** PASS for `DebugController` itself. `reforge dependencies DebugController` → 9 constructor params; only one (`IUserServiceRead userService`) touches a vertical-section contract, and it's the read-only shared-contract exception (`IUserServiceRead` is explicitly one of the allowed exceptions per the Q3 plan's "shared-contract exceptions" list). All other dependencies are Platform-owned (`IClientStatsTracker`, `IHttpStatusTracker`, `ConfigurationRegistry`, `QueryStatistics`, `ICacheStatsProvider`, `IAdminDatabaseDiagnosticsService`) or ASP.NET framework types.

### G3 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repository tests on real Postgres, zero EF-InMemory | N/A | No repository (see G1 #1). |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | N/A | No service layer of its own — controller calls straight into Platform singletons/interfaces. |
| 3 | Invariants/triggers each have a test | **FAIL** | No invariants are documented (no section doc — see G1 #7), and no dedicated `DebugControllerTests.cs` was found anywhere under `tests/` (confirmed by grep across the whole `tests/` tree for `DebugController` — the only Debug-adjacent hits were `UsersAdminDebugControllerTests.cs`, which tests an unrelated `UsersAdminDebugController` in the Users section, a naming coincidence, not this controller). |
| 4 | No skipped tests without an issue ref | N/A | No tests exist to check. |
| 5 | Tests grouped under the section | **FAIL** | Zero tests for `DebugController` exist at all — nothing to group. |

## `DevLoginController` / `DevSeedController` — adjacent dev-tools controllers

Flagged separately: these are gated behind `IWebHostEnvironment`/Development-only checks (dev-persona login + fixture seeding), a different concern from `/Debug/*` diagnostics, and their dependency shape is materially different.

- **`DevSeedController`** (`src/Humans.Web/Controllers/DevSeedController.cs`): depends on `IUserServiceRead` (read-only, fine) plus `IServiceProvider serviceProvider` — a service-locator escape hatch used to resolve section services dynamically for seeding. This is architecturally muddier than a typed dependency list but is a known, deliberate pattern for a dev-only fixture seeder, not a production crosscut violation in the same sense as a controller silently reaching into another section's write path at runtime.
- **`DevLoginController`** (`src/Humans.Web/Controllers/DevLoginController.cs`): depends on `UserManager<User>`, `SignInManager<User>`, **`IUserEmailService`** (the full read/write Users-section service, not `IUserServiceRead`), and `DevPersonaSeeder`. This **does** fail the horizontal crosscut rule as literally stated ("horizontals are strictly forbidden from referencing vertical sections beyond their current state") — `IUserEmailService` is a write-capable interface, not the read-only exception. In practice this is dev-only login tooling gated by `IWebHostEnvironment env`, structurally similar to how test/seed fixtures always need broader write access than production code. **Flagging as an open scope question rather than guessing:** is `DevLogin`/`DevSeed` considered part of the "Debug" section for gate purposes, or a separate dev-tooling concern outside the G1–G6 ladder entirely (analogous to test infrastructure)? This is exactly the kind of ambiguity the G0 "section inventory frozen" checklist item exists to resolve — recommend Peter/the tracker decide explicitly rather than this audit assuming either way.
- Both controllers have test coverage (`DevSeedControllerTests.cs` confirmed; `HumansWebApplicationFactory.cs` references `DevLoginController`/`DevSeedController` for test sign-in infrastructure), so if these are pulled into Debug's scope, G3 predicate 3/5 would look considerably healthier than for `DebugController` alone.

## G1 gap list

1. **No `docs/sections/Debug.md`** — write a minimal section doc: concepts (admin diagnostics surface), routes (`/Debug/Logs`, `/HttpErrors`, `/Maintenance`, `/Configuration`, `/DbVersion`, `/DbStats`, `/CacheStats`, `/ClientStats`, `/Timings`, `/FormatGallery`, `/Translations`), actors (`AdminOnly` only), the "owns zero tables, pure telemetry read + one delegated Hangfire-lock-clear write" invariant, and the crosscut rule it must keep honoring. No migration needed (y).
2. **Section-boundary ambiguity: does "Debug" include `DevLogin`/`DevSeed`?** — needs an explicit decision recorded in the G0 section-inventory step (Q3 plan's "Section inventory frozen: the tracker table below confirmed as the canonical section list"). If yes, `DevLoginController`'s `IUserEmailService` dependency is a crosscut violation to queue for G1 fix (route dev-login through `IUserServiceRead` plus a narrower seeding-specific read/write surface, or explicitly grandfather it as dev-tooling exempt). No migration needed (y).

## G3 gap list

1. **Zero tests for `DebugController`** — add a `DebugControllerTests.cs` (or controller-integration test) covering at minimum: `AdminOnly` authorization is enforced, `DbVersion` stays `[AllowAnonymous]` intentionally (documented as deliberate — "only migration names + counts, no sensitive data"), and the sensitive-value masking logic in `Configuration()` (`IsSensitive` → first-4-chars-then-mask). No migration needed (y).

## G2 queue notes (light)

- Nothing to demolish — Debug owns no tables/columns.
- If `DevLogin`/`DevSeed` are confirmed in-scope, the `IUserEmailService` dependency in `DevLoginController` is a candidate for narrowing once the section boundary decision lands (not a G2/schema item — it's a G1 dependency-shape fix).

## Verdict

`G1: 2 gaps (DebugController) · G3: 1 gap (DebugController)` — plus one unresolved scope question (DevLogin/DevSeed inclusion) that should be settled before this section's gap count is treated as final.
