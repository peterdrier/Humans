# section-doctor — MailerLite — 2026-10-01

- Invocation: unattended daily cloud routine, no arguments (Phase 8 skipped per routine prompt)
- Anchor commit: `d8e6c2209` (origin/main at branch point); branch `section-doctor/2026-10-01T011558Z`.
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
| `src/Sections/Humans.MailerLite/Contracts/IMailerLiteAudienceSync.cs` |  |
| `src/Sections/Humans.MailerLite/Controllers/MailerLiteAdminController.cs` |  |
| `src/Sections/Humans.MailerLite/Data/Configurations/MailerLiteSyncStateConfiguration.cs` |  |
| `src/Sections/Humans.MailerLite/Data/IMailerLiteRepository.cs` |  |
| `src/Sections/Humans.MailerLite/Data/MailerLiteDbContext.cs` |  |
| `src/Sections/Humans.MailerLite/Data/MailerLiteDbContextFactory.cs` |  |
| `src/Sections/Humans.MailerLite/Data/Migrations/20260820232107_InitialMailerLiteSection.Designer.cs` | generated |
| `src/Sections/Humans.MailerLite/Data/Migrations/20260820232107_InitialMailerLiteSection.cs` |  |
| `src/Sections/Humans.MailerLite/Data/Migrations/MailerLiteDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.MailerLite/Data/Repository.cs` |  |
| `src/Sections/Humans.MailerLite/Docs/MailerLite.md` |  |
| `src/Sections/Humans.MailerLite/Docs/authorization.md` |  |
| `src/Sections/Humans.MailerLite/Docs/data-access.md` |  |
| `src/Sections/Humans.MailerLite/Docs/debt.yml` |  |
| `src/Sections/Humans.MailerLite/Docs/features/audience-debug-screen.md` |  |
| `src/Sections/Humans.MailerLite/Docs/health.md` |  |
| `src/Sections/Humans.MailerLite/Domain/MailerLiteSyncState.cs` |  |
| `src/Sections/Humans.MailerLite/Humans.MailerLite.csproj` |  |
| `src/Sections/Humans.MailerLite/Jobs/MailerLiteAudienceSyncJob.cs` |  |
| `src/Sections/Humans.MailerLite/MailerLiteEmailNormalization.cs` |  |
| `src/Sections/Humans.MailerLite/Models/AudienceCardRow.cs` |  |
| `src/Sections/Humans.MailerLite/Models/MailerLiteAudienceDebugSnapshotBuilder.cs` |  |
| `src/Sections/Humans.MailerLite/Models/MailerLiteAudienceDebugViewModel.cs` |  |
| `src/Sections/Humans.MailerLite/Models/MailerLiteDashboardViewModel.cs` |  |
| `src/Sections/Humans.MailerLite/Models/MailerLiteImportPreviewViewModel.cs` |  |
| `src/Sections/Humans.MailerLite/Properties/AssemblyInfo.cs` |  |
| `src/Sections/Humans.MailerLite/Section.cs` |  |
| `src/Sections/Humans.MailerLite/SectionAdminNav.cs` |  |
| `src/Sections/Humans.MailerLite/SectionConfiguration.cs` |  |
| `src/Sections/Humans.MailerLite/SectionJobs.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Audiences/CurrentEventTicketHolders.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Audiences/HasShiftAudience.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Audiences/HasShiftEventAudience.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Audiences/HasShiftSetupAudience.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Audiences/HasShiftStrikeAudience.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Audiences/HasTicketAudience.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Audiences/MailerLiteAudienceBase.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Audiences/MarketingAudience.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Audiences/MarketingNoTicketAudience.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Audiences/ShiftViewAudienceBase.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Audiences/TicketNoShiftsAudience.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Dtos/AudienceStats.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Dtos/AudienceSyncResult.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Dtos/BulkImportResult.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Dtos/ImportPlan.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Dtos/ImportResult.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Dtos/MailerLiteAccountSummary.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Dtos/MailerLiteGroup.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Dtos/MailerLiteSubscriber.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Dtos/MailerLiteSyncSnapshot.cs` |  |
| `src/Sections/Humans.MailerLite/Services/Dtos/SubscriberDecision.cs` |  |
| `src/Sections/Humans.MailerLite/Services/IMailerLiteAudience.cs` |  |
| `src/Sections/Humans.MailerLite/Services/IMailerLiteAudienceSyncService.cs` |  |
| `src/Sections/Humans.MailerLite/Services/IMailerLiteImportService.cs` |  |
| `src/Sections/Humans.MailerLite/Services/IMailerLiteService.cs` |  |
| `src/Sections/Humans.MailerLite/Services/MailerLite/MailerLiteClient.cs` |  |
| `src/Sections/Humans.MailerLite/Services/MailerLite/MailerLiteOptions.cs` |  |
| `src/Sections/Humans.MailerLite/Services/MailerLite/MailerLiteSubscriberConverter.cs` |  |
| `src/Sections/Humans.MailerLite/Services/MailerLiteAudienceSyncService.cs` |  |
| `src/Sections/Humans.MailerLite/Services/MailerLiteGdprContributor.cs` |  |
| `src/Sections/Humans.MailerLite/Services/MailerLiteImportService.cs` |  |
| `src/Sections/Humans.MailerLite/Views/MailerLite/Admin/Debug.cshtml` |  |
| `src/Sections/Humans.MailerLite/Views/MailerLite/Admin/Import.cshtml` |  |
| `src/Sections/Humans.MailerLite/Views/MailerLite/Admin/Index.cshtml` |  |
| `src/Sections/Humans.MailerLite/Views/MailerLite/Admin/_AudiencesCard.cshtml` |  |
| `src/Sections/Humans.MailerLite/Views/MailerLite/Admin/_DebugPager.cshtml` |  |
| `src/Sections/Humans.MailerLite/Views/MailerLite/Admin/_DebugSortHeader.cshtml` |  |
| `src/Sections/Humans.MailerLite/Views/_ViewImports.cshtml` |  |
| `tests/Humans.MailerLite.Tests/Architecture/MailerLiteArchitectureTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Audiences/HasShiftAudienceTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Audiences/HasTicketAudienceTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Audiences/MailerLiteAudienceBaseTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Audiences/MarketingAudienceTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Audiences/MarketingNoTicketAudienceTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Audiences/ShiftViewAudienceTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Audiences/TicketNoShiftsAudienceTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Controllers/MailerLiteAdminControllerAudienceSyncTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Controllers/MailerLiteAdminControllerTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Data/MailerLiteRepositoryTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Humans.MailerLite.Tests.csproj` |  |
| `tests/Humans.MailerLite.Tests/Infrastructure/InMemoryMailerLiteRepository.cs` |  |
| `tests/Humans.MailerLite.Tests/Infrastructure/UserInfoStubHelpers.cs` |  |
| `tests/Humans.MailerLite.Tests/Models/MailerLiteAudienceDebugSnapshotBuilderTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Services/ImportResultTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteAudienceSyncServiceTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteClientCacheTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteClientDeleteSubscriberTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteClientRetryTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteClientWriteGuardTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteGdprContributorTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteImportServiceClassifierTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteImportServiceConflictRuleTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteImportServiceIdempotencyTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteImportServiceThrottleTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteImportServiceWebsiteScopeTests.cs` |  |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteSubscriberConverterTests.cs` |  |

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

