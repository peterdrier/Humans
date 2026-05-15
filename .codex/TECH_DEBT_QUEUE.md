# Humans Tech Debt Queue

Last updated: 2026-05-15
Worktree: `H:\source\humans\.worktrees\techdebt-2026-05-15-codex-1`
Branch: `techdebt/2026-05-15-codex-1`

This file is the durable work queue for autonomous tech-debt passes. Resume here before doing new discovery.

## Mission

Clear or materially shrink the architecture baselines in `tests/Humans.Application.Tests/Architecture/Baselines` by making real code improvements, not by hiding violations. Use `docs/architecture/**` and `memory/architecture/**` as the target-state rules for finding additional debt after the obvious baseline queue is exhausted.

The loop should continue until one of these is true:

1. All feasible baseline entries are removed.
2. Remaining entries are blocked by forbidden areas or intentionally accepted architecture exceptions and are documented here.
3. Validation fails in a way that requires human input.
4. The Codex session is interrupted by tooling/session limits.

Do not stop after an arbitrary batch. If interrupted, the next run should resume from this file.

## Non-Negotiable Limits

Do not touch or alter persistence/storage behavior:

- `src/Humans.Infrastructure/Data/HumansDbContext.cs`
- `src/Humans.Infrastructure/Data/EntityConfigurations/**`
- `src/Humans.Infrastructure/Migrations/**`
- Entity class shapes under `src/Humans.Domain/Entities/**`
- JSON serialization attributes
- Migration files, generated migration operations, or DB schema behavior

Do not remove public actions/members just to satisfy a ratchet. Prefer replacing read surfaces with DTOs/snapshots and preserving callers' behavior.

## Loop Protocol

For each iteration:

1. Pick one high-value baseline entry or one small coherent cluster from the same rule/method.
2. Inspect only the files needed for that item.
3. Make the smallest real refactor that removes the violation.
4. Remove stale baseline line(s) only after the code no longer triggers the rule.
5. Run targeted tests for the touched slice when available.
6. Run `dotnet build Humans.slnx --disable-build-servers -v q`.
7. Commit one coherent improvement with a focused message.
8. Push the branch.
9. Update this file only when queue status, blocked status, or strategy changes.

Recommended recurring validation:

```powershell
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~Application_service_read_methods_do_not_add_new_entity_return_types"
dotnet build Humans.slnx --disable-build-servers -v q
```

Run broader application tests periodically when the branch has several commits:

```powershell
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --no-build --disable-build-servers -v q --filter "FullyQualifiedName~Application"
```

## Baseline Inventory

Active entries as of this queue creation:

| Baseline | Entries | Priority | Strategy |
| --- | ---: | --- | --- |
| `NoObsoleteNavReads.baseline.txt` | 231 | P1 | Remove real cross-domain nav reads by using owning services, IDs, and snapshots. Avoid entity/config forbidden paths. |
| `NoBusinessLogicInControllers.baseline.txt` | 143 | P2 | Move orchestration/business decisions into application services or web presentation helpers. Do not just split methods for line count. |
| `ApplicationServiceEntityReadReturns.baseline.txt` | 111 | P0 | Replace service read returns of EF/domain entities with DTOs/snapshots. This has been the highest-value productive lane. |
| `DisplaySortInControllers.baseline.txt` | 105 | P3 | Move reusable domain ordering out of controllers; keep screen-only sort at web boundary if rule is noisy/intentional. |
| `NoCrossSectionEfJoins.baseline.txt` | 26 | P1/P-blocked | Fix non-forbidden service/repository joins/includes. Entries in `EntityConfigurations/**` are blocked by storage limits. |
| `NoDestructiveMigrationOps.baseline.txt` | 20 | Blocked by default | Migration-file entries are forbidden. Do not edit migrations; only classify/document unless a non-migration false positive appears. |
| `NoLinqAtDbLayer.baseline.txt` | 2 | P2 | Inspect for repository/business LINQ leakage. Fix only if not storage-shape behavior. |
| `CrossSectionRepositoryInjection.baseline.txt` | 1 | P1 | Replace cross-section repository injection with owning service call if not forbidden. |
| `OnlyAuditLogRepositoryWritesAuditLogEntries.baseline.txt` | 1 | P1 | Route direct audit writes through audit service/repository owner. |
| `NoConcurrencyTokens.baseline.txt` | 0 | Done | Keep at zero. |
| `NoStartupGuards.baseline.txt` | 0 | Done | Keep at zero. |
| `OnlyNotificationRepositoryWritesNotificationDbSets.baseline.txt` | 0 | Done | Keep at zero. |

Total active baseline entries: 640.

Regenerate counts with:

```powershell
Get-ChildItem tests/Humans.Application.Tests/Architecture/Baselines -File |
  Sort-Object Name |
  ForEach-Object {
    $count = (Get-Content $_.FullName | Where-Object { $_ -and -not $_.TrimStart().StartsWith('#') }).Count
    [pscustomobject]@{ Name = $_.Name; Entries = $count }
  } | Format-Table -AutoSize
```

## Primary Work Lanes

### Lane A: Service Entity Boundary Ratchet

Source rule: `docs/architecture/service-entity-boundary-ratchet.md`.

Target: service read APIs should not expose EF/domain entities across section boundaries. Prefer records/snapshots/DTOs that contain only fields callers need.

Good candidates:

- Low caller count methods in `ApplicationServiceEntityReadReturns.baseline.txt`.
- Existing DTO-like methods that still embed entities.
- Controller/admin read paths where the web layer immediately maps entity fields into view models.

Avoid:

- Mutation methods returning entities unless already touching the command shape.
- Huge surfaces like agent conversations or team page detail unless taking the full DTO shape coherently.
- User/Profile foundational raw `User` methods unless the replacement identity snapshot is clear.

Useful ranking command:

```powershell
$baseline = Get-Content tests/Humans.Application.Tests/Architecture/Baselines/ApplicationServiceEntityReadReturns.baseline.txt |
  Where-Object { $_ -and -not $_.StartsWith('#') }
$baseline | ForEach-Object {
  if ($_ -match '\.([A-Za-z0-9_]+)Async:') {
    $method = $matches[1] + 'Async'
    $count = (rg -n "$method\(" src tests | Measure-Object).Count
    [pscustomobject]@{ Count = $count; Line = $_ }
  }
} | Sort-Object Count | Select-Object -First 25 | Format-Table -AutoSize
```

### Lane B: Obsolete Navigation Reads

Source rules: `docs/architecture/design-rules.md`, especially sections 2 and 6.

Target: remove reads of obsolete cross-domain navigation properties. Use FK IDs and owning services instead.

Good candidates:

- Application services reading `.User`, `.Team`, `.CreatedByUser`, `.ResolvedByUser`, etc. only for display fields.
- DTO constructors that can receive display fields from a dictionary loaded via `IUserService` or `ITeamService`.
- Web/controller composition where service already returns IDs.

Avoid:

- Entity configuration entries.
- Entity-shape changes.
- Migration-adjacent cleanup.

### Lane C: Controller Business Logic

Source rule: `memory/architecture/no-business-logic-in-controllers.md`.

Target: controllers should authorize, call services, map to view models, and return responses. Business decisions and reusable orchestration belong in application services; pure presentation mapping can live in small web helpers.

Good candidates:

- Actions with branching over domain result/error keys.
- Repeated flash-message or redirect decision mapping.
- Controller-side loops that mutate domain state or coordinate multiple services.

Avoid:

- Moving one-off view-model mapping into application services just to reduce line count.
- Adding service methods for screen-only sorting/filtering.

### Lane D: Cross-Section EF Joins and Repository Boundary

Source rules: `docs/architecture/design-rules.md`, `memory/architecture/no-cross-section-ef-joins.md`, `memory/architecture/repository-required-for-db-access.md`.

Target: repositories own persistence for their section only. Cross-section reads go through owning service interfaces and in-memory stitching.

Good candidates:

- Infrastructure repository includes/joins that pull another section only for display.
- Application services injecting another section's repository.
- Web classes injecting repositories.

Avoid:

- `EntityConfigurations/**` entries.
- DB schema, cascade behavior, FK shape, migrations.

### Lane E: Display Sort and LINQ at DB Layer

Source rules: `memory/architecture/display-sort-in-controllers.md`, `memory/architecture/no-linq-at-db-layer.md`.

Target: reusable domain ordering belongs in services; screen-specific sorting/caps belong at controller/view boundary for finite data. Unbounded operational tables should keep explicit DB-side paging/windowing and may need documented exceptions.

Good candidates:

- Repository methods sorting finite/cached sets for one screen.
- Services/controllers applying the same business ordering repeatedly.

Avoid:

- Audit/outbox/history top-N queries where DB-side ordering/paging is intentional.

## Completed On This Branch

Recent pushed commits on `techdebt/2026-05-15-codex-1`:

| Commit | Summary |
| --- | --- |
| `3ac1a8d5e` | Move user membership ordering out of repository. |
| `0b36f9282` | Route coordinator lookup through team repository. |
| `94424f02d` | Route database diagnostics through repository. |
| `5046bec97` | Move purge login cleanup into account deletion service. |
| `33b99180e` | Avoid user nav reads in team detail. |
| `a643829a7` | Avoid user nav reads in roster slots. |
| `2892cadbd` | Extract guest deletion request flash mapping. |
| `176b3b721` | Return role assignment snapshots to agent context. |
| `bc8f076f8` | Return Google outbox event snapshots. |
| `9c8e6bde3` | Return communication preference snapshots. |
| `1c07ce5d2` | Return account merge snapshots. |
| `7e4c81491` | Return Google resource snapshots. |
| `bd6a481fe` | Return legal document edit snapshots. |
| `424322c15` | Flatten onboarding review detail DTO. |
| `68fc21d33` | Return submitted application snapshots. |
| `cf7875885` | Return orphan email snapshots. |

Earlier small documentation/intent commits also exist on this branch for ticket/audit/agent/auth/budget ordering and team display-sort baselines.

## Known Skips Or Caution Areas

- `LoggingUserStoreDecorator` HUM0009 warning has been intentionally skipped so far as Identity instrumentation.
- Broad agent conversation DTO conversion is valuable but should be handled as a coherent multi-file API/view shape, not as a half-fix.
- Team page detail still leaks `Team` and `TeamRoleDefinition`; fix only when ready to replace the full team page read shape and controller child-team logic coherently.
- Consent dashboard/review tuple shapes are valuable but entangled with tests and views. Prefer explicit consent DTOs when taking that lane.
- Migration/destructive-op baseline entries are blocked unless the fix is outside migration files. Do not edit migrations.
- Cross-section EF join entries inside entity configuration are blocked by the no-storage-behavior rule.

## Additional Architecture-Rule Search Queue

After baseline-driven work slows down, use these rule sources for proactive debt discovery:

- `docs/architecture/design-rules.md`
- `docs/architecture/service-entity-boundary-ratchet.md`
- `memory/architecture/interface-method-additions-are-debt.md`
- `memory/architecture/no-business-logic-in-controllers.md`
- `memory/architecture/no-cross-section-ef-joins.md`
- `memory/architecture/display-sort-in-controllers.md`
- `memory/architecture/repository-required-for-db-access.md`
- `memory/architecture/iuserservice-onestop-userinfo.md`
- `memory/architecture/caching-transparent.md`

Suggested proactive searches:

```powershell
rg -n "\.Include\(|\.ThenInclude\(| join |\.Join\(" src/Humans.Infrastructure src/Humans.Application src/Humans.Web
rg -n "\.OrderBy|\.ThenBy|\.Take\(|Skip\(|Get.*Recent|Top|Limit" src/Humans.Infrastructure
rg -n "DbContext|I.*Repository" src/Humans.Infrastructure/Services src/Humans.Web/Controllers
rg -n "Task<|ValueTask<|IAsyncEnumerable<|IReadOnly|IEnumerable<|\w+Async\(" src/Humans.Application/Interfaces
```

Do not mechanically edit every match. Classify ownership, result-size safety, and caller intent first.

## Next Recommended Iteration

Continue Lane A unless a more urgent violation is obvious. Pick the next low-caller `ApplicationServiceEntityReadReturns` item and replace the read surface with a DTO/snapshot. If the low-caller item is too broad, skip it explicitly here and move to the next.

Current known candidates to reconsider:

- `IAgentService` conversation reads: valuable but broad; needs coherent conversation/message snapshot and web/API view adaptation.
- `ITeamPageService.GetTeamPageDetailAsync`: valuable but broad; needs full team page read model replacement.
- Consent DTOs: valuable; start only with a contained display path.
- Shifts helper reads: inspect for narrow read-only callers before editing.

## Resume Prompt

Use this prompt when restarting:

```text
Continue the humans-tech-debt loop in the existing worktree/branch.

Use .codex/TECH_DEBT_QUEUE.md as the durable queue and tests/Humans.Application.Tests/Architecture/Baselines as the ratchet work list. Prioritize high-value real fixes over comments or easy annotations.

For each iteration:
- choose the highest-value safe baseline entry
- inspect only needed files
- refactor the code, not just the baseline
- run targeted tests and dotnet build
- remove stale baseline entries
- commit and push
- update .codex/TECH_DEBT_QUEUE.md when status or strategy changes

Do not stop after a batch. Continue until interrupted, blocked, or all feasible baseline entries are cleared.
```

## 2026-05-15 checkpoint

Done:
- `794db2327 refactor(dashboard): return member dashboard snapshots` removed `IDashboardService.GetMemberDashboardAsync` entity-return leaks for `Application`, `EventSettings`, `Profile`, `Shift`, and `ShiftSignup` by returning dashboard DTO snapshots and keeping controller mapping entity-free.

Validated:
- `dotnet test tests/Humans.Web.Tests/Humans.Web.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~HomeControllerTests"`
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~Application_service_read_methods_do_not_add_new_entity_return_types"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue selecting high-value clusters from `tests/Humans.Application.Tests/Architecture/Baselines`, prioritizing real application DTO/refactor fixes before documentation-only intentional markers.

## 2026-05-15 checkpoint

Done:
- `a03571435 refactor(profile): move admin list shaping out of controller` removed `ProfileController.AdminList/6` from `NoBusinessLogicInControllers.baseline.txt` by moving admin-list sort/page/view-model shaping into `AdminHumanListViewModelBuilder`.

Validated:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~No_new_business_logic_in_controllers"` (skipped by repository configuration)
- `dotnet build Humans.slnx --disable-build-servers -v q` (succeeded; existing `HUM0009` warning remains)

Next:
- Continue with concentrated `ProfileController` debt or high-count service DTO leaks, preferring real extraction over baseline annotations.

## 2026-05-15 checkpoint

Done:
- `7fc5916ef refactor(profile): move admin detail shaping out of controller` removed `ProfileController.AdminDetail/2` from `NoBusinessLogicInControllers.baseline.txt` by extracting admin detail view-model construction, replacing `ViewBag` campaign/outbox data with model properties, and resolving role creator names via `IUserService` instead of the obsolete `RoleAssignment.CreatedByUser` navigation.

Validated:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~No_new_business_logic_in_controllers"` (skipped by repository configuration)
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue reducing the remaining `ProfileController` action cluster, then switch back to service DTO leaks where controller extraction stops being high-value.

## 2026-05-15 checkpoint

Done:
- `7b174e8b9 refactor(profile): move edit form shaping out of controller` removed `ProfileController.Edit/1` from `NoBusinessLogicInControllers.baseline.txt` by extracting edit-form DTO construction, tier-lock derivation, sorted shift-tag presentation, and Google-photo import eligibility into `ProfileEditViewModelBuilder`.

