# section-doctor — MailerLite — 2026-10-01

- Invocation: unattended daily cloud routine, no arguments (Phase 8 skipped per routine prompt)
- Anchor commit: `d8e6c2209` (origin/main at branch point); branch `section-doctor/2026-10-01T011558Z`.
- Budget: 2.5h.
- PR: pending

## Assessment summary

MailerLite came up on age-plus-churn: its last run merged on 2026-08-25 and the section has
moved since. The shape this run derived matches the code closely. The section is a leaf with one
remote port, an inbound plan/apply pair, an outbound compute/diff/apply loop and one table.

The gaps fell into these groups:

- **Leftover public surface.** A public contract and a public job existed only so the section's
  own job could call its own service. Every other section's jobs are internal.
- **View models that copied service records.** Both copies have been removed.
- **Prose behind the code.** Several comments and docs described moves and states that no
  longer exist, and the invariant doc cited an architecture test that does not exist.

The one behavioural finding is a disagreement between the debug preview and the real push on
suppressed subscribers. The spec and the preview's own pinned test define the preview's
behaviour, so this run changed neither side, and the question goes to Peter.

Independence check: pass. Findings 1, 2, 3, 4 and 13 come from reading the section against the
target, not from any tool. Finding 1 is a difference between what the spec says and what the
code does, which no scanner reports.

## Findings

1. **The debug preview and Sync disagree on suppressed subscribers.** "Suppressed" means
   MailerLite reports the subscriber as unsubscribed, bounced or spam-flagged.
   - Sync unassigns a suppressed group member, because that member is not in its keep set. The
     preview's "currently in ML" table hides suppressed subscribers, so "to remove" never shows
     them.
   - A suppressed expected user shows as "to add", but Sync never adds them.
   - The feature spec defines the preview's exclusion, and
     `MailerLiteAudienceDebugSnapshotBuilderTests.Build_CurrentlyInMl_SkipsSuppressedStatuses`
     pins it.
2. **`Contracts/IMailerLiteAudienceSync` was public cross-section surface with no
   cross-section consumer.** It existed so the section's public job could call its own sync
   service. The job's remarks justified it being public with a claim that the Shell names the
   type, which is false: jobs are contributed through `SectionJobs`.
3. **`Models/AudienceCardRow` copied `AudienceStats` field for field.** The dashboard card
   renders neither `DisplayName` nor `LastSyncSummary`.
4. **The import preview re-projected the service's own records.**
   `MailerLiteImportPreviewViewModel`, `SubscriberDecisionRow` and the controller's `ProjectRows`
   re-projected `SubscriberDecision` into a renamed subset. The view then looked each ambiguous
   row up again by email to recover the field the projection had dropped.
5. **Phantom pin.** `MailerLite.md` and `Views/_ViewImports.cshtml` cited an architecture test,
   `SectionTypesTakeNoStringLocalizer`, that does not exist.
6. **Unpinned invariants.** No test pinned either of these:
   - the import's reserved sync-state key is never an audience key;
   - a duplicate sync-state row resolves to the newest.
7. **`MailerLite.md` drift.**
   - `UserService` was listed as a writer.
   - The doc carried "before" narration and a phantom Actors row.
   - The converter was plural, but there is one.
   - The architecture-test list was incomplete.
   - The delete-guard exemption was worded loosely.
8. **`data-access.md` drift.**
   - The audiences folder was missing.
   - The client's read/write split omitted `DeleteSubscriberAsync`.
   - The erasure scope was stated as primary only.
9. **`features/audience-debug-screen.md` drift.** It named `IUserService`; the builder reads
   `IUserServiceRead`.
10. **Off-section: `docs/features/global/background-jobs.md` drift.** It said the job is
    registered only when the cron is set. The job is always contributed, and it is scheduled only
    when the cron is set.
11. **Comment drift across the section.**
    - Configuration binding was attributed to `Program.cs`.
    - An orphaned FirstName/LastName comment sat in the client.
    - The sync service said it "lives in the Application layer".
    - Timeout comments read "nothing cancels the sync any more".
    - The import interface named speculative webhook callers.
    - An issue reference was bare.
    - The Debug view mentioned the "existing" Sync action.
    - The `DriftReport` null comment was wrong.
    - "Used to" narration appeared in the entity, the import interface and `Section.cs`.
    - `IMailerLiteAudience.DisplayName` claimed to be shown on the dashboard card.
    - The `IMailerLiteService` summary omitted the GDPR delete.
    - The repository doc carried a derived row count.
