# section-doctor — GoogleIntegration — 2026-10-18

- Invocation: unattended daily routine, no arguments; Phase 8 skipped per routine prompt
- Anchor commit: `0c87f75fa` (origin/main at branch point); branch `section-doctor/2026-10-18T011630Z`.
- Budget: 2.5h.
- PR: peterdrier/Humans#2023

## Assessment summary

The section is sound in behavior. No bug was found, and every invariant in the regenerated target traces to an enforcing line. Its prose lagged the code.

- The previous target described a section that no longer exists: a Contracts leaf project, a member-dashboard component and a "Temp" nav group. It also cited ledger rows that were never filed.
- The section and feature docs still placed `google_resources` in Teams and listed stale dependency sets. They named methods that are gone (`PreviewSyncAllAsync`, `RemoveUserFromDriveAsync`, `StubTeamResourceService`).
- Comments in code and tests narrated past PRs and issues instead of stating the present rule. One test summary made a false claim about its harness.

On the code side, one Hangfire compat overload broke standing policy D13 and was cut. A duplicated grant loop folded into one. Two public-surface questions are left for Peter: a public no-op that callers treat as an immediate removal, and a reconciler interface published with no outside caller. The regenerated target is in `src/Sections/Humans.GoogleIntegration/Docs/health.md`.

Independence check: pass. Findings 1, 2 and 3 came from the target's shapes and load-bearing weirdness. The doc drift came from the spec-versus-code delta.

## Findings

1. `IGoogleDriveSync` carried a three-parameter `ReconcileOneAsync` that existed only so already-enqueued Hangfire jobs keep resolving. Standing policy D13 (`memory/process/debt-sweep-standing-policy.md`) says to delete such shims on sight. Its comment also claimed `IGoogleGroupSync` had the same pattern, which it does not.
2. `IGoogleSyncWrite.RemoveUserFromTeamResourcesAsync` is a public no-op: removal waits for the next reconciliation. Users' non-compliant suspension, Teams' `SystemTeamSyncJob` and the outbox processor still call it as if it removed access now.
3. `IGoogleDriveSync` is public in `Contracts/`, but nothing outside the section calls it; Teams uses only `IGoogleGroupSync`. Making it internal has no precedent for a Hangfire-enqueued interface (nobodies-collective/Humans#1180).
4. The section docs, feature specs and `docs/guide/GoogleIntegration.md` drifted from the code:
   - table ownership;
   - dependency lists;
   - methods that no longer exist;
   - folder creation by Workgroups;
   - the Camps membership source;
   - Board and TeamsAdmin access;
   - the retry policy;
   - the settings location.
5. `Section.cs` remarks put the recurring jobs in `Contracts/` and the `GoogleWorkspaceOptions` configuration in the wrong Shell file. They also narrated the move history.
6. `GoogleIntegrationResource.cs` remarks said the resource set is only the `GoogleAccounts_*` keys. The email and notification keys live there too.
7. The previous target was false throughout: a leaf contracts project, a root `SectionMemberDashboard.cs`, a "Temp" nav group, resx only for Accounts, an incomplete authorization line and ledger rows that do not exist.
8. Section code comments and docs narrated history: `#1091`, `#540c`, `#508`, `#1083`, `#635`, the old `/Google/SyncSettings` page and `#992`.
9. Test comments narrated history (PR tombstones, review attributions, removed columns). `TeamResourceServiceDeactivateTests` claimed an Application layer and `UsersDbContext` it does not use.
10. The `SyncAction` summary placed mode enforcement only in `GoogleWorkspaceSyncService`; the reconcilers enforce it too.
11. Repository comments spoke of a migration still to land and told a fixed bug's story.
12. Inbox issue verdicts:
    - nobodies-collective/Humans#33: keep.
    - nobodies-collective/Humans#1175: edit, because the contracts leaf project is gone.
    - nobodies-collective/Humans#1180: edit or close; what remains is the visibility question in 3.
    - peterdrier/Humans#1846: close as a duplicate of peterdrier/Humans#1969.
    - peterdrier/Humans#1969: edit; its pointers are stale.
13. `/Google/Accounts` and the sync-history migration page have no in-content back link.
14. Target invariants with thin tests: the order of recovery capture, and reconciliation continuing past one failed resource.
15. `GoogleWorkspaceSyncService.AddUserToTeamResourcesAsync` wrote out the per-resource grant loop twice, once for the team and once for its parent department.

## Debt verified

The section ledger `src/Sections/Humans.GoogleIntegration/Docs/debt.yml` holds no rows, and the central ledger has none for this section, so there was nothing to verify. The previous target's two ledger citations (the health check's `Google.Apis` use and inline `SyncExecute`) named rows that never existed. Both are judged sound and now sit in the target's load-bearing weirdness without a citation.

