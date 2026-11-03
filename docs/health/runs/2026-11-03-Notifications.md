# section-doctor — Notifications — 2026-11-03

- Invocation: scheduled routine section-doctor-daily: 'Run the section-doctor skill unattended.' (cloud project thread)
- Anchor commit: `3929aea08` (origin/main at branch point); branch `section-doctor/2026-11-03T004202Z`.
- Budget: 2.5h.
- PR: peterdrier/Humans#2254

## Assessment summary

Third run on Notifications (previous: 2026-09-30). Since then the section moved a long way: sources
became section-declared `NotificationSourceDefinition`s, the badge cache went away and the inbox read
for Backdoor moved into the contracts leaf. The code held up; the drift was in what the docs and build
files said about it. The section doc listed other sections' sources while saying it lists none, and
had five claims the code contradicts. The one structural item was a duplicated outcome record between
repository and controller. The target was regenerated: the earlier one said the inbox read lives in a
`Contracts/` folder that does not exist, skipped a shape number, and its invariant cites had drifted
onto the wrong lines; the section itself moved where the source registry landed.

Independence check: pass — findings 2 and 3 come from the target's "one outcome shape" line and
finding 10 from reading the target against the tree; finding 1 is a spec-vs-reality delta.

## Findings

1. The section doc says meter reads skip the abort token, that the board-vote count is cached here, that onboarding-pending is its own count, and places `ResolveBySourceKeyAsync` on the internal inbox interface; it omits Consent as a dependency and keeps a stale per-section emitter list while saying it lists none; its Status line narrates history.
2. `NotificationActionResult` copied `NotificationMutationOutcome` field for field behind a pass-through mapper.
3. `ClickThroughOutcome.NotFound` carried nothing its only caller used; the URL was already null.
4. The meter provider used the pre-rename Volunteer Coordinator name, named its Governance dependency after another type, wrapped the voting count in a one-line pass-through and parsed the user id twice.
5. The section csproj said Email.Contracts references Users.Contracts (it references AuditLog) and narrated a decomposition commit; the contracts csproj listed drifted consumers and called the inbox read model internal; `Section.cs` repeated the emitter's DI-cycle story.
6. Feature specs: the inbox spec said the purge evicts badge counts (nothing is cached) and named a nonexistent `AddedToTeam` source; the API example used a retired source. The board spec's source table and contributor count are as of its writing and were left.
7. `Notifications_Dismiss` is read by nothing in any culture.
8. Test comments restated the next line, and two named a table that no longer exists.
9. The GDPR export slice had no test.
10. The previous target placed the inbox read in a `Contracts/` folder, skipped a shape number, and cited lines that had moved.
11. `Resolve`, `Dismiss` and `MarkRead` POST routes are reached by no page (carry-forward from 2026-08-26, still true).
12. `ClickThrough` is a GET that marks the row read.
13. peterdrier/Humans#1626: its Notifications, AuditLog and Gdpr parts have shipped as section-declared contributors.

## Debt verified

- The section ledger has no rows; nothing to verify.

## Worked

- Finding 1: section doc corrected; inbound list now points at `grep -rl NotificationSourceContributor src/Sections`.
- Findings 2 and 3: one outcome record from repository to controller; click-through returns the URL or null. Reviewer: `pd:orch-opus-high` with the doctor-reviewer brief, APPROVE; it checked HTTP results, click-through nulls, the read mark, blast and layering.
- Finding 4: meter provider tidied; behaviour unchanged, section tests green.
- Findings 5 and 8: comments rewritten or struck.
- Finding 6: inbox and API specs corrected.
- Finding 7: key removed from all six cultures with XML tooling.
- Finding 9: `ContributeForUserAsync_ExportsOnlyTheUsersOwnRows_WithoutTheActionLabel` added in the section test project, which CI runs.
- Finding 10: target regenerated; every cite traced.
- Finding 13: comment posted on peterdrier/Humans#1626.

## Skipped

