# Notifications — G0 First Audit

**Section:** Notifications · **Kind:** vertical, fan-in (per `CONTEXT.md`: touched by nearly every other section, but carries no other section's logic and reaches into no other section's data — this makes it read as a **Crosscut** by the glossary definition, worth flagging for the G0 shared-contract-exception review even though the tracker currently lists it as `vertical`) · **Audited:** 2026-08-03 @ 5a9bbe198

## G1 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repo in-section | PASS | `reforge ownership-violations --owner Notifications --tables notifications,notification_recipients` → 0 violations. |
| 2 | One writer-service per table | PASS | `INotificationRepository` (Singleton, `IDbContextFactory<HumansDbContext>`) is the only non-test type touching `notifications`/`notification_recipients`. `INotificationEmitter`/`INotificationService` split exists specifically to break a DI cycle, not to create two writers — both ultimately route through the one repository. |
| 3 | No EF entity leaks across boundary | PASS — best-in-class | `NotificationRecipient.User` and `Notification.ResolvedByUser` navs were **dropped entirely** (shadow navigations, not merely `[Obsolete]`-marked). Display data resolved via `IUserService.GetByIdsAsync`. This is the end-state the other 3 table-owning sections in this batch are working toward. |
| 4 | No cross-section EF joins (zero baseline entries) | PASS | No Notifications rows in any baseline file. |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]` / baseline rows | PASS | Confirmed by predicate 3 — navs are gone, not obsoleted. No `[Grandfathered]` hits. |
| 6 | Controllers thin — no HUM0031 grandfathers | PASS | No `Grandfathered` hits on `NotificationsController.cs`. |
| 7 | `docs/sections/Notifications.md` current | PASS | Precise and current, including the "meters are computed, never stored" architectural rule and an explicit Design Rationale section with ADR references. |

## G3 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests real Postgres, zero EF-InMemory | FAIL | `tests/.../Notifications/NotificationRepositoryTests.cs:21` calls `.UseInMemoryDatabase(...)`. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | **PARTIAL** | `NotificationServiceTests.cs`, `NotificationEmitterTests.cs`, `NotificationInboxServiceTests.cs` — no `HumansDbContext` references (clean). But `CleanupNotificationsJobTests.cs:17` constructs a real `HumansDbContext` over `.UseInMemoryDatabase(...)` rather than mocking `INotificationRepository` — same anti-pattern as GoogleIntegration's job tests. |
| 3 | Invariants/triggers each have a test | PARTIAL | Not exhaustively mapped. Shared-resolution-across-recipients, actionable-cannot-be-dismissed, and the meter/no-duplicate-notification rule are documented invariants that plausibly map to `NotificationServiceTests`/`NotificationInboxServiceTests`, but no line-level confirmation done. |
| 4 | No skipped tests without an issue ref | PASS | No `Skip=` anywhere in `tests/`. |
| 5 | Tests grouped under section | PASS | `tests/Humans.Application.Tests/Notifications/**` — cleanly grouped, including the repo and job tests. |

## G1 gap list

No G1 gaps found for Notifications.

## G2 queue notes

None identified — this section is schema-clean. Worth a note for the G0 dependency-DAG pass: consider whether Notifications should be reclassified from `vertical` to `Crosscut` in the section tracker per the glossary (fan-in from nearly every section, but zero outbound section-specific logic) — doesn't change any G1–G5 predicate, just the tracker's `Kind` column.

## Verdict

`G1: met · G3: 2 gaps (+1 PARTIAL) — headline gap: NotificationRepositoryTests + CleanupNotificationsJobTests both on EF-InMemory instead of mocked interfaces / shared Postgres fixture`