12. **Redundant test assertions.**
    - Several per-audience tests re-assert the `"Humans - "` prefix that the architecture test
      already pins for every audience.
    - `EmptyRaw_DoesNotEnumerateUsers` asserts an optimisation, not an outcome.
13. **The controller holds business logic.** `MailerLiteAdminController` computes the drift
    report and the >10% plan-drift check on Commit. Moving either into the import service needs
    a new interface method.
14. **Advisory (conformance).** `MailerLiteEmailNormalization.cs` sits at the project root rather
    than under a folder.
15. **Inbox recommendations.** These are read-only; this run changed no issue.
    - nobodies-collective/Humans#524 — edit: its key-file paths name projects that no longer
      exist, and `ContactSource` now lives in `Humans.Users.Contracts`.
    - nobodies-collective/Humans#1197 — relabel to `section:infra`; it is not MailerLite code.
    - nobodies-collective/Humans#204 — keep.
    - nobodies-collective/Humans#1041 — keep.
16. **Carry-forward.** Finding 3 of the 2026-08-25 run still holds: the dashboard's drift row
    is permanently blank. It is unanswered in that run's Needs Peter and is not re-asked here.
17. **Carry-forward.** "Push Now" on the dashboard still posts without a confirm. This is a
    finding only.

## Debt verified

The section ledger was empty at the branch point, so there were no rows to verify.

Filed: MAILERLITE-2, for finding 12.

## Worked

- **Findings 7, 8, 9 and 10:** the section's prose docs and the global jobs doc now say what the
  code does. The phantom pin from finding 5 was cut in the same pass.
- **Finding 6:** tests added.
  - `AllAudiences_HaveUniqueGroupNamesAndKeys` now asserts that no audience takes the reserved
    reconciliation key.
  - `ComputeAllStatsAsync_DuplicateKeyRows_ShowsTheNewest` pins the newest-row read; a `MinBy`
    mutation fails it.
- **Finding 2:** the job is internal and calls `IMailerLiteAudienceSyncService.SyncAllAsync`
  directly. The contract, its adapter and its DI forward are deleted. The section now has no
  cross-section surface.
  - Reviewer: `doctor-reviewer`, APPROVE.
  - It checked behaviour equivalence (no actor, same count, same token), the HUM0034 and
    architecture-test fit, that Hangfire can activate an internal job (the same path as the
    internal jobs in Governance, Workgroups, Users and Consent), DI lifetime, and that no
    reference to the deleted names remains.
- **Findings 3 and 4:** the dashboard card renders `AudienceStats`, and the import preview
  renders `ImportPlan` directly. The ambiguous column now reads the row it draws, so a duplicate
  email can no longer show another row's IDs.
  - Reviewer: `doctor-reviewer`, APPROVE.
  - It checked render equivalence field by field, the timing of the stats-failure catch, the
    empty paths, and the leftover-reference sweep.
- **Finding 11:** each comment was corrected or cut.
- No live render happened: this was an unattended cloud run. The views compile at build and
  pass `razor-lint`. The preview deploy is where the dashboard and import pages are checked.

## Skipped

- **Finding 1** goes to Needs Peter. The spec and the code disagree, and the code may be the
  side that is wrong, so this run changed neither.
- **Finding 3, partly.** `AudienceStats.DisplayName` and `LastSyncSummary` stay, even though no
  view renders them. `LastSyncSummary` is the only reader of the persisted `Summary` column, and
  a sync test pins it. Dropping it turns a column write-only, which is a schema question.
- **Finding 12** is a deletion, so it is reviewer-gated, and it is the least valuable item on
  the ranked list. Ledgered as MAILERLITE-2.
- **Finding 13** goes to Needs Peter, because moving the logic adds public surface.
- **Finding 14** is advisory only.
- **Findings 15, 16 and 17** are recommendations and carry-forwards.
- **Not pursued: a possible GDPR gap.** An unverified identity email can reach MailerLite through
  the notification-target fallback, and erasure deletes only verified addresses and the primary.
  Whether such an address is ever pushed was not proven, so it is not a finding.