Validated:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ProfileControllerEditTests"`
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --no-build --disable-build-servers -v q --filter "FullyQualifiedName~No_new_business_logic_in_controllers"` (skipped by repository configuration)
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue with `ProfileController.Edit/2` if safe, otherwise switch to `ViewProfile/2` or application-service entity-return clusters.

## 2026-05-15 checkpoint

Done:
- `c9a5fd0bb refactor(shifts): return no-show history snapshots` removed `IShiftSignupService.GetNoShowHistoryAsync:ShiftSignup` from `ApplicationServiceEntityReadReturns.baseline.txt` by projecting repository entities into `NoShowHistoryEntry` snapshots and updating `ProfileController.ViewProfile` to use scalar no-show fields.

Validated:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~Application_service_read_methods_do_not_add_new_entity_return_types"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue DTO-return work on remaining shift/team/consent service methods, or finish the remaining `ProfileController` action extractions when they can be kept to one coherent behavior per commit.

## 2026-05-15 checkpoint

Done:
- `da927b542 refactor(consent): return dashboard snapshots` removed the three `IConsentService.GetConsentDashboardAsync` entity-return rows (`ConsentRecord`, `DocumentVersion`, `Team`) by returning consent dashboard DTO records and mapping the web view from scalar fields.

Validated:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ConsentServiceTests"`
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --no-build --disable-build-servers -v q --filter "FullyQualifiedName~Application_service_read_methods_do_not_add_new_entity_return_types"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Remaining high-value DTO leaks: `IConsentService.GetConsentReviewDetailAsync`, team detail/page detail, and shift service methods returning entities.

## 2026-05-15 checkpoint

Done:
- `6ce47a3d0 refactor(consent): return review detail snapshot` removed the two `IConsentService.GetConsentReviewDetailAsync` entity-return rows (`ConsentRecord`, `DocumentVersion`) by returning a nullable `ConsentReviewDetail` DTO used by consent review and onboarding consent screens.

Validated:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ConsentServiceTests"`
- `dotnet test tests/Humans.Web.Tests/Humans.Web.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ConsentControllerTests|FullyQualifiedName~OnboardingWidgetControllerConsentsTests"`
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --no-build --disable-build-servers -v q --filter "FullyQualifiedName~Application_service_read_methods_do_not_add_new_entity_return_types"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue DTO-return work on team detail/page detail and remaining shift service methods returning entities.

## 2026-05-15 checkpoint

Done:
- `a75994131 refactor(shifts): return tag preference snapshots` removed `IShiftManagementService.GetVolunteerTagPreferencesAsync:ShiftTag` from `ApplicationServiceEntityReadReturns.baseline.txt` by returning `ShiftTagPreferenceSummary` records and projecting repository entities inside `ShiftManagementService`.

Validated:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ProfileControllerEditTests|FullyQualifiedName~Dashboard"`
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --no-build --disable-build-servers -v q --filter "FullyQualifiedName~Application_service_read_methods_do_not_add_new_entity_return_types"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue with `IShiftManagementService.GetTagsAsync` or `GetUrgentShiftsAsync` snapshots, then revisit the larger team detail/page detail DTO rewrite.

## 2026-05-15 checkpoint

Done:
- `824704e07 refactor(shifts): return tag catalog snapshots` removed `IShiftManagementService.GetTagsAsync:ShiftTag` from `ApplicationServiceEntityReadReturns.baseline.txt` by returning `ShiftTagSummary` records and updating tag picker view models/tests.

Validated:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ProfileControllerEditTests"`
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --no-build --disable-build-servers -v q --filter "FullyQualifiedName~Application_service_read_methods_do_not_add_new_entity_return_types"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue with `IShiftManagementService.GetUrgentShiftsAsync` or shift signup DTO leaks, then revisit larger team detail/page detail snapshots.

## 2026-05-15 checkpoint

Done:
- `f66302c83 refactor(teams): return drive folder snapshots` removed `ITeamResourceService.GetActiveDriveFoldersAsync:GoogleResource` from `ApplicationServiceEntityReadReturns.baseline.txt` by returning `GoogleResourceSnapshot` records to the drive activity monitor.

Validated:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~DriveActivityMonitorServiceTests"`
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --no-build --disable-build-servers -v q --filter "FullyQualifiedName~Application_service_read_methods_do_not_add_new_entity_return_types"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Remaining entity-return rows are mostly broader team/shift surfaces; prefer focused snapshot seams where callers only need scalar display data.

## 2026-05-15 checkpoint

Done:
- `b665c86c1 refactor(shifts): return created tag snapshot` removed `IShiftManagementService.GetOrCreateTagAsync:ShiftTag` from `ApplicationServiceEntityReadReturns.baseline.txt` by returning `ShiftTagSummary` for existing and newly-created tags.

Validated:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~Application_service_read_methods_do_not_add_new_entity_return_types"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue single-row DTO seams where callers only need scalar data; defer broad `UrgentShift`/team-detail rewrites until they can be split safely.

## 2026-05-15 checkpoint

Done:
- `d68b080d4 refactor(shifts): return available volunteer snapshots` removed `IGeneralAvailabilityService.GetAvailableForDayAsync:GeneralAvailability` from `ApplicationServiceEntityReadReturns.baseline.txt` by returning `GeneralAvailabilitySnapshot` records instead of entities.

Validated:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~GeneralAvailabilityServiceTests"`
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --no-build --disable-build-servers -v q --filter "FullyQualifiedName~Application_service_read_methods_do_not_add_new_entity_return_types"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue with `IGeneralAvailabilityService.GetByUserAsync` or other single-row DTO seams before broad team/shift surfaces.

## 2026-05-15 checkpoint

Done:
- `dd2176f4d refactor(shifts): return user availability snapshot` removed `IGeneralAvailabilityService.GetByUserAsync:GeneralAvailability` from `ApplicationServiceEntityReadReturns.baseline.txt` by returning `GeneralAvailabilitySnapshot` and adapting the shifts controller assignment.

Validated:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~GeneralAvailabilityServiceTests"`
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --no-build --disable-build-servers -v q --filter "FullyQualifiedName~Application_service_read_methods_do_not_add_new_entity_return_types"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue looking for single-row DTO seams before broad team/shift surfaces.

## 2026-05-15 checkpoint

Done:
- `bc79f28a4 refactor(profile): move popover shaping out of controller` removed `ProfileController.Popover/2` from `NoBusinessLogicInControllers.baseline.txt` by extracting sparse-user fallback and profile summary mapping into `ProfileSummaryViewModelBuilder`.

Validated:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ProfileControllerPopoverTests"`
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --no-build --disable-build-servers -v q --filter "FullyQualifiedName~No_new_business_logic_in_controllers"` (skipped by repository configuration)
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue with bounded controller extractions or narrow DTO seams; avoid broad resource/user-email rewrites until they can be split safely.

## 2026-05-15 checkpoint

Done:
- `575f50ce8 refactor(profile): extract facilitated message request shaping` removed `ProfileController.SendMessage/2` from `NoBusinessLogicInControllers.baseline.txt` by moving message sanitization and sender/recipient email validation into `FacilitatedMessageRequestBuilder`.

Validated:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~No_new_business_logic_in_controllers"` (skipped by repository configuration)
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue reducing bounded `ProfileController` action rows or narrow DTO seams.

## 2026-05-15 checkpoint

Done:
- `ced432d63 refactor(profile): move shift info form shaping out of controller` removed `ProfileController.ShiftInfo/0` from `NoBusinessLogicInControllers.baseline.txt` by moving persisted skills/quirks/languages form reconstruction into `ShiftInfoViewModel.FromProfile`.

Validated:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ShiftInfoViewModelTests"`
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --no-build --disable-build-servers -v q --filter "FullyQualifiedName~No_new_business_logic_in_controllers"` (skipped by repository configuration)
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue bounded profile-controller rows, especially small email/linking actions, before taking broader DTO rewrites.

## 2026-05-15 checkpoint

Done:
- `6dcdad3ad refactor(profile): extract add email verification flow` removed `ProfileController.AddEmail/1` from `NoBusinessLogicInControllers.baseline.txt` by extracting verification URL/email sending and add-email flash mapping into private helpers.

Validated:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~No_new_business_logic_in_controllers"` (skipped by repository configuration)
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue bounded profile email/linking actions or return to DTO seams when a safe narrow seam is available.

## 2026-05-15 checkpoint

Done:
- `bd98e894a refactor(profile): extract email verification result mapping` removed `ProfileController.VerifyEmail/3` from `NoBusinessLogicInControllers.baseline.txt` by moving successful verification result logging and view-data mapping into `VerifyEmailSuccess`.

Validated:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~No_new_business_logic_in_controllers"` (skipped by repository configuration)
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue bounded profile-controller email/linking actions or narrow DTO seams.

## 2026-05-15 checkpoint

Done:
- `fa5d3f8e5 refactor(profile): extract email visibility helpers` removed `ProfileController.SetEmailVisibility/2` from `NoBusinessLogicInControllers.baseline.txt` by extracting visibility parsing and self-path audit logging helpers.

Validated:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~No_new_business_logic_in_controllers"` (skipped by repository configuration)
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue bounded profile email/linking actions or switch back to DTO seams when safe.

## Checkpoint - 2026-05-15 - DeleteEmail/1

Done:
- Removed `src/Humans.Web/Controllers/ProfileController.cs:DeleteEmail/1` from the controller business-logic baseline.
- Extracted the self-service delete-email audit write into a helper so the action no longer owns that branching/logging detail.
- Validation: controller business-logic ratchet test was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue bounded profile email/linking controller baseline rows before returning to broader application service entity-return clusters.

## Checkpoint - 2026-05-15 - SetGoogle/2

Done:
- Removed `src/Humans.Web/Controllers/ProfileController.cs:SetGoogle/2` from the controller business-logic baseline.
- Extracted self-service Google-service-email success/rejection cache and flash handling out of the controller action.
- Validation: controller business-logic ratchet test was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue adjacent bounded profile email/linking controller baseline rows, starting with `ClearGoogle/2` or `ClearPrimary/2`.

## Checkpoint - 2026-05-15 - ClearGoogle/2

Done:
- Removed `src/Humans.Web/Controllers/ProfileController.cs:ClearGoogle/2` from the controller business-logic baseline.
- Extracted self-service Google-flag clear success/rejection cache and flash handling out of the controller action.
- Validation: controller business-logic ratchet test was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue adjacent bounded profile email/linking controller baseline rows, with `ClearPrimary/2` next.

## Checkpoint - 2026-05-15 - ClearPrimary/2

Done:
- Removed `src/Humans.Web/Controllers/ProfileController.cs:ClearPrimary/2` from the controller business-logic baseline.
- Extracted self-service primary-flag clear success/rejection cache and flash handling out of the controller action.
- Validation: controller business-logic ratchet test was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue adjacent bounded profile email/linking controller baseline rows, with `Unlink/2` next.

## Checkpoint - 2026-05-15 - Unlink/2

Done:
- Removed `src/Humans.Web/Controllers/ProfileController.cs:Unlink/2` from the controller business-logic baseline.
- Extracted self-service provider unlink success/rejection cache and flash handling out of the controller action.
- Validation: controller business-logic ratchet test was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue admin email/linking controller baseline rows that mirror the self-service cleanup.

## Checkpoint - 2026-05-15 - AdminSetGoogle/3

Done:
- Removed `src/Humans.Web/Controllers/ProfileController.cs:AdminSetGoogle/3` from the controller business-logic baseline.
- Reused shared Google-service-email success/rejection cache and flash handling for the admin action instead of duplicating branching in the controller method.
- Validation: controller business-logic ratchet test was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue admin email/linking mirror rows, with `AdminClearGoogle/3` next.

## Checkpoint - 2026-05-15 - AdminClearGoogle/3

Done:
- Removed `src/Humans.Web/Controllers/ProfileController.cs:AdminClearGoogle/3` from the controller business-logic baseline.
- Reused shared Google-flag clear success/rejection cache and flash handling for the admin action instead of duplicating branching in the controller method.
- Validation: controller business-logic ratchet test was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue admin email/linking mirror rows, with `AdminClearPrimary/3` next.

## Checkpoint - 2026-05-15 - AdminClearPrimary/3

Done:
- Removed `src/Humans.Web/Controllers/ProfileController.cs:AdminClearPrimary/3` from the controller business-logic baseline.
- Reused shared primary-flag clear success/rejection cache and flash handling for the admin action instead of duplicating branching in the controller method.
- Validation: controller business-logic ratchet test was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue admin email/linking mirror rows, with `AdminUnlink/3` next.

## Checkpoint - 2026-05-15 - AdminUnlink/3

Done:
- Removed `src/Humans.Web/Controllers/ProfileController.cs:AdminUnlink/3` from the controller business-logic baseline.
- Reused shared provider unlink success/rejection cache and flash handling for the admin action instead of duplicating branching in the controller method.
- Validation: controller business-logic ratchet test was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue admin email rows with audit/logging branches, starting with `AdminDeleteEmail/3` if it can be safely extracted without changing application service contracts.

## Checkpoint - 2026-05-15 - AdminDeleteEmail/3

Done:
- Removed `src/Humans.Web/Controllers/ProfileController.cs:AdminDeleteEmail/3` from the controller business-logic baseline.
- Extracted admin delete-email result handling, including cache invalidation, audit logging, and success/rejection flash messages, out of the controller action.
- Validation: controller business-logic ratchet test was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue admin email audit rows where the helper extraction can stay local to the controller, then return to broader application-service entity-return baselines.

## Checkpoint - 2026-05-15 - AdminSetVisibility/4

Done:
- Removed `src/Humans.Web/Controllers/ProfileController.cs:AdminSetVisibility/4` from the controller business-logic baseline.
- Extracted admin email visibility audit and success flash handling out of the controller action after the application service call.
- Validation: controller business-logic ratchet test was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue remaining profile controller workflow rows, starting with bounded role actions if they can be safely extracted without changing authorization behavior.

## Checkpoint - 2026-05-15 - AdminAddEmail/3

Done:
- Removed `src/Humans.Web/Controllers/ProfileController.cs:AdminAddEmail/3` from the controller business-logic baseline.
- Extracted the admin post-add email workflow, including verification URL construction, outbound verification email, audit logging, and success flash handling, out of the controller action.
- Validation: controller business-logic ratchet test was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue remaining profile controller rows: role workflows, profile edit/view shaping, and Google photo import.

## Checkpoint - 2026-05-15 - AddRole/2

Done:
- Removed `src/Humans.Web/Controllers/ProfileController.cs:AddRole/2` from the controller business-logic baseline.
- Extracted role-assignment form repopulation and assign-role success/already-active flash handling out of the controller action.
- Validation: controller business-logic ratchet test was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue role workflow rows with `EndRole/3`, then reassess the remaining profile edit/view/photo rows.

## Checkpoint - 2026-05-15 - EndRole/3

Done:
- Removed `src/Humans.Web/Controllers/ProfileController.cs:EndRole/3` from the controller business-logic baseline.
- Extracted end-role success/not-active flash handling out of the controller action while preserving authorization and service behavior.
- Validation: controller business-logic ratchet test was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Reassess remaining profile controller rows: `Edit/2`, `ImportGooglePhoto/1`, and `ViewProfile/2`, then move back to application-service entity-return baselines if those are too broad.

## Checkpoint - 2026-05-15 - ImportGooglePhoto/1

Done:
- Removed `src/Humans.Web/Controllers/ProfileController.cs:ImportGooglePhoto/1` from the controller business-logic baseline.
- Extracted Google avatar source eligibility, trusted avatar URI validation, and HTTP fetch/content validation into helpers while preserving the existing profile-picture update flow.
- Validation: controller business-logic ratchet test was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Remaining profile controller rows are the broader `Edit/2` and `ViewProfile/2`; inspect for safe extraction before moving to application-service entity-return baselines.

## Checkpoint - 2026-05-15 - ViewProfile/2

