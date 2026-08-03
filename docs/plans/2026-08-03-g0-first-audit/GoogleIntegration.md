# GoogleIntegration — G0 First Audit

**Section:** GoogleIntegration · **Kind:** vertical · **Audited:** 2026-08-03 @ 5a9bbe198

## G1 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repo in-section | PASS | `reforge ownership-violations --owner GoogleIntegration --tables sync_service_settings,google_sync_outbox` → 0 violations. |
| 2 | One writer-service per table | **FAIL — corrected 2026-08-03** | `sync_service_settings` is fine (`SyncSettingsService` via `ISyncSettingsRepository`). `google_sync_outbox` is not: the original evidence named two services on one table and still scored it PASS. `GoogleSyncOutboxService` calls `AddAsync`/`AddRangeAsync`; `GoogleWorkspaceSyncService` calls `AddRangeAsync`/`RequeueAsync`/`RequeueAllFailedAsync`; and `ProcessGoogleSyncOutboxJob` mutates the same rows directly through `MarkProcessedAsync` (`:95`), `MarkPermanentlyFailedAsync` (`:117`) and `IncrementRetryAsync` (`:143`). Three write paths, so the predicate fails. (`HumansMetricsService` also injects the repository but is read-only.) |
| 3 | No EF entity leaks across boundary | PASS — **corrected 2026-08-03** | The original PARTIAL recorded `IGoogleResourceRepository` writing `google_resources` as a cross-section repository violation on the premise that the table is Teams-owned. That premise is wrong: the demolition inventory's ownership correction (`2026-08-03-demolition-inventory.md`, `google_resources` section) maps `GoogleResource`, `GoogleResourceRepository` **and** `TeamResourceService` to GoogleIntegration, and `TeamResourceService.cs:17,95` says so outright. The repository access is therefore in-section and there is no violation to schedule. The real boundary item on that table is `GoogleResourceConfiguration`'s `TeamId → teams` FK — see predicate 4. |
| 4 | No cross-section EF joins (zero baseline entries) | **FAIL — corrected 2026-08-03** | Correct that no baseline *file* exists for HUM0024 (it's attribute-allowlisted), but the conclusion drawn from that was backwards. This section has **four** cross-section FKs across three tables: `GoogleResourceConfiguration.cs:54-56` (`TeamId` → `teams`), `GoogleSyncOutboxEventConfiguration.cs:32-34` (`TeamId` → `teams`) and `:37-39` (`UserId` → `AspNetUsers`), `SyncServiceSettingsConfiguration.cs:35-37` (`UpdatedByUserId` → `AspNetUsers`). Notably **none of the four carries a `[Grandfathered(HUM0024)]` marker**, so unlike every other section in this batch these sit outside the allowlist entirely — the analyzer should be flagging them and isn't. (The `SyncServiceSettings` entry does not contradict predicate 5's "`UpdatedByUser` nav was already fully removed": the nav is gone, the physical FK constraint is not.) |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]` / baseline rows owned by section | FAIL | `NoLinqAtDbLayer.baseline.txt` has 2 rows owned by this section: `GoogleResourceRepository.cs:OrderBy#1`, `OrderBy#2`. No `[Grandfathered]` hits in section controllers/services. `SyncServiceSettings.UpdatedByUser` nav was already fully removed (typed-FK, best-practice) — not a gap. `GoogleResource.Team` live nav is Teams-owned, tracked in the doc as a Teams-side follow-up, not counted against GoogleIntegration here. |
| 6 | Controllers thin — no `[Grandfathered("HUM0031"...)]` | PASS | Grep for `Grandfathered` across `Google*.cs` controllers: no matches. |
| 7 | `docs/sections/GoogleIntegration.md` current | PASS | Extremely detailed, self-tracks its own 3 outstanding consumer-side gaps (AuditLog, Teams, Users/Profiles) with file:line precision; matches code as verified above. |

## G3 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests real Postgres, zero EF-InMemory | FAIL | `tests/.../GoogleIntegration/GoogleResourceRepositoryTests.cs:33` and `GoogleSyncOutboxRepositoryTests.cs:34` both call `.UseInMemoryDatabase(...)`. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | FAIL | `SyncSettingsServiceTests.cs:23` builds a real `HumansDbContext` (InMemory) and wraps it in `TestDbContextFactory` instead of mocking `ISyncSettingsRepository`. `ProcessGoogleSyncOutboxJobTests.cs:35-41` does the same for `IGoogleSyncOutboxRepository`/`IGoogleResourceRepository` — constructs real repos over an InMemory `HumansDbContext` rather than substituting the interfaces. |
| 3 | Invariants/triggers each have a test | PARTIAL | Not exhaustively spot-checked. Doc lists ~15 invariants (shared-drive-only, sync-mode-guards-automation, collision handling, permanent-vs-transient error classification, GoogleEmailStatus rules). Needs a dedicated invariant→test mapping pass. |
| 4 | No skipped tests without an issue ref | PASS | No `[Fact(Skip` / `[Theory(Skip` anywhere in `tests/`. |
| 5 | Tests grouped under section | PASS | All GoogleIntegration tests live under `tests/Humans.Application.Tests/GoogleIntegration/`. |

## G1 gap list

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| 2 `NoLinqAtDbLayer` baseline entries (`OrderBy`) | `src/Humans.Infrastructure/Repositories/GoogleIntegration/GoogleResourceRepository.cs` | Move ordering into service/application layer or justify + remove from baseline if DB-side ordering is required for pagination. | y |
| ~~`IGoogleResourceRepository` writes a Teams-owned table~~ — **retracted 2026-08-03** | — | Not a violation: `google_resources` is GoogleIntegration-owned (see predicate 3), so this repository access is in-section. Replaced by the FK row below. | — |
| **Added 2026-08-03:** 4 un-grandfathered cross-section FKs | `GoogleResourceConfiguration.cs:54`, `GoogleSyncOutboxEventConfiguration.cs:32,37`, `SyncServiceSettingsConfiguration.cs:35` | Two → `teams`, two → `AspNetUsers`, across three tables. None carries `[Grandfathered(HUM0024)]`, so they're invisible to the allowlist the rest of this inventory relies on — worth checking why the analyzer doesn't fire before the cuts are queued. FK cuts themselves are G2. | y (investigation); FK cuts are G2 |
| **Added 2026-08-03:** three write paths on `google_sync_outbox` | `GoogleSyncOutboxService`, `GoogleWorkspaceSyncService`, `ProcessGoogleSyncOutboxJob` (`:95,117,143`) | Consolidate enqueue/requeue/mark-processed behind `IGoogleSyncOutboxService` so the job and the workspace-sync service stop injecting `IGoogleSyncOutboxRepository` directly, or record the job's lifecycle writes as an accepted outbox-processor exception. | y |

## G2 queue notes

No dead columns/tables identified for this section in the current pass. `google_sync_outbox` table name vs `design-rules.md §8`'s stale `google_sync_outbox_events` reference should be corrected in that doc (not a G2 schema change, a doc fix).


**Added 2026-08-03 — cross-section FK cuts belong in this queue.** Retiring `[Obsolete]` navs or `[Grandfathered(HUM0024)]` markers is a code-shape change; it does **not** drop the physical constraint. Per the demolition inventory, this section owns **4** cross-section FKs across 3 tables: `google_resources.TeamId` → `teams`; `google_sync_outbox.TeamId` → `teams` and `.UserId` → `AspNetUsers`; `sync_service_settings.UpdatedByUserId` → `AspNetUsers`. Note none carries a HUM0024 marker (see predicate 4), so they are absent from the attribute-based catalog §2 of the inventory relies on. All are G2 cuts — without them listed here, a schema batch driven by this scorecard can complete while every cross-section database dependency survives.

## Verdict

`G1: 3 gaps (corrected 2026-08-03, was 2 — added: three write paths on `google_sync_outbox` and 4 un-grandfathered cross-section FKs; retracted: the `google_resources` "Teams-owned table" gap, which was based on a wrong ownership premise) · G3: 2 gaps (+1 PARTIAL) — headline gap: repo/service tests still on EF-InMemory instead of mocked interfaces / shared Postgres fixture`
