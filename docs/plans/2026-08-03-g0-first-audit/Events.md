# Events — G0 First Audit

Section: Events · Kind: vertical · Audited 2026-08-03 @ 5a9bbe198

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository | PASS | `reforge ownership-violations --owner Events --tables events,event_guide_settings,event_categories,event_venues,event_moderation_actions,event_favourites,event_preferences` → `0 ownership-violations`. |
| 2 | One writer-service per table (no interceptor workarounds) | PASS | `reforge injected IEventRepository` → single consumer `EventService` (`src/Humans.Application/Services/Events/EventService.cs:19`). `docs/sections/Events.md` confirms "no `SaveChangesInterceptor`... enforced by the universal `HUM0025` analyzer." |
| 3 | No EF entity leaks across the boundary | PASS | `IEventServiceRead` (cross-section surface) exposes only 3 methods returning cached view DTOs (`ApprovedEventView`, `EventGuideSettingsView`, favourite-id set) — no `Event` entities. The known #809 `EventSettings` entity belongs to Shifts, not Events (confirmed: `EventSettings*` is in the Shifts JSON block, not Events'). Events' own baseline entries (`GetCampEventAsync`, `GetEventForModerationAsync`, `GetUserEventAsync` on `ApplicationServiceEntityReadReturns.baseline.txt:10-12`) return `Event` entities from `IEventService`, but these three methods are **not** on `IEventServiceRead` — they are Events-internal (controller-only) reads, not consumed cross-section. |
| 4 | No cross-section EF joins (zero baseline entries) | PASS | Cross-section EF joins are tracked via `[Grandfathered("HUM0024", ...)]` on entity configs (there is no separate baseline file for this rule — confirmed the 5 baseline files present are `ApplicationServiceEntityReadReturns`, `DisplaySortInControllers`, `NoDestructiveMigrationOps`, `NoLinqAtDbLayer`, `NoStartupGuards`). Grep for `Grandfathered`/`Obsolete` across `Event*.cs` entities and `Data/Configurations/**/Event*.cs` configs → zero matches. Docs confirm: "Cross-domain navs — Stripped (PR #539, Stage 3)." |
| 5 | No `[Obsolete]` cross-section navs, no `[Grandfathered]`, no owned baseline rows | PARTIAL | Zero `[Obsolete]`/`[Grandfathered]` on Event* entities/configs (grep clean). **But** 3 `ApplicationServiceEntityReadReturns.baseline.txt` rows are owned by `IEventService` (lines 10-12, see #3) — these are pre-existing ratchet-baseline debt, not fresh violations, but the predicate requires zero or a queued G2 item. No G2 item currently tracks them. |
| 6 | Controllers thin — no HUM0031 grandfathers | FAIL | `EventsController.cs` has **5** `[Grandfathered("HUM0031", ...)]` methods (lines 36, 249, 429, 712, 803) — worst offenders at HUM0031 introduction (21-60 statements, cc 11-23). Confirmed by task-list Lane 2 (#857 HUM0031 burndown) as an active parallel lane tonight — does not change tonight's score. |
| 7 | `docs/sections/Events.md` exists and matches reality | PASS | Exists, current, detailed (routing, invariants, cross-section deps, architecture, T-03 caching decorator, read/write split). Matches code structure verified above. |

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repository tests use real Postgres, zero EF-InMemory | FAIL | `tests/Humans.Application.Tests/Events/EventRepositoryTests.cs` uses `.UseInMemoryDatabase(...)`. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | PASS | No `HumansDbContext` match in `Events/EventServiceTests.cs` or `Services/Events/EventServiceCalendarFeedTests.cs`. |
| 3 | Invariants/triggers each have a test (spot-check) | PASS (spot-check) | Submission window inclusivity (`IsSubmissionOpenAt_UsesInclusiveOpenAndCloseWindow`), category-slug validation (`ExcludedCategorySlugs`/`CategorySlugExistsAsync` usage), moderation transition + append (`ApplyModerationAsync_TransitionsEventAndAppendsModerationAction`), email-failure-doesn't-fail-moderation (`ApplyModerationAsync_EmailFailure_DoesNotFailModeration`) all present. Not exhaustively checked: bulk-CSV all-or-nothing invariant, `EventsFeatureFilter`/CORS gating (likely in `Integration.Tests`/Controllers, not verified here). |
| 4 | No skipped tests without an issue ref | PASS | `Skip\s*=` grep on `tests/Humans.Application.Tests/Events/` → no matches. |
| 5 | Tests grouped under the section | PASS | `tests/Humans.Application.Tests/Events/` (4 files) + `Services/Events/` (1 file) + `Integration.Tests/Services/CachingEventServiceTests.cs` (expected split: unit vs. integration project). |

## G1 Gap List

1. **HUM0031 grandfathers in `EventsController.cs` (5 instances)** — where: lines 36, 249, 429, 712, 803. Suggested fix: extract statement/branch logic into service-layer methods per the in-flight #857 lane. No-migration-needed: **y**.
2. **3 `ApplicationServiceEntityReadReturns` baseline rows on `IEventService`** (`GetCampEventAsync`, `GetEventForModerationAsync`, `GetUserEventAsync`) — where: `Humans.Application/Interfaces/Events/IEventService.cs`. These are Events-internal (not consumed cross-section, since `IEventServiceRead` doesn't expose them), so no entity-leak in practice, but they still count against the ratchet baseline. Suggested fix: return DTOs (e.g. a `EventDetailInfo`) from these controller-facing reads instead of the `Event` entity directly, then remove the 3 baseline lines. No-migration-needed: **y**.

## G3 Gap List

1. **`EventRepositoryTests.cs` uses `UseInMemoryDatabase`** — where: `tests/Humans.Application.Tests/Events/EventRepositoryTests.cs`. Suggested fix: convert to the shared Postgres fixture per #764/#766. No-migration-needed: **y**.

## G2 Queue Notes (light)

- No dead columns/tables spotted in `docs/sections/Events.md`'s data model — schema looks lean already (bare-FK pattern already applied, cross-domain navs already stripped per PR #539).
- The 3 entity-leak baseline rows (G1 gap #2) are a light G2/G1 cleanup, not a schema change.