Done:
- Removed `src/Humans.Web/Controllers/ProfileController.cs:ViewProfile/2` from the controller business-logic baseline.
- Extracted no-show history permission checks, team/reviewer lookup, timezone formatting, and view-item projection into a helper so the action only assembles the final profile view model.
- Validation: controller business-logic ratchet test was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Inspect `Edit/2`; if it is too broad for a safe single commit, switch to the highest-value application-service entity-return baseline.

## Checkpoint - 2026-05-15 - Edit/2

Done:
- Removed `src/Humans.Web/Controllers/ProfileController.cs:Edit/2` from the controller business-logic baseline.
- Extracted profile-picture upload size/type validation, HEIF content-type mapping, stream reading, and resize error handling out of the controller action.
- Validation: controller business-logic ratchet test was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- ProfileController is clear from the controller business-logic baseline. Move to the highest-value remaining application-service entity-return baseline or another controller baseline cluster.

## Checkpoint - 2026-05-15 - IRoleAssignmentService.GetByIdAsync

Done:
- Removed `Humans.Application.Interfaces.Auth.IRoleAssignmentService.GetByIdAsync:Humans.Domain.Entities.RoleAssignment` from the application-service entity-return baseline.
- Changed `GetByIdAsync` to return `RoleAssignmentDetailSnapshot` with only the role-ending data needed by the web layer.
- Resolved the display name through `IUserService` instead of stitching/reading the obsolete `RoleAssignment.User` navigation.
- Validation: application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue role-assignment service entity-return rows where snapshot contracts are bounded, then move to broader clusters only when safe.

## Checkpoint - 2026-05-15 - IRoleAssignmentService.GetByUserIdAsync

Done:
- Removed `Humans.Application.Interfaces.Auth.IRoleAssignmentService.GetByUserIdAsync:Humans.Domain.Entities.RoleAssignment` from the application-service entity-return baseline.
- Changed `GetByUserIdAsync` to return `RoleAssignmentSummarySnapshot` with the fields existing callers use instead of returning `RoleAssignment` entities.
- Updated admin detail shaping and role-assignment service tests to consume the snapshot contract.
- Validation: `RoleAssignmentServiceTests` passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue role-assignment service entity-return cleanup with `GetFilteredAsync`, or switch to another bounded snapshot conversion if that list surface is too broad.

## Checkpoint - 2026-05-15 - IRoleAssignmentService.GetFilteredAsync

Done:
- Removed `Humans.Application.Interfaces.Auth.IRoleAssignmentService.GetFilteredAsync:Humans.Domain.Entities.RoleAssignment` from the application-service entity-return baseline.
- Changed `GetFilteredAsync` to return `RoleAssignmentSummarySnapshot` rows enriched with user display/email and creator display data.
- Updated staff and governance role-list pages plus role-assignment service tests to consume snapshot fields instead of `RoleAssignment` navigation properties.
- Validation: `RoleAssignmentServiceTests` passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Reassess remaining application-service entity-return rows for another bounded snapshot conversion.

## Checkpoint - 2026-05-15 - ICommunicationPreferenceService.GetPreferenceOrNullAsync

Done:
- Removed `Humans.Application.Interfaces.Profiles.ICommunicationPreferenceService.GetPreferenceOrNullAsync:Humans.Domain.Entities.CommunicationPreference` from the application-service entity-return baseline.
- Changed the read-only preference lookup to return `CommunicationPreferenceSnapshot` instead of the mutable entity.
- Updated mailer import classification and targeted mailer tests to consume snapshot fixtures.
- Validation: mailer import/communication preference tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue bounded application-service entity-return rows, likely `GetPreferencesAsync` if default-creation semantics can be preserved safely, otherwise move to another small service cluster.

## Checkpoint - 2026-05-15 - ICommunicationPreferenceService.GetPreferencesAsync

Done:
- Removed `Humans.Application.Interfaces.Profiles.ICommunicationPreferenceService.GetPreferencesAsync:Humans.Domain.Entities.CommunicationPreference` from the application-service entity-return baseline.
- Changed the default-creating preference read to return `CommunicationPreferenceSnapshot` rows while preserving default row creation/reload behavior.
- Extended the snapshot with `SubscribedAt` so subscription timestamp tests and callers keep the same read information without entity exposure.
- Validation: communication preference tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue bounded application-service entity-return rows in small service clusters before tackling broad team/shift/budget aggregates.

## Checkpoint - 2026-05-15 - IConsentService.GetUserConsentRecordsAsync

Done:
- Removed `Humans.Application.Interfaces.Consent.IConsentService.GetUserConsentRecordsAsync:Humans.Domain.Entities.ConsentRecord` from the application-service entity-return baseline.
- Changed the per-user consent-record read to return `ConsentRecordSnapshot` rows instead of `ConsentRecord` entities with loaded navigation properties.
- Validation: application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Blocked validation note:
- Attempted targeted integration coverage with `dotnet test tests/Humans.Integration.Tests/Humans.Integration.Tests.csproj --filter "FullyQualifiedName~ChainFollowReadTests"`.
- It failed before assertions in all selected tests because the integration test host resolves Hangfire services without `JobStorage.Current` initialized. This appears unrelated to the consent snapshot change.

Next:
- Continue bounded application-service entity-return rows; avoid relying on that integration target until the Hangfire test-host setup is fixed.

## Checkpoint - 2026-05-15 - RoleAssignmentService obsolete nav stitching

Done:
- Removed stale obsolete-navigation baseline rows for `RoleAssignmentService` after the role-assignment read paths moved to snapshots.
- Removed the now-unused role-assignment nav-stitching helper that wrote `RoleAssignment.User` and `RoleAssignment.CreatedByUser`.
- Validation: obsolete-navigation ratchet was invoked without failure; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue bounded architecture cleanup from remaining baselines, prioritizing real contract/refactor changes over stale-row cleanup.

## Checkpoint - 2026-05-15 - IMembershipQuery.GetUserTeamsAsync

Done:
- Removed `Humans.Application.Interfaces.Governance.IMembershipQuery.GetUserTeamsAsync:Humans.Domain.Entities.TeamMember` from the application-service entity-return baseline.
- Changed the membership query adapter to return `MembershipTeamSnapshot` with only team id, member role, and team system type.
- Updated `MembershipCalculator` and membership tests to use the snapshot instead of `TeamMember.Team` navigation data.
- Validation: membership calculator/partition tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue bounded application-service entity-return rows or stale architecture rows where prior refactors already removed the underlying violation.

## Checkpoint - 2026-05-15 - NoLinqAtDbLayer stale profile storage rows

Done:
- Cleared the remaining `NoLinqAtDbLayer` baseline rows for Consent and Legal profile reads.
- Removed stale `_dbContext.Profiles` wording from comments; the code already routes these cross-section reads through `IProfileService`.
- Validation: NoLinqAtDbLayer ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue bounded application-service entity-return rows, avoiding broad aggregate surfaces unless a narrow snapshot seam is clear.

## Checkpoint - 2026-05-15 - GuestController.UpdatePreference/4

Done:
- Removed `src/Humans.Web/Controllers/GuestController.cs:UpdatePreference/4` from the controller business-logic baseline.
- Extracted communication-preference mutability and update-source attribution decisions out of the controller action.
- Validation: controller business-logic ratchet was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue bounded controller rows or small application-service snapshot conversions, avoiding broad sync/persistence boundary changes without a safe seam.

## Checkpoint - 2026-05-15 - HomeController not-attending flow

Done:
- Removed `src/Humans.Web/Controllers/HomeController.cs:DeclareNotAttending/0` and `src/Humans.Web/Controllers/HomeController.cs:UndoNotAttending/0` from the controller business-logic baseline.
- Extracted active-event-year validation and undo-result flash handling out of the paired controller actions.
- Validation: controller business-logic ratchet was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue bounded controller/service rows with clear helper or snapshot seams.

## Checkpoint - 2026-05-15 - ConsentController.Submit/1

Done:
- Removed `src/Humans.Web/Controllers/ConsentController.cs:Submit/1` from the controller business-logic baseline.
- Extracted unchecked-consent redisplay and consent-submit success/failure flash mapping out of the controller action.
- Validation: controller business-logic ratchet was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue bounded controller rows with service-backed behavior and small extraction seams.

## Checkpoint - 2026-05-15 - FeedbackController.PostMessage/2

Done:
- Removed `src/Humans.Web/Controllers/FeedbackController.cs:PostMessage/2` from the controller business-logic baseline.
- Moved feedback message ownership enforcement into `FeedbackService.PostMessageAsync`, so reporters can only post to their own reports while admins keep the existing path.
- Validation: `FeedbackServiceTests` passed; controller business-logic ratchet was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue bounded feedback controller rows, preferring real service-boundary or presentation-helper extraction over line-count-only edits.

## Checkpoint - 2026-05-15 - FeedbackController.Detail/1

Done:
- Removed `src/Humans.Web/Controllers/FeedbackController.cs:Detail/1` from the controller business-logic baseline.
- Added `IFeedbackService.GetFeedbackByIdForViewerAsync` so reporter/admin read authorization is enforced in the application service instead of the controller.
- Added focused service coverage for non-reporter access returning no report.
- Validation: `FeedbackServiceTests` passed; controller business-logic ratchet was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue bounded controller rows or return to service DTO leaks where a read-surface change has a clear caller boundary.

## Checkpoint - 2026-05-15 - FeedbackController.Index/7

Done:
- Removed `src/Humans.Web/Controllers/FeedbackController.cs:Index/7` from the controller business-logic baseline.
- Moved feedback reply-needed classification into the feedback service read model instead of deriving it from status/message timestamps in the controller list projection.
- Validation: `FeedbackServiceTests` passed; controller business-logic ratchet was invoked and skipped by repository configuration; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Finish the remaining bounded feedback controller row (`Submit/1`) if a real service/result seam is clear; otherwise return to service entity-return DTO leaks.

## Checkpoint - 2026-05-15 - IBudgetService.GetTicketingProjectionAsync

Done:
- Removed `Humans.Application.Interfaces.Budget.IBudgetService.GetTicketingProjectionAsync:Humans.Domain.Entities.TicketingProjection` from the application-service entity-return baseline.
- Changed the public budget read surface to return `TicketingProjectionSnapshot` while keeping internal projection calculations on the repository-owned entity path.
- Validation: application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue bounded service entity-return rows; skip wide surfaces where callers need entity instances for mutation or framework APIs.

## Checkpoint - 2026-05-15 - ICampaignService.GetActiveOrCompletedGrantsForUserAsync

Done:
- Removed `Humans.Application.Interfaces.Campaigns.ICampaignService.GetActiveOrCompletedGrantsForUserAsync:Humans.Domain.Entities.CampaignGrant` from the application-service entity-return baseline.
- Changed the member-facing campaign grant read to return `CampaignGrantSummary` and updated the profile page to render scalar campaign/code fields instead of entity navigations.
- Validation: `CampaignServiceTests` passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue with the adjacent admin campaign grants row if it can reuse `CampaignGrantSummary` cleanly, otherwise select another bounded service DTO leak.

## Checkpoint - 2026-05-15 - ICampaignService.GetAllGrantsForUserAsync

Done:
- Removed `Humans.Application.Interfaces.Campaigns.ICampaignService.GetAllGrantsForUserAsync:Humans.Domain.Entities.CampaignGrant` from the application-service entity-return baseline.
- Reused `CampaignGrantSummary` for the admin profile campaign-code card and removed entity navigation reads from that view model path.
- Validation: `CampaignServiceTests` passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue bounded campaign service reads (`GetAllAsync`/`GetByIdAsync`) only if controller mutation workflows can keep entity usage out of read paths; otherwise move to another compact service DTO leak.

## Checkpoint - 2026-05-15 - ICampaignService.GetDetailPageAsync

Done:
- Removed `Humans.Application.Interfaces.Campaigns.ICampaignService.GetDetailPageAsync:Humans.Domain.Entities.Campaign` from the application-service entity-return baseline.
- Changed the campaign detail page DTO to return `CampaignAdminSummary` with grant summaries instead of wrapping the mutable campaign entity graph.
- Validation: `CampaignServiceTests` passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue with `GetSendWavePageAsync` if the campaign summary can satisfy the send-wave view without entity access.

## Checkpoint - 2026-05-15 - ICampaignService.GetSendWavePageAsync

Done:
- Removed `Humans.Application.Interfaces.Campaigns.ICampaignService.GetSendWavePageAsync:Humans.Domain.Entities.Campaign` from the application-service entity-return baseline.
- Changed the send-wave page DTO and view model to carry `CampaignAdminSummary` instead of a mutable campaign entity.
- Validation: `CampaignServiceTests` passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Reassess remaining campaign rows (`GetAllAsync`, `GetByIdAsync`) separately because several controller command paths still use entity state for validation.

## Checkpoint - 2026-05-15 - ICampaignService.GetAllAsync

Done:
- Removed `Humans.Application.Interfaces.Campaigns.ICampaignService.GetAllAsync:Humans.Domain.Entities.Campaign` from the application-service entity-return baseline.
- Changed the campaign index read to return `CampaignListSummary` with precomputed code/email counts instead of rendering from campaign entity navigation collections.
- Validation: `CampaignServiceTests` passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Treat `ICampaignService.GetByIdAsync` separately; it still supports edit/generate-code validation paths and needs a command-safe replacement rather than a simple list DTO.

## Checkpoint - 2026-05-15 - ICampaignService.GetByIdAsync

Done:
- Removed `Humans.Application.Interfaces.Campaigns.ICampaignService.GetByIdAsync:Humans.Domain.Entities.Campaign` from the application-service entity-return baseline.
- Changed the public campaign lookup used by edit/status validation to return `CampaignEditSnapshot`, while internal campaign page composition loads entities through the repository inside the owning service.
- Validation: `CampaignServiceTests` passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Campaign service entity-return rows are clear except mutation create surfaces; continue with another bounded DTO leak from the remaining baseline.

## Checkpoint - 2026-05-15 - IApplicationDecisionService.GetUserApplicationsAsync

Done:
- Removed `Humans.Application.Interfaces.Governance.IApplicationDecisionService.GetUserApplicationsAsync:Humans.Domain.Entities.Application` from the application-service entity-return baseline.
- Changed the user application history read to return `UserApplicationSnapshot` with the scalar fields used by profile, dashboard, governance, and edit flows.
- Updated profile/admin builders and focused controller tests to consume the snapshot contract.
- Validation: `ApplicationDecisionServiceTests` and `ProfileControllerEditTests` passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue bounded governance/job read DTOs only where callers do not need mutation entities.

## Checkpoint - 2026-05-15 - IApplicationDecisionService.GetExpiringApplicationsNeedingReminderAsync

Done:
- Removed `Humans.Application.Interfaces.Governance.IApplicationDecisionService.GetExpiringApplicationsNeedingReminderAsync:Humans.Domain.Entities.Application` from the application-service entity-return baseline.
- Changed the renewal reminder job read to return `ApplicationRenewalReminderCandidate` snapshots with only grouping, email, and reminder-stamp fields.
- Validation: `ApplicationDecisionServiceTests` passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Convert the adjacent board-digest approved-window read to a scalar snapshot if validation stays bounded.

## Checkpoint - 2026-05-15 - IApplicationDecisionService.GetApprovedInWindowAsync

Done:
- Removed `Humans.Application.Interfaces.Governance.IApplicationDecisionService.GetApprovedInWindowAsync:Humans.Domain.Entities.Application` from the application-service entity-return baseline.
- Changed the board daily digest read to return `ApprovedApplicationDigestEntry` snapshots with only user id and membership tier.
- Validation: `ApplicationDecisionServiceTests` passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Move to another bounded service DTO leak; avoid auth/user-manager surfaces that require entity instances.

## Checkpoint - 2026-05-15 - IBudgetService.GetAuditLogAsync

