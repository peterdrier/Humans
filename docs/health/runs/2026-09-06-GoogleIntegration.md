# GoogleIntegration — section doctor, 2026-09-06

- **Invocation:** unattended daily run, no arguments. Phase 8 (inline round) skipped.
- **Anchor commit:** `10199a23` (`origin/main`)
- **Branch:** `section-doctor/2026-09-06T031623Z` (cloud run, repo root — no worktree)
- **Budget:** 2.5h, single PR.
- **PR:** peterdrier/Humans#1599

## Assessment summary

First doctor pass over GoogleIntegration, the largest never-doctored section (reforge 1780,
loc=12389, cogP95=9, cogMax=27 in `GoogleResourceReconciliationJob.ExecuteAsync`,
maxClassLoc=1778 in `GoogleWorkspaceSyncService`). The target shape
([`health.md`](../../../src/Sections/Humans.GoogleIntegration/Docs/health.md), written this run
before any scan) finds the structure right as built — outbox + drain, one reconciler per
resource kind, a workspace-admin facade, connector interfaces with stubs — and names two
things it should not be: three public interfaces carrying methods no outside caller asks
(finding 13), and a facade that is also the Drive reconciler (load-bearing, noted).

One behavior bug (finding 1: both drift notifications linked admins to a route deleted when
the sync screens moved under `/Google`). Two small shape defects (findings 2, 3) and one dead
resx key (finding 4). Everything else the section carried was prose written *during* the
G5/§15 migration and never rewritten for the code as it stands: project names that no longer
exist, "coming in Part 2b" on connectors that shipped, a 63-line lane chronicle in the
Contracts csproj, a Status paragraph false in three places (findings 8, 9, 10). The
second-opinion reviewer approved the three non-mechanical strikes (5, 7, 12) with conditions,
all applied. The target's own §4 was wrong twice on first writing (finding 11) — caught by the
Tests thread, corrected.

## Ranked findings

Value = bug surface removed, then concepts removed, then words removed.

