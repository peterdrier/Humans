# Notifications — G0 First Audit

**Section:** Notifications · **Kind:** vertical · **Audited:** 2026-08-03 @ 5a9bbe198

**Kind note — corrected 2026-08-03:** the original pass classified Notifications as a
candidate `Crosscut` on the premise that it "carries no other section's logic and reaches
into no other section's data." **False premise** — `NotificationMeterProvider`
(`src/Humans.Application/Services/Notifications/NotificationMeterProvider.cs:36-42`)
constructor-injects `IUserServiceRead`, `IGoogleSyncServiceRead`, `ITeamServiceRead`,
`ITicketSyncService` (full-service, not the read cut), `IApplicationServiceRead`
(Governance), and `ICampServiceRead` — it reads live data out of five other sections'
tables via their services on every meter-count call. This is exactly the read side of the
4 section-level-only cycles the dependency DAG's own Cycles section already documents
(Teams/Camps/Governance/GoogleIntegration ↔ Notifications, all rooted in
`NotificationMeterProvider`). Notifications stays `vertical` — it does reach into other
sections' data, just through their read interfaces rather than raw EF, which is the
*correct* pattern, not evidence of being a crosscut.

## G1 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repo in-section | PASS | `reforge ownership-violations --owner Notifications --tables notifications,notification_recipients` → 0 violations. |
| 2 | One writer-service per table | **FAIL — corrected 2026-08-03** | `INotificationRepository` is the only *repository* touching `notifications`/`notification_recipients`, but three different *services* call mutating repo methods against those tables: `NotificationEmitter.SendAsync` → `repo.AddRangeAsync` (`NotificationEmitter.cs:97`); `NotificationService.SendToRoleAsync` → `repo.AddAsync` (`NotificationService.cs:103`); `NotificationInboxService` → `repo.ResolveAsync`/`DismissAsync`/`MarkReadAsync`/`BulkResolveAsync`/`BulkDismissAsync` (`NotificationInboxService.cs:98,108,118,138,148`). Plus a **fourth** write path (added 2026-08-03): `CleanupNotificationsJob` injects `INotificationRepository` directly and calls `DeleteResolvedOlderThanAsync` (`:43`), `DeleteUnresolvedInformationalOlderThanAsync` (`:46`) and `DeleteUnresolvedBySourcesAsync` (`:49`) against the same tables. The predicate is about writer-*services*, not writer-repositories — one repository funneling four callers' writes is the #751 pattern this predicate is meant to catch, not satisfy. |
| 3 | No EF entity leaks across boundary | PASS — best-in-class | `NotificationRecipient.User` and `Notification.ResolvedByUser` navs were **dropped entirely** (shadow navigations, not merely `[Obsolete]`-marked). Display data resolved via `IUserService.GetByIdsAsync`. This is the end-state the other 3 table-owning sections in this batch are working toward. |
| 4 | No cross-section EF joins (zero baseline entries) | **FAIL — corrected 2026-08-03** | No baseline rows, but HUM0024 is attribute-allowlisted so that can't establish a pass — and the next predicate already names the joins: `NotificationConfiguration.cs:8-12` and `NotificationRecipientConfiguration.cs:8-12` both carry active `[Grandfathered(HUM0024)]` markers over typed `HasOne<User>()` FKs to `AspNetUsers`. |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]` / baseline rows | **FAIL — corrected 2026-08-03** | Predicate 3's "navs are gone" is true for the C# nav *properties*, but the EF **configuration classes** still carry the marker: `NotificationConfiguration.cs:8-12` and `NotificationRecipientConfiguration.cs:8-12` both carry `[Grandfathered(ruleId: "HUM0024", justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.", ...)]` on their typed `HasOne<User>()` FK wiring — the same pattern flagged as a gap on every other section in this batch (Campaigns, Feedback, etc.). Original pass conflated "no leaked nav property" with "no Grandfathered attribute" — they're separate predicates. |
| 6 | Controllers thin — no HUM0031 grandfathers | PASS | No `Grandfathered` hits on `NotificationsController.cs`. |
| 7 | `docs/sections/Notifications.md` current | PASS | Precise and current, including the "meters are computed, never stored" architectural rule and an explicit Design Rationale section with ADR references. |