Done:
- Removed `Humans.Application.Interfaces.Budget.IBudgetService.GetAuditLogAsync:Humans.Domain.Entities.BudgetAuditLog` from the application-service entity-return baseline.
- Changed the finance audit-log feed to return `BudgetAuditLogSnapshot` rows instead of append-only audit entities.
- Validation: application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue selecting bounded DTO leaks; budget year/category/line-item reads are broader because callers use them in command and workflow paths.

## Checkpoint - 2026-05-15 - IProfileService.GetProfileLanguagesAsync

Done:
- Removed `Humans.Application.Interfaces.Profiles.IProfileService.GetProfileLanguagesAsync:Humans.Domain.Entities.ProfileLanguage` from the application-service entity-return baseline.
- Changed the profile-language read path to return `ProfileLanguageSnapshot` while keeping save/reconcile APIs on the Profile-owned entity path.
- Updated profile cards, edit/detail builders, and focused popover/edit tests to consume the snapshot contract.
- Validation: `ProfileControllerEditTests` and `ProfileControllerPopoverTests` passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue bounded profile or onboarding DTO leaks; avoid broad profile entity surfaces unless the caller set is fully contained.

## Checkpoint - 2026-05-15 - IShiftSignupService.GetByBlockIdFirstAsync

Done:
- Removed `Humans.Application.Interfaces.Shifts.IShiftSignupService.GetByBlockIdFirstAsync:Humans.Domain.Entities.ShiftSignup` from the application-service entity-return baseline.
- Replaced the range-signup ownership check with `ShiftSignupTeamProbe`, so the admin controller no longer reads the full signup/shift/rota entity graph just to verify team scope.
- Validation: shift signup targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue with other bounded shift signup read surfaces where callers need only scalar ownership/display fields.

## Checkpoint - 2026-05-15 - IShiftSignupService.GetByIdAsync

Done:
- Removed `Humans.Application.Interfaces.Shifts.IShiftSignupService.GetByIdAsync:Humans.Domain.Entities.ShiftSignup` from the application-service entity-return baseline.
- Reused `ShiftSignupTeamProbe` for single-signup admin ownership checks before approve/refuse/no-show/remove commands.
- Validation: shift signup targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue bounded shift signup reads or switch back to controller rows where service-boundary fixes are clear.

## Checkpoint - 2026-05-15 - IShiftSignupService.GetAllForOrphanScanAsync

Done:
- Removed `Humans.Application.Interfaces.Shifts.IShiftSignupService.GetAllForOrphanScanAsync:Humans.Domain.Entities.ShiftSignup` from the application-service entity-return baseline.
- Changed the orphan-signup diagnostic scan to return `OrphanSignupSnapshot` rows carrying signup, actor, rota, and shift-date fields without exposing the signup/shift/rota/event entity graph.
- Validation: shift signup targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue with bounded read DTOs; remaining shift-signup list reads are broader display surfaces and should be inspected individually.

## Checkpoint - 2026-05-15 - IAuditLogService.GetRecentAsync

Done:
- Removed `Humans.Application.Interfaces.AuditLog.IAuditLogService.GetRecentAsync:Humans.Domain.Entities.AuditLogEntry` from the application-service entity-return baseline.
- Introduced `AuditLogEntrySnapshot` and taught `AuditViewerService` to resolve recent audit entries from snapshots instead of raw audit entities.
- Validation: audit targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue converting remaining `IAuditLogService` read methods one at a time using the same snapshot path.

## Checkpoint - 2026-05-15 - IAuditLogService.GetByResourceAsync

Done:
- Removed `Humans.Application.Interfaces.AuditLog.IAuditLogService.GetByResourceAsync:Humans.Domain.Entities.AuditLogEntry` from the application-service entity-return baseline.
- Changed the resource-scoped audit read to return `AuditLogEntrySnapshot` and updated audit viewer tests/stubs to use the snapshot contract.
- Validation: audit targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue converting remaining `IAuditLogService` read methods one at a time.

## Checkpoint - 2026-05-15 - IAuditLogService.GetGoogleSyncByUserAsync

Done:
- Removed `Humans.Application.Interfaces.AuditLog.IAuditLogService.GetGoogleSyncByUserAsync:Humans.Domain.Entities.AuditLogEntry` from the application-service entity-return baseline.
- Changed Google-sync audit history reads, including merge-chain following, to return `AuditLogEntrySnapshot` rows.
- Validation: audit targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue converting remaining `IAuditLogService` read methods one at a time.

## Checkpoint - 2026-05-15 - IAuditLogService.GetByUserAsync

Done:
- Removed `Humans.Application.Interfaces.AuditLog.IAuditLogService.GetByUserAsync:Humans.Domain.Entities.AuditLogEntry` from the application-service entity-return baseline.
- Changed user-scoped audit history reads, including merge-chain following, to return `AuditLogEntrySnapshot` rows.
- Validation: audit targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Convert remaining paged/filtered audit reads using the same snapshot path.

## Checkpoint - 2026-05-15 - IAuditLogService.GetFilteredEntriesAsync

Done:
- Removed `Humans.Application.Interfaces.AuditLog.IAuditLogService.GetFilteredEntriesAsync:Humans.Domain.Entities.AuditLogEntry` from the application-service entity-return baseline.
- Changed flexible audit-history reads used by audit viewer, issues, expenses, and mailer audience sync to return `AuditLogEntrySnapshot` rows.
- Validation: audit/issues/expense/mailer targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Convert remaining paged audit read (`IAuditLogService.GetFilteredAsync`) to the same snapshot contract.

## Checkpoint - 2026-05-15 - IAuditLogService.GetFilteredAsync

Done:
- Removed `Humans.Application.Interfaces.AuditLog.IAuditLogService.GetFilteredAsync:Humans.Domain.Entities.AuditLogEntry` from the application-service entity-return baseline.
- Changed the paged audit read contract to return `AuditLogEntrySnapshot` rows while keeping repository persistence/query internals entity-based.
- Validation: audit/account-provisioning/communication-preference targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue reducing high-value application-service entity return rows, preferring user-facing service contracts over baseline-only edits.

## Checkpoint - 2026-05-15 - IBudgetService.GetAllYearsAsync

Done:
- Removed `Humans.Application.Interfaces.Budget.IBudgetService.GetAllYearsAsync:Humans.Domain.Entities.BudgetYear` from the application-service entity-return baseline.
- Changed the budget year list contract to return `BudgetYearSummarySnapshot` rows with minimal group summaries for finance admin/navigation views.
- Kept full `BudgetYear` graph reads isolated to detail/calculation paths that still require loaded groups/categories/line items.
- Validation: budget/expense/ticket-query targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue budget read cleanup, likely `GetYearByIdAsync` or category/line-item detail reads if they can be converted without breaking calculation paths.

## Checkpoint - 2026-05-15 - IBudgetService.GetLineItemByIdAsync

Done:
- Removed `Humans.Application.Interfaces.Budget.IBudgetService.GetLineItemByIdAsync:Humans.Domain.Entities.BudgetLineItem` from the application-service entity-return baseline.
- Changed line-item detail reads to return `BudgetLineItemSnapshot`, keeping update/delete mutation contracts unchanged.
- Validation: budget/expense targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue budget read cleanup; avoid category/year graph conversions unless the dependent authorization and calculation paths can stay safe.

## Checkpoint - 2026-05-15 - ITeamResourceService.GetTeamResourcesAsync

Done:
- Removed `Humans.Application.Interfaces.Teams.ITeamResourceService.GetTeamResourcesAsync:Humans.Domain.Entities.GoogleResource` from the application-service entity-return baseline.
- Expanded `GoogleResourceSnapshot` to cover team-resource UI metadata and changed single-team resource reads to return snapshots.
- Validation: team/google targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Convert grouped Google resource reads (`GetResourcesByTeamIdsAsync`) if Google sync consumers can safely use snapshots.

## Checkpoint - 2026-05-15 - ITeamResourceService.GetResourcesByTeamIdsAsync

Done:
- Removed `Humans.Application.Interfaces.Teams.ITeamResourceService.GetResourcesByTeamIdsAsync:Humans.Domain.Entities.GoogleResource` from the application-service entity-return baseline.
- Changed grouped Google-resource reads to return `GoogleResourceSnapshot` dictionaries while keeping repository reads entity-based.
- Updated Google group sync and related tests to consume snapshots for resource reconciliation lookups.
- Validation: team/google targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue with high-value team/user service entity-return rows, avoiding graph-heavy conversions unless the dependent UI/authorization paths are clear.

## Checkpoint - 2026-05-15 - IOnboardingService.GetReviewQueueAsync

Done:
- Removed `Humans.Application.Interfaces.Onboarding.IOnboardingService.GetReviewQueueAsync:Humans.Domain.Entities.Profile` from the application-service entity-return baseline.
- Replaced embedded `Profile` lists in `ReviewQueueData` with `ReviewQueueProfile` rows tailored to the onboarding review UI.
- Validation: onboarding/profile/consent targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue profile read cleanup where a narrow DTO can replace entity exposure without rewriting broad profile edit/detail flows.

## Checkpoint - 2026-05-15 - ILegalDocumentSyncService.GetRequiredDocumentVersionsForTeamAsync

Done:
- Removed `Humans.Application.Interfaces.Legal.ILegalDocumentSyncService.GetRequiredDocumentVersionsForTeamAsync:Humans.Domain.Entities.DocumentVersion` from the application-service entity-return baseline.
- Added `RequiredDocumentVersionSnapshot` for membership consent calculations and removed dependency on the `DocumentVersion.LegalDocument` navigation in `MembershipCalculator`.
- Validation: legal/membership/consent targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue legal read cleanup, likely the global required-version read or version-by-id path if consent detail/submit flows can consume snapshots safely.

## Checkpoint - 2026-05-15 - ILegalDocumentSyncService.GetRequiredVersionsAsync

Done:
- Removed `Humans.Application.Interfaces.Legal.ILegalDocumentSyncService.GetRequiredVersionsAsync:Humans.Domain.Entities.DocumentVersion` from the application-service entity-return baseline.
- Reused `RequiredDocumentVersionSnapshot` for the global required-version read and updated the re-consent reminder job to read document names from the snapshot.
- Validation: legal/membership/consent targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue legal read cleanup; evaluate `GetVersionByIdAsync` and active document reads for safe snapshot conversion.

## Checkpoint - 2026-05-15 - ILegalDocumentSyncService.GetVersionByIdAsync

Done:
- Removed `Humans.Application.Interfaces.Legal.ILegalDocumentSyncService.GetVersionByIdAsync:Humans.Domain.Entities.DocumentVersion` from the application-service entity-return baseline.
- Added `LegalDocumentVersionSnapshot` for consent review/submit flows, including document metadata and multilingual content.
- Updated consent service and tests to use snapshot fields instead of `DocumentVersion.LegalDocument` navigation.
- Validation: legal/membership/consent targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue legal document read cleanup, then return to user/profile/team entity-read rows.

## Checkpoint - 2026-05-15 - ILegalDocumentSyncService.GetActiveRequiredDocumentsForTeamsAsync

Done:
- Removed `Humans.Application.Interfaces.Legal.ILegalDocumentSyncService.GetActiveRequiredDocumentsForTeamsAsync:Humans.Domain.Entities.LegalDocument` from the application-service entity-return baseline.
- Added `ActiveRequiredLegalDocumentSnapshot` for consent dashboard/widget reads, including team display metadata and version snapshots.
- Updated consent service and tests to consume legal document snapshots instead of `LegalDocument.Team` / `LegalDocument.Versions` entity graphs.
- Validation: legal/membership/consent targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue remaining legal active-document read cleanup or move to user/profile rows if active document sync surfaces are mutation-oriented.

## Checkpoint - 2026-05-15 - ILegalDocumentSyncService.GetActiveDocumentsAsync

Done:
- Removed `Humans.Application.Interfaces.Legal.ILegalDocumentSyncService.GetActiveDocumentsAsync:Humans.Domain.Entities.LegalDocument` from the application-service entity-return baseline.
- Added `LegalDocumentSnapshot` for active legal document listing surfaces while keeping sync internals repository/entity-based.
- Validation: legal/membership/consent targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Move to remaining high-value entity-return rows outside the legal cluster, with user/profile/team reads likely next.

## Checkpoint - 2026-05-15 - IProfileService.GetReviewableProfilesAsync

Done:
- Removed `Humans.Application.Interfaces.Profiles.IProfileService.GetReviewableProfilesAsync:Humans.Domain.Entities.Profile` from the application-service entity-return baseline.
- Changed the reviewable-profile read to return `ReviewQueueProfile` rows from ProfileService and its caching decorator.
- Simplified onboarding queue composition so raw `Profile` entities no longer cross into onboarding for this list path.
- Validation: onboarding/profile/consent targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue profile/user read cleanup where narrow snapshots can replace entity returns without breaking Identity/UserManager paths.

## Checkpoint - 2026-05-15 - IUserEmailService.GetEntitiesByUserIdAsync

Done:
- Removed `Humans.Application.Interfaces.Profiles.IUserEmailService.GetEntitiesByUserIdAsync:Humans.Domain.Entities.UserEmail` from the application-service entity-return baseline.
- Added `UserEmailRowSnapshot` with full row metadata and changed single-user email metadata reads to return snapshots.
- Updated Google workspace sync and profile/admin email diagnostics to consume row snapshots instead of `UserEmail` entities.
- Validation: google/profile/user-email targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Convert the batch email row read (`GetEntitiesByUserIdsAsync`) to the same `UserEmailRowSnapshot` contract.

## Checkpoint - 2026-05-15 - IBudgetService.GetCategoryByIdAsync

Done:
- Removed `Humans.Application.Interfaces.Budget.IBudgetService.GetCategoryByIdAsync:Humans.Domain.Entities.BudgetCategory` from the application-service entity-return baseline.
- Changed category detail reads to return `BudgetCategorySnapshot` instead of `BudgetCategory`.
- Updated budget authorization, finance/budget category pages, and expense/Holded category consumers to use snapshot data.
- Validation: budget/expense authorization and expense service targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue budget entity-return rows with `IBudgetService.GetActiveYearAsync` or `IBudgetService.GetYearByIdAsync` only if the budget year graph can be converted safely; otherwise prefer bounded agent or team service rows.

## Checkpoint - 2026-05-15 - IAgentService.ListAllConversationsForAdminWithMessagesAsync

Done:
- Removed `Humans.Application.Interfaces.IAgentService.ListAllConversationsForAdminWithMessagesAsync:Humans.Domain.Entities.AgentConversation` from the application-service entity-return baseline.
- Changed the admin transcript listing API to consume `AgentConversationTranscriptSnapshot` and `AgentMessageSnapshot` from the application service.
- Kept repository/entity access inside the Agent application service and mapped conversation messages at the service boundary.
- Validation: agent targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue the remaining agent conversation rows, preferably `GetConversationForAdminAsync` or `GetConversationForUserAsync` if their web consumers can be switched to the same transcript snapshot safely.

## Checkpoint - 2026-05-15 - IAgentService.GetConversationForAdminAsync

Done:
- Removed `Humans.Application.Interfaces.IAgentService.GetConversationForAdminAsync:Humans.Domain.Entities.AgentConversation` from the application-service entity-return baseline.
- Changed admin conversation detail reads to return `AgentConversationTranscriptSnapshot` from the Agent application service.
- Updated admin/API conversation detail rendering to consume transcript snapshots while preserving the existing user-owned detail path through a temporary controller adapter.
- Validation: agent targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue agent conversation reads, with `GetConversationForUserAsync` or `GetMyConversationAsync` next so the temporary controller adapter can be removed.

## Checkpoint - 2026-05-15 - IAgentService.GetConversationForUserAsync