| # | Finding | Value | Disposition |
|---|---|---|---|
| 1 | **`GoogleResourceReconciliationJob` sent both Admin drift notifications with `actionUrl: "/Admin/GoogleSync"`**, a route the section's own feature doc records as removed; the live page is `/Google/Sync`. Fixed with a test; `Humans.Users/Docs/features/profiles.md` narrated the same dead route and was corrected. | high | **worked** |
| 2 | **`TeamSyncViewModel` was an empty class** passed to `Sync.cshtml`, which never reads `Model`. Deleted. | low | **worked** |
| 3 | **`GoogleController.SyncSettings` / `SyncOutbox` re-injected `[FromServices] IUserServiceRead`**, shadowing the base controller's `UserService`. Dropped. | low | **worked** |
| 4 | **`GoogleAccounts_ResetPasswordConfirm` had no renderer** in any view; two comments counted "three keys". Removed from all six cultures; comments say two. | low | **worked** |
| 5 | **`ITeamResourceService.LinkDriveFolderAsync` / `LinkDriveFileAsync` had no caller outside `TeamResourceService`** (`LinkDriveResourceAsync` dispatches on URL shape). Made private; feature doc names the dispatcher. Reviewer: approve with condition (doc sweep) — applied. | med | **worked** |
| 6 | **`tests/.../Infrastructure/UserInfoProjection.cs` reported as zero-reference.** False: its `ToUserInfo` extension is called by name from `GoogleAdminServiceTests`. The deletion broke the build and was reverted before push. | — | **not a defect** |
| 7 | **Two per-type SDK-containment tests are subsumed** by `GoogleWorkspaceSyncBridgeArchitectureTests.SectionServiceLayer_NamesNoGoogleSdkType`, which sweeps the same namespace and guards against an empty sweep. Deleted in-run on the second-opinion reviewer's approval, then restored in review round 3: `brief-before-retiring-guardrails` requires Peter's go before any architecture test retires, and the reviewer's approval is not that. Peter gave the go on the brief (2026-09-11): both deleted, the sweep is the only containment assertion now, and the invariant doc's architecture-tests bullet names the real files. | med | **worked** |
| 8 | **Stale doc claims:** a `/Google/FixEmailRename` route row with no action; `google_sync_outbox_events` as the table name (three sites); `IGoogleSyncOutboxProcessor` remarks placing the job "in Contracts/"; connectors "in Humans.Infrastructure"; `GoogleWorkspaceOptions` "lives in Humans.Base.Configuration"; `EmailProvisioningService` "used by HumanController"; `/Teams/Sync` as current; `FailedPermanently` doc missing 403; provisioning doc triggered on `AdminController.cs`, which names nothing of it; the invariant doc's triggers missing four cross-section files it asserts about. All fixed. | med | **worked** |
| 9 | **Migration history in comments and docs:** the Contracts csproj lane chronicle, the `SyncAction` reverted-lane saga, job remarks "moved out of Humans.Infrastructure", "§15 Part 2b" / "Humans.Application" across the seven connector interfaces, their implementations and stubs, the invariant doc's Status paragraph and "pending targets" subsection, the feature docs' "formerly at" lines, test-file "old assertion" comments. Cut; the Hangfire serialization landmine, the Directory-API-adds-external-addresses note, `SupportsAllDrives`, and the leaf's two load-bearing absences stay. | med | **worked** |
| 10 | **Restating comments:** ten `GoogleResource` property docs repeating the property name, `// ====` region banners in two repositories and `IGoogleSyncService`, six `// Phase N` labels beside self-describing calls, a numbered step list, two Razor region markers. Cut. | low | **worked** |
| 11 | **The target shape's §4 was wrong twice:** it said admin-triggered actions bypass sync mode (the invariant doc and a passing test say mode gates every Execute) and that all `/Google/*` deny non-Admin (Sync/Preview admit TeamsAdmin and Board; ProvisionEmail admits HumanAdmin). Corrected, plus the run-derivation subtitle dropped per peterdrier/Humans#1590. | med | **worked** |
| 12 | **`GoogleWorkspaceUserServiceTests`: six of seven tests asserted the substitute they arranged.** Collapsed to the blank-last-name guard plus the `ProvisionAccountAsync` forwarding smoke (five same-typed strings, where a swap compiles). Reviewer: approve with condition — applied. | low | **worked** |
| 13 | **Over-exposed public interfaces:** most of `IGoogleSyncService` and `ITeamResourceService`, and part of `IGoogleGroupSync`, have no caller outside the section. The shapes table wants one internal interface per reconciler and a leaf carrying only what Teams, Users, Monitor, Surveys and Notifications ask. Rearch, its own PR — Peter (2026-09-11): filed as nobodies-collective/Humans#1180. | high | **filed** |
| 14 | **Conformance `section-file-layout` flags `Health/`** at the project root; five sections carry it (Agent, Email, Guide, Tickets, GoogleIntegration). The allow-set is behind, not the sections; conformance rows change only at Peter's direction. Peter (2026-09-11): add it. Already added by /section-doctor on Tickets (peterdrier/Humans#1589) and carried in on this branch's base merge — no edit needed here. | low | **worked elsewhere** |
| 15 | **`resource-key-prefix`:** the section's two keys use `GoogleAccounts_`, not `GoogleIntegration_`. Backlog, count only. | low | **no change** |
| 16 | **`Docs/features/drive-activity-monitoring.md` documents a feature Monitor owns end to end** (service, job, schedule); its home is Monitor's `Docs/features/`. Cross-section move — Peter (2026-09-11): move it. Moved, with the four inbound links repointed (`docs/README.md`, `docs/features/global/background-jobs.md`, AuditLog's and GoogleIntegration's feature docs). | low | **worked** |
| 17 | **Test gaps with a positive pin available:** the processor's permanent-failure path (400/403/404 parks without retry and marks the address Rejected), per-event requeue, valid-after-real-add on the drain path, the provisioning audit entry, `Section.ConfigureServices` real-vs-stub by credentials, a per-action `[Authorize]` table for `GoogleController`. `GoogleWorkspaceSyncBridgeDependencyInjectionTests` asserts its own registrations, not `Section.cs`. | med | **queued** |
| 18 | **`Humans.Base/Resources/SharedResource*.resx` carries ten dead `GoogleSync_*` / `AdminGoogleSync_*` keys** (nothing renders them). Base's set — sweep queue. | low | **queued** |
| 19 | **`Views/Google/Index.cshtml` renders `<vc:access-matrix section="Google" />` to nothing:** `AccessMatrixDefinitions` has no "Google" entry. Content gap — Peter (2026-09-11): remove for now. Tag and its explanatory comment dropped from the view. | low | **worked** |
| 20 | **Ledger seams confirmed still open:** `SyncExecute` reconciles inline on the request thread; `GoogleWorkspaceHealthCheck` calls `Google.Apis` directly; no `ITeamResourceServiceRead` for the Teams and Monitor reads. Already in `debt-ledger.yml`; recorded as seams in `health.md` §5. | — | **no change** |
| 21 | **Inbox:** seven open issues on peterdrier/Humans, none section-tagged (six section-doctor skill issues, one repo-wide localization sweep report). No verdicts to give. In-app issues not reachable from the cloud container. | — | **no change** |
| 22 | **Lesson (Phase 3, Tests thread — proposed edit):** a "zero references" claim on a file must grep its public member names as well as its type names; extension methods are called by method name and the type never appears at a call site (finding 6 shipped as "dead" on a type-name grep). Peter (2026-09-11): no — the build caught it, and atoms like this are a bad use of context. | — | **declined** |