## G3 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests real Postgres, zero EF-InMemory | FAIL | `tests/.../Notifications/NotificationRepositoryTests.cs:21` calls `.UseInMemoryDatabase(...)`. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | **FAIL — corrected 2026-08-03** | The "clean" reading of the three service-test files was a false negative. `NotificationServiceTests.cs`, `NotificationEmitterTests.cs` **and** `NotificationInboxServiceTests.cs` each declare a real `HumansDbContext`, configure it with `.UseInMemoryDatabase(...)`, and construct a concrete `NotificationRepository` — the same anti-pattern as `CleanupNotificationsJobTests.cs:17`. All **four** files violate this predicate; converting only the job test would leave it failing. |
| 3 | Invariants/triggers each have a test | PARTIAL | Not exhaustively mapped. Shared-resolution-across-recipients, actionable-cannot-be-dismissed, and the meter/no-duplicate-notification rule are documented invariants that plausibly map to `NotificationServiceTests`/`NotificationInboxServiceTests`, but no line-level confirmation done. |
| 4 | No skipped tests without an issue ref | PASS | No `Skip=` anywhere in `tests/`. |
| 5 | Tests grouped under section | PASS | `tests/Humans.Application.Tests/Notifications/**` — cleanly grouped, including the repo and job tests. |

## G1 gap list

1. **Four write paths on one repository** (predicate 2; corrected 2026-08-03, was three) — `NotificationEmitter.SendAsync`, `NotificationService.SendToRoleAsync`, `NotificationInboxService`'s resolve/dismiss/mark-read/bulk methods, **and `CleanupNotificationsJob`'s three delete calls**, all mutating `notifications`/`notification_recipients`. Consolidating only the three services would leave the daily cleanup job bypassing the owning service and the predicate still failing. Where: `src/Humans.Application/Services/Notifications/{NotificationEmitter,NotificationService,NotificationInboxService}.cs`. The split exists to break a real DI cycle concern, which is a legitimate reason — but it's still a #751-pattern gap as written, not a pass; needs an explicit call on whether this is an accepted exception (like the read/write split elsewhere) or should consolidate. No-migration-needed: y.
2. **HUM0024 cross-section EF join grandfathers** (predicate 5) — `NotificationConfiguration.cs:8-12`, `NotificationRecipientConfiguration.cs:8-12`, typed `HasOne<User>()` FKs to `AspNetUsers`. Same pattern as every other table-owning section in this batch; no queued G2 item beyond the generic doc anchor. No-migration-needed: y (pending liveness verification).

## G3 gap list

1. **`NotificationRepositoryTests.cs:21` on EF-InMemory** — convert to the shared Postgres fixture (#764/#766). No-migration-needed: y.
2. **All four service/job tests build a real `HumansDbContext` over `UseInMemoryDatabase` with a concrete `NotificationRepository`** — `NotificationServiceTests.cs`, `NotificationEmitterTests.cs`, `NotificationInboxServiceTests.cs`, `CleanupNotificationsJobTests.cs:17`. (Corrected 2026-08-03: the first three were originally read as clean, so the remediation list named only the job test — converting just that one would leave G3.2 failing.) Convert all four to `Substitute.For<INotificationRepository>()` per #766. No-migration-needed: y.

## G2 queue notes

The 2 HUM0024 grandfathers above are this section's G2 demolition candidate, same shape as Campaigns/Feedback/etc. — file alongside those if a tracked issue doesn't exist yet.

**Kind reclassification — retracted:** the original note suggesting Notifications move to `Crosscut` per the glossary is retracted — see the corrected Kind note above. `NotificationMeterProvider` does carry real outbound section-specific logic (live reads into 5 other sections), so the "zero outbound logic" premise for a Crosscut reclassification doesn't hold. Stays `vertical`.


**Added 2026-08-03 — cross-section FK cuts belong in this queue.** Retiring `[Obsolete]` navs or `[Grandfathered(HUM0024)]` markers is a code-shape change; it does **not** drop the physical constraint. Per the demolition inventory, this section owns **2** cross-section FKs across 2 tables: `notifications` and `notification_recipients` → `AspNetUsers`, via the two HUM0024-grandfathered configurations already listed as G1 gap 2. All are G2 cuts — without them listed here, a schema batch driven by this scorecard can complete while every cross-section database dependency survives.
