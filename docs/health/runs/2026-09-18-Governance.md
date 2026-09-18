# section-doctor — Governance — 2026-09-18

- Invocation: unattended daily run, no arguments (scheduled routine); Phase 8 skipped per prompt
- Anchor commit: `c654a568` (origin/main at branch point); branch `section-doctor/2026-09-18T011545Z`.
- Budget: 2.5h.
- PR: pending

## Assessment summary

## Findings

## Worked

## Skipped

## Retro

## Needs Peter

## File coverage

| Path | Disposition |
|---|---|
| `docs/guide/Governance.md` |  |
| `src/Sections/Humans.Governance.Contracts/ApplicationStatus.cs` |  |
| `src/Sections/Humans.Governance.Contracts/Humans.Governance.Contracts.csproj` |  |
| `src/Sections/Humans.Governance.Contracts/IApplicationDecisionService.cs` |  |
| `src/Sections/Humans.Governance.Contracts/IApplicationServiceRead.cs` |  |
| `src/Sections/Humans.Governance.Contracts/IMembershipCalculatorRead.cs` |  |
| `src/Sections/Humans.Governance.Contracts/MembershipPartition.cs` |  |
| `src/Sections/Humans.Governance.Contracts/MembershipSnapshot.cs` |  |
| `src/Sections/Humans.Governance.Contracts/MembershipStatus.cs` |  |
| `src/Sections/Humans.Governance.Contracts/MembershipStatusLabels.cs` |  |
| `src/Sections/Humans.Governance/Controllers/GovernanceApplicationsController.cs` |  |
| `src/Sections/Humans.Governance/Controllers/GovernanceBoardVotingController.cs` |  |
| `src/Sections/Humans.Governance/Controllers/GovernanceController.cs` |  |
| `src/Sections/Humans.Governance/Controllers/GovernanceVotesAdminController.cs` |  |
| `src/Sections/Humans.Governance/Controllers/GovernanceVotesController.cs` |  |
| `src/Sections/Humans.Governance/Data/ApplicationRepository.cs` |  |
| `src/Sections/Humans.Governance/Data/AssemblyVoteRepository.cs` |  |
| `src/Sections/Humans.Governance/Data/Configurations/ApplicationConfiguration.cs` |  |
| `src/Sections/Humans.Governance/Data/Configurations/ApplicationStateHistoryConfiguration.cs` |  |
| `src/Sections/Humans.Governance/Data/Configurations/AssemblyBallotConfiguration.cs` |  |
| `src/Sections/Humans.Governance/Data/Configurations/AssemblyBallotHistoryConfiguration.cs` |  |
| `src/Sections/Humans.Governance/Data/Configurations/AssemblyVoteConfiguration.cs` |  |
| `src/Sections/Humans.Governance/Data/Configurations/AssemblyVoteOptionConfiguration.cs` |  |
| `src/Sections/Humans.Governance/Data/Configurations/AssemblyVotePeekConfiguration.cs` |  |
| `src/Sections/Humans.Governance/Data/Configurations/AssemblyVoteRosterConfiguration.cs` |  |
| `src/Sections/Humans.Governance/Data/Configurations/BoardVoteConfiguration.cs` |  |
| `src/Sections/Humans.Governance/Data/Configurations/GovernanceJson.cs` |  |
| `src/Sections/Humans.Governance/Data/GovernanceDbContext.cs` |  |
| `src/Sections/Humans.Governance/Data/GovernanceDbContextFactory.cs` |  |
| `src/Sections/Humans.Governance/Data/IApplicationRepository.cs` |  |
| `src/Sections/Humans.Governance/Data/IAssemblyVoteRepository.cs` |  |
| `src/Sections/Humans.Governance/Data/Migrations/20260809124929_BaselineGovernance.Designer.cs` | generated |
| `src/Sections/Humans.Governance/Data/Migrations/20260809124929_BaselineGovernance.cs` |  |
| `src/Sections/Humans.Governance/Data/Migrations/20260910220748_AddAssemblyVotes.Designer.cs` | generated |
| `src/Sections/Humans.Governance/Data/Migrations/20260910220748_AddAssemblyVotes.cs` |  |
| `src/Sections/Humans.Governance/Data/Migrations/GovernanceDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.Governance/Docs/Governance.md` |  |
| `src/Sections/Humans.Governance/Docs/authorization.md` |  |
| `src/Sections/Humans.Governance/Docs/data-access.md` |  |
| `src/Sections/Humans.Governance/Docs/debt.yml` |  |
| `src/Sections/Humans.Governance/Docs/features/asociado-applications.md` |  |
| `src/Sections/Humans.Governance/Docs/features/assembly-votes.md` |  |
| `src/Sections/Humans.Governance/Docs/features/board-voting.md` |  |
| `src/Sections/Humans.Governance/Docs/features/membership-status.md` |  |
| `src/Sections/Humans.Governance/Docs/features/membership-tiers.md` |  |
| `src/Sections/Humans.Governance/Docs/health.md` |  |
| `src/Sections/Humans.Governance/Domain/Application.cs` |  |
| `src/Sections/Humans.Governance/Domain/ApplicationStateHistory.cs` |  |
| `src/Sections/Humans.Governance/Domain/ApplicationTrigger.cs` |  |
| `src/Sections/Humans.Governance/Domain/AssemblyBallot.cs` |  |
| `src/Sections/Humans.Governance/Domain/AssemblyBallotChoice.cs` |  |
| `src/Sections/Humans.Governance/Domain/AssemblyBallotHistory.cs` |  |
| `src/Sections/Humans.Governance/Domain/AssemblyVote.cs` |  |
| `src/Sections/Humans.Governance/Domain/AssemblyVoteKind.cs` |  |
| `src/Sections/Humans.Governance/Domain/AssemblyVoteOption.cs` |  |
| `src/Sections/Humans.Governance/Domain/AssemblyVotePeek.cs` |  |
| `src/Sections/Humans.Governance/Domain/AssemblyVoteRoster.cs` |  |
| `src/Sections/Humans.Governance/Domain/AssemblyVoteStatus.cs` |  |
| `src/Sections/Humans.Governance/Domain/BallotDisclosure.cs` |  |
| `src/Sections/Humans.Governance/Domain/BoardVote.cs` |  |
| `src/Sections/Humans.Governance/Domain/GovernanceLocalizedText.cs` |  |
| `src/Sections/Humans.Governance/Domain/IndicativeAudience.cs` |  |
| `src/Sections/Humans.Governance/Domain/RequiredMajority.cs` |  |
| `src/Sections/Humans.Governance/Domain/VoteChoice.cs` |  |
| `src/Sections/Humans.Governance/GovernanceResource.ca.resx` |  |
| `src/Sections/Humans.Governance/GovernanceResource.cs` |  |
| `src/Sections/Humans.Governance/GovernanceResource.de.resx` |  |
| `src/Sections/Humans.Governance/GovernanceResource.es.resx` |  |
| `src/Sections/Humans.Governance/GovernanceResource.fr.resx` |  |
| `src/Sections/Humans.Governance/GovernanceResource.it.resx` |  |
| `src/Sections/Humans.Governance/GovernanceResource.resx` |  |
| `src/Sections/Humans.Governance/Humans.Governance.csproj` |  |
| `src/Sections/Humans.Governance/Jobs/AssemblyVoteLapseJob.cs` |  |
| `src/Sections/Humans.Governance/Jobs/TermRenewalReminderJob.cs` |  |
| `src/Sections/Humans.Governance/Models/AdminApplicationViewModels.cs` |  |
| `src/Sections/Humans.Governance/Models/ApplicationViewModels.cs` |  |
| `src/Sections/Humans.Governance/Models/AssemblyVoteViewModels.cs` |  |
| `src/Sections/Humans.Governance/Models/BoardVotingViewModels.cs` |  |
| `src/Sections/Humans.Governance/Models/GovernanceViewModels.cs` |  |
| `src/Sections/Humans.Governance/Properties/AssemblyInfo.cs` |  |
| `src/Sections/Humans.Governance/Section.cs` |  |
| `src/Sections/Humans.Governance/SectionAdminNav.cs` |  |
| `src/Sections/Humans.Governance/SectionChrome.cs` |  |
| `src/Sections/Humans.Governance/SectionJobs.cs` |  |
| `src/Sections/Humans.Governance/SectionMemberDashboard.cs` |  |
| `src/Sections/Humans.Governance/SectionNav.cs` |  |
| `src/Sections/Humans.Governance/SectionThingsToDo.cs` |  |
| `src/Sections/Humans.Governance/Services/ApplicationDecisionService.cs` |  |
| `src/Sections/Humans.Governance/Services/AssemblyVoteCounting.cs` |  |
| `src/Sections/Humans.Governance/Services/AssemblyVoteService.cs` |  |
| `src/Sections/Humans.Governance/Services/AuditEntityTypes.cs` |  |
| `src/Sections/Humans.Governance/Services/Dtos/ApplicationAdminDetailDto.cs` |  |
| `src/Sections/Humans.Governance/Services/Dtos/ApplicationAdminRowDto.cs` |  |
| `src/Sections/Humans.Governance/Services/Dtos/ApplicationStateHistoryDto.cs` |  |
| `src/Sections/Humans.Governance/Services/Dtos/ApplicationUserDetailDto.cs` |  |
| `src/Sections/Humans.Governance/Services/Dtos/AssemblyVoteResult.cs` |  |
| `src/Sections/Humans.Governance/Services/Dtos/AssemblyVoteViews.cs` |  |
| `src/Sections/Humans.Governance/Services/Dtos/BoardVoteRow.cs` |  |
| `src/Sections/Humans.Governance/Services/Dtos/BoardVotingDashboardData.cs` |  |
| `src/Sections/Humans.Governance/Services/Dtos/BoardVotingDashboardRow.cs` |  |
| `src/Sections/Humans.Governance/Services/Dtos/BoardVotingDetailData.cs` |  |
| `src/Sections/Humans.Governance/Services/Dtos/TermExpiryDriftRow.cs` |  |
| `src/Sections/Humans.Governance/Services/GovernanceIndexService.cs` |  |
| `src/Sections/Humans.Governance/Services/GovernanceMetricsService.cs` |  |
| `src/Sections/Humans.Governance/Services/IAssemblyVoteService.cs` |  |
| `src/Sections/Humans.Governance/Services/IGovernanceIndexService.cs` |  |
| `src/Sections/Humans.Governance/Services/IMembershipQuery.cs` |  |
| `src/Sections/Humans.Governance/Services/MembershipCalculator.cs` |  |
| `src/Sections/Humans.Governance/Services/MembershipQuery.cs` |  |
| `src/Sections/Humans.Governance/Services/TermExpiryCalculator.cs` |  |
| `src/Sections/Humans.Governance/ViewComponents/AssemblyVotesCardViewComponent.cs` |  |
| `src/Sections/Humans.Governance/ViewComponents/GovernanceApplicationsTileViewComponent.cs` |  |
| `src/Sections/Humans.Governance/ViewComponents/MemberTermStatusViewComponent.cs` |  |
| `src/Sections/Humans.Governance/ViewComponents/PendingConsentsAlertViewComponent.cs` |  |
| `src/Sections/Humans.Governance/ViewComponents/TierApplicationsCardViewComponent.cs` |  |
| `src/Sections/Humans.Governance/Views/Governance/Applications/Admin.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Governance/Applications/AdminDetail.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Governance/Applications/AdminTermExpiry.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Governance/Applications/Create.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Governance/Applications/Details.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Governance/Applications/Index.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Governance/BoardVoting/Detail.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Governance/BoardVoting/Index.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Governance/BoardVoting/_ViewStart.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Governance/Index.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Admin/Ballots.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Admin/Create.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Admin/Edit.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Admin/Index.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Admin/Peek.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Details.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Index.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Results.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Shared/Components/AssemblyVotesCard/Default.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Shared/Components/GovernanceApplicationsTile/Default.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Shared/Components/MemberTermStatus/Default.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Shared/Components/PendingConsentsAlert/Default.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Shared/Components/TierApplicationsCard/Default.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Shared/_ApplicationHistory.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Shared/_ApplicationResponseSections.cshtml` |  |
| `src/Sections/Humans.Governance/Views/Shared/_ApplicationsListContent.cshtml` |  |
| `src/Sections/Humans.Governance/Views/_ViewImports.cshtml` |  |
| `tests/Humans.Governance.Tests/Data/ApplicationRepositoryTests.cs` |  |
| `tests/Humans.Governance.Tests/Domain/ApplicationTests.cs` |  |
| `tests/Humans.Governance.Tests/Domain/GovernanceLocalizedTextTests.cs` |  |
| `tests/Humans.Governance.Tests/Enums/EnumStringStabilityTests.cs` |  |
| `tests/Humans.Governance.Tests/Humans.Governance.Tests.csproj` |  |
| `tests/Humans.Governance.Tests/Infrastructure/AssemblyVoteServiceFixture.cs` |  |
| `tests/Humans.Governance.Tests/Infrastructure/UserInfoFixtures.cs` |  |
| `tests/Humans.Governance.Tests/Models/AssemblyBallotFormViewModelTests.cs` |  |
| `tests/Humans.Governance.Tests/Services/ApplicationDecisionServiceTests.cs` |  |
| `tests/Humans.Governance.Tests/Services/AssemblyVoteCountingTests.cs` |  |
| `tests/Humans.Governance.Tests/Services/AssemblyVoteEmbargoTests.cs` |  |
| `tests/Humans.Governance.Tests/Services/AssemblyVoteRosterTests.cs` |  |
| `tests/Humans.Governance.Tests/Services/AssemblyVoteServiceTests.cs` |  |
| `tests/Humans.Governance.Tests/Services/GovernanceIndexServiceTests.cs` |  |
| `tests/Humans.Governance.Tests/Services/MembershipCalculatorTests.cs` |  |
| `tests/Humans.Governance.Tests/Services/MembershipPartitionTests.cs` |  |
| `tests/Humans.Governance.Tests/Services/TermExpiryCalculatorTests.cs` |  |

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

