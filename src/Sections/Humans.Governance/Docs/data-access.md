# Governance — Data Access

## Governance

Project: `src/Sections/Humans.Governance`; services under `Services/`,
repository under `Data/`. **DbContext:**
`GovernanceDbContext`. `ApplicationRepository` injects
`IDbContextFactory<GovernanceDbContext>` directly. Owns
`Applications`, `ApplicationStateHistories`, `BoardVotes`.

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