## Worked

Findings 1–5, 7–12, one commit per strike, after the target shape:

- `doctor(GoogleIntegration): first target shape (health.md)`
- `doctor(GoogleIntegration): point drift notifications at /Google/Sync` — finding 1.
- `doctor(GoogleIntegration): drop shadowing IUserServiceRead action parameters` — finding 3.
- `doctor(GoogleIntegration): delete the empty TeamSyncViewModel` — finding 2.
- `doctor(GoogleIntegration): delete the unrendered GoogleAccounts_ResetPasswordConfirm key` — finding 4.
- `doctor(GoogleIntegration): fix stale doc claims` — finding 8.
- `doctor(GoogleIntegration): cut migration history from comments and docs` — finding 9.
- `doctor(GoogleIntegration): cut restating comments and section banners` — finding 10.
- `doctor(GoogleIntegration): delete the two per-type SDK-containment tests` — finding 7;
  the deletion was reverted in review round 3 (see the finding), the doc rewrite kept.
- `doctor(GoogleIntegration): take LinkDriveFolderAsync/LinkDriveFileAsync off the public contract` — finding 5.
- `doctor(GoogleIntegration): collapse GoogleWorkspaceUserService forwarding tests` — finding 12.
- `doctor(GoogleIntegration): correct the target shape` — finding 11.

Surfaces hit: **localization** — one key removed from all six cultures by exact-string
replacement ([`resx-value-edits`](../../../memory/process/resx-value-edits.md)); parity tests
pass. **Authorization** — no behavior change; the contract narrowing (finding 5) leaves
`LinkDriveResourceAsync`, the only entry `TeamAdminController` uses, in place.
**Audit** — unchanged. **GDPR** — untouched; no personal data added or moved.
**Invariant doc** — `GoogleIntegration.md` corrected (route table, triggers, architecture-tests
bullet, history cut) and consistent with the struck code. **Migrations** — none; no schema
change. **Navigation** — the drift notification now reaches a live page (finding 1).
**Tests** — one added (finding 1), eight deleted (findings 7, 12); the section project passes
(`dotnet test tests/Humans.GoogleIntegration.Tests`), and `tests/Humans.Teams.Tests` builds
against the narrowed contract. View changes are a dropped `@model` line and two Razor
comments; render tests for these pages live in `Humans.Integration.Tests`, local-only.

## Skipped

Finding 6 (reverted — not dead) and findings 13–22 (dispositions above; 17 and 18 to the
sweep queue, 13, 14, 16, 19, 22 to Needs Peter, all answered 2026-09-11 and applied — 13 to an
issue, 22 declined). Finding 7 went to Needs Peter after its round-3 revert and shipped on
Peter's go.

Sections passed over as blocked: 20 by open PR (per `selection.txt`).

## Threads

Raw per-thread finding counts before consolidation into the ranked list above.

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | session default (see cost comment) | 4 → findings 2, 3, 5, 13 |
| Behavior & bugs | main | session default (see cost comment) | 2 → findings 1, 19 |
| Freshness | subagent (doctor-reader) | opus (low effort) | 13 → findings 8, 16 |
| Tests | subagent (doctor-reader) | opus (low effort) | 7 + a gap matrix → findings 6, 7, 11, 12, 17 |
| History | subagent (doctor-reader) | opus (low effort) | 21 → finding 9 (one claim wrong: `G5-SECTION-TEMPLATE.md` exists; step citations kept) |
| Comments | subagent (doctor-reader) | opus (low effort) | 11 → finding 10 |
| Prose & surface + Conformance | subagent (one combined run) | haiku | 2 + conformance table → findings 4, 14, 15, 18 |
| Inbox | main (self-run: no subagent dispatched) | session default (see cost comment) | finding 21. Fork-only scope: zero section-tagged issues on peterdrier/Humans; ledger reviewed (finding 20). In-app issues not reachable from this container (per-session limitation, not a rule) |
| Second opinion (findings 5, 7, 12) | subagent (doctor-reviewer) | session default (see cost comment) | 3 verdicts, all approve-with-condition; every condition applied before commit |

