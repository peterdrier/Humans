# Governance — Data Access

## Governance

Project: `src/Sections/Humans.Governance`; services under `Services/`,
repository under `Data/`. **DbContext:**
`GovernanceDbContext`. `ApplicationRepository`
and `AssemblyVoteRepository` inject
`IDbContextFactory<GovernanceDbContext>` directly. Owns
`Applications`, `MembershipTiers` (`governance_membership_tiers`), `ApplicationStateHistories`, `BoardVotes`,
`AssemblyVotes`, `AssemblyVoteOptions`, `AssemblyVoteRosterEntries`,
`AssemblyBallots`, `AssemblyBallotHistories`, `AssemblyVotePeeks`.

All three repositories are registered **Singleton** in `Section.cs`; they hold no state
and take an `IDbContextFactory`. The services below are Scoped.

`IGovernanceManagement` (internal) extends the public `IGovernanceService` (reads) with the
membership-tier expiry and the section's own members. Readers outside this section inject
`IGovernanceService` — tier reads from Users (popover, human summary, admin detail, duplicate
accounts), Onboarding, Workgroups, Tickets, Surveys, Agent and Teams. Nothing outside the section
writes Governance data: the section's own `MembershipTierExpiryJob` calls
`IGovernanceManagement.ExpireMembershipTiersAsync`; `MembershipTierService` records each `TierDowngraded` audit after its tier mutation commits.

Cross-section reads inside the section go through the read surfaces
(`IUserService`, `ITeamService`, `IConsentService`).

### ApplicationDecisionService (Scoped)

Repository: `IApplicationRepository`.

| Table | R/W |
|-------|-----|
| Applications | R/W |
| ApplicationStateHistories | R/W |
| BoardVotes | R/W (removed for GDPR after decision) |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `NavBadge:Voting:{userId}` (`ApplicationDecisionService.VotingBadgeCacheKey`) | 2 min | yes | yes | yes (per voter, in-service) |

Cross-section calls via `IUserService`,
`IRoleAssignmentService`, `IEmailService`, `IUserEmailService`, `INotificationEmitter`,
`ISystemTeamSync`, `IAuditLogService`, `IHumansMetrics`,
and the section's own `GovernanceEmails` builder. Implements `IGovernanceManagement` (which
extends `IGovernanceService`), `IUserDataContributor`, `IUserMerge`.
`EraseForUserAsync` calls `IApplicationRepository.ScrubFreeTextForUserAsync`,
which clears the applicant's own free text (motivation, additional info,
contribution, role understanding) and reviewer prose on `Applications` and
`ApplicationStateHistories`, plus notes on `BoardVotes` the user cast as a
Board member — the tier/status/date skeleton stays (Ley Orgánica 1/2002
Art. 14, GDPR Art. 17(3)(b)).

Tier reads, the grant on approval, term-expiry sync and expiry downgrades
delegate to `MembershipTierService`.

### MembershipTierService (Scoped, internal)

Repositories: `IMembershipTierRepository`, plus `IApplicationRepository` (read-only — grant
and term data for expiry). Both interfaces resolve to `ApplicationRepository`; its membership-tier partial owns tier access so approvals can commit the tier and application together.

| Table | R/W |
|-------|-----|
| MembershipTiers | R/W |
| Applications | R |

Cross-section reads: `IUserService` resolves the merge chain for single-user reads and export.
Implements `IUserDataContributor` (slice `MembershipTier`; erasure retains the row — register
of members, Ley Orgánica 1/2002 Art. 14, GDPR Art. 17(3)(b)) and `IUserMerge` (survivor's tier
wins).

### AssemblyVoteService (Scoped)

Repositories: `IAssemblyVoteRepository`, plus the section's `IApplicationRepository`
(read-only — active approved Asociado/Colaborador tier user ids for the electorate).

| Table | R/W |
|-------|-----|
| Applications | R (via `IApplicationRepository.GetActiveApprovedTierUserIdsAsync`) |
| AssemblyVotes | R/W |
| AssemblyVoteOptions | R/W (draft only — content is immutable once Open) |
| AssemblyVoteRosterEntries | R/W (written once at open; afterwards only the `NotifiedAt` / `ReminderSentAt` stamps — each written only while the vote still closes at the deadline the message announced, and `ReminderSentAt` cleared on extension — and the erasure tombstone) |
| AssemblyBallots | R/W |
| AssemblyBallotHistories | R (append-only — the repository exposes no update or delete) |
| AssemblyVotePeeks | R/W (insert only) |

No cache, no caching decorator — one vote at a time and ~120 voters.

Cross-section calls via `IUserService`,
`IRoleAssignmentService`, `ITeamService`, `IEmailService`,
the section's own `GovernanceEmails` builder, `INotificationEmitter`,
`INotificationAutoResolve`, `IAuditLogService`, `IGoogleTranslationService`,
`IClock`. Implements
`IAssemblyVoteService`, `IUserDataContributor`, `IUserMerge`. Nothing on
`Humans.Governance.Contracts`: no other section reads votes.