Done:
- Removed `Humans.Application.Interfaces.IAgentService.GetConversationForUserAsync:Humans.Domain.Entities.AgentConversation` from the application-service entity-return baseline.
- Changed user-owned conversation detail reads to return `AgentConversationTranscriptSnapshot` directly from the Agent application service.
- Removed the temporary controller-side domain-to-snapshot adapter introduced for non-admin conversation detail rendering.
- Validation: agent targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue remaining agent reads, with `GetMyConversationAsync` next because it still bundles a domain conversation in its view model.

## Checkpoint - 2026-05-15 - IAgentService.GetMyConversationAsync

Done:
- Removed `Humans.Application.Interfaces.IAgentService.GetMyConversationAsync:Humans.Domain.Entities.AgentConversation` from the application-service entity-return baseline.
- Changed the user-facing transcript bundle to carry `AgentConversationTranscriptSnapshot` instead of `AgentConversation`.
- Updated the web transcript view model to consume the snapshot bundle while preserving existing transcript rendering.
- Validation: agent targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue remaining agent list reads, especially `GetHistoryAsync` and `ListAllConversationsForAdminAsync`, so the conversation list UI stops depending on domain entities.

## Checkpoint - 2026-05-15 - IAgentService.GetHistoryAsync

Done:
- Removed `Humans.Application.Interfaces.IAgentService.GetHistoryAsync:Humans.Domain.Entities.AgentConversation` from the application-service entity-return baseline.
- Added `AgentConversationListSnapshot` for conversation list rows.
- Changed user history reads and the conversation list UI row model to use list snapshots instead of domain conversations.
- Validation: agent targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Convert `IAgentService.ListAllConversationsForAdminAsync` to `AgentConversationListSnapshot` and remove the temporary admin-list mapping in `AgentController`.

## Checkpoint - 2026-05-15 - IAgentService.ListAllConversationsForAdminAsync

Done:
- Removed `Humans.Application.Interfaces.IAgentService.ListAllConversationsForAdminAsync:Humans.Domain.Entities.AgentConversation` from the application-service entity-return baseline.
- Changed admin conversation list reads to return `AgentConversationListSnapshot` from the Agent application service.
- Removed the temporary admin-list entity mapping from `AgentController`.
- Validation: agent targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Agent service entity-return baseline rows are cleared; resume with the next highest-value bounded baseline area.

## Checkpoint - 2026-05-15 - IIssuesService.GetIssueListAsync

Done:
- Removed `Humans.Application.Interfaces.Issues.IIssuesService.GetIssueListAsync:Humans.Domain.Entities.Issue` from the application-service entity-return baseline.
- Added `IssueListSnapshot` for issue list/API list responses.
- Changed issue list reads to resolve reporter/assignee display data through `IUserService` instead of stitching and exposing domain navs.
- Updated MVC and API list mappers plus API controller tests to consume snapshots.
- Validation: issue targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Treat `IIssuesService.GetIssueByIdAsync` as higher risk because authorization handlers and mutation routes still use `Issue` as the resource; continue only with a deliberate auth-resource snapshot plan.

## Checkpoint - 2026-05-15 - ITeamService.GetAllRoleDefinitionsAsync

Done:
- Removed `Humans.Application.Interfaces.Teams.ITeamService.GetAllRoleDefinitionsAsync:Humans.Domain.Entities.TeamRoleDefinition` from the application-service entity-return baseline.
- Added `TeamRoleDefinitionSnapshot` and `TeamRoleAssignmentSnapshot` for all-team role definition reads.
- Changed roster generation to use snapshots instead of domain role definitions.
- Updated the team cache decorator and test fakes for the new service contract.
- Validation: team/shift-dashboard targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Consider `ITeamService.GetRoleDefinitionsAsync` separately; it still feeds role-management screens that need richer mutation-oriented context.

## Checkpoint - 2026-05-15 - ITeamService.GetRoleDefinitionsAsync

Done:
- Removed `Humans.Application.Interfaces.Teams.ITeamService.GetRoleDefinitionsAsync:Humans.Domain.Entities.TeamRoleDefinition` from the application-service entity-return baseline.
- Changed the scoped role-definition read surface to return `TeamRoleDefinitionSnapshot`.
- Updated the team role-management page to render assigned slots from role-definition snapshots plus team-member read models instead of domain navigation properties.
- Validation: team/shift-dashboard targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue team entity-return rows; prefer `GetUserTeamsAsync` or active-member read surfaces if callers can be moved to existing membership snapshots safely.

## Checkpoint - 2026-05-15 - ITeamService.GetActiveMembershipsForRoleReconciliationAsync

Done:
- Removed `Humans.Application.Interfaces.Teams.ITeamService.GetActiveMembershipsForRoleReconciliationAsync:Humans.Domain.Entities.TeamMember` from the application-service entity-return baseline.
- Added `TeamRoleReconciliationMembership` so coordinator role reconciliation reads decision data instead of domain member graphs.
- Updated `SystemTeamSyncJob.ReconcileCoordinatorRolesAsync` to use the snapshot and keep mutations routed through `ApplyMemberRoleChangesAsync`.
- Validation: system-team/team targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue team entity-return rows; prefer `GetActiveMembersForTeamsAsync` only if Google drive reconciliation can be normalized to snapshots without expanding the commit too much.

## Checkpoint - 2026-05-15 - ITeamService.GetAllTeamsForAdminAsync

Done:
- Removed `Humans.Application.Interfaces.Teams.ITeamService.GetAllTeamsForAdminAsync:Humans.Domain.Entities.Team` from the application-service entity-return baseline.
- Deleted the unused admin entity-list service method and cache forwarding method.
- Moved admin list coverage to `GetAdminTeamListAsync` snapshots and lowered `ITeamService` surface budget from 70 to 69.
- Validation: team service targeted tests passed; application-service entity-return ratchet passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue team entity-return rows; `GetAllTeamsAsync` has production callers and should be handled by replacing each caller with narrower read models rather than a direct broad snapshot.

## Checkpoint - 2026-05-15 - ITeamPageService.GetTeamPageDetailAsync

Done:
- Removed both `ITeamPageService.GetTeamPageDetailAsync` entity-return rows for `Team` and `TeamRoleDefinition` from the application-service entity-return baseline.
- Added page-specific team summary/link snapshots and returned role-definition snapshots from the TeamPage service boundary.
- Updated the Team details controller/view model mapping to render from TeamPage snapshots instead of application-service domain entities.
- Validation: TeamPage architecture/entity-return targeted tests passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue team service entity-return rows; `GetAllTeamsAsync` and `GetUserTeamsAsync` remain high-value but require caller-by-caller replacement.

## Checkpoint - 2026-05-15 - ITeamService.GetUserPendingRequestAsync

Done:
- Removed `Humans.Application.Interfaces.Teams.ITeamService.GetUserPendingRequestAsync:Humans.Domain.Entities.TeamJoinRequest` from the application-service entity-return baseline.
- Added `TeamJoinRequestSnapshot` and changed the current-user pending-request check to return that read model instead of the domain entity.
- Kept the snapshot mapper off obsolete `TeamJoinRequest.User` navigation; richer pending-request list snapshots should resolve user display fields explicitly when those rows are handled.
- Validation: team service/entity-return targeted tests passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue pending request entity-return rows; `GetPendingRequestsForTeamAsync` can reuse `TeamJoinRequestSnapshot` with explicit stitched user fields.

## Checkpoint - 2026-05-15 - ITeamService.GetPendingRequestsForTeamAsync

Done:
- Removed `Humans.Application.Interfaces.Teams.ITeamService.GetPendingRequestsForTeamAsync:Humans.Domain.Entities.TeamJoinRequest` from the application-service entity-return baseline.
- Changed team pending-request lists to return `TeamJoinRequestSnapshot` read models.
- Updated the team admin members page to consume requester display/email/profile fields from snapshots, with user fields resolved through `IUserService` in the Teams service.
- Validation: team service/entity-return targeted tests passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue pending request rows; `GetPendingRequestsForApproverAsync` can reuse `TeamJoinRequestSnapshot`, but must preserve approver-scope filtering and team names.

## Checkpoint - 2026-05-15 - ITeamService.GetPendingRequestsForApproverAsync

Done:
- Removed `Humans.Application.Interfaces.Teams.ITeamService.GetPendingRequestsForApproverAsync:Humans.Domain.Entities.TeamJoinRequest` from the application-service entity-return baseline.
- Deleted the unused approver pending-request service/cache surface and its now-dead service tests.
- Lowered `ITeamService` surface budget from 69 to 68.
- Validation: team service/entity-return targeted tests passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue team service entity-return rows; remaining join-request surface is cleared, so move back to member/team read surfaces by smallest safe caller set.

## Checkpoint - 2026-05-15 - ITeamService.GetTeamDetailAsync

Done:
- Removed both `ITeamService.GetTeamDetailAsync` entity-return rows for `Team` and `TeamRoleDefinition` from the application-service entity-return baseline.
- Changed `TeamDetailResult` to expose team-page summaries, child team links, and role-definition snapshots instead of domain graphs.
- Updated `TeamPageService` to compose directly from the snapshot detail result and avoid domain detail handling at its boundary.
- Validation: TeamService/TeamPage/entity-return targeted tests passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue team service entity-return rows; remaining high-value rows include `GetUserTeamsAsync`, `GetActiveMembersForTeamsAsync`, `GetAllTeamsAsync`, `GetByIdsWithParentsAsync`, and team lookup methods.

## Checkpoint - 2026-05-15 - ITeamService.GetSystemTeamWithActiveMembersAsync

Done:
- Removed `Humans.Application.Interfaces.Teams.ITeamService.GetSystemTeamWithActiveMembersAsync:Humans.Domain.Entities.Team` from the application-service entity-return baseline.
- Added `SystemTeamMembershipSnapshot` for system-team sync membership reconciliation.
- Updated `SystemTeamSyncJob` to use team identity/flags/member user ids from the snapshot while keeping all membership mutations routed through Teams service commands.
- Validation: system-team/job/team-service/entity-return targeted tests passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue team member read surfaces; `GetActiveMembersForTeamsAsync` remains the next bounded candidate if Google and public team rollups can be normalized to member snapshots.

## Checkpoint - 2026-05-15 - ITeamService.GetActiveMembersForTeamsAsync

Done:
- Removed `Humans.Application.Interfaces.Teams.ITeamService.GetActiveMembersForTeamsAsync:Humans.Domain.Entities.TeamMember` from the application-service entity-return baseline.
- Added `TeamActiveMemberSnapshot` for cross-section active-member reads.
- Updated public team child-member rollups and Google Drive primary-member reconciliation to use member snapshots instead of `TeamMember.User` navigation.
- Validation: TeamService/GoogleWorkspace/entity-return targeted tests passed; `dotnet build Humans.slnx --disable-build-servers -v q` passed.

Next:
- Continue broader team read surfaces; `GetUserTeamsAsync` or `GetAllTeamsAsync` are high-value but require caller-by-caller replacement across profile/governance/google/web surfaces.

## 2026-05-15 checkpoint - feedback submit role context
- Done: Removed `NoBusinessLogicInControllers` baseline entry `src/Humans.Web/Controllers/FeedbackController.cs:Submit/1` by moving role-context formatting into `IFeedbackService.SubmitUserFeedbackAsync` / `FeedbackService`.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~FeedbackServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`; `dotnet build Humans.slnx --disable-build-servers -v q`.
- Next: Continue with the highest-value safe controller business-logic baseline entry; avoid graph-heavy budget/entity-read work until a dedicated read-model plan is in scope.

## 2026-05-15 checkpoint - store stripe webhook controller logic
- Done: Removed `NoBusinessLogicInControllers` baseline entry `src/Humans.Web/Controllers/StoreStripeWebhookController.cs:Receive/1` by moving parsed checkout-event interpretation and payment-session validation into `IStoreService.HandleStripeCheckoutWebhookEventAsync` / `StoreService`.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~StoreServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`; `dotnet build Humans.slnx --disable-build-servers -v q`.
- Blocked note: `StoreStripeWebhookControllerTests` invalid/unknown-event cases pass, but completed/duplicate integration cases fail before the webhook path while seeding sign-in due to Hangfire `JobStorage.Current` not being initialized in the integration fixture.
- Next: Continue with a single-action controller baseline item that can move real decision logic into an existing Application service without touching persistence shape.

## 2026-05-15 checkpoint - store checkout payment rules
- Done: Removed `NoBusinessLogicInControllers` baseline entry `src/Humans.Web/Controllers/StoreController.cs:Pay/3` by moving checkout configuration, amount/balance validation, and Stripe line-item description construction into `IStoreService.CreateStripeCheckoutSessionAsync` / `StoreService`.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~StoreServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`; `dotnet build Humans.slnx --disable-build-servers -v q`.
- Next: Continue with a single safe controller baseline entry backed by an existing Application service seam.

## 2026-05-15 checkpoint - team leave coordinator sync
- Done: Removed `NoBusinessLogicInControllers` baseline entry `src/Humans.Web/Controllers/TeamController.cs:Leave/1` by moving coordinator system-team reconciliation into `TeamService.LeaveTeamAsync` and removing the controller's `ISystemTeamSync` dependency.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~TeamServiceTests|FullyQualifiedName~TeamRoleServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`; `dotnet build Humans.slnx --disable-build-servers -v q`.
- Next: Continue with a single safe controller baseline item where an Application service already owns the side effect.