Independence check: pass.

## Retro

**What the selector/rubric got wrong:** nothing wrong, one thing worth knowing. The
"median never-doctored by reforge score" pick landed on a section eleven times the size of the
previous pick (Settings, loc=1096). The budget held only because the strikes were
comment- and doc-heavy; a section this size with real behavior debt would not fit 2.5h.

**Wasted motion:** the Tests thread's "zero references" verdict on `UserInfoProjection.cs`
(finding 6) cost a broken build and a revert — the file's extension method is called by name.
The History thread claimed `docs/sections/G5-SECTION-TEMPLATE.md` no longer exists (it does),
which would have cut every step citation had it been trusted. Both settled by re-checking.
Every review round returned a hand-written count (`no-derived-aggregates-in-docs`) in
`health.md` or this file — an interface tally, a list size, a line count, a method ratio. The
target shape and the run file should be written predicate-first; a number in either is the
next round's finding.

**What the assessment missed that striking revealed:** the history cut kept finding more once
inside the files — `IGoogleSyncOutboxRepository`'s remarks and `IGoogleSyncServiceRead`'s
`GetPending` doc carried Part 1 / Part 2c / #554 narration the History thread had not
listed. On a 110-file section a single History thread saturates around twenty items; the
striker has to read the whole file it is in, not just the flagged lines. And the target shape
written before the scan absorbed two wrong invariants from the section's own older prose
(finding 11) — the thread cross-check is what caught them.

**Target diff:** none possible — first doctor pass; `health.md` was written this run.

Two auto-compactions occurred (mid-Phase 3 assessment, mid-Phase 4); Phase 5's mandatory
re-read of Phases 5–7 was applied after the second.

## Needs Peter

All answered by Peter on 2026-09-11.

- [x] 13 — split `IGoogleSyncService` / `ITeamResourceService` / `IGoogleGroupSync` into internal reconciler interfaces plus a narrowed leaf. **File an issue to fix** → nobodies-collective/Humans#1180.
- [x] 14 — add `Health/` to `section-file-layout`'s allow-set. **Add.** Already added by /section-doctor on Tickets (peterdrier/Humans#1589); carried in on this branch's base merge.
- [x] 16 — move `drive-activity-monitoring.md` to Monitor's `Docs/features/`. **Move.** Done, four inbound links repointed.
- [x] 19 — a "Google" entry in `AccessMatrixDefinitions`, or drop the tag from `/Google`. **Remove for now.** Tag dropped from `Views/Google/Index.cshtml`.
- [x] 22 — make the "zero references must grep member names too" lesson a `memory/` atom. **No** — the build caught it, and atoms like this are a bad use of context.
- [x] 7 — retire the two subsumed per-type SDK-containment tests. **Go.** `GoogleIntegrationArchitectureTests.cs` deleted and `GoogleWorkspaceUserService_DoesNotReferenceGoogleSdkTypes` removed; `GoogleWorkspaceSyncBridgeArchitectureTests.SectionServiceLayer_NamesNoGoogleSdkType` is the containment assertion, and the invariant doc's architecture-tests bullet names the real files.
- [x] Skill gap behind 7: the section-doctor reviewer gate never routes a guardrail retirement through `brief-before-retiring-guardrails` — peterdrier/Humans#1600.
- [x] `Section.cs` throws in Production without Google credentials, against the hard rule `no-startup-guards`. **File an issue to fix** → nobodies-collective/Humans#1179.

## Sweep queue

- debt: GoogleIntegration — test gaps with a positive pin available: the processor's permanent-failure path (400/403/404 parks without retry and marks the address Rejected), per-event requeue, valid-after-real-add on the drain path, the provisioning audit entry, `Section.ConfigureServices` real-vs-stub by credentials, a per-action `[Authorize]` table for `GoogleController` (finding 17, 2026-09-06-GoogleIntegration)
- debt: Base — `src/Humans.Base/Resources/SharedResource*.resx` carries ten dead `GoogleSync_*` / `AdminGoogleSync_*` keys that nothing renders; deletable under `localization-admin-exempt` (finding 18, 2026-09-06-GoogleIntegration)

