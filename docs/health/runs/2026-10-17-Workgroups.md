# section-doctor — Workgroups — 2026-10-17

- Invocation: unattended daily routine, no arguments; Phase 8 skipped per routine prompt
- Anchor commit: `a8ab49d83` (origin/main at branch point); branch `section-doctor/2026-10-17T011628Z`.
- Budget: 2.5h.
- PR: peterdrier/Humans#1995

## Assessment summary

The section moved a long way since the last run: join requests with coordinator approval,
sensitive groups, budgets through Holded, a Drive Content Manager level and a section-owned
settings table all shipped, and every rule failure became a returned refusal instead of a throw.
The previous target was true when written; the section outgrew it, and the regenerated target in
`src/Sections/Humans.Workgroups/Docs/health.md` records the new shape. The code was sound — no
bug found — but its prose lagged: dependency lists, comments and outside-section docs still
described the September section. Structural repeats (the refusal-to-flash map and the
load-and-check-Active preamble) and a duplicated by-slug read collapsed. Session note: no live
render happened; the preview deploy is where a page is checked.

Independence check: pass — the refusal-to-flash map, the guard preamble and the sensitive-comment question came from the target's structure and invariants; the join and refusal drift came from the spec-versus-code delta.

Run note: the branch is `section-doctor/2026-10-17T011628Z`, not the routine's
claude/dreamy-gates-ddas51, because `doctor.py` requires the section-doctor branch pattern.

## Findings

1. Dependency lists in `Docs/Workgroups.md` and `Docs/data-access.md` named `ITeamServiceRead` on the service (only the member controller and the Drive source use it), omitted `IHoldedFinanceService`, `IHoldedClient`, the Auth read and `IUserEmailService`, missed `UpdatedByUserId` among the bare columns, and said only Withdrawn folders stay claimed.
2. Comments disagreed with the code: joining "is immediate", registration "throws", "six tables", a dangling `debt.yml` cite in the test fixtures, a false "only place that filters" claim on `UpcomingMeetings`, the `DraftKey` summary describing `ActAsync`, plus dated attributions and restating lines.
3. The resource keys `Workgroups_Field_Coordinator1` and `Workgroups_Field_Coordinator2`, had no reference in any culture.
4. The member controller mapped a refusal to a flash message identically in `ActAsync` and `ManageMembersAsync`.
5. Group-loading member writes each repeated load, unwrap and the Active check.
6. Log-entry kind and body validation was written out in both add and edit.
7. The design record's banner did not mention anything shipped after it.
8. Outside-section docs (`docs/architecture/service-data-access-map.md`, `docs/architecture/design-rules.md`, `docs/authorization-inventory.md`, `docs/features/global/gdpr-export.md`, `src/Sections/Humans.AuditLog/Docs/AuditLog.md`) missed the settings table, the legacy root fallback, coordinator-only membership decisions, pending export rows and the budget audit action.
9. Target invariants with no test: system log entries are immutable, hiding audits its reason, reserved slugs are skipped, reactivation clears reasons, an early close keeps comments.
10. The inner `GetBySlugAsync` queried the repository while the decorator filtered the register; the repository's by-slug query had no other caller.
11. The freshness triggers did not cover the cross-section contracts the section docs assert about, and `Docs/authorization.md` and `Docs/data-access.md` had none.
12. A sensitive group's documents are readable by the Board but `AddCommentAsync` refuses the Board's comments, because the sensitive gate admits only members.
13. peterdrier/Humans#1937 cites a stale line for `CreateGroupFolderAsync` (now in `Services/WorkgroupService.Lifecycle.cs`), and nothing checks the root folder is reachable when it is saved.
14. The previous target was stale throughout: open joining, throwing guards, a coordinator seam that is now a rule, and the shapes missing the membership, budget and edit routes.

## Debt verified

