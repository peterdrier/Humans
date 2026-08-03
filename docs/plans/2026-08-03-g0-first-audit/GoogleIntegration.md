# GoogleIntegration — G0 First Audit

**Section:** GoogleIntegration · **Kind:** vertical · **Audited:** 2026-08-03 @ 5a9bbe198

## G1 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repo in-section | PASS | `reforge ownership-violations --owner GoogleIntegration --tables sync_service_settings,google_sync_outbox` → 0 violations. |
| 2 | One writer-service per table | **FAIL — corrected 2026-08-03** | `sync_service_settings` is fine (`SyncSettingsService` via `ISyncSettingsRepository`). `google_sync_outbox` is not: the original evidence named two services on one table and still scored it PASS. `GoogleSyncOutboxService` calls `AddAsync`/`AddRangeAsync`; `GoogleWorkspaceSyncService` calls `AddRangeAsync`/`RequeueAsync`/`RequeueAllFailedAsync`; and `ProcessGoogleSyncOutboxJob` mutates the same rows directly through `MarkProcessedAsync` (`:95`), `MarkPermanentlyFailedAsync` (`:117`) and `IncrementRetryAsync` (`:143`). Three write paths, so the predicate fails. (`HumansMetricsService` also injects the repository but is read-only.) |
| 3 | No EF entity leaks across boundary | PARTIAL | `IGoogleResourceRepository` performs narrow writes into `google_resources`, a **Teams-owned** table (per `docs/sections/GoogleIntegration.md` and `Teams.md`), for reconciliation-loop atomicity. Documented and scoped ("all broader reads/writes route through `ITeamResourceService`"), but it is a repository in this section touching another section's table — flag for the Teams-side audit as the mirror finding. |
| 4 | No cross-section EF joins (zero baseline entries) | PASS | No `tests/.../Architecture/Baselines/*` file for cross-section-EF-join exists at all (analyzer-enforced with zero suppressions); no `SuppressMessage` hits in `src/Humans.Infrastructure/Repositories/GoogleIntegration/**`. |
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
| `IGoogleResourceRepository` writes a Teams-owned table | `src/Humans.Infrastructure/Repositories/GoogleIntegration/GoogleResourceRepository.cs` | Cross-reference with Teams' own G1 audit; either fold into `ITeamResourceService` narrow-write surface or keep documented exception. | y (design decision, not schema) |
| **Added 2026-08-03:** three write paths on `google_sync_outbox` | `GoogleSyncOutboxService`, `GoogleWorkspaceSyncService`, `ProcessGoogleSyncOutboxJob` (`:95,117,143`) | Consolidate enqueue/requeue/mark-processed behind `IGoogleSyncOutboxService` so the job and the workspace-sync service stop injecting `IGoogleSyncOutboxRepository` directly, or record the job's lifecycle writes as an accepted outbox-processor exception. | y |

## G2 queue notes

No dead columns/tables identified for this section in the current pass. `google_sync_outbox` table name vs `design-rules.md §8`'s stale `google_sync_outbox_events` reference should be corrected in that doc (not a G2 schema change, a doc fix).

## Verdict

`G1: 3 gaps (corrected 2026-08-03, was 2 — added: three write paths on `google_sync_outbox`) · G3: 2 gaps (+1 PARTIAL) — headline gap: repo/service tests still on EF-InMemory instead of mocked interfaces / shared Postgres fixture`