## File coverage

`generated` = excluded from review per the skill.

**Changed:**
`src/Sections/Humans.GoogleIntegration.Contracts/DriveActivityEvent.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/GoogleResource.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/GoogleSyncOutboxEvent.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/GoogleWorkspaceOptions.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/Humans.GoogleIntegration.Contracts.csproj` ·
`src/Sections/Humans.GoogleIntegration.Contracts/IGoogleDriveActivityClient.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/IGoogleSyncOutboxProcessor.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/IGoogleSyncService.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/IGoogleSyncServiceRead.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/ITeamResourceService.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/SyncAction.cs` ·
`src/Sections/Humans.GoogleIntegration/Controllers/GoogleController.cs` ·
`src/Sections/Humans.GoogleIntegration/Data/GoogleSyncOutboxRepository.cs` ·
`src/Sections/Humans.GoogleIntegration/Data/IGoogleResourceRepository.cs` ·
`src/Sections/Humans.GoogleIntegration/Data/IGoogleSyncOutboxRepository.cs` ·
`src/Sections/Humans.GoogleIntegration/Data/ISyncSettingsRepository.cs` ·
`src/Sections/Humans.GoogleIntegration/Data/SyncSettingsRepository.cs` ·
`src/Sections/Humans.GoogleIntegration/Docs/GoogleIntegration.md` ·
`src/Sections/Humans.GoogleIntegration/Docs/features/google-integration.md` ·
`src/Sections/Humans.GoogleIntegration/Docs/features/workspace-account-provisioning.md` ·
`src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.ca.resx` ·
`src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.cs` ·
`src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.de.resx` ·
`src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.es.resx` ·
`src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.fr.resx` ·
`src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.it.resx` ·
`src/Sections/Humans.GoogleIntegration/GoogleIntegrationResource.resx` ·
`src/Sections/Humans.GoogleIntegration/Humans.GoogleIntegration.csproj` ·
`src/Sections/Humans.GoogleIntegration/Jobs/GoogleResourceReconciliationJob.cs` ·
`src/Sections/Humans.GoogleIntegration/Jobs/ProcessGoogleSyncOutboxJob.cs` ·
`src/Sections/Humans.GoogleIntegration/Models/TeamSyncViewModels.cs` ·
`src/Sections/Humans.GoogleIntegration/Section.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/EmailProvisioningService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/GoogleAdminService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/GoogleIntegrationMetricsService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/GoogleSyncOutboxProcessor.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/GoogleTranslationService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/IGoogleGroupSyncScheduler.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/TeamResourceService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleCredentialLoader.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleDirectoryClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleDriveActivityClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleDrivePermissionsClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleGroupProvisioningClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/IGoogleDirectoryClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/IGoogleDrivePermissionsClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/IGoogleGroupMembershipClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/IGoogleGroupProvisioningClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/IGoogleTranslationClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/ITeamResourceGoogleClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/IWorkspaceUserDirectoryClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/StubGoogleDirectoryClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/StubGoogleDriveActivityClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/StubGoogleDrivePermissionsClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/StubGoogleGroupMembershipClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/StubGoogleGroupProvisioningClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/StubGoogleTranslationClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/StubWorkspaceUserDirectoryClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/WorkspaceUserDirectoryClient.cs` ·
`src/Sections/Humans.GoogleIntegration/ViewComponents/GoogleSyncLogViewComponent.cs` ·
`src/Sections/Humans.GoogleIntegration/Views/Google/Accounts.cshtml` ·
`src/Sections/Humans.GoogleIntegration/Views/Google/Sync.cshtml` ·
`src/Sections/Humans.GoogleIntegration/Views/_ViewImports.cshtml` ·
`tests/Humans.GoogleIntegration.Tests/Architecture/GoogleIntegrationArchitectureTests.cs` (deleted) ·
`tests/Humans.GoogleIntegration.Tests/Architecture/GoogleWorkspaceSyncBridgeArchitectureTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/Architecture/GoogleWorkspaceUserArchitectureTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/EmailProvisioningServiceTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/Enums/EnumStringStabilityTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/GoogleResourceReconciliationJobTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/GoogleWorkspaceUserServiceTests.cs` ·
`src/Sections/Humans.GoogleIntegration/Docs/health.md` (new) ·
outside the section: `src/Sections/Humans.Users/Docs/features/profiles.md`

