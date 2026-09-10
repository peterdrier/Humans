# Governance — Data Access

## Governance

Project: `src/Sections/Humans.Governance`; services under `Services/`,
repository under `Data/`. **DbContext:**
`GovernanceDbContext`. `ApplicationRepository` and
`AssemblyVoteRepository` inject
`IDbContextFactory<GovernanceDbContext>` directly. Owns
`Applications`, `ApplicationStateHistories`, `BoardVotes`,
`AssemblyVotes`, `AssemblyVoteOptions`, `AssemblyVoteRosterEntries`,
`AssemblyBallots`, `AssemblyBallotHistories`, `AssemblyVotePeeks`.

`IApplicationDecisionService` extends `IApplicationServiceRead`; external
readers (`GovernanceIndexService`, `OnboardingService`,
`NotificationMeterProvider`, `AdminDashboardService`)
inject the narrow `IApplicationServiceRead` rather than the full decision
service. `IMembershipCalculator` extends `IMembershipCalculatorRead`.
Cross-section reads inside the section go through the read surfaces
(`IUserServiceRead`, `ITeamServiceRead`, `IConsentServiceRead`).

### ApplicationDecisionService (Scoped)

Repository: `IApplicationRepository`.

| Table | R/W |
|-------|-----|
| Applications | R/W |
| ApplicationStateHistories | R/W |
| BoardVotes | R/W (removed for GDPR after decision) |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `NavBadge:Voting:{userId}` (`IVotingBadgeCacheInvalidator`) | 2 min | yes | yes | yes (per voter, via `IVotingBadgeCacheInvalidator`) |

| Cache (via invalidators) | Invalidate |
|-------------------------|------------|
| `FeedbackBadgeCount` (`INavBadgeCacheInvalidator`) | yes |
| `NotificationMeters` (`INotificationMeterCacheInvalidator`) | yes |

Cross-section calls via `IUserService`, `IRoleAssignmentService`,
`IEmailService`, `IUserEmailService`, `INotificationEmitter`,
`ISystemTeamSync`, `IAuditLogService`, `IHumansMetrics`,
`IEmailMessageFactory`. Implements `IApplicationDecisionService` (which
extends `IApplicationServiceRead`), `IUserDataContributor`, `IUserMerge`.
`EraseForUserAsync` calls `IApplicationRepository.ScrubFreeTextForUserAsync`,
which clears the applicant's own free text (motivation, additional info,
contribution, role understanding) and reviewer prose on `Applications` and
`ApplicationStateHistories`, plus notes on `BoardVotes` the user cast as a
Board member — the tier/status/date skeleton stays (Ley Orgánica 1/2002
Art. 14, GDPR Art. 17(3)(b)).

### AssemblyVoteService (Scoped)

Repository: `IAssemblyVoteRepository`.

| Table | R/W |
|-------|-----|
| AssemblyVotes | R/W |
| AssemblyVoteOptions | R/W (draft only — content is immutable once Open) |
| AssemblyVoteRosterEntries | R/W (written once at open; afterwards only the `NotifiedAt` / `ReminderSentAt` stamps and the erasure tombstone) |
| AssemblyBallots | R/W |
| AssemblyBallotHistories | R (append-only — the repository exposes no update or delete) |
| AssemblyVotePeeks | R/W (insert only) |

No cache, no caching decorator — one vote at a time and ~120 voters.

Cross-section calls via `IUserServiceRead`, `IUserEmailService`,
`IRoleAssignmentService`, `ITeamServiceRead`, `IEmailService`,
`IEmailMessageFactory`, `INotificationEmitter`,
`INotificationAutoResolve`, `IAuditLogService`, `IClock`. Implements
`IAssemblyVoteService`, `IUserDataContributor`, `IUserMerge`. Nothing on
`Humans.Governance.Contracts`: no other section reads votes.

`EraseForUserAsync` nulls the member's `AssemblyVoteRosterEntries.UserId`
and retains the linked ballot and history rows unlinked — the vote is a
legal record of the association (GDPR Art. 17(3)(b) / (e)), and the
tombstone keeps the turnout counts and the stored `ResultJson` valid.
`ReassignAsync` re-FKs roster rows and ballots from source to target; when
both accounts sit on the same roster the target's row wins and the
source's ballot is dropped with an audit entry.

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
Polls `IMembershipCalculatorRead`, `IApplicationServiceRead`,
`IUserServiceRead` on a timer to publish `humans.asociados`,
`humans.applications_pending`, `humans.pending_consents`,
`humans.consent_deadline_approaching`.

### MembershipCalculator (Scoped)

No repository. Pure read computation over `IMembershipQuery`,
`IUserServiceRead`, `ILegalDocumentSyncService`, `IConsentServiceRead`
(resolved lazily via `IServiceProvider` to break a DI cycle), and
`IClock`. Implements `IMembershipCalculator` (which extends
`IMembershipCalculatorRead`). No DB access, no cache.

### MembershipQuery (Scoped)

No repository. Read-only fan-out over `ITeamServiceRead`,
`IRoleAssignmentService`. Exists to break the DI cycle through
`ISystemTeamSync`. No DB access, no cache.

### GovernanceIndexService (Scoped)

No repository. Read-only assembly of the governance index view over
`IApplicationServiceRead`, `ILegalDocumentService`, `IUserServiceRead`.
No DB access, no cache.

---