## 2026-05-15 checkpoint - expense attachment storage read
- Done: Removed `NoBusinessLogicInControllers` baseline entry `src/Humans.Web/Controllers/ExpensesController.cs:Attachment/1` by moving attachment lookup, storage-key construction, and file reads into `IExpenseReportService.TryReadAttachmentAsync` / `ExpenseReportService`.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ExpenseReportServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`; `dotnet build Humans.slnx --disable-build-servers -v q`.
- Next: Continue with a single controller baseline item that removes direct storage/workflow logic from MVC.

## 2026-05-15 - checkpoint
- Done: moved GovernanceApplicationsController POST Create tier and Asociado submission validation into ApplicationDecisionService.SubmitAsync.
- Removed baseline: src/Humans.Web/Controllers/GovernanceApplicationsController.cs:Create/1.
- Validation: dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ApplicationDecisionServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"; dotnet build Humans.slnx --disable-build-servers -v q.
- Next: continue selecting safe high-value controller or service-boundary baseline entries; avoid broad multi-row extractions unless they can be split cleanly.

## 2026-05-15 - checkpoint
- Done: moved AdminLegalDocumentsController create-and-initial-sync orchestration into AdminLegalDocumentService.CreateLegalDocumentWithInitialSyncAsync.
- Removed baseline: src/Humans.Web/Controllers/AdminLegalDocumentsController.cs:CreateLegalDocument/1.
- Validation: dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~AdminLegalDocumentServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"; dotnet build Humans.slnx --disable-build-servers -v q.
- Next: continue with high-value controller rows where multi-service orchestration can be collapsed into a single application command.

## 2026-05-15 - checkpoint
- Done: moved CampaignController GenerateCodes validation, vendor generation, and generated-code import into CampaignService.GenerateAndImportDiscountCodesAsync.
- Removed baseline: src/Humans.Web/Controllers/CampaignController.cs:GenerateCodes/4.
- Validation: dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~CampaignServiceTests|FullyQualifiedName~NoBusinessLogicInControllers|FullyQualifiedName~CampaignsArchitectureTests"; dotnet build Humans.slnx --disable-build-servers -v q.
- Blocked/unrelated: ServiceBoundaryArchitectureTests.Repository_ownership_map_covers_all_repositories currently fails on missing IAdminDatabaseDiagnosticsRepository ownership when run directly; not caused by this campaign change.
- Next: continue with controller rows that contain validation plus multi-service orchestration.

## 2026-05-15 - checkpoint
- Done: moved IssuesController PostComment comment-and-resolve orchestration into IssuesService.PostCommentAsync with resolveOnPost support.
- Removed baseline: src/Humans.Web/Controllers/IssuesController.cs:PostComment/2.
- Validation: dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~IssuesServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"; dotnet build Humans.slnx --disable-build-servers -v q.
- Next: continue with issue mutation rows or other controller rows where status/assignment/routing rules are still in MVC.

## 2026-05-15 - checkpoint
- Done: moved IssuesController Submit reporter-role additional-context composition and 2000-character cap into IssuesService.SubmitIssueAsync.
- Removed baseline: src/Humans.Web/Controllers/IssuesController.cs:Submit/1.
- Validation: dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~IssuesServiceTests|FullyQualifiedName~IssuesApiControllerTests|FullyQualifiedName~NoBusinessLogicInControllers"; dotnet build Humans.slnx --disable-build-servers -v q.
- Next: continue with remaining issue mutation rows or other controller rows where MVC still owns business command composition.

## 2026-05-15 - checkpoint
- Done: moved CampController AddMember active-season lookup and invalid-user/no-active-season outcomes into CampService.AddCampMemberToActiveSeasonAsLeadAsync.
- Removed baseline: src/Humans.Web/Controllers/CampController.cs:AddMember/3.
- Validation: dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~CampServiceTests|FullyQualifiedName~CampsArchitectureTests|FullyQualifiedName~NoBusinessLogicInControllers"; dotnet build Humans.slnx --disable-build-servers -v q.
- Next: continue with camp role assignment or membership rows that still resolve active seasons or map business outcomes in MVC.

## 2026-05-15 - checkpoint
- Done: moved CampController AssignRoleByUser active-season lookup and add-member-then-assign orchestration into CampService.AddMemberAndAssignRoleInActiveSeasonAsync.
- Removed baseline: src/Humans.Web/Controllers/CampController.cs:AssignRoleByUser/4.
- Validation: dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~CampServiceTests|FullyQualifiedName~CampsArchitectureTests|FullyQualifiedName~NoBusinessLogicInControllers"; dotnet build Humans.slnx --disable-build-servers -v q.
- Next: continue with remaining camp membership/role rows or switch to another high-value controller baseline if camp rows require broad UI mapping changes.

## 2026-05-15 - checkpoint
- Done: moved CampaignController Create required-field validation and campaign field trimming into CampaignService.CreateAsync.
- Removed baseline: src/Humans.Web/Controllers/CampaignController.cs:Create/5.
- Validation: dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~CampaignServiceTests|FullyQualifiedName~NoBusinessLogicInControllers|FullyQualifiedName~CampaignsArchitectureTests"; dotnet build Humans.slnx --disable-build-servers -v q.
- Next: continue with remaining campaign edit row or switch to another controller baseline with service-owned validation/command rules.

## 2026-05-15 - checkpoint
- Done: moved CampaignController Edit required-field validation and update field trimming into CampaignService.UpdateAsync.
- Removed baseline: src/Humans.Web/Controllers/CampaignController.cs:Edit/6.
- Validation: dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~CampaignServiceTests|FullyQualifiedName~NoBusinessLogicInControllers|FullyQualifiedName~CampaignsArchitectureTests"; dotnet build Humans.slnx --disable-build-servers -v q.
- Next: continue selecting controller rows with service-owned validation/command rules rather than presentation-only mapping.

## 2026-05-15 - checkpoint
- Done: moved CityPlanningApiController SaveCampPolygon GeoJSON validation into CityPlanningService.SaveCampPolygonAsync.
- Removed baseline: src/Humans.Web/Controllers/CityPlanningApiController.cs:SaveCampPolygon/3.
- Validation: dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~CityPlanningServiceTests|FullyQualifiedName~CityPlanningArchitectureTests|FullyQualifiedName~NoBusinessLogicInControllers"; dotnet build Humans.slnx --disable-build-servers -v q.
- Next: continue with city-planning settings upload/date rows or other controller validation rows.

## 2026-05-15 - checkpoint
- Done: moved CityPlanningController UpdatePlacementDates string parsing and invalid-date outcomes into CityPlanningService.UpdatePlacementDatesAsync.
- Removed baseline: src/Humans.Web/Controllers/CityPlanningController.cs:UpdatePlacementDates/3.
- Validation: dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~CityPlanningServiceTests|FullyQualifiedName~CityPlanningArchitectureTests|FullyQualifiedName~NoBusinessLogicInControllers"; dotnet build Humans.slnx --disable-build-servers -v q.
- Next: continue with GeoJSON upload rows or another bounded controller validation row.

## 2026-05-15 - checkpoint
- Done: moved CityPlanningController UploadLimitZone file validation, size limit, stream read, JSON validation, and persistence into CityPlanningService.UpdateLimitZoneFromUploadAsync.
- Removed baseline: src/Humans.Web/Controllers/CityPlanningController.cs:UploadLimitZone/2.
- Validation: dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~CityPlanningServiceTests|FullyQualifiedName~CityPlanningArchitectureTests|FullyQualifiedName~NoBusinessLogicInControllers"; dotnet build Humans.slnx --disable-build-servers -v q.
- Next: continue with UploadOfficialZones or another upload/validation baseline row.

## 2026-05-15 - checkpoint
- Done: moved CityPlanningController UploadOfficialZones file validation, size limit, stream read, JSON validation, and persistence into CityPlanningService.UpdateOfficialZonesFromUploadAsync.
- Removed baseline: src/Humans.Web/Controllers/CityPlanningController.cs:UploadOfficialZones/2.
- Validation: dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~CityPlanningServiceTests|FullyQualifiedName~CityPlanningArchitectureTests|FullyQualifiedName~NoBusinessLogicInControllers"; dotnet build Humans.slnx --disable-build-servers -v q.
- Next: continue selecting remaining feasible baseline rows with real service-boundary movement.

## 2026-05-15 - Account magic-link signup controller debt
- Done: Removed `src/Humans.Web/Controllers/AccountController.cs:CompleteSignup/4` from `NoBusinessLogicInControllers.baseline.txt`.
- Done: Moved magic-link signup creation, verified-email double-submit detection, last-login update, verified-email row creation, orphan rollback, and stub-profile creation into `IAccountProvisioningService.CompleteMagicLinkSignupAsync`.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~AccountProvisioningServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `97d611973 refactor(auth): complete magic link signup in service` pushed.
- Next: Continue with the next highest-value safe baseline item from `tests/Humans.Application.Tests/Architecture/Baselines`.

## 2026-05-15 - Store add-line controller debt
- Done: Removed `src/Humans.Web/Controllers/StoreController.cs:AddLine/4` from `NoBusinessLogicInControllers.baseline.txt`.
- Done: Added `IStoreService.AddLineWithResultAsync` so expected add-line validation/rejection outcomes are mapped inside Store instead of controller catch branches.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~StoreServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `468464c40 refactor(store): return add-line validation results from service` pushed.
- Next: Continue with the next highest-value safe Store or baseline item.

## 2026-05-15 - Store remove-line controller debt
- Done: Removed `src/Humans.Web/Controllers/StoreController.cs:RemoveLine/3` from `NoBusinessLogicInControllers.baseline.txt`.
- Done: Added `IStoreService.RemoveLineWithResultAsync` so expected remove-line rejection outcomes are mapped inside Store instead of controller catch branches.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~StoreServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `e532dfc7c refactor(store): return remove-line validation results from service` pushed.
- Next: Continue with the next highest-value safe baseline item.

## 2026-05-15 - Store counterparty controller debt
- Done: Removed `src/Humans.Web/Controllers/StoreController.cs:UpdateCounterparty/3` from `NoBusinessLogicInControllers.baseline.txt`.
- Done: Added `IStoreService.UpdateCounterpartyWithResultAsync` so expected counterparty update rejections are mapped inside Store instead of controller catch branches.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~StoreServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `028fe842c refactor(store): return counterparty validation results from service` pushed.
- Next: Continue with the next highest-value safe baseline item.

## 2026-05-15 - Store catalog save controller debt
- Done: Removed `src/Humans.Web/Controllers/StoreAdminController.cs:Save/2` from `NoBusinessLogicInControllers.baseline.txt`.
- Done: Added `IStoreService.SaveProductWithResultAsync` so catalog save date parsing, DTO construction, create/update selection, and expected validation mapping live in Store instead of the controller.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~StoreServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `71cfe3ab2 refactor(store): save catalog products through service result` pushed.
- Next: Continue with the next highest-value safe baseline item.

## 2026-05-15 - Expenses add-line controller debt
- Done: Removed `src/Humans.Web/Controllers/ExpensesController.cs:AddLine/2` from `NoBusinessLogicInControllers.baseline.txt`.
- Done: Added `IExpenseReportService.AddLineWithResultAsync` so expected add-line failures are mapped inside Expenses instead of controller catch branches.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ExpenseReportServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `f354a5948 refactor(expenses): return add-line results from service` pushed.
- Next: Continue with the next highest-value safe baseline item.

## 2026-05-15 - Expenses update-line controller debt
- Done: Removed `src/Humans.Web/Controllers/ExpensesController.cs:UpdateLine/2` from `NoBusinessLogicInControllers.baseline.txt`.
- Done: Added `IExpenseReportService.UpdateLineWithResultAsync` so expected update-line failures are mapped inside Expenses instead of controller catch branches.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ExpenseReportServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `60c9e0f95 refactor(expenses): return update-line results from service` pushed.
- Next: Continue with the next highest-value safe baseline item.

## 2026-05-15 - Expenses remove-line service-result cleanup
- Done: Added `IExpenseReportService.RemoveLineWithResultAsync` and switched `ExpensesController.RemoveLine` to use service-result mapping instead of controller catch branches.
- Note: No baseline row was removed in this checkpoint because `ExpensesController.RemoveLine` was not present in the remaining baseline list; remaining Expenses rows were rechecked before the next iteration.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ExpenseReportServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `3f64515e3 refactor(expenses): return remove-line results from service` pushed.
- Next: Continue with actual remaining Expenses baseline rows: Approve, AttachFile, CoordinatorReject, Endorse, Reject, Detail, Edit, Iban, Submit, Withdraw.

## 2026-05-15 - Expenses remove-line service-result refactor
- Done: Added `IExpenseReportService.RemoveLineWithResultAsync` so expected remove-line failures are mapped inside Expenses instead of controller catch branches.
- Note: No baseline row was removed for this commit because `ExpensesController.RemoveLine/2` was not present in `NoBusinessLogicInControllers.baseline.txt`; remaining Expenses rows were rechecked before the next iteration.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ExpenseReportServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `3f64515e3 refactor(expenses): return remove-line results from service` pushed.
- Next: Continue with `src/Humans.Web/Controllers/ExpensesController.cs:AttachFile/3`.

## 2026-05-15 - Expenses attachment upload service-result refactor
- Done: Removed `src/Humans.Web/Controllers/ExpensesController.cs:AttachFile/3` from the controller-business-logic baseline.
- Done: Added `IExpenseReportService.AttachFileToLineWithResultAsync` so expected attachment upload failures are logged and mapped inside the expense service instead of controller catch branches.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ExpenseReportServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `6636708d4 refactor(expenses): return attachment upload results from service` pushed.
- Next: Continue with another remaining `ExpensesController` baseline row.

## 2026-05-15 - Expenses submit service-result refactor
- Done: Removed `src/Humans.Web/Controllers/ExpensesController.cs:Submit/1` from the controller-business-logic baseline.
- Done: Added `IExpenseReportService.SubmitWithResultAsync` so submit validation failures and exception mapping live in the expense service instead of the controller.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ExpenseReportServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `ac2cecfcd refactor(expenses): return submit results from service` pushed.
- Next: Continue with another remaining `ExpensesController` baseline row.

## 2026-05-15 - Expenses withdraw service-result refactor
- Done: Removed `src/Humans.Web/Controllers/ExpensesController.cs:Withdraw/1` from the controller-business-logic baseline.
- Done: Added `IExpenseReportService.WithdrawWithResultAsync` so withdrawal failure/exception mapping lives in the expense service instead of the controller.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ExpenseReportServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `c87ddfdfd refactor(expenses): return withdraw results from service` pushed.
- Next: Continue with another remaining `ExpensesController` baseline row.

## 2026-05-15 - Expenses coordinator endorse service-result refactor
- Done: Removed `src/Humans.Web/Controllers/ExpensesController.cs:Endorse/1` from the controller-business-logic baseline.
- Done: Added `IExpenseReportService.CoordinatorEndorseWithResultAsync` so endorsement failure/exception mapping lives in the expense service instead of the controller.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ExpenseReportServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `c5212fea7 refactor(expenses): return coordinator endorse results from service` pushed.
- Next: Continue with another remaining `ExpensesController` baseline row.

## 2026-05-15 - Expenses coordinator reject service-result refactor
- Done: Removed `src/Humans.Web/Controllers/ExpensesController.cs:CoordinatorReject/2` from the controller-business-logic baseline.
- Done: Added `IExpenseReportService.CoordinatorRejectWithResultAsync` so coordinator rejection failure/exception mapping lives in the expense service instead of the controller.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ExpenseReportServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `aa7bf1931 refactor(expenses): return coordinator reject results from service` pushed.
- Next: Continue with another remaining `ExpensesController` baseline row.

## 2026-05-15 - Expenses approval service-result refactor
- Done: Removed `src/Humans.Web/Controllers/ExpensesController.cs:Approve/2` from the controller-business-logic baseline.
- Done: Added `IExpenseReportService.ApproveWithResultAsync` so finance approval failure/exception mapping lives in the expense service instead of the controller.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ExpenseReportServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `95df5f3f9 refactor(expenses): return approval results from service` pushed.
- Next: Continue with another remaining `ExpensesController` baseline row.

## 2026-05-15 - Expenses finance reject service-result refactor
- Done: Removed `src/Humans.Web/Controllers/ExpensesController.cs:Reject/2` from the controller-business-logic baseline.
- Done: Added `IExpenseReportService.FinanceRejectWithResultAsync` so finance rejection failure/exception mapping lives in the expense service instead of the controller.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ExpenseReportServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `54c4fea68 refactor(expenses): return finance reject results from service` pushed.
- Next: Continue with another remaining `ExpensesController` baseline row.

## 2026-05-15 - Expenses submitter IBAN service-result refactor
- Done: Removed `src/Humans.Web/Controllers/ExpensesController.cs:Iban/2` from the controller-business-logic baseline.
- Done: Added `IExpenseReportService.SaveSubmitterIbanWithResultAsync` so IBAN normalization, validation, profile-write result mapping, and redisplay state live in the expense service instead of the controller.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ExpenseReportServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `756e9a539 refactor(expenses): save submitter iban through service result` pushed.
- Next: Continue with remaining `ExpensesController` view/read-model baseline rows.

## 2026-05-15 - Expenses IBAN modal view service projection
- Done: Removed `src/Humans.Web/Controllers/ExpensesController.cs:Iban/1` from the controller-business-logic baseline.
- Done: Added `IExpenseReportService.GetSubmitterIbanViewAsync` so IBAN modal masking/projection lives in the expense service instead of the controller.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ExpenseReportServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `9f0a6f1c5 refactor(expenses): build submitter iban view in service` pushed.
- Next: Continue with remaining `ExpensesController` detail/edit baseline rows.

## 2026-05-15 - Expenses draft update service-result refactor
- Done: Removed `src/Humans.Web/Controllers/ExpensesController.cs:Edit/2` from the controller-business-logic baseline.
- Done: Added `IExpenseReportService.UpdateDraftWithResultAsync` so draft-update exception mapping lives in the expense service, and isolated edit redisplay setup in a controller helper.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ExpenseReportServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `e4e64d5a1 refactor(expenses): return draft update results from service` pushed.
- Next: Continue with remaining `ExpensesController` detail/edit read-model baseline rows.

## 2026-05-15 - Expenses edit view composition reuse
- Done: Removed `src/Humans.Web/Controllers/ExpensesController.cs:Edit/1` from the controller-business-logic baseline.
- Done: Reused the shared edit-model composition helper from the draft-update refactor so GET and POST redisplay no longer duplicate category/editability setup.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"` passed with the known skipped architecture test.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `d0d27fd43 refactor(expenses): reuse edit model composition` pushed.
- Next: Continue with remaining `src/Humans.Web/Controllers/ExpensesController.cs:Detail/1`.