Off-section debt filed this run: `TEAMS-11` in `src/Sections/Humans.Teams/Docs/debt.yml`. `src/Sections/Humans.Teams/Docs/Teams.md` still says `GoogleResource.Team` is declared.

## Worked

- 7: target regenerated.
- 1: compat overload deleted from `IGoogleDriveSync` and its implementation. Reviewer verdict: approve. It checked that no caller or enqueue site uses the overload, and that D13 governs.
- 4, 8 (docs): doc drift fixed by a sonnet executor. Kept additions beyond the list, each grep-verified:
  - the Teams and Camps registrations as membership sources;
  - the `IGoogleResourceRepository`, `ITeamWrite` and `ILegacyGoogleSyncAuditReader` injections.
- 5, 6, 8, 10, 11: section code comments corrected by a sonnet executor, with no additions beyond the list.
- 9: test comments corrected by a sonnet executor, with no additions beyond the list. The outbox repository test's table name was also corrected to `google_sync_outbox`.
- 2 (Peter: delete): the method is gone from `IGoogleSyncWrite` and both implementations. Users' suspension and Teams' system-team sync no longer call it, and the suspension job drops its now-unused team and Google dependencies. The outbox processor still accepts queued removal events, completing them without a call; a test pins that. Teams keeps writing the removal event with the member-left row.
- 3 (Peter: internal): `IGoogleDriveSync` moved from `Contracts/` to `Services/` as an internal interface. Under D13, a Drive sync job still queued under the old type name is dropped at deploy.
- 12 (Peter: per-issue rulings): applied after the push.
  - nobodies-collective/Humans#33: kept, unchanged.
  - nobodies-collective/Humans#1175: body refreshed. The `.Contracts` project is dropped and the SDK-using connectors now point at `Services/Workspace/`.
  - nobodies-collective/Humans#1180: narrowed to `IGoogleGroupSync`, whose `RequestSyncAsync` and `ReconcileOneAsync` have no outside caller. Every `ITeamResourceService` read method has an outside caller, so it left scope; relabelled `size:S`.
  - peterdrier/Humans#1846: closed as a duplicate of peterdrier/Humans#1969.
  - peterdrier/Humans#1969: code pointers moved to `GoogleDriveAccessSyncService` and `GoogleClientError.IsDriveTargetRejection`.
- 15: the team and parent-department grant loops folded into one local function. Reviewer verdict: approve. It checked that the two bodies were identical, the call order was preserved, the captured state was assigned before first use, and the local-function precedent in other section services.

## Skipped

- 13: the admin sidebar reaches both pages, and they are admin-only.
- 14: the Tests thread did not confirm the gaps against the test bodies, so a ledger row would be unverified.
- The Workgroups design record still names `IGoogleSyncService.CreateSubfolderAsync`; the method is on `IGoogleSyncWrite`. It is a dated design record, not a live claim, so it is left alone.
- No section was passed over as blocked.

## Retro

**Selector.** GoogleIntegration was a good pick. Contracts folded into the section, Drive access moved to source fan-outs, and Camps and Workgroups became sources. All of that had left the docs and the target a generation behind.

**Wasted motion.** As in the previous run, the reading threads went out before the target was regenerated, so they read against the stale target. The order cost little because the lenses did not depend on it, but it inverts the skill's sequence. The Comments and Tests threads came back partial, which left main to bring every unclaimed path to the reviewed bar. Main did that mechanically with `doctor.py trace` per path. One executor was logged twice in the dispatch log after a context compaction.

**What striking revealed.** The comment executor came back with an empty report, so main checked its whole diff. That included the two facts it had written itself: the Shell file that configures the options, and the resource key families against the resx. A strike whose report is missing still has a diff, and the diff is what gets verified.

