# section-doctor — Notifications — 2026-09-30

- Invocation: unattended daily routine, no arguments (Phase 8 skipped per routine prompt)
- Anchor commit: `f11c2bcc7` (origin/main at branch point); branch `section-doctor/2026-09-30T011604Z`.
- Budget: 2.5h.
- PR: peterdrier/Humans#1866

## Assessment summary

Second run on Notifications (previous: 2026-08-26). The target was regenerated from code; it moved where the section moved (the resolver is gone, the inbox read surface for Backdoor is new) and was otherwise right. The headline is a row-shape collapse: inbox reads loaded every co-recipient to render one name, and two row shapes carried fields no page renders. The rest is doc and comment drift — a stale consumer list, a cache-ownership map pointing at the bell component, result semantics that said "returns false" — plus tests for the role fan-out's preference filter and the non-recipient bulk paths.

## Findings

1. Inbox and popup reads include every co-recipient and look up all their names; only the resolver's name renders.
2. The row view model duplicated the service DTO; its `ActionLabel`, `Source` and `IsRead` were never rendered, and the controller localised a default label nothing showed.
3. `Body`, `ActionLabel` and `TargetGroupName` are accepted and stored (Body also exported and searched), but no page renders any of them since the slim-row change.
4. `ActionableCount` duplicated `Actionable.Count` on the popup result and view model.
5. The Contracts csproj consumer list was wrong and carried a "later pass" plan and Hangfire narrative.
6. `service-data-access-map.md` said the bell view component owns the badge cache; the section's `data-access.md` badge row left Read/Write blank.
7. Stale doc comments: Resolve/Dismiss "returns false", popup read "ordered newest first", click-through missing the no-URL case, `SectionJobs` naming one of three purges.
8. Provenance and prior-state narration across docs, comments, csproj and test headers.
9. `ReassignRecipientsToUserAsync` took an unused `updatedAt`.
10. Role fan-out's preference filter and all-suppressed path, and dismiss/bulk actions by a non-recipient, had no tests.
11. `SendToRoleAsync` re-implements the emitter's filter/persist/evict loop.
12. Inbox-service tests re-run repository paths the repository tests already pin.
13. `G5-SECTION-TEMPLATE.md` names the deleted recipient resolver.
14. Resource keys use `Notification_`, not the section-name prefix (standing backlog).
15. The architecture test pinning `NotificationInboxService`'s constructor shape is analyzer-shaped.
16. Carry-forward items 1, 2, 18, 19 and 20 from 2026-08-26 are still open; its item 15 (resolver) is resolved.
17. Open issues touching the section: nobodies-collective/Humans#852 and peterdrier/Humans#1626.
18. Click-through returns 404 for a found row with no action URL; no page links such a row.
19. Two more stale comments, found by the coverage pass: `INotificationAutoResolve` counted the inbox service's other members wrongly, and two test constructors placed `communication_preferences` on a "main pile" that no longer exists.

Independence check: pass — findings 1, 2 and 11 come from the target's row-shape and one-dispatch-path lines, finding 3 from the spec-vs-reality read.

## Debt verified

- The section carries no debt.yml; no in-section rows to verify.
- Off-section ledger rows naming the section (checked by the Inbox thread): the HUM0028 invalidator row and CENTRAL-45 still hold; CENTRAL-58 left unverified. Not this section's to close.

## Worked

- 1 — inbox and popup reads load only the resolver's name (reviewed, APPROVE).
- 2, 4 — one row shape between service and view; the popup reads its count off the list (reviewed, REJECT then APPROVE after two fixes).
- 5, 6, 7, 8 — comments and docs corrected; provenance cut. The section doc's `**Status:**` line keeps its issue refs because `SECTION-TEMPLATE.md` asks for them.
- 9 — unused parameter cut with finding 1.
- 15 — constructor-shape architecture test deleted at Peter's ruling; its file held no other test.
- 19 — both comments corrected; the tests' `_dbContext` field name, which misleads the same way, is left as is.
- 10 — tests added; the preference tests fail when the filter is disabled. The dismiss test pins today's Forbidden answer, which 2026-08-26 finding 19 may change.

## Skipped