## 2026-05-15 - Expenses detail view-data service projection
- Done: Removed `src/Humans.Web/Controllers/ExpensesController.cs:Detail/1` from the controller-business-logic baseline.
- Done: Added `IExpenseReportService.GetDetailViewDataAsync` so category display, submit/edit/withdraw flags, and IBAN projection live in the expense service instead of the controller.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ExpenseReportServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `7c4f90827 refactor(expenses): build detail view data in service` pushed.
- Next: Re-scan architecture baselines for the next high-value feasible item.

## 2026-05-15 - Onboarding reject review messaging extraction
- Done: Removed `src/Humans.Web/Controllers/OnboardingReviewController.cs:Reject/2` from the controller-business-logic baseline.
- Done: Extracted signup-reject outcome localization into `SetRejectSignupResultMessage`, leaving the action as orchestration around the onboarding service call.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"` passed with the known skipped architecture test.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `3f16376d2 refactor(onboarding): extract reject review messaging` pushed.
- Next: Resolve stale `src/Humans.Web/Controllers/OnboardingReviewController.cs:Finalize/1` baseline row or continue to next real controller-business-logic entry.

## 2026-05-15 - Stale onboarding finalize baseline removal
- Done: Removed stale `src/Humans.Web/Controllers/OnboardingReviewController.cs:Finalize/1` from the controller-business-logic baseline because the action no longer exists in `OnboardingReviewController`.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"` passed with the known skipped architecture test.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `ac71805d1 test(architecture): remove stale onboarding finalize baseline` pushed.
- Next: Continue with the next high-value real controller-business-logic baseline item.

## 2026-05-15 - Budget line-item create service-result refactor
- Done: Removed `src/Humans.Web/Controllers/BudgetController.cs:CreateLineItem/7` from the controller-business-logic baseline.
- Done: Added `IBudgetService.CreateLineItemWithResultAsync` so create-line expected failures are logged and mapped in the budget service instead of controller catch branches.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~BudgetServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `947b75093 refactor(budget): return line item create results from service` pushed.
- Next: Continue with `src/Humans.Web/Controllers/BudgetController.cs:UpdateLineItem/8`.

## 2026-05-15 - Budget line-item update service-result refactor
- Done: Removed `src/Humans.Web/Controllers/BudgetController.cs:UpdateLineItem/8` from the controller-business-logic baseline.
- Done: Added `IBudgetService.UpdateLineItemWithResultAsync` so update-line expected failures are logged and mapped in the budget service instead of controller catch branches.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~BudgetServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `43da0860e refactor(budget): return line item update results from service` pushed.
- Next: Continue with remaining high-value controller-business-logic baseline items.

## 2026-05-15 - Budget coordinator landing service view-data
- Done: Removed `src/Humans.Web/Controllers/BudgetController.cs:Index/0` from the controller-business-logic baseline.
- Done: Added `IBudgetService.GetCoordinatorBudgetViewDataAsync` so coordinator redirect decisions, editable team ids, and active-year lookup are assembled in the budget service instead of the controller.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~BudgetServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `f4a633931 refactor(budget): build coordinator budget view data in service` pushed.
- Next: Continue with `src/Humans.Web/Controllers/BudgetController.cs:CategoryDetail/1`.

## 2026-05-15 - Budget category detail service view-data
- Done: Removed `src/Humans.Web/Controllers/BudgetController.cs:CategoryDetail/1` from the controller-business-logic baseline.
- Done: Added `IBudgetService.GetCoordinatorCategoryDetailViewDataAsync` so category detail loading, non-finance visibility gating, and team option loading live in the budget service instead of the controller.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~BudgetServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"` passed.
- Validation: `dotnet build Humans.slnx --disable-build-servers -v q` passed.
- Commit: `6471023ec refactor(budget): build category detail view data in service` pushed.
- Next: Continue with next high-value controller-business-logic baseline item.

## Checkpoint - 2026-05-15 - Issues status update result
Done:
- Cleared `src/Humans.Web/Controllers/IssuesController.cs:UpdateStatus/2` from `NoBusinessLogicInControllers.baseline.txt`.
- Added `IIssuesService.UpdateStatusWithResultAsync` so mutation failure semantics live in the application service instead of controller try/catch logic.
- Added service tests for successful status updates and missing issue results.
Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~IssuesServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None.
Next:
- Continue the IssuesController mutation cluster, prioritizing `UpdateAssignee/2`, `UpdateSection/2`, or `SetGitHubIssue/2` as the next safe service-result refactor.

## Checkpoint - 2026-05-15 - Issues assignee update result
Done:
- Cleared `src/Humans.Web/Controllers/IssuesController.cs:UpdateAssignee/2` from `NoBusinessLogicInControllers.baseline.txt`.
- Added `IIssuesService.UpdateAssigneeWithResultAsync` so controller mutation failure handling delegates to the application service.
- Added service tests for successful assignee updates and missing issue results.
Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~IssuesServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None.
Next:
- Continue the IssuesController mutation cluster with `UpdateSection/2` or `SetGitHubIssue/2`.

## Checkpoint - 2026-05-15 - Issues section update result
Done:
- Cleared `src/Humans.Web/Controllers/IssuesController.cs:UpdateSection/2` from `NoBusinessLogicInControllers.baseline.txt`.
- Added `IIssuesService.UpdateSectionWithResultAsync` so expected section-update rejection handling lives in the application service.
- Added service tests for successful section updates and terminal issue rejection messages.
Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~IssuesServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None.
Next:
- Continue the IssuesController cluster with `SetGitHubIssue/2` or inspect the remaining issue list/detail shaping rows.

## Checkpoint - 2026-05-15 - Issues GitHub link result
Done:
- Cleared `src/Humans.Web/Controllers/IssuesController.cs:SetGitHubIssue/2` from `NoBusinessLogicInControllers.baseline.txt`.
- Added `IIssuesService.SetGitHubIssueNumberWithResultAsync` so GitHub link mutation failures are handled by the application service.
- Added service tests for successful GitHub issue linking and missing issue results.
Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~IssuesServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None.
Next:
- Inspect the remaining baseline for the next high-value safe cluster outside the completed Issues mutation rows.

## Checkpoint - 2026-05-15 - Calendar create validation result
Done:
- Cleared `src/Humans.Web/Controllers/CalendarController.cs:Create/2` from `NoBusinessLogicInControllers.baseline.txt`.
- Added `ICalendarService.CreateEventWithResultAsync` so recurrence/timezone validation failures are returned from the application service instead of caught in the controller.
- Added a service validation test proving malformed recurrence rules return a field-specific result without writing through the repository.
Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~CalendarServiceValidationTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None.
Next:
- Apply the same service-result pattern to `src/Humans.Web/Controllers/CalendarController.cs:Edit/3`.

## Checkpoint - 2026-05-15 - Calendar edit validation result
Done:
- Cleared `src/Humans.Web/Controllers/CalendarController.cs:Edit/3` from `NoBusinessLogicInControllers.baseline.txt`.
- Added `ICalendarService.UpdateEventWithResultAsync` so edit validation and race-not-found handling are returned by the application service.
- Added a service validation test proving unknown recurrence timezones return a field-specific result.
Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~CalendarServiceValidationTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None.
Next:
- Select the next high-value safe baseline cluster outside Calendar.

## Checkpoint - 2026-05-15 - Team resource inherited access result
Done:
- Cleared `src/Humans.Web/Controllers/TeamAdminController.cs:ToggleRestrictInheritedAccess/3` from `NoBusinessLogicInControllers.baseline.txt`.
- Added `ITeamResourceService.SetRestrictInheritedAccessWithResultAsync` so Google Drive mutation failures are returned from the application service instead of caught in the controller.
- Added a team-resource service test for a successful inherited-access toggle.
Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~TeamResourceServiceDeactivateTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None.
Next:
- Continue with resource-linking rows such as `TeamAdminController.LinkDriveResource/2` or `TeamAdminController.LinkGroup/2` if they can be thinned without moving UI localization into application services.

## Checkpoint - 2026-05-15 - Team resource drive link messaging
Done:
- Cleared `src/Humans.Web/Controllers/TeamAdminController.cs:LinkDriveResource/2` from `NoBusinessLogicInControllers.baseline.txt`.
- Extracted Drive resource link success/error flash mapping into helper methods so the action only performs authorization, validation, service call, and redirect.
Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None.
Next:
- Apply the shared resource-link error builder to `TeamAdminController.LinkGroup/2`.

## Checkpoint - 2026-05-15 - Team resource group link messaging
Done:
- Cleared `src/Humans.Web/Controllers/TeamAdminController.cs:LinkGroup/2` from `NoBusinessLogicInControllers.baseline.txt`.
- Reused the shared resource-link error builder for Google Group linking, reducing duplicated service-account hint composition in controller actions.
Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None.
Next:
- Continue to the next high-value safe baseline row, likely another TeamAdmin role/page mutation if it can be moved behind service-result handling.

## Checkpoint - 2026-05-15 - Team page update result
Done:
- Cleared `src/Humans.Web/Controllers/TeamAdminController.cs:EditPage/2` from `NoBusinessLogicInControllers.baseline.txt`.
- Replaced the old `ITeamService.UpdateTeamPageContentAsync` shape with a result-returning application input that owns CTA filtering/trimming and update failure reporting without increasing the interface method count.
- Updated the caching team service and shift-dashboard test stub to the new contract.
- Added a team-service test covering CTA normalization and page update persistence.
Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~TeamServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None.
Next:
- Continue with TeamAdmin role mutation rows or another high-value controller mutation baseline.

## Checkpoint - 2026-05-15 - Google account provisioning validation
Done:
- Cleared `src/Humans.Web/Controllers/GoogleController.cs:ProvisionAccount/1` from `NoBusinessLogicInControllers.baseline.txt`.
- Moved standalone Workspace account required-field validation into `GoogleAdminService.ProvisionStandaloneAccountAsync` without adding interface surface.
- Added a Google admin service test proving incomplete input returns an error before any Workspace calls.
Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~GoogleAdminServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None.
Next:
- Continue Google admin rows such as password reset or group settings remediation, favoring service-owned validation/result handling.

## Checkpoint - 2026-05-15 - Google password reset validation
Done:
- Cleared `src/Humans.Web/Controllers/GoogleController.cs:ResetPassword/1` from `NoBusinessLogicInControllers.baseline.txt`.
- Moved missing-email validation into `GoogleAdminService.ResetPasswordAsync` without adding interface surface.
- Added a Google admin service test proving empty email returns an error before Workspace reset calls.
Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~GoogleAdminServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None.
Next:
- Apply the same service-owned validation pattern to `GoogleController.ResetPasswordAndGenerate2Fa/1`.

## Checkpoint - 2026-05-15 - Google recovery reset validation
Done:
- Cleared `src/Humans.Web/Controllers/GoogleController.cs:ResetPasswordAndGenerate2Fa/1` from `NoBusinessLogicInControllers.baseline.txt`.
- Moved missing-email validation into `GoogleAdminService.ResetPasswordAndGenerate2FaAsync` without adding interface surface.
- Added a Google admin service test proving blank email returns an error before Workspace account lookup or password reset calls.
Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~GoogleAdminServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None.
Next:
- Continue Google group settings remediation rows if they can be moved behind service result handling safely.

## Checkpoint - 2026-05-15 - Google group remediation result
Done:
- Cleared `src/Humans.Web/Controllers/GoogleController.cs:RemediateGroupSettings/2` from `NoBusinessLogicInControllers.baseline.txt`.
- Changed `IGoogleSyncService.RemediateGroupSettingsAsync` to return `GroupSettingsRemediationResult`, keeping interface method count flat while moving Google API failure handling into the service.
- Updated the stub sync service, Google reconciliation job, and controller callers to use the explicit result.
Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None.
Next:
- Continue with `GoogleController.RemediateAllGroupSettings/0`, which can now reuse the remediation result instead of per-group exception handling.

## Checkpoint - 2026-05-15 - Google batch remediation result usage
Done:
- Cleared `src/Humans.Web/Controllers/GoogleController.cs:RemediateAllGroupSettings/0` from `NoBusinessLogicInControllers.baseline.txt`.
- Simplified batch remediation to consume `GroupSettingsRemediationResult` for each drifted group instead of using per-group exception handling in the controller.
Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None.
Next:
- Select the next safe baseline cluster outside completed Google admin remediation/password rows.

## Checkpoint - 2026-05-15 camp image upload
- Done: Cleared `CampController.cs:UploadImage/2` by replacing expected upload validation exceptions with `CampImageUploadResult` from `ICampService.UploadImageAsync`.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~CampServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`; `dotnet build Humans.slnx --disable-build-servers -v q`.
- Commit: `15cc4ec8b refactor(camps): return image upload results from service`.
- Next: Continue with the camp controller baseline cluster, preferring membership/contact/edit flows where service result contracts can remove controller business logic.

## Checkpoint - 2026-05-15 camp leave membership
- Done: Cleared `CampController.cs:LeaveMembership/2` by making `ICampService.LeaveCampAsync` return `CampMembershipMutationResult` instead of throwing for expected ownership/status failures.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~CampServiceTests|FullyQualifiedName~CampServiceEarlyEntryTests|FullyQualifiedName~NoBusinessLogicInControllers"`; `dotnet build Humans.slnx --disable-build-servers -v q`.
- Commit: `517436b7c refactor(camps): return leave membership results from service`.
- Next: Continue the camp controller baseline cluster, preferring request/contact/edit flows that can move decision logic into application services without changing persistence shape.

## Checkpoint - 2026-05-15 camp membership request
- Done: Cleared `CampController.cs:RequestMembership/1` by moving membership request notice message/severity onto `CampMemberRequestResult` and removing controller-side outcome branching and expected exception handling.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~CampServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`; `dotnet build Humans.slnx --disable-build-servers -v q`.
- Commit: `840effcf6 refactor(camps): return membership request notices from service`.
- Next: Continue camp controller debt, prioritizing remaining mutation actions that catch expected service exceptions.

## Checkpoint - 2026-05-15 camp role assignment
- Done: Cleared `CampController.cs:AssignRole/4` by removing duplicate controller-side role outcome mapping and reusing the shared assignment flash helper.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~CampRoleServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`; `dotnet build Humans.slnx --disable-build-servers -v q`.
- Commit: `35292bb18 refactor(camps): reuse role assignment flash mapping`.
- Next: Continue camp controller debt, prioritizing create/edit/contact/register flows where controller validation can move behind service result contracts.

## Checkpoint - 2026-05-15 camp contact workflow
- Done: Cleared `CampController.cs:Contact/2` by moving facilitated-contact lead notification dispatch into `CampContactService` alongside email, rate limiting, and audit logging.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~CampContactServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`; `dotnet build Humans.slnx --disable-build-servers -v q`.
- Commit: `971006b62 refactor(camps): notify leads from contact service`.
- Next: Continue camp controller debt, prioritizing registration/edit flows with controller validation and orchestration.

## Checkpoint - 2026-05-15 camp edit orchestration
- Done: Cleared `CampController.cs:Edit/2` by moving camp field updates, season updates, and name-lock-aware season renaming behind `CampService.UpdateCampAsync(CampUpdateInput)` with a result contract.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~CampServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`; `dotnet build Humans.slnx --disable-build-servers -v q`.
- Commit: `e4ef886d9 refactor(camps): move edit update orchestration into service`.
- Next: Continue remaining controller baseline entries; evaluate `CampController.cs:Register/1` only if it can be narrowed safely, otherwise move to the next high-value controller/service boundary item.