The ledger `src/Sections/Humans.Workgroups/Docs/debt.yml` holds no rows; nothing to verify. Its
"emptied" comment was cut, and the decision it recorded (no grant revocation when the root folder
changes, peterdrier/Humans#1650) moved to the target's deliberately-not-done list.

## Worked

- 14 — target regenerated.
- 2 — comment strikes (sonnet executor; no additions beyond the list).
- 3 — dead resx keys deleted in every culture with XML-checked edits.
- 1, 7, 8, 11 — doc drift (sonnet executor). Kept additions beyond the list: the authorization inventory lives at `docs/authorization-inventory.md`, not under `docs/architecture/`, and gained a row for coordinator-only membership decisions; the Teams bullet in `Docs/Workgroups.md` now names who uses `ITeamServiceRead`.
- 4 — one `Flash` helper in the member controller.
- 5 — `RequireActiveAsync` (reviewer: approve with correction — the comment-response and comment-hide sites added; it checked behaviour at every converted site, the ordering of refusals, the sites deliberately left alone, and test coverage).
- 10 — slug lookup over the register, repository query deleted (reviewer: approve; it checked slug case semantics, production reachability through the decorator, and that no reference remains).
- 9 — tests added under `tests/Humans.Workgroups.Tests`.

## Skipped

- 6 — a shared log-entry validator loses the compiler's null-state flow on the body at both call sites; the wrapper or null-forgiving operator it needs costs more than the duplicated lines.
- The document methods still check Active by hand: their loader is shared with the Board's disposition, which must not be gated.
- The view-component tests that only assert a substitute's exception propagates: low value, left alone.
- 12, 13 — judgment or issue edits; queued under Needs Peter.
- No section was passed over as blocked.

## Retro

**Selector.** Workgroups was a good pick: heavy feature churn since its last run had left its prose behind its code, which is exactly the decay this skill exists to catch.

**Wasted motion.** The reading threads were dispatched before the target was regenerated, so they read against the stale September target; the new target was written afterwards from main's own reading. The order cost nothing here because the threads' lenses did not depend on the target's claims, but it inverts the skill's sequence. reforge `surface-score` refused to run on an unbuilt solution and needed a full build first; a failed chain also committed a red build once, undone before any push.

**What striking revealed.** The guard collapse surfaced that the document loader cannot carry the Active check, because the Board's disposition shares it — a structural fact the assessment had flattened into "one guard written out many times".

**Target diff.** The section moved: membership approval, sensitivity and budgets are new shapes, and the coordinator went from a reserved seam to an enforced rule. The earlier target was right for its day.

## Needs Peter

- [ ] 12 — may the Board comment on a sensitive group's documents?
- [ ] 13 — apply the proposed edit to peterdrier/Humans#1937 (fresh line cite, reachability check on save as acceptance)?

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.Workgroups/Authorization/WorkgroupAuthorizationHandler.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Authorization/WorkgroupOperationRequirement.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Controllers/WorkgroupsAdminController.cs` | changed |
| `src/Sections/Humans.Workgroups/Controllers/WorkgroupsController.cs` | changed |
| `src/Sections/Humans.Workgroups/Data/Configurations/WorkgroupConfiguration.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Configurations/WorkgroupDocumentCommentConfiguration.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Configurations/WorkgroupDocumentConfiguration.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Configurations/WorkgroupLogEntryConfiguration.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Configurations/WorkgroupMeetingConfiguration.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Configurations/WorkgroupMemberConfiguration.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Configurations/WorkgroupsJson.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Configurations/WorkgroupsSettingsConfiguration.cs` | changed |
| `src/Sections/Humans.Workgroups/Data/IWorkgroupRepository.cs` | changed |
| `src/Sections/Humans.Workgroups/Data/Migrations/20260911012653_AddWorkgroups.Designer.cs` | generated |
| `src/Sections/Humans.Workgroups/Data/Migrations/20260911012653_AddWorkgroups.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Migrations/20260916163259_DropWorkgroupDormantSince.Designer.cs` | generated |
| `src/Sections/Humans.Workgroups/Data/Migrations/20260916163259_DropWorkgroupDormantSince.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Migrations/20260925175158_WorkgroupBudget.Designer.cs` | generated |
| `src/Sections/Humans.Workgroups/Data/Migrations/20260925175158_WorkgroupBudget.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Migrations/20261007055740_OwnWorkgroupSettings.Designer.cs` | generated |
| `src/Sections/Humans.Workgroups/Data/Migrations/20261007055740_OwnWorkgroupSettings.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Migrations/20261012111250_WorkgroupMembershipApprovalAndSensitive.Designer.cs` | generated |
| `src/Sections/Humans.Workgroups/Data/Migrations/20261012111250_WorkgroupMembershipApprovalAndSensitive.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Data/Migrations/WorkgroupsDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.Workgroups/Data/WorkgroupRepository.cs` | changed |
| `src/Sections/Humans.Workgroups/Data/WorkgroupsDbContext.cs` | changed |
| `src/Sections/Humans.Workgroups/Data/WorkgroupsDbContextFactory.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Docs/2026-09-10-workgroups-section-design.md` | changed |
| `src/Sections/Humans.Workgroups/Docs/Workgroups.md` | changed |
| `src/Sections/Humans.Workgroups/Docs/authorization.md` | changed |
| `src/Sections/Humans.Workgroups/Docs/data-access.md` | changed |
| `src/Sections/Humans.Workgroups/Docs/debt.yml` | changed |
| `src/Sections/Humans.Workgroups/Docs/health.md` | changed |
| `src/Sections/Humans.Workgroups/Domain/Enums.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Domain/Workgroup.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Domain/WorkgroupDocument.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Domain/WorkgroupDocumentComment.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Domain/WorkgroupLogEntry.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Domain/WorkgroupMeeting.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Domain/WorkgroupMember.cs` | changed |
| `src/Sections/Humans.Workgroups/Domain/WorkgroupsSettings.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Humans.Workgroups.csproj` | reviewed |
| `src/Sections/Humans.Workgroups/Jobs/WorkgroupRhythmJob.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Models/WorkgroupViewModels.cs` | changed |
| `src/Sections/Humans.Workgroups/Properties/AssemblyInfo.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Section.cs` | reviewed |
| `src/Sections/Humans.Workgroups/SectionAdminTiles.cs` | reviewed |
| `src/Sections/Humans.Workgroups/SectionChrome.cs` | reviewed |
| `src/Sections/Humans.Workgroups/SectionJobs.cs` | changed |
| `src/Sections/Humans.Workgroups/SectionMemberDashboard.cs` | reviewed |
| `src/Sections/Humans.Workgroups/SectionThingsToDo.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/AuditEntityTypes.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/CachingWorkgroupService.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/Contributors/WorkgroupCalendarContributor.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/Contributors/WorkgroupDriveAccessSource.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/Dtos/WorkgroupDtos.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/IWorkgroupService.cs` | changed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupErrorKeys.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupNoticeRequest.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupRhythm.cs` | changed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupService.Documents.cs` | changed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupService.Gdpr.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupService.Helpers.cs` | changed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupService.Lifecycle.cs` | changed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupService.Rhythm.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupService.cs` | changed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupsEmailPreviews.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Services/WorkgroupsEmails.cs` | reviewed |
| `src/Sections/Humans.Workgroups/ViewComponents/GovernanceWorkgroupsViewComponent.cs` | reviewed |
| `src/Sections/Humans.Workgroups/ViewComponents/MyWorkgroupsViewComponent.cs` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Shared/Components/GovernanceWorkgroups/Default.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Shared/Components/MyWorkgroups/Default.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Workgroups/Apply.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Workgroups/Details.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Workgroups/Document.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Workgroups/DocumentForm.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Workgroups/Edit.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Workgroups/Index.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Workgroups/LogEntryForm.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/Workgroups/MeetingForm.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/WorkgroupsAdmin/Index.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/WorkgroupsAdmin/RegisterExisting.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/WorkgroupsAdmin/Settings.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/Views/_ViewImports.cshtml` | reviewed |
| `src/Sections/Humans.Workgroups/WorkgroupsResource.ca.resx` | changed |
| `src/Sections/Humans.Workgroups/WorkgroupsResource.cs` | reviewed |
| `src/Sections/Humans.Workgroups/WorkgroupsResource.de.resx` | changed |
| `src/Sections/Humans.Workgroups/WorkgroupsResource.es.resx` | changed |
| `src/Sections/Humans.Workgroups/WorkgroupsResource.fr.resx` | changed |
| `src/Sections/Humans.Workgroups/WorkgroupsResource.it.resx` | changed |
| `src/Sections/Humans.Workgroups/WorkgroupsResource.resx` | changed |
| `tests/Humans.Workgroups.Tests/Authorization/WorkgroupAuthorizationHandlerTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Controllers/SectionAdminNavTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Controllers/WorkgroupsAdminControllerTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Controllers/WorkgroupsControllerAuthorizationTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Humans.Workgroups.Tests.csproj` | reviewed |
| `tests/Humans.Workgroups.Tests/Infrastructure/AssemblyFixtures.cs` | changed |
| `tests/Humans.Workgroups.Tests/Infrastructure/TestWorkgroupsEmails.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Infrastructure/WorkgroupsModelWarmupFixture.cs` | changed |
| `tests/Humans.Workgroups.Tests/Infrastructure/WorkgroupsTestHarness.cs` | changed |
| `tests/Humans.Workgroups.Tests/Services/CachingWorkgroupServiceTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Services/Contributors/WorkgroupCalendarContributorTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Services/Contributors/WorkgroupDriveAccessSourceTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Services/WorkgroupInputValidationTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Services/WorkgroupServiceBudgetTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Services/WorkgroupServiceDocumentTests.cs` | changed |
| `tests/Humans.Workgroups.Tests/Services/WorkgroupServiceGdprTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Services/WorkgroupServiceLifecycleTests.cs` | changed |
| `tests/Humans.Workgroups.Tests/Services/WorkgroupServiceMembershipTests.cs` | changed |
| `tests/Humans.Workgroups.Tests/Services/WorkgroupServiceRegistrationTests.cs` | changed |
| `tests/Humans.Workgroups.Tests/Services/WorkgroupServiceRhythmTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/Services/WorkgroupsEmailsTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/ViewComponents/GovernanceWorkgroupsViewComponentTests.cs` | reviewed |
| `tests/Humans.Workgroups.Tests/ViewComponents/MyWorkgroupsViewComponentTests.cs` | reviewed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | session model | findings 4, 5, 6, 10 |
| Behavior & bugs | main | session model | finding 12; no bug found |
| Freshness | subagent (`doctor-reader`) | opus low | findings 1, 7, 8, 11, 14 |
| Conformance | detectors on main; no hits, so no haiku classifier dispatched | — | none |
| Tests | subagent (`doctor-reader`) | opus low | finding 9 |
| Prose & surface | main (dead-key check run directly; reforge surface-score after a build); haiku not dispatched | — | finding 3 |
| History | subagent (`doctor-reader`) | opus low | folded into finding 2 |
| Comments | subagent (`doctor-reader`) | opus low | finding 2 |
| Inbox | subagent (`doctor-reader`) | opus low | finding 13 |

