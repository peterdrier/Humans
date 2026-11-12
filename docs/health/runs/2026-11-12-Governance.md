# section-doctor — Governance — 2026-11-12

- Invocation: unattended (scheduled routine section-doctor-daily)
- Anchor commit: `0b86513ca` (origin/main at branch point); branch `section-doctor/2026-11-12T003843Z`.
- Budget: 2.5h.
- PR: pending

## Assessment summary

Second re-doctor of Governance; the diff since the last run is large. Since then the assembly-vote service was split into collaborators it builds itself (drafts, administration, settlement, member workflow, effects, roster builder, result reader, export, presentation), and a per-member tier register (`governance_membership_tiers`, `MembershipTierService`, `MembershipTierExpiryJob`) took the tier off the profile. The target moved for both reasons, and one earlier claim was wrong: the request-more-info seam is gone from the state machine, and the term-renewal job is internal, not public.

The section is well tested and heavily commented; most of the work was truth. Docs still put the tier on the profile and the vote logic in one class, and the contracts leaf published three records only Governance reads.

Independence check: pass. Findings 1, 3, 9 and 10 came from the target (the vote-service split, the over-exposed contract records, the unwritten review-start field and the lower-tier approval against the spec's downgrade rule); the rest came from the reading threads.

## Findings

1. The target's invariant cites pointed into the pre-split `AssemblyVoteService.cs`; the request-more-info seam no longer exists; the tier register and its expiry job were missing from the target.
2. Comments false against the code: `IApplicationRepository.GetByUserIdAsync` claimed an ordering it does not apply; `AssemblyVoteResult` counted "five other cultures"; the test csproj counted "three controllers"; the bounded-column rationale sat on an alias in `AssemblyVoteService` instead of beside `Fits` in `AssemblyVoteDrafts`.
3. `AssemblyVoteService` reached its settlement collaborator through private one-line wrappers, and re-exported collaborator constants that only tests and docs read.
4. Docs and comments narrated history (the Users decomposition move of the tier, the dropped profile column, the retired submission notification, the removed information-request workflow).
5. Docs still placed the tier on the profile (assembly-votes roster `Tier`, the member guide's approval paragraph, the roster builder's remark) and claimed roster rows are never updated after open, though the delivery stamps are.
6. `data-access.md` named `AssemblyVoteService` for work its collaborators do, cited a stale `Section.cs` line, and had no entry for six of the collaborators.
7. The tier-expiry job comment called the hourly system-team sync a midnight sync.
8. `health.md` said `TermRenewalReminderJob` is public because Shell names it; it is internal.
9. `ApplicationDecisionResult`, `ApplicationRenewalReminderCandidate` and `ApplicationAdminStats` were public on the contracts leaf though only Governance and its tests name them.
10. `Application.ReviewStartedAt` is never written (private setter, no assignment), yet both application detail pages, two DTOs and a view model carry it.
11. Approving a Colaborador application for a member who holds an in-term Asociado tier rewrites their tier row to Colaborador (`ApplicationDecisionService.FinalizeDecisionAsync` grants the approved tier unconditionally); `src/Sections/Humans.Governance/Docs/features/membership-tiers.md` says downgrades happen only at term expiry.
12. The temporary term-expiry repair page had no ledger row (GOV-12).
13. `ReviewNotes` duplicates `DecisionNote` on every decision (GOV-13).
14. The Board meeting date is required only by the Finalize controller (GOV-14).
15. The member dashboard's term state and 90-day window duplicate the reminder job's window (GOV-15).
16. `MembershipTierExpiryJobTests.ExecuteAsync_ExpiresBothTiers` asserts the mock (GOV-16).
17. The `_ViewImports.cshtml` reason for keeping `Application_*` keys in SharedResource no longer holds (GOV-17).
18. `IApplicationRepository.GetAdminStatsAsync` returns a DTO (GOV-18).
19. Governance's `Section.cs` declares access-matrix rows for features other sections and the shell own (CENTRAL-110).
20. The admin Peek page is a GET that writes a peek row and an audit entry, and the results page writes an audit entry for Board and Admin viewers. Deliberate audited reads; recorded, not changed.
21. Unpinned invariants from the coverage matrix: a Board vote on a non-Submitted application, a member ballot after the deadline before the hourly job, the instant-runoff exhausted-field tie, and AdminOnly on the term-expiry repair page. The orderings (peek before tally, status before ballot read) are pinned only by their consequences.

## Debt verified

- The section ledger held no rows at the start of the run, so there was nothing to verify.

## Worked

- 1: target re-derived; cites re-traced after the strikes.
- 2: comments corrected; the bounded-column rationale moved beside `Fits` in `AssemblyVoteDrafts`.
- 3: wrappers inlined and aliases removed; tests and docs name the owning constants. Reviewer (critical tier) approved with a cite correction applied in Phase 7.
- 4, 5, 6, 7, 8: docs and comments made true; `data-access.md` gained one block naming the six collaborators. Issue refs in `assembly-votes.md` qualified.
- 9: the records moved to `Services/Dtos/ApplicationRecords.cs` as internal. Reviewer (critical tier) approved with the contracts csproj comment corrected, which also dropped its false claim of a Users.Contracts reference.
- 12 to 18: ledgered as GOV-12 to GOV-18; 19 as CENTRAL-110.
- No GitHub issue comments: the covered repo has no open Governance issue.

## Skipped

- Restating comments in `MembershipCalculatorTests` and `MembershipPartitionTests`, `MembershipCalculator.cs` line 48 and the `Section.cs` job comment: judged to carry setup or a summary the code does not show at a glance.
- 10 and 11: queued for Peter.
- 20: deliberate; a route change is behaviour.
- 21: test gaps noted; the run added no tests because each sits beside an existing pin of the same rule.
- Blocked sections passed over by the selector: Preapprovals (open PR).
- nobodies-collective/Humans issues: not covered (HTTP 403, repository not enabled for this session).
- 2026-11-12 session: cloud container, no app boot; no live render of any page. The `doctor-reader` and `doctor-reviewer-critical` agent types loaded in this session.

## Retro

**Selector.** Governance was the right pick: two large changes landed since the last run and both had left the docs behind.

**Wasted motion.** The Comments thread proposed rewording a test comment to say nothing writes `ReviewNotes`, which is false; checking it on main cost a grep and avoided a new false comment.

**What striking revealed.** Moving the contract records surfaced a stale csproj comment and a repository returning a DTO, neither of which any thread had flagged.

**Target diff.** The section moved (the service split and the tier register), and the earlier target was wrong twice: the request-more-info seam and the public job.

## Needs Peter

- [ ] 10 — drop `ReviewStartedAt` from the two detail pages and mark the column obsolete?
- [ ] 11 — may approving a lower tier replace a member's in-term higher tier, or must it be refused?

## File coverage

| Path | Disposition |
|---|---|
| `docs/guide/Governance.md` | reviewed, changed |
| `src/Sections/Humans.Governance.Contracts/ApplicationSnapshots.cs` | reviewed, changed |
| `src/Sections/Humans.Governance.Contracts/ApplicationStatus.cs` | reviewed |
| `src/Sections/Humans.Governance.Contracts/Humans.Governance.Contracts.csproj` | reviewed, changed |
| `src/Sections/Humans.Governance.Contracts/IGovernanceService.cs` | reviewed, changed |
| `src/Sections/Humans.Governance.Contracts/IMembershipCalculator.cs` | reviewed |
| `src/Sections/Humans.Governance.Contracts/MembershipPartition.cs` | reviewed |
| `src/Sections/Humans.Governance.Contracts/MembershipSnapshot.cs` | reviewed |
| `src/Sections/Humans.Governance.Contracts/MembershipStatus.cs` | reviewed |
| `src/Sections/Humans.Governance.Contracts/MembershipStatusLabels.cs` | reviewed |
| `src/Sections/Humans.Governance.Contracts/MembershipTier.cs` | reviewed |
| `src/Sections/Humans.Governance/Controllers/GovernanceApplicationsController.cs` | reviewed |
| `src/Sections/Humans.Governance/Controllers/GovernanceBoardVotingController.cs` | reviewed |
| `src/Sections/Humans.Governance/Controllers/GovernanceController.cs` | reviewed |
| `src/Sections/Humans.Governance/Controllers/GovernanceVotesAdminController.cs` | reviewed |
| `src/Sections/Humans.Governance/Controllers/GovernanceVotesController.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/ApplicationRepository.MembershipTiers.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/ApplicationRepository.cs` | reviewed, changed |
| `src/Sections/Humans.Governance/Data/AssemblyVoteRepository.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/ApplicationConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/ApplicationStateHistoryConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/AssemblyBallotConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/AssemblyBallotHistoryConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/AssemblyVoteConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/AssemblyVoteOptionConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/AssemblyVotePeekConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/AssemblyVoteRosterConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/BoardVoteConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/GovernanceJson.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/MembershipTierRecordConfiguration.cs` | reviewed, changed |
| `src/Sections/Humans.Governance/Data/GovernanceDbContext.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/GovernanceDbContextFactory.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/IApplicationRepository.cs` | reviewed, changed |
| `src/Sections/Humans.Governance/Data/IAssemblyVoteRepository.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/IMembershipTierRepository.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Migrations/20260809124929_BaselineGovernance.Designer.cs` | generated |
| `src/Sections/Humans.Governance/Data/Migrations/20260809124929_BaselineGovernance.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Migrations/20260910220748_AddAssemblyVotes.Designer.cs` | generated |
| `src/Sections/Humans.Governance/Data/Migrations/20260910220748_AddAssemblyVotes.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Migrations/20261021011610_GovernanceMembershipTiers.Designer.cs` | generated |
| `src/Sections/Humans.Governance/Data/Migrations/20261021011610_GovernanceMembershipTiers.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Migrations/GovernanceDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.Governance/Docs/Governance.md` | reviewed, changed |
| `src/Sections/Humans.Governance/Docs/authorization.md` | reviewed |
| `src/Sections/Humans.Governance/Docs/data-access.md` | reviewed, changed |
| `src/Sections/Humans.Governance/Docs/debt.yml` | reviewed, changed |
| `src/Sections/Humans.Governance/Docs/features/asociado-applications.md` | reviewed, changed |
| `src/Sections/Humans.Governance/Docs/features/assembly-votes.md` | reviewed, changed |
| `src/Sections/Humans.Governance/Docs/features/board-voting.md` | reviewed |
| `src/Sections/Humans.Governance/Docs/features/membership-status.md` | reviewed |
| `src/Sections/Humans.Governance/Docs/features/membership-tiers.md` | reviewed |
| `src/Sections/Humans.Governance/Docs/health.md` | reviewed, changed |
| `src/Sections/Humans.Governance/Docs/help/Admin.glossary.md` | reviewed |
| `src/Sections/Humans.Governance/Docs/help/Admin.guide.md` | reviewed |
| `src/Sections/Humans.Governance/Docs/help/Board.glossary.md` | reviewed |
| `src/Sections/Humans.Governance/Docs/help/Board.guide.md` | reviewed |
| `src/Sections/Humans.Governance/Docs/help/Governance.glossary.md` | reviewed |
| `src/Sections/Humans.Governance/Docs/help/Governance.guide.md` | reviewed |
| `src/Sections/Humans.Governance/Domain/Application.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/ApplicationStateHistory.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/ApplicationTrigger.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/AssemblyBallot.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/AssemblyBallotChoice.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/AssemblyBallotHistory.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/AssemblyVote.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/AssemblyVoteKind.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/AssemblyVoteOption.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/AssemblyVotePeek.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/AssemblyVoteRoster.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/AssemblyVoteStatus.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/BallotDisclosure.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/BoardVote.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/GovernanceLocalizedText.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/IndicativeAudience.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/MembershipTierRecord.cs` | reviewed, changed |
| `src/Sections/Humans.Governance/Domain/RequiredMajority.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/VoteChoice.cs` | reviewed |
| `src/Sections/Humans.Governance/GovernanceAuditActions.cs` | reviewed |
| `src/Sections/Humans.Governance/GovernanceNotificationSources.cs` | reviewed, changed |
| `src/Sections/Humans.Governance/GovernanceResource.ca.resx` | reviewed |
| `src/Sections/Humans.Governance/GovernanceResource.cs` | reviewed |
| `src/Sections/Humans.Governance/GovernanceResource.de.resx` | reviewed |
| `src/Sections/Humans.Governance/GovernanceResource.es.resx` | reviewed |
| `src/Sections/Humans.Governance/GovernanceResource.fr.resx` | reviewed |
| `src/Sections/Humans.Governance/GovernanceResource.it.resx` | reviewed |
| `src/Sections/Humans.Governance/GovernanceResource.nl.resx` | reviewed |
| `src/Sections/Humans.Governance/GovernanceResource.resx` | reviewed |
| `src/Sections/Humans.Governance/Humans.Governance.csproj` | reviewed |
| `src/Sections/Humans.Governance/Jobs/AssemblyVoteLapseJob.cs` | reviewed |
| `src/Sections/Humans.Governance/Jobs/MembershipTierExpiryJob.cs` | reviewed |
| `src/Sections/Humans.Governance/Jobs/TermRenewalReminderJob.cs` | reviewed |
| `src/Sections/Humans.Governance/Models/AdminApplicationViewModels.cs` | reviewed |
| `src/Sections/Humans.Governance/Models/ApplicationViewModels.cs` | reviewed |
| `src/Sections/Humans.Governance/Models/AssemblyVoteViewModels.cs` | reviewed |
| `src/Sections/Humans.Governance/Models/BoardVotingViewModels.cs` | reviewed |
| `src/Sections/Humans.Governance/Models/GovernanceViewModels.cs` | reviewed |
| `src/Sections/Humans.Governance/Properties/AssemblyInfo.cs` | reviewed |
| `src/Sections/Humans.Governance/Section.cs` | reviewed, changed |
| `src/Sections/Humans.Governance/Services/ApplicationDecisionNotices.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/ApplicationDecisionService.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/ApplicationReader.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/AssemblyVoteAdministration.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/AssemblyVoteCounting.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/AssemblyVoteDataExport.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/AssemblyVoteDrafts.cs` | reviewed, changed |
| `src/Sections/Humans.Governance/Services/AssemblyVoteEffects.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/AssemblyVoteMemberWorkflow.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/AssemblyVotePresentation.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/AssemblyVoteResultReader.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/AssemblyVoteRosterBuilder.cs` | reviewed, changed |
| `src/Sections/Humans.Governance/Services/AssemblyVoteService.cs` | reviewed, changed |
| `src/Sections/Humans.Governance/Services/AssemblyVoteSettlement.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/AuditEntityTypes.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/ApplicationAdminDetailDto.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/ApplicationAdminRowDto.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/ApplicationRecords.cs` | reviewed, changed |
| `src/Sections/Humans.Governance/Services/Dtos/ApplicationStateHistoryDto.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/ApplicationUserDetailDto.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/AssemblyVoteResult.cs` | reviewed, changed |
| `src/Sections/Humans.Governance/Services/Dtos/AssemblyVoteViews.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/BoardVoteRow.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/BoardVotingDashboardData.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/BoardVotingDashboardRow.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/BoardVotingDetailData.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/TermExpiryDriftRow.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/GovernanceEmailPreviews.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/GovernanceEmails.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/GovernanceIndexService.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/GovernanceMetricsService.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/IAssemblyVoteService.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/IGovernanceIndexService.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/IGovernanceManagement.cs` | reviewed, changed |
| `src/Sections/Humans.Governance/Services/IMembershipQuery.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/MembershipCalculator.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/MembershipQuery.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/MembershipTierService.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/TermExpiryCalculator.cs` | reviewed |
| `src/Sections/Humans.Governance/ViewComponents/AssemblyVotesCardViewComponent.cs` | reviewed |
| `src/Sections/Humans.Governance/ViewComponents/GovernanceApplicationsTileViewComponent.cs` | reviewed |
| `src/Sections/Humans.Governance/ViewComponents/GovernanceGalleryViewComponent.cs` | reviewed |
| `src/Sections/Humans.Governance/ViewComponents/MemberTermStatusViewComponent.cs` | reviewed |
| `src/Sections/Humans.Governance/ViewComponents/MembershipTierBadgeViewComponent.cs` | reviewed |
| `src/Sections/Humans.Governance/ViewComponents/PendingConsentsAlertViewComponent.cs` | reviewed |
| `src/Sections/Humans.Governance/ViewComponents/TierApplicationsCardViewComponent.cs` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Applications/Admin.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Applications/AdminDetail.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Applications/AdminTermExpiry.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Applications/Create.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Applications/Details.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Applications/Index.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/BoardVoting/Detail.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/BoardVoting/Index.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Index.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Admin/Ballots.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Admin/Create.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Admin/Edit.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Admin/Index.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Admin/Peek.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Details.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Index.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Results.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/Components/AssemblyVotesCard/Default.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/Components/GovernanceApplicationsTile/Default.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/Components/GovernanceGallery/Default.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/Components/MemberTermStatus/Default.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/Components/MembershipTierBadge/Default.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/Components/PendingConsentsAlert/Default.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/Components/TierApplicationsCard/Default.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/_ApplicationHistory.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/_ApplicationResponseSections.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/_ApplicationsListContent.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/_ViewImports.cshtml` | reviewed |
| `tests/Humans.Governance.Tests/Controllers/GovernanceAuthorizationTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Data/ApplicationRepositoryTests.cs` | reviewed, changed |
| `tests/Humans.Governance.Tests/Domain/ApplicationTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Domain/GovernanceLocalizedTextTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Enums/EnumStringStabilityTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Humans.Governance.Tests.csproj` | reviewed, changed |
| `tests/Humans.Governance.Tests/Infrastructure/AssemblyVoteServiceFixture.cs` | reviewed |
| `tests/Humans.Governance.Tests/Infrastructure/TestGovernanceEmails.cs` | reviewed |
| `tests/Humans.Governance.Tests/Infrastructure/UserInfoFixtures.cs` | reviewed |
| `tests/Humans.Governance.Tests/Models/AssemblyBallotFormViewModelTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Models/AssemblyBallotIdentityViewTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/SectionAccessMatrixTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/SectionHelpTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/ApplicationDecisionServiceTests.cs` | reviewed, changed |
| `tests/Humans.Governance.Tests/Services/AssemblyVoteCountingTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/AssemblyVoteEmbargoTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/AssemblyVoteRosterTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/AssemblyVoteServiceTests.cs` | reviewed, changed |
| `tests/Humans.Governance.Tests/Services/GovernanceEmailsTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/GovernanceIndexServiceTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/MembershipCalculatorTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/MembershipPartitionTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/MembershipTierExpiryJobTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/MembershipTierServiceTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/TermExpiryCalculatorTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/TermRenewalReminderJobTests.cs` | reviewed, changed |
| `tests/Humans.Governance.Tests/ViewComponents/MemberTermStatusViewComponentTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/ViewComponents/MembershipTierBadgeViewComponentTests.cs` | reviewed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | opus | 1, 3, 9, 19 |
| Behavior & bugs | main | opus | 10, 11, 13 |
| Controller intent | subagent (`doctor-reader`) | opus-low | 14, 15, 20 |
| Freshness | subagent (`doctor-reader`) | opus-low | 5, 6, 7 |
| Conformance | self-run on main (detectors only; the hits are the cross-section audit-actions and notification-sources root-file convention, advisory) | opus | none |
| Tests | subagent (`doctor-reader`) | opus-low | 16, 21 |
| Prose & surface | subagent (`pd:orch-haiku-medium`) | haiku-medium | none (no dead keys, translation parity clean) |
| History | subagent (`doctor-reader`) | opus-low | 4 |
| Comments | subagent (`doctor-reader`) | opus-low | 2, 12, 17 |
| Inbox | self-run on main; peterdrier/Humans: covered (no open Governance issue); nobodies-collective/Humans: not covered: HTTP 403 | opus | none |

