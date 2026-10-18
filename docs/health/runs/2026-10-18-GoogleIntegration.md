# section-doctor — GoogleIntegration — 2026-10-18

- Invocation: unattended daily routine, no arguments; Phase 8 skipped per routine prompt
- Anchor commit: `0c87f75fa` (origin/main at branch point); branch `section-doctor/2026-10-18T011630Z`.
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
| `docs/guide/GoogleIntegration.md` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/DomainGroupInfo.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/DriveActivityEvent.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/DrivePermissionLevel.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/GoogleResource.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/GoogleResourceType.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/GoogleSyncOutboxEvent.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/GoogleSyncOutboxEventTypes.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/GoogleWorkspaceOptions.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/GroupLinkResult.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/GroupSettingsDriftResult.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/IEmailProvisioningService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleDriveAccessSource.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleDriveActivityClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleDriveSync.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleGroupMembershipSource.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleGroupSync.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleSyncLogViewer.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleSyncOutboxProcessor.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleSyncOutboxService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleSyncService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleSyncWrite.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleTranslationService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/ITeamResourceService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/ITeamResourceWrite.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/LinkResourceResult.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/ResourceSyncDiff.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/SyncAction.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/SyncMode.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/SyncServiceSettings.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Contracts/SyncServiceType.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Controllers/GoogleController.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Controllers/GoogleSyncHistoryMigrationAdminController.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Data/Configurations/GoogleResourceConfiguration.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Data/Configurations/GoogleSyncLogEntryConfiguration.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Data/Configurations/GoogleSyncOutboxEventConfiguration.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Data/Configurations/SyncServiceSettingsConfiguration.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Data/GoogleIntegrationDbContext.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Data/GoogleIntegrationDbContextFactory.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Data/GoogleResourceRepository.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Data/GoogleSyncLogRepository.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Data/GoogleSyncOutboxRepository.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Data/IGoogleResourceRepository.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Data/IGoogleSyncLogRepository.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Data/IGoogleSyncOutboxRepository.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Data/ISyncSettingsRepository.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Data/Migrations/20260809125108_BaselineGoogleIntegration.Designer.cs` | generated |
| `src/Sections/Humans.GoogleIntegration/Data/Migrations/20260809125108_BaselineGoogleIntegration.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Data/Migrations/20260820172800_AddGoogleSyncLog.Designer.cs` | generated |
| `src/Sections/Humans.GoogleIntegration/Data/Migrations/20260820172800_AddGoogleSyncLog.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Data/Migrations/GoogleIntegrationDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.GoogleIntegration/Data/SyncSettingsRepository.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Docs/GoogleIntegration.md` |  |
| `src/Sections/Humans.GoogleIntegration/Docs/authorization.md` |  |
| `src/Sections/Humans.GoogleIntegration/Docs/data-access.md` |  |
| `src/Sections/Humans.GoogleIntegration/Docs/debt.yml` |  |
| `src/Sections/Humans.GoogleIntegration/Docs/features/43-google-group-membership-sync.md` |  |
| `src/Sections/Humans.GoogleIntegration/Docs/features/google-integration.md` |  |
| `src/Sections/Humans.GoogleIntegration/Docs/features/google-removal-notifications.md` |  |
| `src/Sections/Humans.GoogleIntegration/Docs/features/workspace-account-provisioning.md` |  |
| `src/Sections/Humans.GoogleIntegration/Docs/health.md` |  |
| `src/Sections/Humans.GoogleIntegration/Domain/GoogleSyncLogEntry.cs` |  |
| `src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.ca.resx` |  |
| `src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.cs` |  |
| `src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.de.resx` |  |
| `src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.es.resx` |  |
| `src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.fr.resx` |  |
| `src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.it.resx` |  |
| `src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.resx` |  |
| `src/Sections/Humans.GoogleIntegration/Health/GoogleWorkspaceHealthCheck.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Humans.GoogleIntegration.csproj` |  |
| `src/Sections/Humans.GoogleIntegration/Jobs/GoogleResourceReconciliationJob.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Jobs/ProcessGoogleSyncOutboxJob.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Models/GoogleSyncViewModels.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Models/SyncServiceNameFormatter.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Models/SyncSettingsViewModels.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Models/TeamSyncViewModels.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Models/WorkspaceEmailViewModels.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Properties/AssemblyInfo.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Section.cs` |  |
| `src/Sections/Humans.GoogleIntegration/SectionAdminNav.cs` |  |
| `src/Sections/Humans.GoogleIntegration/SectionConfiguration.cs` |  |
| `src/Sections/Humans.GoogleIntegration/SectionHealthChecks.cs` |  |
| `src/Sections/Humans.GoogleIntegration/SectionJobs.cs` |  |
| `src/Sections/Humans.GoogleIntegration/SectionSettings.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/DriveFolderPath.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/DrivePermissionErrorPolicy.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/DrivePermissionRoleMapper.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/EmailProvisioningService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/EmailRenameDetectionResult.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleAdminService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleDriveAccessSyncService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleGroupKeyHelper.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleGroupSyncService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleIntegrationEmailPreviews.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleIntegrationEmails.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleIntegrationMetricsService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleRemovalNotificationService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleSyncHistoryMigrationService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleSyncLogService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleSyncMemberHydration.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleSyncOutboxProcessor.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleSyncOutboxService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleTranslationService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleWorkspaceSyncService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleWorkspaceUserService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/GroupSettingsPolicy.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/HangfireGoogleDriveAccessSyncScheduler.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/HangfireGoogleGroupSyncScheduler.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/IGoogleAdminService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/IGoogleDriveAccessSyncScheduler.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/IGoogleGroupSyncScheduler.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/IGoogleRemovalNotificationService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/IGoogleSyncHistoryMigrationService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/IGoogleSyncLogService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/IGoogleSyncManagement.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/IGoogleWorkspaceUserService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/ISyncSettingsService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/ITeamResourceManagement.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/StubGoogleSyncService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/SyncSettingsService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/TeamResourceService.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/UserEmailMatchOwner.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleCredentialLoader.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleDirectoryClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleDriveActivityClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleDrivePermissionsClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleGroupMembershipClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleGroupProvisioningClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleTranslationClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/IGoogleDirectoryClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/IGoogleDrivePermissionsClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/IGoogleGroupMembershipClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/IGoogleGroupProvisioningClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/IGoogleTranslationClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/ITeamResourceGoogleClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/IWorkspaceUserDirectoryClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/StubGoogleDirectoryClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/StubGoogleDriveActivityClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/StubGoogleDrivePermissionsClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/StubGoogleGroupMembershipClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/StubGoogleGroupProvisioningClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/StubGoogleTranslationClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/StubTeamResourceGoogleClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/StubWorkspaceUserDirectoryClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/TeamResourceGoogleClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/WorkspaceUserDirectoryClient.cs` |  |
| `src/Sections/Humans.GoogleIntegration/ViewComponents/GoogleSyncLogViewComponent.cs` |  |
| `src/Sections/Humans.GoogleIntegration/ViewComponents/GoogleSyncSettingsTabViewComponent.cs` |  |
| `src/Sections/Humans.GoogleIntegration/Views/Google/Accounts.cshtml` |  |
| `src/Sections/Humans.GoogleIntegration/Views/Google/ActivityLog.cshtml` |  |
| `src/Sections/Humans.GoogleIntegration/Views/Google/AllGroups.cshtml` |  |
| `src/Sections/Humans.GoogleIntegration/Views/Google/EmailFlagViolations.cshtml` |  |
| `src/Sections/Humans.GoogleIntegration/Views/Google/EmailRenames.cshtml` |  |
| `src/Sections/Humans.GoogleIntegration/Views/Google/GroupSettingsResults.cshtml` |  |
| `src/Sections/Humans.GoogleIntegration/Views/Google/Index.cshtml` |  |
| `src/Sections/Humans.GoogleIntegration/Views/Google/Sync.cshtml` |  |
| `src/Sections/Humans.GoogleIntegration/Views/Google/SyncAudit.cshtml` |  |
| `src/Sections/Humans.GoogleIntegration/Views/Google/SyncOutbox.cshtml` |  |
| `src/Sections/Humans.GoogleIntegration/Views/Google/SyncResults.cshtml` |  |
| `src/Sections/Humans.GoogleIntegration/Views/Google/_SyncOutboxTable.cshtml` |  |
| `src/Sections/Humans.GoogleIntegration/Views/Google/_SyncTabContent.cshtml` |  |
| `src/Sections/Humans.GoogleIntegration/Views/GoogleSyncHistoryMigrationAdmin/Index.cshtml` |  |
| `src/Sections/Humans.GoogleIntegration/Views/Shared/Components/GoogleSyncLog/Default.cshtml` |  |
| `src/Sections/Humans.GoogleIntegration/Views/Shared/Components/GoogleSyncSettingsTab/Default.cshtml` |  |
| `src/Sections/Humans.GoogleIntegration/Views/_ViewImports.cshtml` |  |
| `tests/Humans.GoogleIntegration.Tests/Architecture/GoogleWorkspaceSyncBridgeArchitectureTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/Architecture/GoogleWorkspaceUserArchitectureTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/Architecture/TeamResourceArchitectureTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/Browser/sync-preview.test.cjs` |  |
| `tests/Humans.GoogleIntegration.Tests/EmailProvisioningServiceTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/Enums/EnumStringStabilityTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleAdminServiceTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleControllerSyncAuditTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleControllerSyncCancellationTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleCredentiallessProductionTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleDriveAccessSyncServiceTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleGroupKeyHelperTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleGroupSyncServiceTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleIntegrationEmailsTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleRemovalNotificationServiceTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleResourceReconciliationJobTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleResourceRepositoryTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleSyncHistoryMigrationServiceTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleSyncLogActivityLogTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleSyncLogServiceGdprTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleSyncOutboxProcessorTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleSyncOutboxRepositoryTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleSyncRemovalNotificationIntegrationTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleSyncSettingsTabViewComponentTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleWorkspaceSyncServiceReconciliationTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleWorkspaceSyncServiceTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/GoogleWorkspaceUserServiceTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/HangfireSyncSchedulerTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/Humans.GoogleIntegration.Tests.csproj` |  |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/GoogleDrivePermissionsClientClassifierTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/GoogleDrivePermissionsClientTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/GoogleIntegrationTestHarness.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/GoogleSdkContainment.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/GoogleWorkspaceSyncBridgeDependencyInjectionTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/StubGoogleDirectoryClientTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/StubGoogleDrivePermissionsClientTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/StubGoogleGroupMembershipClientTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/StubGoogleGroupProvisioningClientTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/StubWorkspaceUserDirectoryClientTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/TestGoogleIntegrationEmails.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/TestMetrics.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/UserInfoProjection.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/WorkspaceUserDirectoryClientTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/ProcessGoogleSyncOutboxJobTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/SectionSettingsTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/SyncOutboxViewTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/SyncSettingsServiceTests.cs` |  |
| `tests/Humans.GoogleIntegration.Tests/TeamResourceServiceDeactivateTests.cs` |  |

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

