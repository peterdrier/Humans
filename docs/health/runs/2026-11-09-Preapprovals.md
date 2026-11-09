# section-doctor — Preapprovals — 2026-11-09

- Invocation: scheduled routine section-doctor-daily: 'Run the section-doctor skill unattended.'
- Anchor commit: `d6802bf58` (origin/main at branch point); branch `section-doctor/2026-11-09T003854Z`.
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
| `src/Sections/Humans.Preapprovals/Authorization/PreapprovalsPolicies.cs` |  |
| `src/Sections/Humans.Preapprovals/Authorization/PreapprovalsRequirements.cs` |  |
| `src/Sections/Humans.Preapprovals/Authorization/ProgramOperationRequirement.cs` |  |
| `src/Sections/Humans.Preapprovals/Contracts/GrantOption.cs` |  |
| `src/Sections/Humans.Preapprovals/Contracts/GrantStatus.cs` |  |
| `src/Sections/Humans.Preapprovals/Contracts/IPreapprovalService.cs` |  |
| `src/Sections/Humans.Preapprovals/Contracts/IPreapprovalWrite.cs` |  |
| `src/Sections/Humans.Preapprovals/Controllers/PreapprovalsAdminController.cs` |  |
| `src/Sections/Humans.Preapprovals/Controllers/PreapprovalsController.cs` |  |
| `src/Sections/Humans.Preapprovals/Data/Configurations/PreapprovalProgramConfiguration.cs` |  |
| `src/Sections/Humans.Preapprovals/Data/IPreapprovalRepository.cs` |  |
| `src/Sections/Humans.Preapprovals/Data/Migrations/20261016101851_AddPreapprovals.Designer.cs` | generated |
| `src/Sections/Humans.Preapprovals/Data/Migrations/20261016101851_AddPreapprovals.cs` |  |
| `src/Sections/Humans.Preapprovals/Data/Migrations/PreapprovalsDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.Preapprovals/Data/PreapprovalRepository.cs` |  |
| `src/Sections/Humans.Preapprovals/Data/PreapprovalsDbContext.cs` |  |
| `src/Sections/Humans.Preapprovals/Data/PreapprovalsDbContextFactory.cs` |  |
| `src/Sections/Humans.Preapprovals/Docs/Preapprovals.md` |  |
| `src/Sections/Humans.Preapprovals/Docs/authorization.md` |  |
| `src/Sections/Humans.Preapprovals/Docs/data-access.md` |  |
| `src/Sections/Humans.Preapprovals/Docs/debt.yml` |  |
| `src/Sections/Humans.Preapprovals/Domain/Enums.cs` |  |
| `src/Sections/Humans.Preapprovals/Domain/PreapprovalGrant.cs` |  |
| `src/Sections/Humans.Preapprovals/Domain/PreapprovalProgram.cs` |  |
| `src/Sections/Humans.Preapprovals/Humans.Preapprovals.csproj` |  |
| `src/Sections/Humans.Preapprovals/Jobs/PreapprovalRemindersJob.cs` |  |
| `src/Sections/Humans.Preapprovals/Models/PreapprovalViewModels.cs` |  |
| `src/Sections/Humans.Preapprovals/PreapprovalsAuditActions.cs` |  |
| `src/Sections/Humans.Preapprovals/PreapprovalsNotificationSources.cs` |  |
| `src/Sections/Humans.Preapprovals/PreapprovalsResource.ca.resx` |  |
| `src/Sections/Humans.Preapprovals/PreapprovalsResource.cs` |  |
| `src/Sections/Humans.Preapprovals/PreapprovalsResource.de.resx` |  |
| `src/Sections/Humans.Preapprovals/PreapprovalsResource.es.resx` |  |
| `src/Sections/Humans.Preapprovals/PreapprovalsResource.fr.resx` |  |
| `src/Sections/Humans.Preapprovals/PreapprovalsResource.it.resx` |  |
| `src/Sections/Humans.Preapprovals/PreapprovalsResource.nl.resx` |  |
| `src/Sections/Humans.Preapprovals/PreapprovalsResource.resx` |  |
| `src/Sections/Humans.Preapprovals/Properties/AssemblyInfo.cs` |  |
| `src/Sections/Humans.Preapprovals/Section.cs` |  |
| `src/Sections/Humans.Preapprovals/Services/AuditEntityTypes.cs` |  |
| `src/Sections/Humans.Preapprovals/Services/Dtos.cs` |  |
| `src/Sections/Humans.Preapprovals/Services/IPreapprovalManagement.cs` |  |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalDeadlineReminders.cs` |  |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalErrorKeys.cs` |  |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalNoticeRequest.cs` |  |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalNotices.cs` |  |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalService.Gdpr.cs` |  |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalService.Grants.cs` |  |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalService.Programs.cs` |  |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalService.cs` |  |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalsEmailPreviews.cs` |  |
| `src/Sections/Humans.Preapprovals/Services/PreapprovalsEmails.cs` |  |
| `src/Sections/Humans.Preapprovals/Views/Preapprovals/Index.cshtml` |  |
| `src/Sections/Humans.Preapprovals/Views/Preapprovals/Program.cshtml` |  |
| `src/Sections/Humans.Preapprovals/Views/PreapprovalsAdmin/Form.cshtml` |  |
| `src/Sections/Humans.Preapprovals/Views/PreapprovalsAdmin/Index.cshtml` |  |
| `src/Sections/Humans.Preapprovals/Views/PreapprovalsAdmin/Program.cshtml` |  |
| `src/Sections/Humans.Preapprovals/Views/_ViewImports.cshtml` |  |
| `tests/Humans.Preapprovals.Tests/Authorization/AuthorizationHandlerTests.cs` |  |
| `tests/Humans.Preapprovals.Tests/Controllers/ControllerTests.cs` |  |
| `tests/Humans.Preapprovals.Tests/Controllers/SectionNavTests.cs` |  |
| `tests/Humans.Preapprovals.Tests/Humans.Preapprovals.Tests.csproj` |  |
| `tests/Humans.Preapprovals.Tests/Infrastructure/AssemblyFixtures.cs` |  |
| `tests/Humans.Preapprovals.Tests/Infrastructure/PreapprovalsModelWarmupFixture.cs` |  |
| `tests/Humans.Preapprovals.Tests/Infrastructure/PreapprovalsTestHarness.cs` |  |
| `tests/Humans.Preapprovals.Tests/Infrastructure/TestPreapprovalsEmails.cs` |  |
| `tests/Humans.Preapprovals.Tests/Services/ExpensesTransitionTests.cs` |  |
| `tests/Humans.Preapprovals.Tests/Services/GdprTests.cs` |  |
| `tests/Humans.Preapprovals.Tests/Services/GrantTests.cs` |  |
| `tests/Humans.Preapprovals.Tests/Services/MoneyValidationTests.cs` |  |
| `tests/Humans.Preapprovals.Tests/Services/ProgramTests.cs` |  |
| `tests/Humans.Preapprovals.Tests/Services/ReminderTests.cs` |  |
| `tests/Humans.Preapprovals.Tests/ViewComponents/ProgramViewTests.cs` |  |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main |  |  |
| Behavior & bugs | main |  |  |
| Controller intent |  |  |  |
| Freshness |  |  |  |
| Conformance |  |  |  |
| Tests |  |  |  |
| Prose & surface |  |  |  |
| History |  |  |  |
| Comments |  |  |  |
| Inbox |  |  |  |