`EraseForUserAsync` nulls the member's `AssemblyVoteRosterEntries.UserId`
and retains the linked ballot and history rows unlinked — the vote is a
legal record of the association (GDPR Art. 17(3)(b) / (e)), and the
tombstone keeps the turnout counts and the stored `ResultJson` valid.
`ReassignAsync` (`ReassignVoteActorsToUserAsync`) moves only the vote actor
columns; roster rows and ballots stay on the merged-away id, audited per
vote, because they are the record of the vote rather than account state.
Member-facing reads resolve the chain instead — `EffectiveRosterAsync` for
the roster row, the resolved record's `UserInfo.AllUserIds` looped in
`ContributeForUserAsync`, and `RecipientsAsync` resolving every roster row's
user id through `GetUserInfosAsync` and grouping by the resolved human, so
mail reaches the survivor of a merge and one human gets one email however
many roster rows they hold.

**The embargo is a data-access property, not a UI one.** Only
`GetResultsAsync` (stored result, Closed only), `PeekAsync` (AdminOnly,
audited, writes its peek row in the same unit of work) and
`GetBallotsForBoardAsync` (Closed only, audited) return anything derived
from ballot content. `IAssemblyVoteRepository.GetParticipationAsync`
aggregates roster and ballot rows without reading `Choice` or `Ranking`,
which is what makes the live stats safe to show to everyone.

### AssemblyVoteCounting (static)

Not a service and not in the `IApplicationService` inventory: a pure
static counting function over ballot values with no clock, no repository
and no DI. `AssemblyVoteService` calls it once per close and stores the
output.

### GovernanceMetricsService (Hosted, `PolledGaugeService`)

No repository, no cache, not part of the `IApplicationService` inventory
(a `System.Diagnostics.Metrics` gauge publisher, not a section service).
Polls `IConsentService`, `IGovernanceManagement`,
`IUserService` on a timer to publish `humans.asociados`,
`humans.applications_pending`, `humans.pending_consents`,
`humans.consent_deadline_approaching`.

### GovernanceIndexService (Scoped)

No repository. Read-only assembly of the governance index view over
`IGovernanceService`, `ILegalDocumentService`, plus `IClock`.
No DB access, no cache.

### GovernanceEmails (Scoped, internal)

No repository. Pure builder — reads `GovernanceResource` (via
`IStringLocalizer<GovernanceResource>`) and `EmailSettings`, writes
nothing. Returns `EmailMessage` values for `ApplicationDecisionService`
and `AssemblyVoteService` to pass to `IEmailService.SendAsync`. No DB
access, no cache.

### GovernanceEmailPreviews (Scoped)

No repository. Read-only gallery contributor (`IEmailPreviewContributor`,
`Section.cs:88`) — builds one sample per template via `GovernanceEmails`
for `/Email/EmailPreview`. No DB access, no cache.

---



### ApplicationReader (internal, owned by ApplicationDecisionService)

Repository: `IApplicationRepository`. Reads application member/admin/Board views,
pending snapshots and summary counts; enriches through the existing Users and Auth
role-assignment contracts. Owns shared vote/history projection. No writes, cache
or separate DI registration; badge caching remains in the decision service.

### AssemblyVoteDrafts (internal, owned by AssemblyVoteService)

Repository: `IAssemblyVoteRepository`. Owns draft authoring and its existing
state/revision guards; uses the existing translation contract, clock and logger.
Shares bounded-text and safe-URL policy with the service. No legal-vote transitions,
cache or separate DI registration.

### ApplicationDecisionNotices (internal, owned by ApplicationDecisionService)

No repository. Sends the approval, rejection and withdrawal email and in-app
notification for a decided `Application` through `IUserService`, `IEmailService`,
`INotificationEmitter` and the `GovernanceEmails` builder, localised to the applicant's
language. Not DI-registered — the decision service constructs it. No DB access, no cache.

### AssemblyVoteDataExport (internal, owned by AssemblyVoteService)

Repository: `IAssemblyVoteRepository`. Builds the member's GDPR export slices
(`AssemblyVotes`, `AssemblyVoteActions`) behind `AssemblyVoteService`'s
`IUserDataContributor`; resolves the merge chain through `IUserService`. Read-only. Not
DI-registered. No cache.

| Table | R/W |
|-------|-----|
| AssemblyVoteRosterEntries | R (`GetVotingRecordForUserAsync`) |
| AssemblyVotes | R (voting record; votes the user created or peeked at via `GetActorRecordForUserAsync`) |
| AssemblyBallots | R |
| AssemblyBallotHistories | R (revisions included with the ballot) |
| AssemblyVotePeeks | R (`GetActorRecordForUserAsync`) |

### AssemblyVoteEffects (internal, owned by AssemblyVoteService)

Repository: `IAssemblyVoteRepository`. Owns the notification side of the vote lifecycle:
open and close notices, the pre-close reminder sweep, and the roster `NotifiedAt` /
`ReminderSentAt` stamps. Not DI-registered. No cache.

| Table | R/W |
|-------|-----|
| AssemblyVotes | R (`GetByIdAsync`, `GetOpenVotesClosingBetweenAsync`) |
| AssemblyVoteOptions | R (loaded with the vote) |
| AssemblyVoteRosterEntries | R/W (roster reads; `StampNotifiedAsync`, `StampReminderSentAsync` write the stamps) |
| AssemblyBallots | R (`GetBallotForRosterAsync`, reminder filter) |

Cross-section calls via `IUserService`, `IEmailService`, `INotificationEmitter`,
`INotificationAutoResolve`, `IAuditLogService`, `IClock`, and the section's `GovernanceEmails`.