**Target diff.** This section moved structurally: the contracts project became a folder, reconcilers split per kind behind a facade, and the Drive access source joined the group source. The previous target was right for its day.

## Needs Peter

- [x] 2 — `RemoveUserFromTeamResourcesAsync`: delete it (callers rely on reconciliation) or make it remove now? **Peter: delete — done.**
- [x] 3 — `IGoogleDriveSync`: make it internal, or keep it public for Hangfire? **Peter: internal — done.**
- [x] 12 — apply the inbox verdicts to the five issues? **Peter: keep #33, fix #1175, narrow #1180, close #1846, fix #1969 — done.**

## File coverage

| Path | Disposition |
|---|---|
| `docs/guide/GoogleIntegration.md` | changed |
| `src/Sections/Humans.GoogleIntegration/Contracts/DomainGroupInfo.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/DriveActivityEvent.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/DrivePermissionLevel.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/GoogleResource.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/GoogleResourceType.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/GoogleSyncOutboxEvent.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/GoogleSyncOutboxEventTypes.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/GoogleWorkspaceOptions.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/GroupLinkResult.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/GroupSettingsDriftResult.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/IEmailProvisioningService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleDriveAccessSource.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleDriveActivityClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleGroupMembershipSource.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleGroupSync.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleSyncLogViewer.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleSyncOutboxProcessor.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleSyncOutboxService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleSyncService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleSyncWrite.cs` | changed |
| `src/Sections/Humans.GoogleIntegration/Contracts/IGoogleTranslationService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/ITeamResourceService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/ITeamResourceWrite.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/LinkResourceResult.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/ResourceSyncDiff.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/SyncAction.cs` | changed |
| `src/Sections/Humans.GoogleIntegration/Contracts/SyncMode.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/SyncServiceSettings.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Contracts/SyncServiceType.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Controllers/GoogleController.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Controllers/GoogleSyncHistoryMigrationAdminController.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Data/Configurations/GoogleResourceConfiguration.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Data/Configurations/GoogleSyncLogEntryConfiguration.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Data/Configurations/GoogleSyncOutboxEventConfiguration.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Data/Configurations/SyncServiceSettingsConfiguration.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Data/GoogleIntegrationDbContext.cs` | changed |
| `src/Sections/Humans.GoogleIntegration/Data/GoogleIntegrationDbContextFactory.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Data/GoogleResourceRepository.cs` | changed |
| `src/Sections/Humans.GoogleIntegration/Data/GoogleSyncLogRepository.cs` | changed |
| `src/Sections/Humans.GoogleIntegration/Data/GoogleSyncOutboxRepository.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Data/IGoogleResourceRepository.cs` | changed |
| `src/Sections/Humans.GoogleIntegration/Data/IGoogleSyncLogRepository.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Data/IGoogleSyncOutboxRepository.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Data/ISyncSettingsRepository.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Data/Migrations/20260809125108_BaselineGoogleIntegration.Designer.cs` | generated |
| `src/Sections/Humans.GoogleIntegration/Data/Migrations/20260809125108_BaselineGoogleIntegration.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Data/Migrations/20260820172800_AddGoogleSyncLog.Designer.cs` | generated |
| `src/Sections/Humans.GoogleIntegration/Data/Migrations/20260820172800_AddGoogleSyncLog.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Data/Migrations/GoogleIntegrationDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.GoogleIntegration/Data/SyncSettingsRepository.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Docs/GoogleIntegration.md` | changed |
| `src/Sections/Humans.GoogleIntegration/Docs/authorization.md` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Docs/data-access.md` | changed |
| `src/Sections/Humans.GoogleIntegration/Docs/debt.yml` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Docs/features/43-google-group-membership-sync.md` | changed |
| `src/Sections/Humans.GoogleIntegration/Docs/features/google-integration.md` | changed |
| `src/Sections/Humans.GoogleIntegration/Docs/features/google-removal-notifications.md` | changed |
| `src/Sections/Humans.GoogleIntegration/Docs/features/workspace-account-provisioning.md` | changed |
| `src/Sections/Humans.GoogleIntegration/Docs/health.md` | changed |
| `src/Sections/Humans.GoogleIntegration/Domain/GoogleSyncLogEntry.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.ca.resx` | reviewed |
| `src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.cs` | changed |
| `src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.de.resx` | reviewed |
| `src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.es.resx` | reviewed |
| `src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.fr.resx` | reviewed |
| `src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.it.resx` | reviewed |
| `src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.resx` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Health/GoogleWorkspaceHealthCheck.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Humans.GoogleIntegration.csproj` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Jobs/GoogleResourceReconciliationJob.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Jobs/ProcessGoogleSyncOutboxJob.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Models/GoogleSyncViewModels.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Models/SyncServiceNameFormatter.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Models/SyncSettingsViewModels.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Models/TeamSyncViewModels.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Models/WorkspaceEmailViewModels.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Properties/AssemblyInfo.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Section.cs` | changed |
| `src/Sections/Humans.GoogleIntegration/SectionAdminNav.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/SectionConfiguration.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/SectionHealthChecks.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/SectionJobs.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/SectionSettings.cs` | changed |
| `src/Sections/Humans.GoogleIntegration/Services/DriveFolderPath.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/DrivePermissionErrorPolicy.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/DrivePermissionRoleMapper.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/EmailProvisioningService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/EmailRenameDetectionResult.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleAdminService.cs` | changed |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleDriveAccessSyncService.cs` | changed |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleGroupKeyHelper.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleGroupSyncService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleIntegrationEmailPreviews.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleIntegrationEmails.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleIntegrationMetricsService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleRemovalNotificationService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleSyncHistoryMigrationService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleSyncLogService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleSyncMemberHydration.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleSyncOutboxProcessor.cs` | changed |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleSyncOutboxService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleTranslationService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleWorkspaceSyncService.cs` | changed |
| `src/Sections/Humans.GoogleIntegration/Services/GoogleWorkspaceUserService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/GroupSettingsPolicy.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/HangfireGoogleDriveAccessSyncScheduler.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/HangfireGoogleGroupSyncScheduler.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/IGoogleAdminService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/IGoogleDriveAccessSyncScheduler.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/IGoogleDriveSync.cs` | changed |
| `src/Sections/Humans.GoogleIntegration/Services/IGoogleGroupSyncScheduler.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/IGoogleRemovalNotificationService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/IGoogleSyncHistoryMigrationService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/IGoogleSyncLogService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/IGoogleSyncManagement.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/IGoogleWorkspaceUserService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/ISyncSettingsService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/ITeamResourceManagement.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/StubGoogleSyncService.cs` | changed |
| `src/Sections/Humans.GoogleIntegration/Services/SyncSettingsService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/TeamResourceService.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/UserEmailMatchOwner.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleCredentialLoader.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleDirectoryClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleDriveActivityClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleDrivePermissionsClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleGroupMembershipClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleGroupProvisioningClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleTranslationClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/IGoogleDirectoryClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/IGoogleDrivePermissionsClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/IGoogleGroupMembershipClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/IGoogleGroupProvisioningClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/IGoogleTranslationClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/ITeamResourceGoogleClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/IWorkspaceUserDirectoryClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/StubGoogleDirectoryClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/StubGoogleDriveActivityClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/StubGoogleDrivePermissionsClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/StubGoogleGroupMembershipClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/StubGoogleGroupProvisioningClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/StubGoogleTranslationClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/StubTeamResourceGoogleClient.cs` | changed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/StubWorkspaceUserDirectoryClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/TeamResourceGoogleClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Services/Workspace/WorkspaceUserDirectoryClient.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/ViewComponents/GoogleSyncLogViewComponent.cs` | reviewed |
| `src/Sections/Humans.GoogleIntegration/ViewComponents/GoogleSyncSettingsTabViewComponent.cs` | changed |
| `src/Sections/Humans.GoogleIntegration/Views/Google/Accounts.cshtml` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Views/Google/ActivityLog.cshtml` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Views/Google/AllGroups.cshtml` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Views/Google/EmailFlagViolations.cshtml` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Views/Google/EmailRenames.cshtml` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Views/Google/GroupSettingsResults.cshtml` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Views/Google/Index.cshtml` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Views/Google/Sync.cshtml` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Views/Google/SyncAudit.cshtml` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Views/Google/SyncOutbox.cshtml` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Views/Google/SyncResults.cshtml` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Views/Google/_SyncOutboxTable.cshtml` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Views/Google/_SyncTabContent.cshtml` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Views/GoogleSyncHistoryMigrationAdmin/Index.cshtml` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Views/Shared/Components/GoogleSyncLog/Default.cshtml` | reviewed |
| `src/Sections/Humans.GoogleIntegration/Views/Shared/Components/GoogleSyncSettingsTab/Default.cshtml` | changed |
| `src/Sections/Humans.GoogleIntegration/Views/_ViewImports.cshtml` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/Architecture/GoogleWorkspaceSyncBridgeArchitectureTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/Architecture/GoogleWorkspaceUserArchitectureTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/Architecture/TeamResourceArchitectureTests.cs` | changed |
| `tests/Humans.GoogleIntegration.Tests/Browser/sync-preview.test.cjs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/EmailProvisioningServiceTests.cs` | changed |
| `tests/Humans.GoogleIntegration.Tests/Enums/EnumStringStabilityTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/GoogleAdminServiceTests.cs` | changed |
| `tests/Humans.GoogleIntegration.Tests/GoogleControllerSyncAuditTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/GoogleControllerSyncCancellationTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/GoogleCredentiallessProductionTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/GoogleDriveAccessSyncServiceTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/GoogleGroupKeyHelperTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/GoogleGroupSyncServiceTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/GoogleIntegrationEmailsTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/GoogleRemovalNotificationServiceTests.cs` | changed |
| `tests/Humans.GoogleIntegration.Tests/GoogleResourceReconciliationJobTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/GoogleResourceRepositoryTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/GoogleSyncHistoryMigrationServiceTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/GoogleSyncLogActivityLogTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/GoogleSyncLogServiceGdprTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/GoogleSyncOutboxProcessorTests.cs` | changed |
| `tests/Humans.GoogleIntegration.Tests/GoogleSyncOutboxRepositoryTests.cs` | changed |
| `tests/Humans.GoogleIntegration.Tests/GoogleSyncRemovalNotificationIntegrationTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/GoogleSyncSettingsTabViewComponentTests.cs` | changed |
| `tests/Humans.GoogleIntegration.Tests/GoogleWorkspaceSyncServiceReconciliationTests.cs` | changed |
| `tests/Humans.GoogleIntegration.Tests/GoogleWorkspaceSyncServiceTests.cs` | changed |
| `tests/Humans.GoogleIntegration.Tests/GoogleWorkspaceUserServiceTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/HangfireSyncSchedulerTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/Humans.GoogleIntegration.Tests.csproj` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/GoogleDrivePermissionsClientClassifierTests.cs` | changed |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/GoogleDrivePermissionsClientTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/GoogleIntegrationTestHarness.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/GoogleSdkContainment.cs` | changed |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/GoogleWorkspaceSyncBridgeDependencyInjectionTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/StubGoogleDirectoryClientTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/StubGoogleDrivePermissionsClientTests.cs` | changed |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/StubGoogleGroupMembershipClientTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/StubGoogleGroupProvisioningClientTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/StubWorkspaceUserDirectoryClientTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/TestGoogleIntegrationEmails.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/TestMetrics.cs` | changed |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/UserInfoProjection.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/Infrastructure/WorkspaceUserDirectoryClientTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/ProcessGoogleSyncOutboxJobTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/SectionSettingsTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/SyncOutboxViewTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/SyncSettingsServiceTests.cs` | reviewed |
| `tests/Humans.GoogleIntegration.Tests/TeamResourceServiceDeactivateTests.cs` | changed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | session model | findings 1, 2, 3, 10, 15 |
| Behavior & bugs | main | session model | none; no bug found |
| Freshness | subagent (`doctor-reader`) | opus-low | findings 4, 7 |
| Conformance | subagent (`general-purpose`) | haiku | folded into finding 7 |
| Tests | subagent (`doctor-reader`) | opus-low | finding 14; folded into finding 7 |
| Prose & surface | subagent (`general-purpose`); reforge surface-score on main after a build | haiku | findings 6, 13 |
| History | subagent (`doctor-reader`) | opus-low | findings 8, 9 |
| Comments | subagent (`doctor-reader`) | opus-low | findings 5, 11 |
| Inbox | subagent (`doctor-reader`) | opus-low | finding 12 |