- Findings 11 and 12: Peter's call (see below).
- Finding 6, board spec: a dated design record, not a description of today.
- Carry-forwards from 2026-08-26 items 2, 18 and 20 no longer hold (meter titles are localized; the category switch and the badge cache are gone); item 19 is documented as deliberate in the section doc. That run file is not this run's to tick.
- Sections passed over as blocked: Feedback (pushed branch), Finance (open PR #2240).
- Session line, 2026-11-03: no live render; the cloud run does not boot the app.

## Retro

The selector picked well: the section had churned heavily since its last run and the drift it left was
real. Wasted motion was small; two lenses were folded into one dispatch each to save tokens, which cost
nothing in coverage. Striking revealed that the previous target's invariant cites had drifted silently;
trace caught it only because this run re-ran it before Phase 4 ended. The target diff says the section
moved (source registry, contracts leaf) and the old target was also wrong in one structural line.

## Needs Peter

- [ ] 11 — Delete the three unreached single-item routes, or add single-item buttons that call them?
- [ ] 12 — Keep click-through as a GET that marks the row read, or make it a POST?

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.Notifications.Contracts/Humans.Notifications.Contracts.csproj` | changed |
| `src/Sections/Humans.Notifications.Contracts/INotificationAutoResolve.cs` | reviewed |
| `src/Sections/Humans.Notifications.Contracts/INotificationEmitter.cs` | reviewed |
| `src/Sections/Humans.Notifications.Contracts/INotificationInboxRead.cs` | reviewed |
| `src/Sections/Humans.Notifications.Contracts/INotificationRetention.cs` | reviewed |
| `src/Sections/Humans.Notifications.Contracts/INotificationService.cs` | reviewed |
| `src/Sections/Humans.Notifications.Contracts/INotificationSourceContributor.cs` | reviewed |
| `src/Sections/Humans.Notifications.Contracts/NotificationClass.cs` | reviewed |
| `src/Sections/Humans.Notifications.Contracts/NotificationPriority.cs` | reviewed |
| `src/Sections/Humans.Notifications.Contracts/NotificationSourceDefinition.cs` | reviewed |
| `src/Sections/Humans.Notifications/Controllers/NotificationsController.cs` | reviewed |
| `src/Sections/Humans.Notifications/Data/Configurations/NotificationConfiguration.cs` | reviewed |
| `src/Sections/Humans.Notifications/Data/Configurations/NotificationRecipientConfiguration.cs` | reviewed |
| `src/Sections/Humans.Notifications/Data/INotificationRepository.cs` | changed |
| `src/Sections/Humans.Notifications/Data/Migrations/20260809032723_BaselineNotifications.Designer.cs` | generated |
| `src/Sections/Humans.Notifications/Data/Migrations/20260809032723_BaselineNotifications.cs` | reviewed |
| `src/Sections/Humans.Notifications/Data/Migrations/NotificationsDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.Notifications/Data/NotificationRepository.cs` | changed |
| `src/Sections/Humans.Notifications/Data/NotificationsDbContext.cs` | reviewed |
| `src/Sections/Humans.Notifications/Data/NotificationsDbContextFactory.cs` | reviewed |
| `src/Sections/Humans.Notifications/Docs/Notifications.md` | changed |
| `src/Sections/Humans.Notifications/Docs/authorization.md` | reviewed |
| `src/Sections/Humans.Notifications/Docs/data-access.md` | reviewed |
| `src/Sections/Humans.Notifications/Docs/debt.yml` | reviewed |
| `src/Sections/Humans.Notifications/Docs/features/notification-api.md` | changed |
| `src/Sections/Humans.Notifications/Docs/features/notification-board.md` | reviewed |
| `src/Sections/Humans.Notifications/Docs/features/notification-inbox.md` | changed |
| `src/Sections/Humans.Notifications/Docs/health.md` | changed |
| `src/Sections/Humans.Notifications/Domain/Notification.cs` | reviewed |
| `src/Sections/Humans.Notifications/Domain/NotificationRecipient.cs` | reviewed |
| `src/Sections/Humans.Notifications/Humans.Notifications.csproj` | changed |
| `src/Sections/Humans.Notifications/Jobs/CleanupNotificationsJob.cs` | reviewed |
| `src/Sections/Humans.Notifications/Models/NotificationsViewModels.cs` | reviewed |
| `src/Sections/Humans.Notifications/NotificationsResource.ca.resx` | changed |
| `src/Sections/Humans.Notifications/NotificationsResource.cs` | reviewed |
| `src/Sections/Humans.Notifications/NotificationsResource.de.resx` | changed |
| `src/Sections/Humans.Notifications/NotificationsResource.es.resx` | changed |
| `src/Sections/Humans.Notifications/NotificationsResource.fr.resx` | changed |
| `src/Sections/Humans.Notifications/NotificationsResource.it.resx` | changed |
| `src/Sections/Humans.Notifications/NotificationsResource.resx` | changed |
| `src/Sections/Humans.Notifications/Properties/AssemblyInfo.cs` | reviewed |
| `src/Sections/Humans.Notifications/Section.cs` | changed |
| `src/Sections/Humans.Notifications/Services/Dtos/NotificationMeter.cs` | reviewed |
| `src/Sections/Humans.Notifications/Services/INotificationInboxService.cs` | changed |
| `src/Sections/Humans.Notifications/Services/NotificationEmitter.cs` | reviewed |
| `src/Sections/Humans.Notifications/Services/NotificationInboxRead.cs` | reviewed |
| `src/Sections/Humans.Notifications/Services/NotificationInboxService.cs` | changed |
| `src/Sections/Humans.Notifications/Services/NotificationMeterProvider.cs` | changed |
| `src/Sections/Humans.Notifications/Services/NotificationService.cs` | reviewed |
| `src/Sections/Humans.Notifications/Services/NotificationSourceRegistry.cs` | reviewed |
| `src/Sections/Humans.Notifications/ViewComponents/NotificationBellViewComponent.cs` | reviewed |
| `src/Sections/Humans.Notifications/Views/Notifications/Index.cshtml` | reviewed |
| `src/Sections/Humans.Notifications/Views/Notifications/_NotificationPopup.cshtml` | reviewed |
| `src/Sections/Humans.Notifications/Views/Notifications/_NotificationRow.cshtml` | reviewed |
| `src/Sections/Humans.Notifications/Views/Shared/Components/NotificationBell/Default.cshtml` | reviewed |
| `src/Sections/Humans.Notifications/Views/_ViewImports.cshtml` | reviewed |
| `tests/Humans.Notifications.Tests/Browser/inbox-keyboard.test.cjs` | reviewed |
| `tests/Humans.Notifications.Tests/Browser/notification-popup-lifecycle.test.cjs` | reviewed |
| `tests/Humans.Notifications.Tests/Controllers/NotificationsControllerTests.cs` | reviewed |
| `tests/Humans.Notifications.Tests/Enums/EnumStringStabilityTests.cs` | reviewed |
| `tests/Humans.Notifications.Tests/Humans.Notifications.Tests.csproj` | reviewed |
| `tests/Humans.Notifications.Tests/Services/NotificationEmitterTests.cs` | changed |
| `tests/Humans.Notifications.Tests/Services/NotificationInboxReadTests.cs` | reviewed |
| `tests/Humans.Notifications.Tests/Services/NotificationInboxServiceTests.cs` | changed |
| `tests/Humans.Notifications.Tests/Services/NotificationMeterProviderTests.cs` | reviewed |
| `tests/Humans.Notifications.Tests/Services/NotificationRepositoryTests.cs` | changed |
| `tests/Humans.Notifications.Tests/Services/NotificationRetentionTests.cs` | reviewed |
| `tests/Humans.Notifications.Tests/Services/NotificationServiceTests.cs` | changed |
| `tests/Humans.Notifications.Tests/Services/NotificationSourceRegistryTests.cs` | reviewed |
| `tests/Humans.Notifications.Tests/TestInfrastructure.cs` | reviewed |
| `tests/Humans.Notifications.Tests/ViewComponents/NotificationBellViewComponentTests.cs` | reviewed |
| `tests/Humans.Notifications.Tests/ViewComponents/NotificationBellViewTests.cs` | reviewed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | session model | findings 2, 3, 4, 10 |
| Behavior & bugs | main | session model | no defect found; finding 4 |
| Controller intent | subagent (`pd:orch-opus-low`) | opus-low | findings 11, 12 |
| Freshness | subagent (`pd:orch-opus-low`) | opus-low | findings 1, 6 |
| Conformance | subagent (`pd:orch-haiku-medium`), one dispatch with Prose & surface | haiku-medium | clean |
| Tests | subagent (`pd:orch-opus-low`) | opus-low | finding 9 |
| Prose & surface | subagent (`pd:orch-haiku-medium`), one dispatch with Conformance; InspectCode not run | haiku-medium | finding 7 |
| History | subagent (`pd:orch-opus-low`), one dispatch with Comments | opus-low | finding 1 |
| Comments | subagent (`pd:orch-opus-low`), one dispatch with History | opus-low | findings 5, 8 |
| Inbox | subagent (`pd:orch-opus-low`); peterdrier/Humans covered, nobodies-collective/Humans not covered (REST 403, repo not enabled for this session) | opus-low | finding 13 |

