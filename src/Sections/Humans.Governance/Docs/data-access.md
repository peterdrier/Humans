# Governance — Data Access

## Governance

Project: `src/Sections/Humans.Governance`; services under `Services/`,
repository under `Data/`. **DbContext:**
`GovernanceDbContext`. `ApplicationRepository`
and `AssemblyVoteRepository` inject
`IDbContextFactory<GovernanceDbContext>` directly. Owns
`Applications`, `MembershipTiers` (`governance_membership_tiers`), `ApplicationStateHistories`, `BoardVotes`,
`AssemblyVotes`, `AssemblyVoteOptions`, `AssemblyVoteRosterEntries`,
`AssemblyBallots`, `AssemblyBallotHistories`, `AssemblyVotePeeks`, `GovernanceBodyLinks` (`governance_body_links`),
`GovernanceBodyFiles` (`governance_body_files`), `GovernanceBoardPositions` (`governance_board_positions`).

All four repositories (`GovernanceBodyRepository` included) are registered **Singleton** in `Section.cs`; they hold no state
and take an `IDbContextFactory`. The services below are Scoped.

`IGovernanceManagement` (internal) carries application reads, admin counts and renewal reminders
on `ApplicationDecisionService`. The existing `MembershipTierService` implements the public
`IGovernanceService` directly. Readers outside this section inject
`IGovernanceService` — tier reads from Users (popover, human summary, admin detail, duplicate
accounts), Onboarding, Workgroups, Tickets, Surveys, Agent and Teams. Nothing outside the section
writes Governance data: the section's own `MembershipTierExpiryJob` calls
the existing `MembershipTierService.ExpireAsync` directly; `MembershipTierService` records each `TierDowngraded` audit after its tier mutation commits.

Cross-section reads inside the section go through the read surfaces
(`IUserService`, `ITeamService`, `IConsentService`).

### ApplicationDecisionService (Scoped)

Repository: `IApplicationRepository`.

| Table | R/W |
|-------|-----|
| Applications | R/W |
| ApplicationStateHistories | R/W |
| BoardVotes | R/W (removed for GDPR after decision) |

Cached by `CachingGovernanceManagement` (Singleton `TrackedCache<Guid, int>`, `Governance.UnvotedApplicationCounts`):
each Board member's unvoted count, loaded on first read. This service clears it after submit, withdraw,
approve, reject and merge, and evicts the voter's entry after a Board vote.

Cross-section calls via `IUserService`,
`ISystemRoleService`, `IEmailService`, `IUserEmailService`, `INotificationEmitter`,
`IAuditLogService`, `IHumansMetrics`, the section's `GovernanceBodySync`,
and the section's own `GovernanceEmails` builder. Implements `IGovernanceManagement`, `IUserDataContributor`, `IUserMerge`.
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

Cached by `CachingGovernanceService` (Singleton `TrackedCache<Guid, MembershipTier>`, `Governance.MembershipTiers`),
which wraps `IGovernanceService`: each looked-up member's tier, loaded on first read. This service clears it after
an expiry downgrade or a merge; `ApplicationDecisionService` clears it when an approval grants a tier.

Cross-section reads: `IUserService` resolves the merge chain for single-user reads and export.
Implements `IUserDataContributor` (slice `MembershipTier`; erasure retains the row — register
of members, Ley Orgánica 1/2002 Art. 14, GDPR Art. 17(3)(b)) and `IUserMerge` (survivor's tier
wins).

### GovernanceBodyLinksService (Scoped, internal)

Repository: `IGovernanceBodyRepository` (`GovernanceBodyRepository`).

| Table | R/W |
|-------|-----|
| GovernanceBodyLinks | R/W |
| GovernanceBodyFiles | R/W |

Each body's (Asociados, Colaboradores, Board) Google group prefix and Google files, and their edits from the
Manage card (audited, then `GovernanceBodySync`). Cross-section reads: for the one-time
`governance.body-links-from-teams` data migration, `ITeamService.GetTeamAsync` (the old system team's
`GoogleGroupPrefix`) and `ITeamResourceService.GetResourcesByTeamIdsAsync` (its active non-Group resources);
for adding a file, `ITeamResourceService.LookUpDriveLinkAsync`. Writes `IAuditLogService`. No personal data.

### GovernanceBodyRosters (Scoped, internal)

Each body's members before account filtering: `IApplicationRepository.GetActiveApprovedTierUserIdsAsync`
(Asociados, Colaboradores) and `ISystemRoleService.GetActiveUserIdsInRoleAsync(Board)`. Reads no
Governance service, so Google's sync can reach it without a cycle. Used by `GovernanceIndexService` (which
filters through `IUserService`) and `GovernanceBodyGoogleSource`.

### GovernanceBodyGoogleSource (Scoped, internal)

`IGoogleGroupMembershipSource` and `IGoogleDriveAccessSource` for the bodies: reads `IGovernanceBodyRepository`
(R) and `GovernanceBodyRosters`. User ids only, no `IUserService`.

### GovernanceBodySync (Scoped, internal)

Reads `IGovernanceBodyRepository` (R) and asks `IGoogleGroupSync.RequestSyncAsync` /
`IGoogleSyncWrite.RequestSyncAsync` to sync one body's group and files. Called by `ApplicationDecisionService`
on approval, by `GovernanceBoardSeats` after a seat is added or ended, and by `GovernanceBodyLinksService` after an edit.

### GovernanceBoardSeats (Scoped, internal)

Adds and ends Board seats through `ISystemRoleWrite.AssignRoleAsync` / `RevokeRoleAsync`, finding the
seat with `ISystemRoleService.GetByUserIdAsync`; Auth stores and audits them. Owns the Board offices in
`governance_board_positions` through `IGovernanceBodyRepository` (R/W), and is that table's GDPR contributor and merge step.

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
`ISystemRoleService`, `ITeamService`, `IEmailService`,
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
`Section.cs:113`) — builds one sample per template via `GovernanceEmails`
for `/Email/EmailPreview`. No DB access, no cache.

---

### AssemblyVote static helpers

- `AssemblyVotePresentation`: static; builds the acta text. No data access.