## Checkpoint - 2026-05-15 team join approval notification
- Done: Cleared `TeamAdminController.cs:ApproveRequest/3` by moving approved-request notification dispatch into `TeamService.ApproveJoinRequestAsync`.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~TeamServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`; `dotnet build Humans.slnx --disable-build-servers -v q`.
- Commit: `df1e0ccae refactor(teams): notify approved join requests from service`.
- Next: Continue with `TeamAdminController.cs:RejectRequest/3`, applying the same service-owned notification pattern if safe.

## Checkpoint - 2026-05-15 team join rejection notification
- Done: Cleared `TeamAdminController.cs:RejectRequest/3` by moving requester lookup and rejected-request notification dispatch into `TeamService.RejectJoinRequestAsync`.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~TeamServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`; `dotnet build Humans.slnx --disable-build-servers -v q`.
- Commit: `d82852e92 refactor(teams): notify rejected join requests from service`.
- Next: Continue remaining TeamAdmin controller baseline items, prioritizing membership/role mutation paths that can move decision logic into `TeamService`.

## Checkpoint - 2026-05-15 team management role toggle
- Done: Cleared `TeamAdminController.cs:ToggleManagement/2` by replacing the role-management setter with `ToggleRoleIsManagementAsync`, moving role lookup and state inversion into `TeamService` while keeping interface method count flat.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~TeamRoleServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`; `dotnet build Humans.slnx --disable-build-servers -v q`.
- Commit: `f26a3da93 refactor(teams): toggle management role in service`.
- Next: Continue remaining controller baseline entries; prefer mutation flows where service contracts can absorb validation/result decisions without adding method surface.

## Checkpoint - 2026-05-15 team join notifications
- Done: Cleared `TeamController.cs:Join/2` by moving coordinator notifications for join requests and direct joins into `TeamService`.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~TeamServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`; `dotnet build Humans.slnx --disable-build-servers -v q`.
- Commit: `c067f152d refactor(teams): notify join flows from service`.
- Next: Continue team controller membership baseline entries, prioritizing join GET/leave/withdraw service-result refactors where safe.

## Checkpoint - 2026-05-15 finance ticketing projection
- Done: Cleared `FinanceController.cs:UpdateTicketingProjection/11` by replacing the wide action parameter list with `TicketingProjectionUpdateForm` and moving save-plus-refresh orchestration into `TicketingBudgetService.UpdateProjectionAndRefreshAsync`.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~TicketingBudgetServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`; `dotnet build Humans.slnx --disable-build-servers -v q`.
- Commit: `5f87dd3ee refactor(finance): update ticketing projections through service`.
- Next: Continue finance/controller baseline items; `FinanceController.cs:EnsureTicketingGroup/1` is a likely small service-result follow-up.

## Checkpoint - 2026-05-15 finance ticketing group ensure
- Done: Cleared `FinanceController.cs:EnsureTicketingGroup/1` by returning `EnsureTicketingGroupResult` from `BudgetService` with the created/already-exists message.
- Validation: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~BudgetServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`; `dotnet build Humans.slnx --disable-build-servers -v q`.
- Commit: `9671aba72 refactor(finance): return ticketing group ensure result`.
- Next: Continue remaining controller baseline entries; prefer budget/shift/service-result refactors over display-only helpers.

## Checkpoint - 2026-05-15 - Shift creation controller debt

Done:
- Cleared `ShiftAdminController.cs:CreateShift/2` from `NoBusinessLogicInControllers.baseline.txt`.
- Replaced controller-owned shift entity construction and rota period validation with `IShiftManagementService.CreateShiftAsync(CreateShiftInput)`.
- Kept service surface count flat by replacing the existing create method contract rather than adding a method.
- Preserved dev dashboard seeding by returning the created shift id from `ShiftMutationResult`.
- Added shift service coverage for successful create and rota-period rejection.

Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ShiftManagementServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue with another high-value safe baseline item from `tests/Humans.Application.Tests/Architecture/Baselines`.

## Checkpoint - 2026-05-15 - Shift edit controller debt

Done:
- Cleared `ShiftAdminController.cs:EditShift/3` from `NoBusinessLogicInControllers.baseline.txt`.
- Replaced controller-owned shift loading, team ownership checks, rota period validation, and entity mutation with `IShiftManagementService.UpdateShiftAsync(UpdateShiftInput)`.
- Kept service method count flat by replacing the existing entity-taking update method.
- Added shift service coverage for successful edit and wrong-team rejection.

Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ShiftManagementServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue the shifts cluster while it remains the highest-value feasible baseline area.

## Checkpoint - 2026-05-15 - Shift generation controller debt

Done:
- Cleared `ShiftAdminController.cs:GenerateShifts/3` from `NoBusinessLogicInControllers.baseline.txt`.
- Replaced controller-owned rota lookup, team ownership check, generated-count calculation, and generation success messaging with `IShiftManagementService.GenerateEventShiftsAsync(GenerateEventShiftsInput)`.
- Added service validation for event-period bounds, empty slot lists, volunteer count ordering, and wrong-team rota access.
- Updated shift service tests to cover the result contract.

Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ShiftManagementServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue with another high-value safe baseline entry from the shift/admin cluster.

## Checkpoint - 2026-05-15 - Shift staffing controller debt

Done:
- Cleared `ShiftAdminController.cs:ConfigureStaffing/3` from `NoBusinessLogicInControllers.baseline.txt`.
- Replaced controller-owned rota ownership checks, staffing dictionary construction, and created-count messaging with `IShiftManagementService.CreateBuildStrikeShiftsAsync(ConfigureBuildStrikeStaffingInput)`.
- Preserved additive shift creation and moved duplicate-day collapse plus min/max validation into the service.
- Updated shift service tests for the result contract.

Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ShiftManagementServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue with remaining shift/admin baseline items.

## Checkpoint - 2026-05-15 - Shift rota move controller debt

Done:
- Cleared `ShiftAdminController.cs:MoveRota/3` from `NoBusinessLogicInControllers.baseline.txt`.
- Replaced controller-owned source rota lookup, target team lookup, and success-message composition with `IShiftManagementService.MoveRotaToTeamAsync(MoveRotaInput)`.
- Service now validates source team ownership and returns the target redirect slug with the operation result.
- Updated rota move audit coverage for the new result contract.

Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ShiftManagementServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue with remaining high-value shift/admin baseline items.

## Checkpoint - 2026-05-15 - Store index controller debt

Done:
- Cleared `StoreController.cs:Index/1` from `NoBusinessLogicInControllers.baseline.txt`.
- Moved store index page-data assembly into `IStoreService.GetIndexDataAsync`.
- Store service now owns active store year resolution, catalog sorting, camp-lead season order lookup, and no-camp-orders messaging state.
- Added `StoreIndexData`/`StoreCampSeasonOrders` DTOs and service coverage for empty lead sections.

Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~StoreServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue with another high-value feasible baseline entry.

## Checkpoint - 2026-05-15 - Store order controller debt

Done:
- Cleared `StoreController.cs:Order/2` from `NoBusinessLogicInControllers.baseline.txt`.
- Moved order page-data assembly into `IStoreService.GetOrderPageDataAsync`.
- Store service now owns edit-catalog loading, camp-name lookup, balance-based payment eligibility, and Stripe checkout configuration state.
- Removed now-unused Store controller shift/clock/stripe dependencies.
- Added Store service coverage for order page-data construction.

Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~StoreServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue with another high-value feasible baseline entry.

## Checkpoint - 2026-05-15 - Governance index controller debt

Done:
- Cleared `GovernanceController.cs:Index/0` from `NoBusinessLogicInControllers.baseline.txt`.
- Added `IGovernanceIndexService` and `GovernanceIndexService` to own governance landing page data assembly.
- Moved latest application selection, statutes lookup, tier counts, can-apply calculation, and approved-colaborador detection out of the controller.

Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue with another high-value feasible baseline entry.

## Checkpoint - 2026-05-15 - Ticket participation backfill controller debt

Done:
- Cleared `TicketController.cs:ParticipationBackfill/1` from `NoBusinessLogicInControllers.baseline.txt`.
- Added `IUserParticipationBackfillService` to own default-year resolution, CSV parsing, empty-input handling, and participation backfill delegation.
- Removed controller-side CSV parsing and the now-unused ticket controller clock dependency.

Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue with another high-value feasible baseline entry.

## Checkpoint - 2026-05-15 - Dev camp role seed controller debt

Done:
- Cleared `DevSeedController.cs:SeedCampRoles/1` from `NoBusinessLogicInControllers.baseline.txt`.
- Added `DevelopmentCampRoleSeeder` to own seed definitions, duplicate-name detection, create/skip counts, and success message formatting.
- Registered the seeder in web DI and reduced the controller action to environment/current-user checks plus seeder invocation.

Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue with another feasible baseline entry.

## Checkpoint - 2026-05-15 - Dev budget seed controller debt

Done:
- Cleared `DevSeedController.cs:SeedBudget/1` from `NoBusinessLogicInControllers.baseline.txt`.
- Moved budget seed success-message formatting onto `DevelopmentBudgetSeedResult`, keeping seeding result semantics with the seeder.
- Reduced the controller action to dev/current-user checks, seeder invocation, and flash dispatch.

Validation:
- `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`

Next:
- Continue with another feasible baseline entry.

## 2026-05-15 checkpoint - camp role update result
Done:
- Cleared `src/Humans.Web/Controllers/CampAdminController.cs:EditRole/3` from `NoBusinessLogicInControllers.baseline.txt` by moving role update outcome and success-message construction into `ICampRoleService.UpdateDefinitionAsync`.
Validation:
- `dotnet test tests\Humans.Application.Tests\Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~CampRoleServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None for this item.
Next:
- Continue with the highest-value safe remaining controller baseline item.

## 2026-05-15 checkpoint - team role management preservation
Done:
- Cleared `src/Humans.Web/Controllers/TeamAdminController.cs:EditRole/3` from `NoBusinessLogicInControllers.baseline.txt` by moving management-flag preservation into `ITeamService.UpdateRoleDefinitionAsync`.
Validation:
- `dotnet test tests\Humans.Application.Tests\Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~TeamRoleServiceTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None for this item.
Next:
- Continue with the highest-value safe remaining controller baseline item.

## 2026-05-15 checkpoint - shift mine bucketing
Done:
- Cleared `src/Humans.Web/Controllers/ShiftsController.cs:Mine/0` from `NoBusinessLogicInControllers.baseline.txt` by extracting shared signup bucketing used by both the Mine page and the shift signups view component.
Validation:
- `dotnet test tests\Humans.Application.Tests\Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~ShiftSignupsBucketingTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None for this item.
Next:
- Continue with the highest-value safe remaining controller baseline item.

## 2026-05-15 checkpoint - shift settings form mapping
Done:
- Cleared `src/Humans.Web/Controllers/ShiftsController.cs:Settings/1` from `NoBusinessLogicInControllers.baseline.txt` by extracting event-settings form parsing and entity mapping into `EventSettingsFormMapper`.
Validation:
- `dotnet test tests\Humans.Application.Tests\Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~EventSettingsFormMapperTests|FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None for this item.
Next:
- Continue with the highest-value safe remaining controller baseline item.

## 2026-05-15 checkpoint - dashboard volunteer search outcomes
Done:
- Cleared `src/Humans.Web/Controllers/ShiftDashboardController.cs:SearchVolunteers/2` from `NoBusinessLogicInControllers.baseline.txt` by centralizing empty-query, not-found, and success outcomes in `ShiftVolunteerSearchBuilder`.
Validation:
- `dotnet test tests\Humans.Application.Tests\Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None for this item.
Next:
- Continue with the highest-value safe remaining controller baseline item.

## 2026-05-15 checkpoint - admin volunteer search outcomes
Done:
- Cleared `src/Humans.Web/Controllers/ShiftAdminController.cs:SearchVolunteers/3` from `NoBusinessLogicInControllers.baseline.txt` by reusing the shared volunteer-search outcome builder after the department ownership check.
Validation:
- `dotnet test tests\Humans.Application.Tests\Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None for this item.
Next:
- Continue with the highest-value safe remaining controller baseline item.

## 2026-05-15 checkpoint - shift browse page builder
Done:
- Cleared `src/Humans.Web/Controllers/ShiftsController.cs:Index/8` from `NoBusinessLogicInControllers.baseline.txt` by moving browse filter parsing, period/tag filtering, department grouping, and model assembly into `ShiftBrowsePageBuilder`.
Validation:
- `dotnet test tests\Humans.Application.Tests\Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None for this item.
Next:
- Continue with the highest-value safe remaining controller baseline item.

## 2026-05-15 checkpoint - shift admin page builder
Done:
- Cleared `src/Humans.Web/Controllers/ShiftAdminController.cs:Index/2` from `NoBusinessLogicInControllers.baseline.txt` by moving admin page composition into `ShiftAdminPageBuilder`.
Validation:
- `dotnet test tests\Humans.Application.Tests\Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None for this item.
Next:
- Continue with the highest-value safe remaining controller baseline item.

## 2026-05-15 checkpoint - shift dashboard page builder
Done:
- Cleared `src/Humans.Web/Controllers/ShiftDashboardController.cs:Index/7` from `NoBusinessLogicInControllers.baseline.txt` by moving dashboard query/model assembly and countdown calculation into `ShiftDashboardPageBuilder`.
Validation:
- `dotnet test tests\Humans.Application.Tests\Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None for this item.
Next:
- Continue with the highest-value safe remaining controller baseline item.

## 2026-05-15 checkpoint - shift signup redirect handling
Done:
- Cleared `src/Humans.Web/Controllers/ShiftsController.cs:SignUpRange/10` from `NoBusinessLogicInControllers.baseline.txt` by consolidating single-shift and range signup result-to-redirect handling.
Validation:
- `dotnet test tests\Humans.Application.Tests\Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None for this item.
Next:
- Continue with the highest-value safe remaining controller baseline item.

## 2026-05-15 checkpoint - ticket dashboard page builder
Done:
- Cleared `src/Humans.Web/Controllers/TicketController.cs:Index/0` from `NoBusinessLogicInControllers.baseline.txt` by moving dashboard stats, vendor capacity fallback, finance break-even mapping, and user/ticket set membership into `TicketDashboardPageBuilder`.
Validation:
- `dotnet test tests\Humans.Application.Tests\Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None for this item.
Next:
- Continue with the highest-value safe remaining controller baseline item.

## 2026-05-15 checkpoint - camp csv export builder
Done:
- Cleared `src/Humans.Web/Controllers/CampAdminController.cs:ExportCamps/0` from `NoBusinessLogicInControllers.baseline.txt` by moving lead resolution and CSV construction into `CampCsvExportBuilder`.
Validation:
- `dotnet test tests\Humans.Application.Tests\Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None for this item.
Next:
- Continue with the highest-value safe remaining controller baseline item.

## 2026-05-15 checkpoint - camp admin page builder
Done:
- Cleared `src/Humans.Web/Controllers/CampAdminController.cs:Index/0` from `NoBusinessLogicInControllers.baseline.txt` by moving admin landing-page model assembly into `CampAdminPageBuilder`.
Validation:
- `dotnet test tests\Humans.Application.Tests\Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None for this item.
Next:
- Continue with the highest-value safe remaining controller baseline item.

## 2026-05-15 checkpoint - dev login persona lookup
Done:
- Cleared `src/Humans.Web/Controllers/DevLoginController.cs:SignIn/2` from `NoBusinessLogicInControllers.baseline.txt` by extracting seeded persona provisioning and fallback lookup out of the action.
Validation:
- `dotnet test tests\Humans.Application.Tests\Humans.Application.Tests.csproj --disable-build-servers -v q --filter "FullyQualifiedName~NoBusinessLogicInControllers"`
- `dotnet build Humans.slnx --disable-build-servers -v q`
Blocked:
- None for this item.
Next:
- Continue with the highest-value safe remaining controller baseline item.