- **Not pursued:** the Push All form renders a duplicate antiforgery token. This is cosmetic.
- No section was passed over as blocked; the selector's pool had none.

## Retro

**What the selector got wrong: nothing.** MailerLite's churn since its last run was real, and
most of it came from the section's own moves. Moves are where stale narration collects, and
narration was the largest class of finding here.

**Wasted motion.** The reading threads were dispatched before the target was rewritten, so they
read against the previous run's target. It cost little, because the shape had barely moved, but
the skill's order exists for a reason, and on a section whose shape had moved the threads would
have judged against the wrong target. The environment added its own costs:

- The first reforge surface run went out before the restore and build, and it refused to print.
- `gh`'s GraphQL endpoint is blocked in this container, so PR and issue listings went through
  repo-scoped REST.

Neither meets the bar for filing against the skill.

**What striking revealed that the assessment missed.** It revealed the reason the public
contract survived. Its doc comment carried a careful argument for being narrower than the
service interface, and that argument is correct about everything except why the contract
existed at all. The job's remarks supplied the premise, that the Shell names the type, and the
premise had gone stale. A well-reasoned comment over a false premise outlives a sloppy one,
because nobody re-checks a comment that reads as considered. The ambiguous-column re-lookup was
similar: the projection dropped a field, and the view paid for it with a search.

**What the target diff says.** The structure barely moved since 2026-08-25. What changed is that
the target now states "no cross-section surface" and "a view takes the service's own record"
outright, and both claims became true in this run. A target that stops moving while its claims
start holding is a section near its end state. What remains is the question in finding 1 and
the controller logic in finding 13, and both need Peter, not a strike.

## Needs Peter

