# section-doctor — Notifications — 2026-09-30

- Invocation: unattended daily routine, no arguments (Phase 8 skipped per routine prompt)
- Anchor commit: `f11c2bcc7` (origin/main at branch point); branch `section-doctor/2026-09-30T011604Z`.
- Budget: 2.5h.
- PR: pending

## Assessment summary

## Findings

## Debt verified

## Worked

## Skipped

## Retro

## Needs Peter

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.Notifications.Contracts/Humans.Notifications.Contracts.csproj` |  |
| `src/Sections/Humans.Notifications.Contracts/INotificationAutoResolve.cs` |  |
| `src/Sections/Humans.Notifications.Contracts/INotificationEmitter.cs` |  |
| `src/Sections/Humans.Notifications.Contracts/INotificationRetention.cs` |  |
| `src/Sections/Humans.Notifications.Contracts/INotificationService.cs` |  |
| `src/Sections/Humans.Notifications.Contracts/NotificationClass.cs` |  |
| `src/Sections/Humans.Notifications.Contracts/NotificationPriority.cs` |  |
| `src/Sections/Humans.Notifications.Contracts/NotificationSource.cs` |  |
| `src/Sections/Humans.Notifications/Contracts/INotificationInboxRead.cs` |  |
| `src/Sections/Humans.Notifications/Controllers/NotificationsController.cs` |  |
| `src/Sections/Humans.Notifications/Data/Configurations/NotificationConfiguration.cs` |  |
| `src/Sections/Humans.Notifications/Data/Configurations/NotificationRecipientConfiguration.cs` |  |
| `src/Sections/Humans.Notifications/Data/INotificationRepository.cs` |  |
| `src/Sections/Humans.Notifications/Data/Migrations/20260809032723_BaselineNotifications.Designer.cs` | generated |
| `src/Sections/Humans.Notifications/Data/Migrations/20260809032723_BaselineNotifications.cs` |  |
| `src/Sections/Humans.Notifications/Data/Migrations/NotificationsDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.Notifications/Data/NotificationRepository.cs` |  |
| `src/Sections/Humans.Notifications/Data/NotificationsDbContext.cs` |  |
| `src/Sections/Humans.Notifications/Data/NotificationsDbContextFactory.cs` |  |
| `src/Sections/Humans.Notifications/Docs/Notifications.md` |  |
| `src/Sections/Humans.Notifications/Docs/authorization.md` |  |
| `src/Sections/Humans.Notifications/Docs/data-access.md` |  |
| `src/Sections/Humans.Notifications/Docs/features/notification-api.md` |  |
| `src/Sections/Humans.Notifications/Docs/features/notification-board.md` |  |
| `src/Sections/Humans.Notifications/Docs/features/notification-inbox.md` |  |
| `src/Sections/Humans.Notifications/Docs/health.md` |  |
| `src/Sections/Humans.Notifications/Domain/Notification.cs` |  |
| `src/Sections/Humans.Notifications/Domain/NotificationRecipient.cs` |  |
| `src/Sections/Humans.Notifications/Humans.Notifications.csproj` |  |
| `src/Sections/Humans.Notifications/Jobs/CleanupNotificationsJob.cs` |  |
| `src/Sections/Humans.Notifications/Models/NotificationsViewModels.cs` |  |
| `src/Sections/Humans.Notifications/NotificationsResource.ca.resx` |  |
| `src/Sections/Humans.Notifications/NotificationsResource.cs` |  |
| `src/Sections/Humans.Notifications/NotificationsResource.de.resx` |  |
| `src/Sections/Humans.Notifications/NotificationsResource.es.resx` |  |
| `src/Sections/Humans.Notifications/NotificationsResource.fr.resx` |  |
| `src/Sections/Humans.Notifications/NotificationsResource.it.resx` |  |
| `src/Sections/Humans.Notifications/NotificationsResource.resx` |  |
| `src/Sections/Humans.Notifications/Properties/AssemblyInfo.cs` |  |
| `src/Sections/Humans.Notifications/Section.cs` |  |
| `src/Sections/Humans.Notifications/SectionChrome.cs` |  |
| `src/Sections/Humans.Notifications/SectionJobs.cs` |  |
| `src/Sections/Humans.Notifications/Services/Dtos/NotificationMeter.cs` |  |
| `src/Sections/Humans.Notifications/Services/INotificationInboxService.cs` |  |
| `src/Sections/Humans.Notifications/Services/NotificationEmitter.cs` |  |
| `src/Sections/Humans.Notifications/Services/NotificationInboxRead.cs` |  |
| `src/Sections/Humans.Notifications/Services/NotificationInboxService.cs` |  |
| `src/Sections/Humans.Notifications/Services/NotificationMeterProvider.cs` |  |
| `src/Sections/Humans.Notifications/Services/NotificationService.cs` |  |
| `src/Sections/Humans.Notifications/Services/NotificationSourceMapping.cs` |  |
| `src/Sections/Humans.Notifications/ViewComponents/NotificationBellViewComponent.cs` |  |
| `src/Sections/Humans.Notifications/Views/Notifications/Index.cshtml` |  |
| `src/Sections/Humans.Notifications/Views/Notifications/_NotificationPopup.cshtml` |  |
| `src/Sections/Humans.Notifications/Views/Notifications/_NotificationRow.cshtml` |  |
| `src/Sections/Humans.Notifications/Views/Shared/Components/NotificationBell/Default.cshtml` |  |
| `src/Sections/Humans.Notifications/Views/_ViewImports.cshtml` |  |
| `tests/Humans.Notifications.Tests/Controllers/NotificationsControllerTests.cs` |  |
| `tests/Humans.Notifications.Tests/Enums/EnumStringStabilityTests.cs` |  |
| `tests/Humans.Notifications.Tests/Humans.Notifications.Tests.csproj` |  |
| `tests/Humans.Notifications.Tests/NotificationsArchitectureTests.cs` |  |
| `tests/Humans.Notifications.Tests/Services/NotificationEmitterTests.cs` |  |
| `tests/Humans.Notifications.Tests/Services/NotificationInboxReadTests.cs` |  |
| `tests/Humans.Notifications.Tests/Services/NotificationInboxServiceTests.cs` |  |
| `tests/Humans.Notifications.Tests/Services/NotificationMeterProviderTests.cs` |  |
| `tests/Humans.Notifications.Tests/Services/NotificationRepositoryTests.cs` |  |
| `tests/Humans.Notifications.Tests/Services/NotificationRetentionTests.cs` |  |
| `tests/Humans.Notifications.Tests/Services/NotificationServiceTests.cs` |  |
| `tests/Humans.Notifications.Tests/TestInfrastructure.cs` |  |
| `tests/Humans.Notifications.Tests/ViewComponents/NotificationBellViewComponentTests.cs` |  |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main |  |  |
| Behavior & bugs | main |  |  |
| Freshness |  |  |  |
| Conformance |  |  |  |
| Tests |  |  |  |
| Prose & surface |  |  |  |
| History |  |  |  |
| Comments |  |  |  |
| Inbox |  |  |  |