- 3 — Peter: leave for now.
- 17 — Peter: keep both issues open.
- 11 — the approved Notification Board redesign replaces both dispatch loops.
- 12 — optional; not worth a reviewer round this run.
- 13 — a past-tense narrative in a template, not a claim about the tree.
- 14 — standing backlog; `resource-key-prefix-matches-section` forbids backfill as a side effect.
- 16 — already queued in the 2026-08-26 run file; not re-asked.
- 18 — unreachable from the pages; noted, not changed.
- Backdoor passed over as blocked (nobodies-collective/Humans#1860).
- The harness named its own claude/ branch as the development branch; the run used `section-doctor/2026-09-30T011604Z` as the skill's mechanics and the routine prompt require.

## Retro

**What the selector got wrong.** Nothing. Notifications was due, small, and the previous target gave the run a diff to read against.

**Wasted motion.** The first full-suite run failed in two untouched sections with Razor parse errors; the build servers were wedged, and `dotnet build-server shutdown` cleared it. A rerun of an unchanged-tree failure should reset build servers before anything else. The prose gate also refused a table row over a column width, costing a round.

**What striking revealed.** The collapse reviewer caught a claim this run wrote: that `ActionLabel` is exported. It is not; the assessment had carried the same error into finding 3's wording. Writing a doc line from the finding rather than from the export code is how it slipped.

**What the target diff says.** The section moved: the recipient resolver is gone and the Backdoor read surface arrived, and the regenerated target records both. Nothing in the earlier target was wrong in intent; its line cites drifted and were re-traced.

## Needs Peter

- [x] 3 — Render `Body` (and `ActionLabel`) on the row, or stop accepting, storing and searching them? Peter: leave for now.
- [x] 15 — Move the constructor-shape architecture test to an analyzer, or delete it? Peter: delete; applied.
- [x] 17 — Keep nobodies-collective/Humans#852 and peterdrier/Humans#1626 open as they stand? Peter: keep both.

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.Notifications.Contracts/Humans.Notifications.Contracts.csproj` | changed |
| `src/Sections/Humans.Notifications.Contracts/INotificationAutoResolve.cs` | changed |
| `src/Sections/Humans.Notifications.Contracts/INotificationEmitter.cs` | reviewed |
| `src/Sections/Humans.Notifications.Contracts/INotificationRetention.cs` | reviewed |
| `src/Sections/Humans.Notifications.Contracts/INotificationService.cs` | reviewed |
| `src/Sections/Humans.Notifications.Contracts/NotificationClass.cs` | reviewed |
| `src/Sections/Humans.Notifications.Contracts/NotificationPriority.cs` | reviewed |
| `src/Sections/Humans.Notifications.Contracts/NotificationSource.cs` | reviewed |
| `src/Sections/Humans.Notifications/Contracts/INotificationInboxRead.cs` | reviewed |
| `src/Sections/Humans.Notifications/Controllers/NotificationsController.cs` | changed |
| `src/Sections/Humans.Notifications/Data/Configurations/NotificationConfiguration.cs` | reviewed |
| `src/Sections/Humans.Notifications/Data/Configurations/NotificationRecipientConfiguration.cs` | reviewed |
| `src/Sections/Humans.Notifications/Data/INotificationRepository.cs` | changed |
| `src/Sections/Humans.Notifications/Data/Migrations/20260809032723_BaselineNotifications.Designer.cs` | generated |
| `src/Sections/Humans.Notifications/Data/Migrations/20260809032723_BaselineNotifications.cs` | reviewed |
| `src/Sections/Humans.Notifications/Data/Migrations/NotificationsDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.Notifications/Data/NotificationRepository.cs` | changed |
| `src/Sections/Humans.Notifications/Data/NotificationsDbContext.cs` | changed |
| `src/Sections/Humans.Notifications/Data/NotificationsDbContextFactory.cs` | reviewed |
| `src/Sections/Humans.Notifications/Docs/Notifications.md` | changed |
| `src/Sections/Humans.Notifications/Docs/authorization.md` | reviewed |
| `src/Sections/Humans.Notifications/Docs/data-access.md` | changed |
| `src/Sections/Humans.Notifications/Docs/features/notification-api.md` | reviewed |
| `src/Sections/Humans.Notifications/Docs/features/notification-board.md` | reviewed |
| `src/Sections/Humans.Notifications/Docs/features/notification-inbox.md` | changed |
| `src/Sections/Humans.Notifications/Docs/health.md` | changed |
| `src/Sections/Humans.Notifications/Domain/Notification.cs` | reviewed |
| `src/Sections/Humans.Notifications/Domain/NotificationRecipient.cs` | reviewed |
| `src/Sections/Humans.Notifications/Humans.Notifications.csproj` | changed |
| `src/Sections/Humans.Notifications/Jobs/CleanupNotificationsJob.cs` | reviewed |
| `src/Sections/Humans.Notifications/Models/NotificationsViewModels.cs` | changed |
| `src/Sections/Humans.Notifications/NotificationsResource.ca.resx` | reviewed |
| `src/Sections/Humans.Notifications/NotificationsResource.cs` | reviewed |
| `src/Sections/Humans.Notifications/NotificationsResource.de.resx` | reviewed |
| `src/Sections/Humans.Notifications/NotificationsResource.es.resx` | reviewed |
| `src/Sections/Humans.Notifications/NotificationsResource.fr.resx` | reviewed |
| `src/Sections/Humans.Notifications/NotificationsResource.it.resx` | reviewed |
| `src/Sections/Humans.Notifications/NotificationsResource.resx` | reviewed |
| `src/Sections/Humans.Notifications/Properties/AssemblyInfo.cs` | reviewed |
| `src/Sections/Humans.Notifications/Section.cs` | changed |
| `src/Sections/Humans.Notifications/SectionChrome.cs` | reviewed |
| `src/Sections/Humans.Notifications/SectionJobs.cs` | changed |
| `src/Sections/Humans.Notifications/Services/Dtos/NotificationMeter.cs` | reviewed |
| `src/Sections/Humans.Notifications/Services/INotificationInboxService.cs` | changed |
| `src/Sections/Humans.Notifications/Services/NotificationEmitter.cs` | reviewed |
| `src/Sections/Humans.Notifications/Services/NotificationInboxRead.cs` | reviewed |
| `src/Sections/Humans.Notifications/Services/NotificationInboxService.cs` | changed |
| `src/Sections/Humans.Notifications/Services/NotificationMeterProvider.cs` | reviewed |
| `src/Sections/Humans.Notifications/Services/NotificationService.cs` | changed |
| `src/Sections/Humans.Notifications/Services/NotificationSourceMapping.cs` | reviewed |
| `src/Sections/Humans.Notifications/ViewComponents/NotificationBellViewComponent.cs` | reviewed |
| `src/Sections/Humans.Notifications/Views/Notifications/Index.cshtml` | reviewed |
| `src/Sections/Humans.Notifications/Views/Notifications/_NotificationPopup.cshtml` | changed |
| `src/Sections/Humans.Notifications/Views/Notifications/_NotificationRow.cshtml` | changed |
| `src/Sections/Humans.Notifications/Views/Shared/Components/NotificationBell/Default.cshtml` | reviewed |
| `src/Sections/Humans.Notifications/Views/_ViewImports.cshtml` | reviewed |
| `tests/Humans.Notifications.Tests/Controllers/NotificationsControllerTests.cs` | changed |
| `tests/Humans.Notifications.Tests/Enums/EnumStringStabilityTests.cs` | changed |
| `tests/Humans.Notifications.Tests/Humans.Notifications.Tests.csproj` | reviewed |
| `tests/Humans.Notifications.Tests/Services/NotificationEmitterTests.cs` | changed |
| `tests/Humans.Notifications.Tests/Services/NotificationInboxReadTests.cs` | reviewed |
| `tests/Humans.Notifications.Tests/Services/NotificationInboxServiceTests.cs` | changed |
| `tests/Humans.Notifications.Tests/Services/NotificationMeterProviderTests.cs` | reviewed |
| `tests/Humans.Notifications.Tests/Services/NotificationRepositoryTests.cs` | changed |
| `tests/Humans.Notifications.Tests/Services/NotificationRetentionTests.cs` | reviewed |
| `tests/Humans.Notifications.Tests/Services/NotificationServiceTests.cs` | changed |
| `tests/Humans.Notifications.Tests/TestInfrastructure.cs` | changed |
| `tests/Humans.Notifications.Tests/ViewComponents/NotificationBellViewComponentTests.cs` | reviewed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | main session | 1, 2, 4, 9, 11 |
| Behavior & bugs | main | main session | 3, 18 |
| Freshness | subagent (`doctor-reader`) | opus-low | 5, 6 |
| Conformance | subagent (`general-purpose`) | haiku | 14 |
| Tests | subagent (`doctor-reader`) | opus-low | 10, 12, 15 |
| Prose & surface | subagent (`general-purpose`) | haiku | 2 |
| History | subagent (`doctor-reader`) | opus-low | 5, 8 |
| Comments | subagent (`doctor-reader`) | opus-low | 7, 18, 19 |
| Inbox | subagent (`doctor-reader`) | opus-low | 13, 16, 17 |