- [ ] 1 — debug preview vs Sync on suppressed subscribers: make the preview match Sync, or stop Sync unassigning suppressed members?
- [ ] 13 — drift report and the >10% Commit check: move into the import service (a new interface method), or leave in the controller?

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.MailerLite/Controllers/MailerLiteAdminController.cs` | changed |
| `src/Sections/Humans.MailerLite/Data/Configurations/MailerLiteSyncStateConfiguration.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Data/IMailerLiteRepository.cs` | changed |
| `src/Sections/Humans.MailerLite/Data/MailerLiteDbContext.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Data/MailerLiteDbContextFactory.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Data/Migrations/20260820232107_InitialMailerLiteSection.Designer.cs` | generated |
| `src/Sections/Humans.MailerLite/Data/Migrations/20260820232107_InitialMailerLiteSection.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Data/Migrations/MailerLiteDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.MailerLite/Data/Repository.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Docs/MailerLite.md` | changed |
| `src/Sections/Humans.MailerLite/Docs/authorization.md` | reviewed |
| `src/Sections/Humans.MailerLite/Docs/data-access.md` | changed |
| `src/Sections/Humans.MailerLite/Docs/debt.yml` | changed |
| `src/Sections/Humans.MailerLite/Docs/features/audience-debug-screen.md` | changed |
| `src/Sections/Humans.MailerLite/Docs/health.md` | changed |
| `src/Sections/Humans.MailerLite/Domain/MailerLiteSyncState.cs` | changed |
| `src/Sections/Humans.MailerLite/Humans.MailerLite.csproj` | reviewed |
| `src/Sections/Humans.MailerLite/Jobs/MailerLiteAudienceSyncJob.cs` | changed |
| `src/Sections/Humans.MailerLite/MailerLiteEmailNormalization.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Models/MailerLiteAudienceDebugSnapshotBuilder.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Models/MailerLiteAudienceDebugViewModel.cs` | changed |
| `src/Sections/Humans.MailerLite/Models/MailerLiteDashboardViewModel.cs` | changed |
| `src/Sections/Humans.MailerLite/Properties/AssemblyInfo.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Section.cs` | changed |
| `src/Sections/Humans.MailerLite/SectionAdminNav.cs` | reviewed |
| `src/Sections/Humans.MailerLite/SectionConfiguration.cs` | reviewed |
| `src/Sections/Humans.MailerLite/SectionJobs.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Audiences/CurrentEventTicketHolders.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Audiences/HasShiftAudience.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Audiences/HasShiftEventAudience.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Audiences/HasShiftSetupAudience.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Audiences/HasShiftStrikeAudience.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Audiences/HasTicketAudience.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Audiences/MailerLiteAudienceBase.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Audiences/MarketingAudience.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Audiences/MarketingNoTicketAudience.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Audiences/ShiftViewAudienceBase.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Audiences/TicketNoShiftsAudience.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Dtos/AudienceStats.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Dtos/AudienceSyncResult.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Dtos/BulkImportResult.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Dtos/ImportPlan.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Dtos/ImportResult.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Dtos/MailerLiteAccountSummary.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Dtos/MailerLiteGroup.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Dtos/MailerLiteSubscriber.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Dtos/MailerLiteSyncSnapshot.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/Dtos/SubscriberDecision.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/IMailerLiteAudience.cs` | changed |
| `src/Sections/Humans.MailerLite/Services/IMailerLiteAudienceSyncService.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/IMailerLiteImportService.cs` | changed |
| `src/Sections/Humans.MailerLite/Services/IMailerLiteService.cs` | changed |
| `src/Sections/Humans.MailerLite/Services/MailerLite/MailerLiteClient.cs` | changed |
| `src/Sections/Humans.MailerLite/Services/MailerLite/MailerLiteOptions.cs` | changed |
| `src/Sections/Humans.MailerLite/Services/MailerLite/MailerLiteSubscriberConverter.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/MailerLiteAudienceSyncService.cs` | changed |
| `src/Sections/Humans.MailerLite/Services/MailerLiteGdprContributor.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Services/MailerLiteImportService.cs` | reviewed |
| `src/Sections/Humans.MailerLite/Views/MailerLite/Admin/Debug.cshtml` | changed |
| `src/Sections/Humans.MailerLite/Views/MailerLite/Admin/Import.cshtml` | changed |
| `src/Sections/Humans.MailerLite/Views/MailerLite/Admin/Index.cshtml` | reviewed |
| `src/Sections/Humans.MailerLite/Views/MailerLite/Admin/_AudiencesCard.cshtml` | changed |
| `src/Sections/Humans.MailerLite/Views/MailerLite/Admin/_DebugPager.cshtml` | reviewed |
| `src/Sections/Humans.MailerLite/Views/MailerLite/Admin/_DebugSortHeader.cshtml` | reviewed |
| `src/Sections/Humans.MailerLite/Views/_ViewImports.cshtml` | changed |
| `tests/Humans.MailerLite.Tests/Architecture/MailerLiteArchitectureTests.cs` | changed |
| `tests/Humans.MailerLite.Tests/Audiences/HasShiftAudienceTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Audiences/HasTicketAudienceTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Audiences/MailerLiteAudienceBaseTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Audiences/MarketingAudienceTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Audiences/MarketingNoTicketAudienceTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Audiences/ShiftViewAudienceTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Audiences/TicketNoShiftsAudienceTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Controllers/MailerLiteAdminControllerAudienceSyncTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Controllers/MailerLiteAdminControllerTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Data/MailerLiteRepositoryTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Humans.MailerLite.Tests.csproj` | reviewed |
| `tests/Humans.MailerLite.Tests/Infrastructure/InMemoryMailerLiteRepository.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Infrastructure/UserInfoStubHelpers.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Models/MailerLiteAudienceDebugSnapshotBuilderTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Services/ImportResultTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteAudienceSyncServiceTests.cs` | changed |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteClientCacheTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteClientDeleteSubscriberTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteClientRetryTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteClientWriteGuardTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteGdprContributorTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteImportServiceClassifierTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteImportServiceConflictRuleTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteImportServiceIdempotencyTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteImportServiceThrottleTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteImportServiceWebsiteScopeTests.cs` | reviewed |
| `tests/Humans.MailerLite.Tests/Services/MailerLiteSubscriberConverterTests.cs` | reviewed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | — | 4 |
| Behavior & bugs | main | — | 1 |
| Freshness | subagent (`doctor-reader`) | opus-low | 5 |
| Conformance | subagent (`general-purpose`) | haiku | 1, advisory only |
| Tests | subagent (`doctor-reader`) | opus-low | 3 |
| Prose & surface | subagent (`general-purpose`) | haiku | 1 |
| History | subagent (`doctor-reader`) | opus-low | 4 |
| Comments | subagent (`doctor-reader`) | opus-low | 2, each a batch |
| Inbox | subagent (`doctor-reader`) | opus-low | 1, issue recommendations only |