**Reviewed:**
`src/Sections/Humans.GoogleIntegration.Contracts/DomainGroupInfo.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/DrivePermissionLevel.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/GoogleResourceType.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/GoogleSyncOutboxEventTypes.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/GroupLinkResult.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/GroupSettingsDriftResult.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/IEmailProvisioningService.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/IGoogleGroupMembershipSource.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/IGoogleGroupSync.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/IGoogleSyncOutboxService.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/IGoogleTranslationService.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/LinkResourceResult.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/ResourceSyncDiff.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/SyncMode.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/SyncServiceSettings.cs` ·
`src/Sections/Humans.GoogleIntegration.Contracts/SyncServiceType.cs` ·
`src/Sections/Humans.GoogleIntegration/Contracts/IGoogleSyncLogViewer.cs` ·
`src/Sections/Humans.GoogleIntegration/Controllers/GoogleSyncHistoryMigrationAdminController.cs` ·
`src/Sections/Humans.GoogleIntegration/Data/Configurations/GoogleResourceConfiguration.cs` ·
`src/Sections/Humans.GoogleIntegration/Data/Configurations/GoogleSyncLogEntryConfiguration.cs` ·
`src/Sections/Humans.GoogleIntegration/Data/Configurations/GoogleSyncOutboxEventConfiguration.cs` ·
`src/Sections/Humans.GoogleIntegration/Data/Configurations/SyncServiceSettingsConfiguration.cs` ·
`src/Sections/Humans.GoogleIntegration/Data/GoogleIntegrationDbContext.cs` ·
`src/Sections/Humans.GoogleIntegration/Data/GoogleIntegrationDbContextFactory.cs` ·
`src/Sections/Humans.GoogleIntegration/Data/GoogleResourceRepository.cs` ·
`src/Sections/Humans.GoogleIntegration/Data/GoogleSyncLogRepository.cs` ·
`src/Sections/Humans.GoogleIntegration/Data/IGoogleSyncLogRepository.cs` ·
`src/Sections/Humans.GoogleIntegration/Data/Migrations/20260809125108_BaselineGoogleIntegration.cs` ·
`src/Sections/Humans.GoogleIntegration/Data/Migrations/20260820172800_AddGoogleSyncLog.cs` ·
`src/Sections/Humans.GoogleIntegration/Docs/authorization.md` ·
`src/Sections/Humans.GoogleIntegration/Docs/data-access.md` ·
`src/Sections/Humans.GoogleIntegration/Docs/features/43-google-group-membership-sync.md` ·
`src/Sections/Humans.GoogleIntegration/Docs/features/drive-activity-monitoring.md` (moved to Monitor) ·
`src/Sections/Humans.GoogleIntegration/Docs/features/google-removal-notifications.md` ·
`src/Sections/Humans.GoogleIntegration/Domain/GoogleSyncLogEntry.cs` ·
`src/Sections/Humans.GoogleIntegration/Health/GoogleWorkspaceHealthCheck.cs` ·
`src/Sections/Humans.GoogleIntegration/Models/GoogleSyncViewModels.cs` ·
`src/Sections/Humans.GoogleIntegration/Models/SyncSettingsViewModels.cs` ·
`src/Sections/Humans.GoogleIntegration/Models/WorkspaceEmailViewModels.cs` ·
`src/Sections/Humans.GoogleIntegration/Properties/AssemblyInfo.cs` ·
`src/Sections/Humans.GoogleIntegration/SectionAdminNav.cs` ·
`src/Sections/Humans.GoogleIntegration/SectionHealthChecks.cs` ·
`src/Sections/Humans.GoogleIntegration/SectionJobs.cs` ·
`src/Sections/Humans.GoogleIntegration/SectionMemberDashboard.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/EmailRenameDetectionResult.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/GoogleGroupKeyHelper.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/GoogleGroupSyncService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/GoogleRemovalNotificationService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/GoogleSyncHistoryMigrationService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/GoogleSyncLogService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/GoogleSyncOutboxService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/GoogleWorkspaceSyncService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/GoogleWorkspaceUserService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/GroupSettingsPolicy.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/HangfireGoogleGroupSyncScheduler.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/IGoogleAdminService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/IGoogleRemovalNotificationService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/IGoogleSyncHistoryMigrationService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/IGoogleSyncLogService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/IGoogleWorkspaceUserService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/ISyncSettingsService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/StubGoogleSyncService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/SyncSettingsService.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/UserEmailMatchOwner.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleGroupMembershipClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/GoogleTranslationClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/StubTeamResourceGoogleClient.cs` ·
`src/Sections/Humans.GoogleIntegration/Services/Workspace/TeamResourceGoogleClient.cs` ·
`src/Sections/Humans.GoogleIntegration/ViewComponents/MyGoogleResourcesViewComponent.cs` ·
`src/Sections/Humans.GoogleIntegration/Views/Google/AllGroups.cshtml` ·
`src/Sections/Humans.GoogleIntegration/Views/Google/EmailFlagViolations.cshtml` ·
`src/Sections/Humans.GoogleIntegration/Views/Google/EmailRenames.cshtml` ·
`src/Sections/Humans.GoogleIntegration/Views/Google/GroupSettingsResults.cshtml` ·
`src/Sections/Humans.GoogleIntegration/Views/Google/Index.cshtml` ·
`src/Sections/Humans.GoogleIntegration/Views/Google/SyncOutbox.cshtml` ·
`src/Sections/Humans.GoogleIntegration/Views/Google/SyncResults.cshtml` ·
`src/Sections/Humans.GoogleIntegration/Views/Google/SyncSettings.cshtml` ·
`src/Sections/Humans.GoogleIntegration/Views/Google/_SyncOutboxTable.cshtml` ·
`src/Sections/Humans.GoogleIntegration/Views/Google/_SyncTabContent.cshtml` ·
`src/Sections/Humans.GoogleIntegration/Views/Google/_ViewStart.cshtml` ·
`src/Sections/Humans.GoogleIntegration/Views/GoogleSyncHistoryMigrationAdmin/Index.cshtml` ·
`src/Sections/Humans.GoogleIntegration/Views/Shared/Components/GoogleSyncLog/Default.cshtml` ·
`src/Sections/Humans.GoogleIntegration/Views/Shared/Components/MyGoogleResources/Default.cshtml` ·
`tests/Humans.GoogleIntegration.Tests/Architecture/TeamResourceArchitectureTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/GoogleAdminServiceTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/GoogleControllerSyncCancellationTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/GoogleGroupKeyHelperTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/GoogleGroupSyncServiceTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/GoogleRemovalNotificationServiceTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/GoogleResourceRepositoryTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/GoogleSyncHistoryMigrationServiceTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/GoogleSyncOutboxProcessorTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/GoogleSyncOutboxRepositoryTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/GoogleSyncRemovalNotificationIntegrationTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/GoogleWorkspaceSyncServiceReconciliationTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/GoogleWorkspaceSyncServiceTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/Humans.GoogleIntegration.Tests.csproj` ·
`tests/Humans.GoogleIntegration.Tests/Infrastructure/GoogleDrivePermissionsClientClassifierTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/Infrastructure/GoogleIntegrationTestHarness.cs` ·
`tests/Humans.GoogleIntegration.Tests/Infrastructure/GoogleSdkContainment.cs` ·
`tests/Humans.GoogleIntegration.Tests/Infrastructure/GoogleWorkspaceSyncBridgeDependencyInjectionTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/Infrastructure/StubGoogleDirectoryClientTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/Infrastructure/StubGoogleDrivePermissionsClientTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/Infrastructure/StubGoogleGroupMembershipClientTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/Infrastructure/StubGoogleGroupProvisioningClientTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/Infrastructure/TestMetrics.cs` ·
`tests/Humans.GoogleIntegration.Tests/Infrastructure/UserInfoProjection.cs` ·
`tests/Humans.GoogleIntegration.Tests/SyncSettingsServiceTests.cs` ·
`tests/Humans.GoogleIntegration.Tests/TeamResourceServiceDeactivateTests.cs`

**Generated:**
`src/Sections/Humans.GoogleIntegration/Data/Migrations/20260809125108_BaselineGoogleIntegration.Designer.cs` ·
`src/Sections/Humans.GoogleIntegration/Data/Migrations/20260820172800_AddGoogleSyncLog.Designer.cs` ·
`src/Sections/Humans.GoogleIntegration/Data/Migrations/GoogleIntegrationDbContextModelSnapshot.cs`
