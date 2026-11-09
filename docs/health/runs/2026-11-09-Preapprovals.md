# section-doctor — Preapprovals — 2026-11-09

- Invocation: scheduled routine section-doctor-daily: 'Run the section-doctor skill unattended.'
- Anchor commit: `d6802bf58` (origin/main at branch point); branch `section-doctor/2026-11-09T003854Z`.
- Budget: 2.5h.
- PR: peterdrier/Humans#2350

## Assessment summary

First doctoring of a section built in one feature PR and hardened over many review rounds. The code
was sound; the cost sat in prose. The invariant doc had grown bullets that narrated cancellation
plumbing, view ordering and pipeline sharing, and two of its claims were false. One real defect:
the amount-changed email preview rendered an empty "Before" line. The admin index kept its own copy
of the rule for who sees which program. Target: `src/Sections/Humans.Preapprovals/Docs/health.md`
(new this run; no previous target to diff).

Independence check: pass. Findings 2, 4, 5 and 6 came from reading the target's shapes against the
code, not from a tool.

## Findings

1. The email preview gallery's amount-changed sample passed a detail line that email never renders and no previous amount, so its "Before" line was empty; no test covered the gallery.
2. `Preapprovals.md` and the `IPreapprovalManagement.ApproveAsync` summary said an explicit amount on a fixed-per-person approve is ignored; the code refuses it and `GrantTests` pins the refusal.
3. `Preapprovals.md` said the admin pages stitch names with `IUserService.GetUserInfosAsync`; they render people through the human component, and that read serves only notice recipients.
4. `Preapprovals.md` Invariants and Triggers carried review-round narration (request cancellation, display dictionaries, grant ordering, shared issuance pipelines, Expenses' coordination of its own transitions) beside the rules.
5. `ProgramInfo.Consumed`, `ProgramInfo.Requested` and `GrantInfo.ProgramKind` had no reader; the admin index status cell tested `AcceptsApplications` beside a predicate that already includes it.
6. The GDPR export read every program to name programs its loaded rows already carry.
7. The access policy asked whether the person manages a program, then asked `HasAnythingForUserAsync`, which asks it again.
8. The admin index restated who may see which program beside `FinancePolicies.Read` instead of asking the program handler (ledger row PREAPPROVALS-1).
9. Comments: the ceiling comment claimed only grant paths check the ceiling; the notice-kind summaries listed each email's contents; two comments repeated their neighbours.
10. Both controllers declared their own Madrid time zone beside the service's.
11. The decline start-state guard had no test.
12. The rule that a grant action names a grant of the route's program lives only in the controller (PREAPPROVALS-2).
13. The optional `afterCommit` default on the Expenses transitions serves only this section's tests (PREAPPROVALS-3).
14. `ProgramFormViewModel.ToSave` clears a per-person amount on a per-grant program, so the service's refusal of one never fires from the page; deliberate, recorded as load-bearing weirdness.
15. A single issue with no person chosen builds its refusal in the controller; one line, left as is.

## Debt verified

- PREAPPROVALS-1 — closed — the admin index now asks `ProgramOperationRequirement.View` per program — `PreapprovalsAdminController.cs:39`, pinned by `ControllerTests` (manager of A sees only A; Board sees every program)

## Worked

- Finding 1: preview passes a previous amount; `EmailPreviewTests` pins one sample per notice kind and the Before line (red before the fix, green after).
- Findings 2, 3, 4: invariant doc rewritten to its rules; health target written.
- Finding 5: dead view members deleted.
- Finding 6: GDPR export reads names from loaded rows.
- Finding 7: access policy drops the repeated query; the cancellation test drives the manager read through the Manage requirement.
- Finding 8: admin index asks the program handler; `authorization.md` updated; PREAPPROVALS-1 closed. Reviewer (opus high) approved with that doc correction, after checking role sets, merged ids, the unresolvable-user path and both pinning tests.
- Finding 9: false and restating comments cut or corrected.
- Finding 10: controllers reuse `PreapprovalService.MadridZone`.
- Finding 11: `Decline_NotApplied_RefusedAndUnchanged` added.
- Findings 12, 13: filed as PREAPPROVALS-2 and PREAPPROVALS-3.
- Issue comments: none; no open issue names this section.

## Skipped

- Users: blocked by an open section-doctor PR.
- Comments thread suggestions kept as written: the repeated "the grant committed" warning at each `CancellationToken.None` reassignment (each explains its own line), the csproj comments (the shared section template), the "No FK" notes (a contract a reader needs) and the test class summaries.
- Finding 15: one line in the controller, no rule to move.

## Retro

Selector: right pick. A first-run section built in one PR and then patched through many review rounds is exactly where prose drift collects, and that is where most of the value was.

Wasted motion: the prose and surface thread ended without handing back its report; its dead-key file on disk carried the result, so nothing was lost, but its other checks are unverified.

Striking revealed: the access-policy simplification broke a cancellation test that pinned which read the handler made, not what it guaranteed; the test was moved onto the requirement that still makes that read.

Target diff: no previous target; this is the first.

The prose gate reads the legal citation Art. 17(3)(b) in the invariant doc as a parenthesised count; it is a citation and stays.

## Needs Peter

- [ ] 13 — make `afterCommit` required on the Expenses transitions?

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.Preapprovals/Authorization/PreapprovalsPolicies.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Authorization/PreapprovalsRequirements.cs` | changed |
| `src/Sections/Humans.Preapprovals/Authorization/ProgramOperationRequirement.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Contracts/GrantOption.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Contracts/GrantStatus.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Contracts/IPreapprovalService.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Contracts/IPreapprovalWrite.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Controllers/PreapprovalsAdminController.cs` | changed |
| `src/Sections/Humans.Preapprovals/Controllers/PreapprovalsController.cs` | changed |
| `src/Sections/Humans.Preapprovals/Data/Configurations/PreapprovalProgramConfiguration.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Data/IPreapprovalRepository.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Data/Migrations/20261016101851_AddPreapprovals.Designer.cs` | generated |
| `src/Sections/Humans.Preapprovals/Data/Migrations/20261016101851_AddPreapprovals.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Data/Migrations/PreapprovalsDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.Preapprovals/Data/PreapprovalRepository.cs` | changed |
| `src/Sections/Humans.Preapprovals/Data/PreapprovalsDbContext.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Data/PreapprovalsDbContextFactory.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Docs/Preapprovals.md` | changed |
| `src/Sections/Humans.Preapprovals/Docs/authorization.md` | changed |
| `src/Sections/Humans.Preapprovals/Docs/data-access.md` | reviewed |
| `src/Sections/Humans.Preapprovals/Docs/debt.yml` | changed |
| `src/Sections/Humans.Preapprovals/Docs/health.md` | changed |
| `src/Sections/Humans.Preapprovals/Domain/Enums.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Domain/PreapprovalGrant.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Domain/PreapprovalProgram.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Humans.Preapprovals.csproj` | reviewed |
| `src/Sections/Humans.Preapprovals/Jobs/PreapprovalRemindersJob.cs` | changed |
| `src/Sections/Humans.Preapprovals/Models/PreapprovalViewModels.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/PreapprovalsAuditActions.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/PreapprovalsNotificationSources.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/PreapprovalsResource.ca.resx` | reviewed |
| `src/Sections/Humans.Preapprovals/PreapprovalsResource.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/PreapprovalsResource.de.resx` | reviewed |
| `src/Sections/Humans.Preapprovals/PreapprovalsResource.es.resx` | reviewed |
| `src/Sections/Humans.Preapprovals/PreapprovalsResource.fr.resx` | reviewed |
| `src/Sections/Humans.Preapprovals/PreapprovalsResource.it.resx` | reviewed |
| `src/Sections/Humans.Preapprovals/PreapprovalsResource.nl.resx` | reviewed |
| `src/Sections/Humans.Preapprovals/PreapprovalsResource.resx` | reviewed |
| `src/Sections/Humans.Preapprovals/Properties/AssemblyInfo.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Section.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Services/AuditEntityTypes.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Services/Dtos.cs` | changed |
| `src/Sections/Humans.Preapprovals/Services/IPreapprovalManagement.cs` | changed |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalDeadlineReminders.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalErrorKeys.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalNoticeRequest.cs` | changed |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalNotices.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalService.Gdpr.cs` | changed |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalService.Grants.cs` | changed |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalService.Programs.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalService.cs` | changed |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalsEmailPreviews.cs` | changed |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalsEmails.cs` | reviewed |
| `src/Sections/Humans.Preapprovals/Views/Preapprovals/Index.cshtml` | reviewed |
| `src/Sections/Humans.Preapprovals/Views/Preapprovals/Program.cshtml` | reviewed |
| `src/Sections/Humans.Preapprovals/Views/PreapprovalsAdmin/Form.cshtml` | reviewed |
| `src/Sections/Humans.Preapprovals/Views/PreapprovalsAdmin/Index.cshtml` | changed |
| `src/Sections/Humans.Preapprovals/Views/PreapprovalsAdmin/Program.cshtml` | reviewed |
| `src/Sections/Humans.Preapprovals/Views/_ViewImports.cshtml` | reviewed |
| `tests/Humans.Preapprovals.Tests/Authorization/AuthorizationHandlerTests.cs` | changed |
| `tests/Humans.Preapprovals.Tests/Controllers/ControllerTests.cs` | reviewed |
| `tests/Humans.Preapprovals.Tests/Controllers/SectionNavTests.cs` | reviewed |
| `tests/Humans.Preapprovals.Tests/Humans.Preapprovals.Tests.csproj` | reviewed |
| `tests/Humans.Preapprovals.Tests/Infrastructure/AssemblyFixtures.cs` | reviewed |
| `tests/Humans.Preapprovals.Tests/Infrastructure/PreapprovalsModelWarmupFixture.cs` | reviewed |
| `tests/Humans.Preapprovals.Tests/Infrastructure/PreapprovalsTestHarness.cs` | reviewed |
| `tests/Humans.Preapprovals.Tests/Infrastructure/TestPreapprovalsEmails.cs` | reviewed |
| `tests/Humans.Preapprovals.Tests/Services/EmailPreviewTests.cs` | changed |
| `tests/Humans.Preapprovals.Tests/Services/ExpensesTransitionTests.cs` | reviewed |
| `tests/Humans.Preapprovals.Tests/Services/GdprTests.cs` | reviewed |
| `tests/Humans.Preapprovals.Tests/Services/GrantTests.cs` | changed |
| `tests/Humans.Preapprovals.Tests/Services/MoneyValidationTests.cs` | reviewed |
| `tests/Humans.Preapprovals.Tests/Services/ProgramTests.cs` | reviewed |
| `tests/Humans.Preapprovals.Tests/Services/ReminderTests.cs` | reviewed |
| `tests/Humans.Preapprovals.Tests/ViewComponents/ProgramViewTests.cs` | changed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | session model | findings 5, 6, 7, 14 |
| Behavior & bugs | main | session model | findings 1, 12, 13 |
| Controller intent | subagent (`pd:orch-opus-low`) | opus-low | findings 8, 10, 12, 14, 15 |
| Freshness | subagent (`pd:orch-opus-low`) | opus-low | findings 2, 3, 4 |
| Conformance | self-run on main (detectors only; the hits were trivial to classify) | session model | none; the loose audit-action and notification-source files at the root are the cross-section convention (advisory) |
| Tests | subagent (`pd:orch-opus-low`) | opus-low | findings 1, 11 |
| Prose & surface | subagent (`pd:orch-haiku-medium`); report not handed back, result read from its dead-key file | haiku-medium | none (both dead-key halves ran; no dead key) |
| History | self-run on main (the extractor output was a few lines) | session model | none |
| Comments | subagent (`pd:orch-opus-low`) | opus-low | finding 9 |
| Inbox | self-run on main (no open issue to review) | session model | none. peterdrier/Humans: covered. nobodies-collective/Humans: not covered: REST read of a known issue refused, repository not enabled for this session |

