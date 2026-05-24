# Service Data Access Map

Audit of which services access which database tables and cache keys, organized by section.
The goal is to identify cross-section table overlap, duplicated caching, and cache configuration issues.

**Generated:** 2026-05-25

> **Methodology.** Tables are resolved by following each service's injected
> repository interface to its EF-backed implementation in
> `src/Humans.Infrastructure/Repositories/`, then mapping the `DbSet<>`
> properties used to the declarations in
> `src/Humans.Infrastructure/Data/HumansDbContext.cs`. Cache keys come from
> `src/Humans.Application/CacheKeys.cs` and the invalidator extensions in
> `src/Humans.Application/Extensions/MemoryCacheExtensions.cs`. Cross-cutting
> invalidator interfaces (`INavBadgeCacheInvalidator`,
> `INotificationMeterCacheInvalidator`, `IVotingBadgeCacheInvalidator`,
> `IRoleAssignmentClaimsCacheInvalidator`, `IActiveTeamsCacheInvalidator`,
> `ICampLeadJoinRequestsBadgeCacheInvalidator`, `IShiftAuthorizationInvalidator`,
> `IUserInfoInvalidator`, `IShiftViewInvalidator`, `IIssuesBadgeCacheInvalidator`,
> `ICampInfoInvalidator`, `IEventViewInvalidator`, `ILegalDocumentCacheInvalidator`,
> `IConsentCacheInvalidator`, `IRoleAssignmentCacheInvalidator`,
> `ITicketCacheInvalidator`) are resolved via
> `Humans.Infrastructure/Caching/MemoryCacheInvalidators.cs` (for the
> `IMemoryCache`-backed badge/meter set) or to the Singleton caching decorator
> that implements them directly (for the `TrackedCache`-backed read models).
>
> **Caching has moved decisively to Singleton `TrackedCache` decorators.**
> Most read-heavy sections now register a keyed-Scoped inner service plus a
> Singleton `CachingXService` decorator (Infrastructure) that owns the
> canonical projection dictionary and surfaces it on `/Admin/CacheStats` via
> `ICacheStats`. The legacy per-key `IMemoryCache` entries for camps and
> tickets have been retired in favour of these decorators (`MemoryCacheExtensions`
> no longer carries camp/ticket invalidators — see T-06 / T-07). Per-key
> `IMemoryCache` now only backs short-TTL badge/meter/rate-limit values.
>
> At ~500-user single-server scale this map is diagnostic, not gating —
> **cross-section table reads are flagged as design-rule violations per
> [`design-rules.md` §"Services own their data"](design-rules.md)**, but
> serve as a backlog rather than a blocker.

---

## Table of Contents

1. [Profiles](#profiles)
2. [Users](#users)
3. [Onboarding](#onboarding)
4. [Human Lifecycle](#human-lifecycle)
5. [Governance](#governance)
6. [Auth](#auth)
7. [Teams](#teams)
8. [Google Integration](#google-integration)
9. [Camps](#camps)
10. [Containers](#containers)
11. [City Planning](#city-planning)
12. [Calendar](#calendar)
13. [Shifts](#shifts)
14. [Legal](#legal)
15. [Consent](#consent)
16. [Notifications](#notifications)
17. [Tickets](#tickets)
18. [Budget](#budget)
19. [Campaigns](#campaigns)
20. [Email](#email)
21. [Mailer](#mailer)
22. [Feedback](#feedback)
23. [Issues](#issues)
24. [Events (Event Guide)](#events-event-guide)
25. [Expenses](#expenses)
26. [Store](#store)
27. [Agent](#agent)
28. [Search](#search)
29. [Dashboard](#dashboard)
30. [Gdpr](#gdpr)
31. [AuditLog](#auditlog)
32. [Cross-Section Analysis](#cross-section-analysis)
33. [Cache Inventory](#cache-inventory)
34. [Appendix A: Out-of-Service Database Access](#appendix-a-out-of-service-database-access)
35. [Appendix B: Out-of-Service Cache Access](#appendix-b-out-of-service-cache-access)

---

## Profiles

Folder: `src/Humans.Application/Services/Profiles/`. Owns `Profiles`,
`ContactFields`, `ProfileLanguages`, `VolunteerHistoryEntries`,
`UserEmails`, `CommunicationPreferences`, `AccountMergeRequests`. All
Profiles repositories are now registered as **Singletons** (see
`ProfileSectionExtensions`) so `CachingUserService` can inject them
directly without a scope factory. **There is no longer a Profiles caching
decorator** — the old `CachingProfileService` / `FullProfile` dictionary
was retired and its per-user data folded into the `UserInfo` read-model
owned by `CachingUserService` (Users section). Profile mutations evict the
`UserInfo` cache via `IUserInfoInvalidator`.

### ProfileService (Scoped)

Repositories: `IProfileRepository`, `IUserEmailRepository`,
`IContactFieldRepository`, `ICommunicationPreferenceRepository`.

| Table | R/W | Repo |
|-------|-----|------|
| Profiles | R/W | IProfileRepository |
| ProfileLanguages | R/W | IProfileRepository |
| ContactFields | R/W | IProfileRepository (also read via IContactFieldRepository) |
| VolunteerHistoryEntries | R/W | IProfileRepository |
| UserEmails | R | IUserEmailRepository |
| CommunicationPreferences | R | ICommunicationPreferenceRepository |

Cross-section reads via owning interface: `IUserService`. Also uses
`IAuditLogService`, `IFileStorage`. Invalidates the `UserInfo` cache via
`IUserInfoInvalidator`. Implements `IUserDataContributor` (GDPR) and
`IUserMerge` (account-merge fan-out). No `IMemoryCache` injection.

### ContactFieldService (Scoped)

Repositories: `IContactFieldRepository`, `IProfileRepository`.

| Table | R/W |
|-------|-----|
| ContactFields | R/W |
| Profiles | R |

Cross-section reads via `IUserServiceRead`, `ITeamServiceRead`,
`IRoleAssignmentService`. Invalidates the `UserInfo` cache via
`IUserInfoInvalidator`. Implements `IUserMerge`. No `IMemoryCache`.

### CommunicationPreferenceService (Scoped)

Repository: `ICommunicationPreferenceRepository`.

| Table | R/W |
|-------|-----|
| CommunicationPreferences | R/W |

Cross-section reads via `IUserServiceRead`. Uses `IUnsubscribeTokenProvider`
for one-click links and `IAuditLogService`. Implements `IUserMerge`. No
cache.

### UserEmailService (Scoped)

Repository: `IUserEmailRepository`.

| Table | R/W |
|-------|-----|
| UserEmails | R/W |
| Users | R/W (via `IUserEmailRepository`, the only direct EF write to `Users.GoogleEmail` / `Users.GoogleEmailStatus` / `Users.Email`) |

Cross-section calls via `IUserService`, ASP.NET `UserManager<User>`, and
`IServiceProvider` for lazy resolution. Invalidates `UserInfo` via
`IUserInfoInvalidator`. Implements `IUserMerge`. Owns the
`GetNobodiesTeamEmailsByUserIdsAsync` read that previously backed the
`NobodiesTeamEmails_All` `IMemoryCache` entry (now served from the cached
`UserInfo` / repos, no dedicated cache key). No `IMemoryCache` directly.
**Cross-section design-rule note:** the repository's `Users` writes
overlap the User section — the audited bridge for Google email status
updates, intentional per the §15 Profiles design.

### AccountMergeService (Scoped)

Repositories: `IAccountMergeRepository`, `IUserEmailRepository`.

| Table | R/W |
|-------|-----|
| AccountMergeRequests | R/W |
| UserEmails | R/W |

The merge fan-out happens through the `IEnumerable<IUserMerge>` aggregator —
each section's service implements `IUserMerge` and handles its own owned-table
reassignment. Direct collaborators: `IUserService`,
`IRoleAssignmentService`, `INotificationService`, `IAuditLogService`,
plus the cache invalidators `IUserInfoInvalidator`,
`IActiveTeamsCacheInvalidator`, `IConsentCacheInvalidator`. Implements
`IUserDataContributor`.

### DuplicateAccountService (Scoped)

Repositories: `IUserRepository`, `IUserEmailRepository`,
`IProfileRepository`.

| Table | R/W |
|-------|-----|
| Profiles | R/W |
| ContactFields | R/W (via `IProfileRepository`) |
| ProfileLanguages | R/W (via `IProfileRepository`) |
| VolunteerHistoryEntries | R/W (via `IProfileRepository`) |
| UserEmails | R/W |
| Users | R/W |
| EventParticipations | R/W (via `IUserRepository`) |

**Cross-section table writes (design-rule violations):** `Users`,
`EventParticipations` are owned by the User section but written here
directly via `IUserRepository`. Tracked under the §15 "merge
orchestrator" carve-out — long-term should converge with
`AccountMergeService` on the `IUserMerge` aggregator.

Cross-section calls via `IUserService`, `ITeamService`,
`IRoleAssignmentService`, `IAuditLogService`, `IUserInfoInvalidator`.

### EmailProblemsService / AdminHumanListAssembler / PersonSearchFields / PersonSearchMatcher

Read-only DTO assemblers — no repository, no cache. `EmailProblemsService`
fans out over `IUserEmailService`, `IUserService`. The assemblers compose
over `IProfileService`, `IUserService`, `IUserEmailService`,
`IRoleAssignmentService`, `ITeamService`.

---

## Users

Folder: `src/Humans.Application/Services/Users/`. Owns `Users`,
`EventParticipations`, ASP.NET `IdentityUserLogins`. The inner
`IUserService` registration is keyed (`CachingUserService.InnerServiceKey`)
and wrapped by `Humans.Infrastructure.Services.Users.CachingUserService`
(Singleton decorator inheriting `TrackedCache<Guid, UserInfo>`) which holds
the canonical `UserInfo` read-model spanning User + Profile sections.

### UserService (Scoped — wrapped by CachingUserService Singleton decorator)

Repositories: `IUserRepository`, `IUserEmailRepository`,
`IProfileRepository`, `IContactFieldRepository`,
`ICommunicationPreferenceRepository`.

| Table | R/W | Repo |
|-------|-----|------|
| Users | R/W | IUserRepository |
| EventParticipations | R/W | IUserRepository |
| UserEmails | R | IUserEmailRepository |
| Profiles | R | IProfileRepository |
| ContactFields | R | IContactFieldRepository |
| CommunicationPreferences | R | ICommunicationPreferenceRepository |

The five-repo injection composes the `UserInfo` projection — a single
cached read-model fanning out over the User + Profile section
repositories. Invalidates its own cache via `IUserInfoInvalidator`. Uses
`IAdminAuthorizationService`. Implements `IUserDataContributor`,
`IUserMerge`. No direct `IMemoryCache` — caching is in the Singleton
decorator.

### CachingUserService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, UserInfo>` (in-process, no `IMemoryCache`) | Per-User | yes | yes | yes (`IUserInfoInvalidator` — fired by UserService/ProfileService/ContactFieldService/UserEmailService writes and by `IUserMerge` participants) |

Implements `IUserService`, `IUserServiceRead`, `IUserMerge`,
`IUserInfoInvalidator`, `IUserInfoSliceRefresher`, `ICacheStats`, and runs
as an `IHostedService` (warm-on-startup). Surfaced on `/Admin/CacheStats`.

### AccountProvisioningService (Scoped)

Repository: `IUserRepository`.

| Table | R/W |
|-------|-----|
| Users | R/W |
| UserEmails | R/W (via `IUserEmailService`) |
| Profiles | R/W (via `IProfileService`) |

Uses ASP.NET `UserManager<User>` for password and identity primitives.
Cross-section calls via `IUserEmailService`, `IProfileService`,
`IAuditLogService`. No cache.

### AccountDeletionService (Scoped) — `Users/AccountLifecycle/`

No repository. GDPR right-to-deletion orchestrator. Fans out over
`IUserService`, `IUserEmailService`, `ITeamService`,
`IRoleAssignmentService`, `IShiftSignupService`,
`IShiftManagementService`, `IProfileService`, `ITicketQueryService`,
`IAuditLogService`, `IEmailService`. Invalidates
`IRoleAssignmentClaimsCacheInvalidator`,
`IShiftAuthorizationInvalidator`, `IShiftViewInvalidator`. No cache, no
direct DB access — all writes go through owning services.

### UnsubscribeService (Scoped)

Repository: `IUserRepository`.

| Table | R/W |
|-------|-----|
| Users | R |

Reads via `IUserServiceRead`; calls `ICommunicationPreferenceService` to
flip per-category opt-outs; uses `IDataProtectionProvider` for token
validation. No cache.

### UserEmailProviderBackfillService (Scoped)

Repositories: `IUserRepository`, `IUserEmailRepository`.

| Table | R/W |
|-------|-----|
| Users | R |
| UserEmails | R/W |

One-shot backfill — populates `EmailProvider` on legacy `UserEmails`
rows. Uses `UserManager<User>` and `IAuditLogService`. No cache.

### UserParticipationBackfillService (Scoped)

No repository. Fan-out over `IUserService` and `IShiftManagementService`
to backfill `EventParticipations`. No direct DB access, no cache.

---

## Onboarding

Folder: `src/Humans.Application/Services/Onboarding/`. Orchestrator
section — owns no DB tables, holds no `IMemoryCache` injection.

### OnboardingService (Scoped)

No repository injected. Cross-section calls via `IProfileService`,
`IUserServiceRead`, `IApplicationDecisionService`, `IEmailService`,
`INotificationService`, `ISystemTeamSync`, `IMembershipCalculator`,
`IHumansMetrics`. No `IMemoryCache`. State changes flow through the
owning services so cache invalidation happens at the boundary they each
own.

`OnboardingWidgetState` is a value DTO with no behavior.

---

## Human Lifecycle

Folder: `src/Humans.Application/Services/HumanLifecycle/`. Orchestrator —
owns no DB tables. Pairs with `OnboardingService`; the two together
handle suspend/unsuspend/restore state transitions.

### HumanLifecycleService (Scoped)

No repository. Fans out over `IProfileService`, `INotificationService`,
`INotificationInboxService`, `IHumansMetrics`. No direct DB access, no
cache. All `Profile.State` writes go through `IProfileService` which
invalidates the `UserInfo` cache downstream.

---

## Governance

Folder: `src/Humans.Application/Services/Governance/`. Owns
`Applications`, `ApplicationStateHistories`, `BoardVotes`.

### ApplicationDecisionService (Scoped)

Repository: `IApplicationRepository`.

| Table | R/W |
|-------|-----|
| Applications | R/W |
| ApplicationStateHistories | R/W |
| BoardVotes | R/W (removed for GDPR after decision) |

| Cache (via invalidators) | Invalidate |
|-------------------------|------------|
| `NavBadgeCounts` (`INavBadgeCacheInvalidator`) | yes |
| `NotificationMeters` (`INotificationMeterCacheInvalidator`) | yes |
| `NavBadge:Voting:{userId}` (`IVotingBadgeCacheInvalidator`) | yes (per voter) |

Cross-section calls via `IUserServiceRead`, `IProfileService`,
`IRoleAssignmentService`, `IEmailService`, `IUserEmailService`,
`INotificationService`, `ISystemTeamSync`, `IAuditLogService`,
`IHumansMetrics`. Implements `IUserDataContributor`, `IUserMerge`.

### MembershipCalculator (Scoped)

No repository. Pure read computation over `IMembershipQuery`,
`IUserServiceRead`, `ILegalDocumentSyncService`, `IConsentServiceRead`
(resolved lazily via `IServiceProvider` to break a DI cycle), and
`IClock`. No DB access, no cache.

### MembershipQuery (Scoped)

No repository. Read-only pass-through over `ITeamServiceRead`,
`IRoleAssignmentService`. Exists to break the
`MembershipCalculator → ITeamService → ISystemTeamSync →
IMembershipCalculator` DI cycle. No DB access, no cache.

### GovernanceIndexService (Scoped)

No repository. Read-only assembly of the governance index view over
`IApplicationDecisionService`, `ILegalDocumentService`,
`IUserServiceRead`. No DB access, no cache.

---

## Auth

Folder: `src/Humans.Application/Services/Auth/`. Owns `RoleAssignments`.
The inner `IRoleAssignmentService` registration is keyed
(`CachingRoleAssignmentService.InnerServiceKey`) and wrapped by
`Humans.Infrastructure.Services.Auth.CachingRoleAssignmentService`
(Singleton decorator inheriting `TrackedCache<Guid, RoleAssignmentRow>`).

### RoleAssignmentService (Scoped — wrapped by CachingRoleAssignmentService Singleton decorator)

Repository: `IRoleAssignmentRepository`.

| Table | R/W |
|-------|-----|
| RoleAssignments | R/W |

| Cache (via invalidators) | Invalidate |
|-------------------------|------------|
| `NavBadgeCounts` (`INavBadgeCacheInvalidator`) | yes |
| `claims:{userId}` (`IRoleAssignmentClaimsCacheInvalidator`) | yes |
| `TrackedCache<Guid, RoleAssignmentRow>` (`IRoleAssignmentCacheInvalidator`) | yes |

Cross-section calls via `IUserServiceRead`, `ISystemTeamSync`,
`INotificationEmitter`, `IAuditLogService`. Implements
`IUserDataContributor` for GDPR exports and `IUserMerge` for account
merges.

### CachingRoleAssignmentService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, RoleAssignmentRow>` (in-process, no `IMemoryCache`) | Per-User | yes | yes | yes (via `IRoleAssignmentCacheInvalidator`, fired from `RoleAssignmentService` writes) |

Implements `IRoleAssignmentService`, `IRoleAssignmentCacheInvalidator`,
`ICacheStats`, and runs as an `IHostedService` (warm-on-startup).
Surfaced on `/Admin/CacheStats`.

### MagicLinkService (Scoped)

No repository. Uses ASP.NET `UserManager<User>` plus `IUserEmailService`,
`IUserServiceRead`, `IEmailService`, `IMagicLinkRateLimiter`,
`IMagicLinkUrlBuilder`. No direct `IMemoryCache` — rate-limit/replay
sentinels are owned by `IMagicLinkRateLimiter` (Infrastructure) which
writes `magic_link_used:{tokenPrefix}` and
`magic_link_signup:{normalizedEmail}` into `IMemoryCache`.

### AdminAuthorizationService (Scoped)

Repository: `IRoleAssignmentRepository`.

| Table | R/W |
|-------|-----|
| RoleAssignments | R |

Read-only — answers "is this user a board member / coordinator / admin"
for cross-section authorization checks. Uses `ICurrentUserContext`.
Cycle-safe. No cache.

---

## Teams

Folder: `src/Humans.Application/Services/Teams/`. Owns `Teams`,
`TeamMembers`, `TeamJoinRequests`, `TeamJoinRequestStateHistories`,
`TeamRoleAssignments`, `TeamRoleDefinitions`. Also owns the
`GoogleResources` table via `TeamResourceService`, and writes
`GoogleSyncOutboxEvents` atomically on team mutations (cross-section
bridge — Google Integration is the read owner). The inner `ITeamService`
registration is keyed (`CachingTeamService.InnerServiceKey`) and wrapped by
`Humans.Infrastructure.Services.Teams.CachingTeamService` (Singleton
decorator inheriting `TrackedCache<Guid, TeamInfo>`).

### TeamService (Scoped — wrapped by CachingTeamService Singleton decorator)

Repository: `ITeamRepository`.

| Table | R/W |
|-------|-----|
| Teams | R/W |
| TeamMembers | R/W |
| TeamJoinRequests | R/W |
| TeamJoinRequestStateHistories | R/W |
| TeamRoleAssignments | R/W |
| TeamRoleDefinitions | R |
| GoogleSyncOutboxEvents | W (outbox events emitted on team mutations) |

| Cache (via invalidators) | Invalidate |
|-------------------------|------------|
| `NotificationMeters` (`INotificationMeterCacheInvalidator`) | yes |
| `shift-auth:{userId}` (`IShiftAuthorizationInvalidator`) | yes |

Cross-section calls via `IAuditLogService`, `INotificationEmitter`,
`IShiftManagementService`, `IAdminAuthorizationService`, plus
`IServiceProvider` for cycle-breaking. Implements
`IGoogleGroupMembershipSource`, `IUserDataContributor`, `IUserMerge`.

**Cross-section table write (design-rule violation, audited):**
`GoogleSyncOutboxEvents` is owned by the Google Integration section but
written directly by `ITeamRepository.AddOutboxEventAsync` so team
mutations are atomic with their outbox event. Acceptable per the Google
Integration section's outbox design.

### CachingTeamService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, TeamInfo>` (in-process, no `IMemoryCache`) | Per-Entity | yes | yes | yes (via `IActiveTeamsCacheInvalidator` → `InvalidateActiveTeamsCache`, called from `IUserMerge` flows and direct mutation paths) |

Implements `ITeamService`, `ITeamServiceRead`, `IUserMerge`, `ICacheStats`,
and runs as an `IHostedService` (warm-on-startup). Replaces the prior
`ActiveTeams` `IMemoryCache` entry — the `TeamInfo` dictionary is the
canonical source. Surfaced on `/Admin/CacheStats`.

### TeamPageService (Scoped)

No repository. Read-only fan-out over `ITeamService`,
`ITeamResourceService`, `IShiftManagementService`, `IUserServiceRead`.
No DB access, no cache.

### TeamResourceService (Scoped)

Repository: `IGoogleResourceRepository`.

| Table | R/W |
|-------|-----|
| GoogleResources | R/W |

Sole owner of `google_resources`. All consumers call
`ITeamResourceService` read methods rather than touching
`DbSet<GoogleResource>`; ownership is enforced by the
`Architecture.GoogleResourceOwnership` analyzer. Cross-section calls via
`ITeamService`, `ITeamResourceGoogleClient`, `IGoogleDrivePermissionsClient`,
`IAuditLogService`, plus `IServiceProvider` to lazily resolve
`IRoleAssignmentService` (breaks a DI cycle). No cache.

### TeamDirectoryBuilder / TeamPageSummaryMapper

Stateless helpers used by `TeamPageService` — no DI dependencies beyond
plain data shaping.

---

## Google Integration

Folder: `src/Humans.Application/Services/GoogleIntegration/`. Owns
`SyncServiceSettings` and `GoogleSyncOutboxEvents` (the outbox is
appended-to atomically by `TeamRepository`; Google Integration is the
read/process owner). `GoogleResources` is owned by Teams via
`TeamResourceService`. `Users.GoogleEmail` / `GoogleEmailStatus` writes
go through `IUserService` / `IUserEmailService` per §15.

### GoogleWorkspaceSyncService (Scoped)

Repositories: `IGoogleResourceRepository`, `IGoogleSyncOutboxRepository`.

| Table | R/W |
|-------|-----|
| GoogleResources | R/W |
| GoogleSyncOutboxEvents | R/W |

Implements `IGoogleSyncService`. Cross-section calls via `IUserService`,
`ITeamService`, `IUserEmailService`, `IGoogleGroupSync`,
`ISyncSettingsService`, `IAuditLogService`, `IGoogleDirectoryClient`,
`IGoogleDrivePermissionsClient`, `IGoogleGroupProvisioningClient`,
`ITeamResourceGoogleClient`, `IGoogleRemovalNotificationService`. Lazy
`IServiceProvider` resolution for parallel/per-batch scope creation. No
`IMemoryCache`.

### GoogleGroupSyncService (Scoped)

No repository directly — operates over external clients and the
in-process `IEnumerable<IGoogleGroupMembershipSource>` (currently
`TeamService` and `CampRoleService`). Cross-section calls via
`IGoogleGroupMembershipClient`, `IGoogleGroupProvisioningClient`,
`ITeamResourceGoogleClient`, `ITeamResourceService`, `ITeamService`,
`IUserService`, `IUserEmailService`, `ISyncSettingsService`,
`IAuditLogService`, `IGoogleRemovalNotificationService`,
`IGoogleGroupSyncScheduler`. No direct DB access, no cache.

### GoogleAdminService (Scoped)

No repository.

Read-mostly admin facade. Fans out over `IGoogleWorkspaceUserService`,
`IGoogleSyncService`, `ITeamService`, `ITeamResourceService`,
`IUserService`, `IUserEmailService`, `IAuditLogService`. No cache. The
prior direct `IUserEmailRepository` injection has been removed — all
`UserEmails` access now routes through `IUserEmailService`.

### GoogleWorkspaceUserService (Scoped)

No repository. Thin facade over `IWorkspaceUserDirectoryClient`
(Infrastructure). No DB access, no cache.

### EmailProvisioningService (Scoped)

No repository. Wraps `IUserServiceRead`, `IGoogleWorkspaceUserService`,
`IUserEmailService`, `ITeamServiceRead`, `IEmailService`,
`INotificationService`, `IAuditLogService` to provision Google Workspace
mailboxes. No direct DB access, no cache.

### SyncSettingsService (Scoped)

Repository: `ISyncSettingsRepository`.

| Table | R/W |
|-------|-----|
| SyncServiceSettings | R/W |

No cross-section calls, no cache.

### DriveActivityMonitorService (Scoped)

Repository: `IDriveActivityMonitorRepository`.

| Table | R/W |
|-------|-----|
| AuditLogEntries | R/W (via repo) |
| Users | R (via repo) |
| IdentityUserLogins | R (via repo, `Set<IdentityUserLogin<Guid>>`) |
| SystemSettings | R/W (key `DriveActivityMonitor:LastRunAt`) |

**Cross-section reads (design-rule violations):** the
`DriveActivityMonitorRepository` queries `AuditLogEntries`, `Users`, and
ASP.NET `IdentityUserLogins` directly. (`GoogleResources` is no longer
read directly — the service now obtains resources via
`ITeamResourceService`.) The cleanup path is to inject `IAuditLogService`
/ `IUserService` and reduce the repo to its own `SystemSettings` key plus
the audit-write append.

Cross-section calls via `IGoogleDriveActivityClient`,
`ITeamResourceService`. No cache.

### GoogleRemovalNotificationService (Scoped)

No repository. Wraps `IUserEmailService`, `IUserServiceRead`,
`IEmailService` to send notifications when access is removed. No direct
DB access, no cache.

---

## Camps

Folder: `src/Humans.Application/Services/Camps/`. Owns `Camps`,
`CampSeasons`, `CampLeads`, `CampHistoricalNames`, `CampImages`,
`CampSettings`, `CampMembers`, `CampRoleDefinitions`,
`CampRoleAssignments`. The inner `ICampService` registration is keyed
(`CachingCampService.InnerServiceKey`) and wrapped by
`Humans.Infrastructure.Services.Camps.CachingCampService` (Singleton
decorator inheriting `TrackedCache<Guid, CampInfo>` plus a single-slot
`CampSettingsInfo`). **The legacy `camps_year_{year}` / `CampSettings`
`IMemoryCache` keys were retired in T-06** — eviction is now owned by the
decorator and reached through `ICampInfoInvalidator`.

### CampService (Scoped — wrapped by CachingCampService Singleton decorator)

Repositories: `ICampRepository`, `ICampRoleRepository`.

| Table | R/W |
|-------|-----|
| Camps | R/W |
| CampSeasons | R/W |
| CampLeads | R/W |
| CampHistoricalNames | R/W |
| CampImages | R/W |
| CampSettings | R/W |
| CampMembers | R/W |
| CampRoleAssignments | R (via `ICampRepository` for lead resolution) |

| Cache (via invalidators) | Invalidate |
|-------------------------|------------|
| `TrackedCache<Guid, CampInfo>` + `CampSettingsInfo` (`ICampInfoInvalidator`) | yes |
| `NavBadge:CampLeadJoinRequests:{userId}` (`ICampLeadJoinRequestsBadgeCacheInvalidator`) | yes (per lead) |

Cross-section calls via `IUserServiceRead`, `IAuditLogService`,
`ISystemTeamSync`, `IFileStorage`, `INotificationEmitter`, plus
`Lazy<ICampRoleService>` to break a DI cycle. Implements
`IUserDataContributor`, `IUserMerge`.

**Note (former violation resolved):** `CampRepository.GetCampLeadsAsync`
no longer reads `Users` directly — lead identity now comes from
`CampRoleAssignment` / `CampLead` rows, and consumers fan out through
`IUserServiceRead`. The §15 cleanup item for Camp→Users is closed.

### CachingCampService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, CampInfo>` (in-process, no `IMemoryCache`) | Per-Entity | yes | yes | yes (`ICampInfoInvalidator.InvalidateCampAsync`, fired by every write path) |
| `CampSettingsInfo` (single slot, in-process) | Static | yes | yes | yes (`ICampInfoInvalidator.InvalidateSettingsAsync`) |

Year-keyed reads are filtered snapshots, not separate cache entries.
Implements `ICampService`, `ICampServiceRead`, `IUserMerge`,
`ICampInfoInvalidator`, `ICacheStats`, and runs as an `IHostedService`
(warm-on-startup). Surfaced on `/Admin/CacheStats`.

### CampRoleService (Scoped)

Repositories: `ICampRoleRepository`, `ICampRepository`.

| Table | R/W |
|-------|-----|
| CampRoleDefinitions | R/W |
| CampRoleAssignments | R/W |
| CampMembers | R |

Cross-section calls via `ICampService`, `IUserServiceRead`,
`IUserEmailService`, `IAuditLogService`, `INotificationEmitter`, plus
`GoogleWorkspaceOptions`. Implements `IGoogleGroupMembershipSource`. No
`IMemoryCache`.

### CampContactService (Scoped)

No repository. Rate-limited contact relay. Cross-section calls via
`IEmailService`, `IAuditLogService`, `INotificationEmitter`.

| Cache Key | TTL | Type |
|-----------|-----|------|
| `CampContactRateLimit:{userId}:{campId}` | 10 min | Rate limit |

---

## Containers

Folder: `src/Humans.Application/Services/Containers/`. Owns
`Containers`, `ContainerPlacements`.

### ContainerService (Scoped)

Repository: `IContainerRepository`.

| Table | R/W |
|-------|-----|
| Containers | R/W |
| ContainerPlacements | R/W |

Cross-section calls via `ICampServiceRead`, `IAuditLogService`,
`IFileStorage`. No cache.

---

## City Planning

Folder: `src/Humans.Application/Services/CityPlanning/`. Owns
`CityPlanningSettings`, `CampPolygons`, `CampPolygonHistories`.

### CityPlanningService (Scoped)

Repository: `ICityPlanningRepository`.

| Table | R/W |
|-------|-----|
| CityPlanningSettings | R/W |
| CampPolygons | R/W |
| CampPolygonHistories | R/W |

Cross-section calls via `ICampService`, `ITeamServiceRead`,
`IUserServiceRead`. Uses `CityPlanningOptions`. No `IMemoryCache`.

---

## Calendar

Folder: `src/Humans.Application/Services/Calendar/`. Owns
`CalendarEvents`, `CalendarEventExceptions`. The inner `ICalendarService`
registration is keyed (`CachingCalendarService.InnerServiceKey`) and
wrapped by `Humans.Infrastructure.Services.Calendar.CachingCalendarService`
(Singleton decorator inheriting `TrackedCache<Guid, CalendarEventInfo>`).
**The legacy `calendar:active-events` `IMemoryCache` key is retired** —
the decorator holds the canonical event dictionary.

### CalendarService (Scoped — wrapped by CachingCalendarService Singleton decorator)

Repository: `ICalendarRepository`.

| Table | R/W |
|-------|-----|
| CalendarEvents | R/W |
| CalendarEventExceptions | R/W |

Cross-section reads via `ITeamServiceRead` (team-name resolution is done
in the service/decorator, not by an EF join). Uses `IAuditLogService`,
`IClock`.

**Note (former violation resolved):** `CalendarRepository` no longer
joins `Teams` — team names are stitched in memory via `ITeamServiceRead`
inside the caching decorator. The §15 Calendar→Teams item is closed.

### CachingCalendarService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, CalendarEventInfo>` (in-process, no `IMemoryCache`) | Per-Entity | yes | yes | yes (write methods delegate to the inner service then `ReplaceAsync` the touched key) |

Implements `ICalendarService`, `ICalendarServiceRead`, `ICacheStats`, and
runs as an `IHostedService` (warm-on-startup). Resolves team names via
`ITeamServiceRead` per-window. Surfaced on `/Admin/CacheStats`.

---

## Shifts

Folder: `src/Humans.Application/Services/Shifts/`. Owns `Rotas`,
`Shifts`, `ShiftSignups`, `EventSettings`, `GeneralAvailability`,
`VolunteerEventProfiles`, `VolunteerBuildStatuses`, `ShiftTags`,
`VolunteerTagPreferences`.

The Application-layer `ShiftViewService` provides the inner
implementation of `IShiftView`; it is wrapped by
`Humans.Infrastructure.Services.Shifts.CachingShiftViewService`
(Singleton decorator with two `TrackedCache` dictionaries for user and
rota views).

### ShiftManagementService (Scoped)

Repository: `IShiftManagementRepository`.

| Table | R/W |
|-------|-----|
| Rotas | R/W |
| Shifts | R/W |
| ShiftSignups | R |
| EventSettings | R/W |
| VolunteerEventProfiles | R/W |
| ShiftTags | R/W |
| VolunteerTagPreferences | R/W |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `shift-auth:{userId}` | 60 sec | yes | yes | yes (also via `IShiftAuthorizationInvalidator`) |

Cross-section calls via `IAuditLogService`, `IAdminAuthorizationService`,
`IShiftViewInvalidator`, plus `IServiceProvider` for cycle-breaking.
Implements `IShiftAuthorizationInvalidator`, `IUserMerge`. Injects
`IMemoryCache` only as the substrate for the `shift-auth` key.

### BurnSettingsService (Scoped)

Repository: `IShiftManagementRepository`.

| Table | R/W |
|-------|-----|
| EventSettings | R |

Read-only adapter mapping `EventSettings` → `BurnSettingsInfo` at the
section boundary (#719). Provides the active-event configuration to other
sections (notably Events) **without** exposing the Shifts table directly.
No caching (single active row, cold path), no cross-section calls.

### ShiftSignupService (Scoped)

Repository: `IShiftSignupRepository`.

| Table | R/W |
|-------|-----|
| ShiftSignups | R/W |
| Shifts | R (via repo) |
| Rotas | R (via repo) |
| GeneralAvailability | R (via repo, for conflict checks) |
| VolunteerEventProfiles | R/W (via repo) |
| VolunteerTagPreferences | R (via repo) |

Cross-section calls via `IShiftManagementService`,
`IMembershipCalculator`, `IAuditLogService`, `INotificationService`,
`IAdminAuthorizationService`, `IShiftViewInvalidator`,
`IServiceProvider`. Implements `IUserDataContributor`, `IUserMerge`. No
`IMemoryCache`.

**Cross-section table read (in-section note):** the repository reads
`GeneralAvailability` (owned by `IGeneralAvailabilityService` in the same
section) for conflict detection. In-section, but worth flagging because
the two services nominally own different tables.

### GeneralAvailabilityService (Scoped)

Repository: `IGeneralAvailabilityRepository`.

| Table | R/W |
|-------|-----|
| GeneralAvailability | R/W |

Cross-section calls via `IShiftViewInvalidator`. Implements `IUserMerge`.
No `IMemoryCache`.

### VolunteerTrackingService (Scoped)

Repositories: `IVolunteerTrackingRepository`, `IShiftManagementRepository`,
`IGeneralAvailabilityRepository`.

| Table | R/W |
|-------|-----|
| EventSettings | R |
| ShiftSignups | R |
| VolunteerBuildStatuses | R/W |
| Shifts | R |
| Rotas | R |
| GeneralAvailability | R |
| VolunteerEventProfiles | R |
| ShiftTags | R |
| VolunteerTagPreferences | R |

Cross-section calls via `IUserService`, `IShiftViewInvalidator`. No
cache. Holds the gap-detection algorithm + heatmap data assembly.

### VolunteerTrackingExportService (Scoped)

Repository: `IVolunteerTrackingRepository`.

| Table | R/W |
|-------|-----|
| VolunteerBuildStatuses | R |
| ShiftSignups | R |
| EventSettings | R |

Builds the volunteer-tracking export. Stitches team names in memory via
`IShiftManagementService.GetDepartmentsWithRotasAsync` (the repository
deliberately does **not** query `Teams` — see
`memory/architecture/no-cross-section-ef-joins.md`). Cross-section reads
via `IUserServiceRead`. No cache.

### RotaCoordinatorMessageService (Scoped)

Repository: `IShiftSignupRepository`.

| Table | R/W |
|-------|-----|
| ShiftSignups | R |

Coordinator → signed-up volunteers message relay. Cross-section calls via
`IUserServiceRead`, `IEmailService`, `IAuditLogService`. No cache.

### WorkloadService (Scoped)

Repository: `IShiftManagementRepository`.

| Table | R/W |
|-------|-----|
| Rotas | R |
| Shifts | R |
| EventSettings | R |

Read-only workload/coverage assembler. Cross-section calls via
`IShiftView`, `ITeamService`, `IUserServiceRead`. No cache.

### ShiftViewService (Scoped — wrapped by CachingShiftViewService Singleton decorator)

Repositories: `IShiftManagementRepository`, `IShiftSignupRepository`,
`IGeneralAvailabilityRepository`, `IVolunteerTrackingRepository`.

| Table | R/W |
|-------|-----|
| EventSettings | R |
| Rotas | R |
| Shifts | R |
| ShiftSignups | R |
| GeneralAvailability | R |
| VolunteerEventProfiles | R |
| VolunteerBuildStatuses | R |
| ShiftTags | R |
| VolunteerTagPreferences | R |

Implements `IShiftView`. Pure read assembler — composes user + rota
views from the four repositories. Wrapped by `CachingShiftViewService`
which caches both projection types per-entity. Service-keyed as
`"shift-view-inner"` so the decorator can resolve it without
self-recursion.

### CachingShiftViewService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, ShiftUserView>` (in-process, no `IMemoryCache`) | Per-User | yes | yes | yes (via `IShiftViewInvalidator.InvalidateUser`) |
| `TrackedCache<Guid, ShiftRotaView>` (in-process, no `IMemoryCache`) | Per-Entity | yes | yes | yes (via `IShiftViewInvalidator.InvalidateRota`) |

Implements `IShiftView`, `IShiftViewInvalidator`, and runs as an
`IHostedService`. Resolves the inner Scoped `IShiftView` via
`IServiceScopeFactory`. Both cache instances surface `ICacheStats` on
`/Admin/CacheStats`.

### EarlyEntryCapacityCalculator

Stateless calculator — no DI dependencies, no DB access.

---

## Legal

Folder: `src/Humans.Application/Services/Legal/`. Owns `LegalDocuments`,
`DocumentVersions`. The inner `ILegalDocumentSyncService` registration is
keyed (`CachingLegalDocumentSyncService.InnerServiceKey`) and wrapped by
`Humans.Infrastructure.Services.Legal.CachingLegalDocumentSyncService`
(Singleton decorator inheriting `TrackedCache<Guid, LegalDocumentInfo>`).

### LegalDocumentService (Scoped)

No repository (read-through service). Uses `IGitHubLegalDocumentConnector`
+ `IMemoryCache`.

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `Legal:{slug}` | 1 hr | yes | yes | yes |

No DB access. Documents are cached from the GitHub source.

### LegalDocumentSyncService (Scoped — wrapped by CachingLegalDocumentSyncService Singleton decorator)

Repository: `ILegalDocumentRepository`.

| Table | R/W |
|-------|-----|
| LegalDocuments | R/W |
| DocumentVersions | R/W |

Cross-section calls via `INotificationService`, `ITeamService`,
`IUserServiceRead`, `IGitHubLegalDocumentConnector`. Periodic background
sync of legal documents from the legal-internal repo. Cache lives in the
decorator.

### CachingLegalDocumentSyncService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, LegalDocumentInfo>` (in-process, no `IMemoryCache`) | Per-Entity | yes | yes | yes (via `ILegalDocumentCacheInvalidator.InvalidateAll`) |

Implements `ILegalDocumentSyncService`, `ILegalDocumentCacheInvalidator`,
`ICacheStats`, and runs as an `IHostedService` (warm-on-startup).
Surfaced on `/Admin/CacheStats`.

### AdminLegalDocumentService (Scoped)

Repository: `ILegalDocumentRepository`.

| Table | R/W |
|-------|-----|
| LegalDocuments | R/W |
| DocumentVersions | R/W |

Admin-only mutation surface. Cross-section calls via
`ILegalDocumentSyncService`, `ITeamService`. Uses `GitHubSettings`. No
`IMemoryCache`.

---

## Consent

Folder: `src/Humans.Application/Services/Consent/`. Owns `ConsentRecords`.
The inner `IConsentService` registration is keyed
(`CachingConsentService.InnerServiceKey`) and wrapped by
`Humans.Infrastructure.Services.Consent.CachingConsentService` (Singleton
decorator inheriting `TrackedCache<Guid, UserConsentInfo>`).

### ConsentService (Scoped — wrapped by CachingConsentService Singleton decorator)

Repository: `IConsentRepository`.

| Table | R/W |
|-------|-----|
| ConsentRecords | R/W |

Cross-section calls via `ILegalDocumentSyncService`,
`INotificationInboxService`, `ISystemTeamSync`, `IUserServiceRead`,
`IHumansMetrics`, plus `IServiceProvider` for cycle-breaking. Implements
`IUserDataContributor`. No `IMemoryCache` — caching is in the decorator.

### CachingConsentService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, UserConsentInfo>` (in-process, no `IMemoryCache`) | Per-User | yes | yes | yes (via `IConsentCacheInvalidator`, fired by ConsentService writes and `AccountMergeService`) |

Lazy warm (`warmOnStartup: false`). Implements `IConsentService`,
`IConsentServiceRead`, `IConsentCacheInvalidator`, `ICacheStats`, and runs
as an `IHostedService`. Surfaced on `/Admin/CacheStats`.

---

## Notifications

Folder: `src/Humans.Application/Services/Notifications/`. Owns
`Notifications`, `NotificationRecipients`.

### NotificationService (Scoped)

Repository: `INotificationRepository`.

| Table | R/W |
|-------|-----|
| Notifications | R/W |
| NotificationRecipients | R/W |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `NotificationBadge:{userId}` | 2 min | | | yes (on dispatch) |

Cross-section calls via `INotificationEmitter`,
`INotificationRecipientResolver`, `ICommunicationPreferenceService`,
`IClock`. Implements `IUserMerge`. Injects `IMemoryCache` for the badge key.

### NotificationEmitter (Scoped)

Repository: `INotificationRepository`.

| Table | R/W |
|-------|-----|
| Notifications | R/W |
| NotificationRecipients | R/W |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `NotificationBadge:{userId}` | 2 min | | | yes |

Low-level emitter used by `NotificationService` and direct callers
(`TeamService`, `CampService`, `CampRoleService`, `CampContactService`,
`RoleAssignmentService`) that have a single-recipient dispatch already
targeted. Cross-section calls via `ICommunicationPreferenceService`.

### NotificationInboxService (Scoped)

Repository: `INotificationRepository`.

| Table | R/W |
|-------|-----|
| Notifications | R |
| NotificationRecipients | R/W (read state, dismissal) |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `NotificationBadge:{userId}` | 2 min | | | yes (on read/dismiss) |

Cross-section calls via `IUserServiceRead`. Implements
`IUserDataContributor`. Injects `IMemoryCache` for the badge key.

### NotificationMeterProvider (Scoped)

No repository. Pure read-aggregation over owning services.

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `NotificationMeters` | 2 min | yes | yes | (per `INotificationMeterCacheInvalidator` callers) |
| `NavBadge:Voting:{userId}` | 2 min | yes | yes | (per `IVotingBadgeCacheInvalidator`) |
| `NavBadge:CampLeadJoinRequests:{userId}` | 2 min | yes | yes | (per `ICampLeadJoinRequestsBadgeCacheInvalidator`) |

Cross-section calls via `IProfileService`, `IUserServiceRead`,
`IGoogleSyncService`, `ITeamServiceRead`, `ITicketSyncService`,
`IApplicationDecisionService`, `ICampService`. **No direct DB access** —
every counter fans out through an owning-service interface call.

### NotificationRecipientResolver (Scoped)

No repository. Pass-through over `ITeamServiceRead`,
`IRoleAssignmentService`. Exists so `INotificationService` doesn't depend
on those services directly (DI-cycle break). No DB access, no cache.

---

## Tickets

Folder: `src/Humans.Application/Services/Tickets/`. Owns `TicketOrders`,
`TicketAttendees`, `TicketSyncStates`, `TicketTransferRequests`. The
inner `ITicketQueryService` registration is keyed
(`CachingTicketQueryService.InnerServiceKey`) and wrapped by
`Humans.Infrastructure.Services.Tickets.CachingTicketQueryService`
(Singleton decorator owning a per-order `TrackedCache<Guid, TicketOrderInfo>`
plus short-lived per-user `IMemoryCache` entries). **The ticket
invalidation extensions were retired in T-07** — `MemoryCacheExtensions`
no longer carries them; the decorator pokes the per-user keys directly.

### TicketQueryService (Scoped — wrapped by CachingTicketQueryService Singleton decorator)

Repository: `ITicketRepository`.

| Table | R/W |
|-------|-----|
| TicketOrders | R |
| TicketAttendees | R |
| TicketSyncStates | R |
| UserEmails | R (via `ITicketRepository`, materialises a `UserEmail` projection from `Set<UserEmail>`) |

Cross-section calls via `IBudgetService`, `ICampaignService`,
`IUserService`, `IUserEmailService`, `ITeamServiceRead`,
`IShiftManagementService`. Implements `IUserDataContributor`.

**Cross-section table read (design-rule violation):** `TicketRepository`
materialises a `UserEmail` projection for attendee matching.
`IUserEmailService` does not yet expose a bulk lookup; this is the
cleanest single fix to retire the violation.

### CachingTicketQueryService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, TicketOrderInfo>` ("Tickets.Orders", in-process) | Per-Entity | yes | yes | yes (`ITicketCacheInvalidator` — `InvalidateAll` / after transfer / contact-import / user-merge) |
| `UserTicketCount:{userId}` (`IMemoryCache`) | Per-User (5 min) | yes | yes | (TTL + targeted Remove) |
| `UserTicketHoldings:{userId}` (`IMemoryCache`) | Per-User (5 min) | yes | yes | (TTL + targeted Remove) |
| `TicketEventSummary:{eventId}` (`IMemoryCache`) | Per-Entity | | | yes (`InvalidateVendorEventSummary`) |

Implements `ITicketQueryService`, `ITicketCacheInvalidator`,
`ICacheStats`, and runs as an `IHostedService` (warm-on-startup of the
orders dictionary). Surfaced on `/Admin/CacheStats`.

### TicketSyncService (Scoped)

Repositories: `ITicketRepository`, `ITicketTransferRepository`.

| Table | R/W |
|-------|-----|
| TicketOrders | R/W |
| TicketAttendees | R/W |
| TicketSyncStates | R/W |
| TicketTransferRequests | R |

Invalidates ticket caches via `ITicketCacheInvalidator`. Cross-section
calls via `ITicketVendorService`, `IStripeService`, `IUserService`,
`ICampaignService`, `IShiftManagementService`. Uses `TicketVendorSettings`.
Implements `IUserMerge`.

### TicketTransferService (Scoped)

Repositories: `ITicketTransferRepository`, `ITicketRepository`.

| Table | R/W |
|-------|-----|
| TicketOrders | R |
| TicketAttendees | R/W |
| TicketTransferRequests | R/W |

Cross-section calls via `IUserService`, `IUserEmailService`,
`IEmailService`, `IAuditLogService`. No `IMemoryCache`.

### TicketingBudgetService (Scoped)

Repository: `ITicketingBudgetRepository`.

| Table | R/W |
|-------|-----|
| TicketOrders | R |

Cross-section calls via `IBudgetService`. Aggregates ticket revenue
into a budget-side projection. No cache.

### AttendeeContactImportService (Scoped)

Repository: `ITicketRepository`.

| Table | R/W |
|-------|-----|
| TicketAttendees | R |

Cross-section calls via `IUserEmailService`, `IAccountProvisioningService`,
`IUserService`, `IShiftManagementService`, `ITicketQueryService`,
`IAuditLogService`. Imports attendee contact data into the system. No
cache.

### OnsiteRosterService (Scoped)

No repository. Builds the "Who's onsite" roster (#736) — joins
`IUserService.GetOnsiteUsersAsync` output with camp / team /
governance-role names. Pure orchestration over `IUserServiceRead`,
`IShiftManagementService`, `ICampService`, `ITeamServiceRead`,
`IRoleAssignmentService`. No DB access, no cache.

---

## Budget

Folder: `src/Humans.Application/Services/Budget/`. Owns `BudgetYears`,
`BudgetGroups`, `BudgetCategories`, `BudgetLineItems`, `BudgetAuditLogs`,
`TicketingProjections`.

### BudgetService (Scoped)

Repository: `IBudgetRepository`.

| Table | R/W |
|-------|-----|
| BudgetYears | R/W |
| BudgetGroups | R/W |
| BudgetCategories | R/W |
| BudgetLineItems | R/W |
| BudgetAuditLogs | R/W |
| TicketingProjections | R/W |

Cross-section calls via `ITeamService`, `IUserServiceRead`. Implements
`IUserDataContributor`. No `IMemoryCache`.

**Note (former violation resolved):** `BudgetRepository` no longer reads
`Teams` directly — team display names are resolved through
`ITeamService`. The §15 Budget→Teams item is closed.

---

## Campaigns

Folder: `src/Humans.Application/Services/Campaigns/`. Owns `Campaigns`,
`CampaignCodes`, `CampaignGrants`.

### CampaignService (Scoped)

Repository: `ICampaignRepository`.

| Table | R/W |
|-------|-----|
| Campaigns | R/W |
| CampaignCodes | R/W |
| CampaignGrants | R/W |

Cross-section calls via `ITeamServiceRead`, `IUserEmailService`,
`IUserServiceRead`, `INotificationService`, `ICommunicationPreferenceService`,
`IEmailService`, `ITicketVendorService`. Implements `IUserDataContributor`,
`IUserMerge`. No `IMemoryCache`.

---

## Email

Folder: `src/Humans.Application/Services/Email/`. Owns
`EmailOutboxMessages`; owns `SystemSettings` key
`email_outbox_paused`.

### EmailOutboxService (Scoped)

Repository: `IEmailOutboxRepository`.

| Table | R/W |
|-------|-----|
| EmailOutboxMessages | R/W |
| SystemSettings | R/W (only key `email_outbox_paused`) |

No cross-section calls beyond `IClock`. No `IMemoryCache`.

### OutboxEmailService (Scoped)

Repository: `IEmailOutboxRepository`.

| Table | R/W |
|-------|-----|
| EmailOutboxMessages | R/W |

Implements `IEmailService` — the canonical send path. Cross-section
calls via `IUserEmailService`, `IEmailRenderer`, `IEmailBodyComposer`,
`IImmediateOutboxProcessor`, `IHumansMetrics`,
`ICommunicationPreferenceService`. No `IMemoryCache`.

---

## Mailer

Folder: `src/Humans.Application/Services/Mailer/`. No owned DB tables —
MailerLite is the external system; classifier writes through other
sections' services. Audience definitions live under `Mailer/Audiences/`.

### MailerImportService (Scoped)

No repository. Cross-section calls via `IMailerLiteService` (external),
`IUserEmailService`, `IUserServiceRead`, `IAccountProvisioningService`,
`ICommunicationPreferenceService`, `IAuditLogService`. Inbound import
slice — reads MailerLite subscribers and provisions matching accounts.
No DB access, no cache.

### MailerAudienceSyncService (Scoped)

No repository. Cross-section calls via `IMailerLiteService`,
`IUserEmailService`, `IAuditLogService`, plus
`IEnumerable<IMailerAudience>` (audience definitions). Outbound slice —
pushes computed audiences back to MailerLite groups. No DB access, no
cache.

### Audiences (`IMailerAudience` implementations)

All audiences derive from `MailerAudienceBase(IUserServiceRead users)` and
compute membership over cached read surfaces — **no direct DB access, no
cache.** Current members:

| Audience | Reads via |
|----------|-----------|
| `MarketingAudience` | `IUserServiceRead` |
| `MarketingNoTicketAudience` | `IUserServiceRead`, `ITicketQueryService` |
| `HasShiftAudience` | `IShiftView`, `IUserServiceRead` |
| `HasTicketAudience` | `ITicketQueryService`, `IUserServiceRead` |
| `TicketNoShiftsAudience` | `ITicketQueryService`, `IShiftView`, `IUserServiceRead` |

---

## Feedback

Folder: `src/Humans.Application/Services/Feedback/`. Owns
`FeedbackReports`, `FeedbackMessages`.

### FeedbackService (Scoped)

Repository: `IFeedbackRepository`.

| Table | R/W |
|-------|-----|
| FeedbackReports | R/W |
| FeedbackMessages | R/W |

| Cache (via invalidators) | Invalidate |
|-------------------------|------------|
| `NavBadgeCounts` (`INavBadgeCacheInvalidator`) | yes |

Cross-section calls via `IUserService`, `IUserEmailService`,
`ITeamServiceRead`, `IEmailService`, `INotificationService`,
`IAuditLogService`, `IHostEnvironment`. Implements `IUserDataContributor`,
`IUserMerge`.

---

## Issues

Folder: `src/Humans.Application/Services/Issues/`. Owns `Issues`,
`IssueComments`.

### IssuesService (Scoped)

Repository: `IIssuesRepository`.

| Table | R/W |
|-------|-----|
| Issues | R/W |
| IssueComments | R/W |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `NavBadge:Issues:{userId}` (`IIssuesBadgeCacheInvalidator`) | 2 min | yes | yes | yes |
| `NavBadgeCounts` (`INavBadgeCacheInvalidator`) | 2 min | | | yes |

Cross-section calls via `IUserService`, `IUserEmailService`,
`IRoleAssignmentService`, `IEmailService`, `INotificationService`,
`IAuditLogService`, `IHostEnvironment`. Injects `IMemoryCache` as the
substrate for the badge keys. Implements `IUserDataContributor`.

---

## Events (Event Guide)

Folder: `src/Humans.Application/Services/Events/`. Owns `Events`,
`EventGuideSettings`, `EventCategories`, `EventVenues`,
`EventModerationActions`, `EventPreferences`, `EventFavourites`. The inner
`IEventService` registration is keyed (`CachingEventService.InnerServiceKey`)
and wrapped by `Humans.Infrastructure.Services.Events.CachingEventService`
(Singleton decorator owning a `TrackedCache<Guid, ApprovedEventView>`).

### EventService (Scoped — wrapped by CachingEventService Singleton decorator)

Repository: `IEventRepository`.

| Table | R/W |
|-------|-----|
| Events | R/W |
| EventGuideSettings | R/W |
| EventCategories | R/W |
| EventVenues | R/W |
| EventModerationActions | R/W |
| EventPreferences | R/W |
| EventFavourites | R/W |

Cross-section reads via `IBurnSettingsService` (the Shifts-owned read
adapter over `EventSettings`) for active-event scoping. Uses `IClock`.
Implements `IUserDataContributor`. Caching is in the decorator.

**Note (former violation resolved):** `EventRepository` no longer reads
the Shifts `EventSettings` table directly — active-event discovery now
goes through `IBurnSettingsService` (Shifts section). The §15
Events→EventSettings item is closed. (`EventGuideSettings.EventSettingsId`
is a FK column on an Events-owned table, not a read of the Shifts table.)

### CachingEventService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, ApprovedEventView>` ("Event.ApprovedEventView", in-process) | Per-Entity | yes | yes | yes (via `IEventViewInvalidator`; write methods delegate to inner then invalidate the affected slice) |

Lazy warm (`warmOnStartup: false`; the decorator owns cross-cutting
warmup). Implements `IEventService`, `IEventViewInvalidator`,
`ICacheStats`, and runs as an `IHostedService`. Surfaced on
`/Admin/CacheStats`.

---

## Expenses

Folder: `src/Humans.Application/Services/Expenses/`. Owns
`ExpenseReports`, `ExpenseLines`, `ExpenseAttachments`,
`HoldedExpenseOutboxEvents`.

### ExpenseReportService (Scoped)

Repository: `IExpenseRepository`.

| Table | R/W |
|-------|-----|
| ExpenseReports | R/W |
| ExpenseLines | R/W |
| ExpenseAttachments | R/W |
| HoldedExpenseOutboxEvents | R/W (outbox to Holded) |

Cross-section calls via `IFileStorage`, `IBudgetService`, `ITeamService`,
`IUserServiceRead`, `IProfileService`, `IAuditLogService`, `IHoldedClient`
(Infrastructure). Implements `IUserDataContributor`. No `IMemoryCache`.

### SepaPaymentFileBuilder

Stateless builder — formats SEPA XML payment files. No DI dependencies
beyond `SepaConfig` options. No DB access.

---

## Store

Folder: `src/Humans.Application/Services/Store/`. Owns `StoreProducts`,
`StoreOrders`, `StoreOrderLines`, `StorePayments`, `StoreInvoices`,
`StoreTreasurySyncStates`.

### StoreService (Scoped)

Repository: `IStoreRepository`.

| Table | R/W |
|-------|-----|
| StoreProducts | R/W |
| StoreOrders | R/W |
| StoreOrderLines | R/W |
| StorePayments | R/W |
| StoreInvoices | R/W |
| StoreTreasurySyncStates | R/W |

Cross-section calls via `IAuditLogService`, `ICampService`,
`IShiftManagementService`, `IStripeService` (Infrastructure). No
`IMemoryCache`.

### BalanceCalculator

Stateless calculator — no DI dependencies, no DB access.

---

## Agent

Folder: `src/Humans.Application/Services/Agent/` (Application surface)
and `src/Humans.Infrastructure/Services/Agent/` (Infrastructure
adapters). Owns `AgentConversations`, `AgentMessages`, `AgentSettings`.

### AgentService (Scoped, Application)

Repository: `IAgentRepository`.

| Table | R/W |
|-------|-----|
| AgentConversations | R/W |
| AgentMessages | R/W |
| AgentSettings | R (via `IAgentSettingsService`) |

Cross-section calls via `IAgentSettingsService`, `IAgentRateLimitStore`,
`IAgentAbuseDetector`, `IAgentUserSnapshotProvider`,
`IAgentPreloadCorpusBuilder`, `IAgentPromptAssembler`,
`IAgentToolDispatcher`, `IAnthropicClient`. Implements
`IUserDataContributor`. Uses `AnthropicOptions`. No `IMemoryCache`.

### AgentAdminStatusService (Scoped, Application)

Repository: `IAgentRepository`.

| Table | R/W |
|-------|-----|
| AgentConversations | R (status/usage counters) |
| AgentMessages | R |
| AgentSettings | R (via `IAgentSettingsService`) |

Read-only admin status facade. Cross-section calls via
`IAgentSettingsService`, `IAgentRateLimitStore`, `IAgentRetentionRunStore`,
`IAgentAnthropicBalanceProvider`. No cache.

### AgentSettingsService / AgentPromptAssembler / AgentToolDispatcher / AgentUserSnapshotProvider / AgentAbuseDetector (Infrastructure)

Live under `src/Humans.Infrastructure/Services/Agent/`. The settings
service is the only one that touches `AgentSettings` directly (via
`AgentRepository`). The others are stateless adapters or fan-out over
public service interfaces (`ITeamService`, `IUserService`,
`IProfileService`, `IRoleAssignmentService`, `ICampService`, `IShiftView`,
etc.) for the agent's tool-dispatch and user-snapshot surfaces. No
`IMemoryCache`.

### AnthropicClient (Infrastructure)

Outbound API client over `AnthropicOptions`. No DB access, no cache.

---

## Search

Folder: `src/Humans.Application/Services/Search/`. No owned DB tables.

### SearchService (Scoped)

No repository. Pure read-aggregation over `IUserServiceRead`,
`ITeamServiceRead`, `ICampServiceRead`, `IShiftManagementService`,
`IEventService`, plus `IConfiguration`. No DB access, no cache. All
search results come from the cached UserInfo / TeamInfo / CampInfo /
shift / event projections.

---

## Dashboard

Folder: `src/Humans.Application/Services/Dashboard/`. No owned DB tables.

### DashboardService (Scoped)

No repository. Read-only fan-out over `IMembershipCalculator`,
`IApplicationDecisionService`, `IShiftManagementService`, `IShiftView`,
`ITicketQueryService`, `IUserServiceRead`, `ITeamServiceRead`. Uses
`TicketVendorSettings`. No DB access, no cache.

### AdminDashboardService (Scoped)

No repository. Fan-out over `IUserServiceRead`, `IMembershipCalculator`,
`IApplicationDecisionService`, `IShiftManagementService`, `IShiftView`. No
DB access, no cache.

---

## Gdpr

Folder: `src/Humans.Application/Services/Gdpr/`. No owned DB tables —
the export orchestrator runs over per-section `IUserDataContributor`
fan-out.

### GdprExportService (Scoped)

No repository. Injects `IEnumerable<IUserDataContributor>` — every
section that owns per-user tables implements this and contributes its
slice. Current contributors: Profiles (`ProfileService`), Users
(`UserService`), Auth (`RoleAssignmentService`), Governance
(`ApplicationDecisionService`), Camps (`CampService`), Shifts
(`ShiftSignupService`), Tickets (`TicketQueryService`), Notifications
(`NotificationInboxService`), AuditLog (`AuditLogService`), Budget
(`BudgetService`), Campaigns (`CampaignService`), Feedback
(`FeedbackService`), Issues (`IssuesService`), Events (`EventService`),
Expenses (`ExpenseReportService`), Agent (`AgentService`), Teams
(`TeamService`), Consent (`ConsentService`), Profiles merge
(`AccountMergeService`). No direct DB access, no cache.

---

## AuditLog

Folder: `src/Humans.Application/Services/AuditLog/`. Owns
`AuditLogEntries`.

### AuditLogService (Scoped)

Repository: `IAuditLogRepository`.

| Table | R/W |
|-------|-----|
| AuditLogEntries | R/W |

Cross-section calls via `IUserServiceRead`. Implements
`IUserDataContributor`. No `IMemoryCache`.

### AuditViewerService (Scoped)

No repository. Read-only view assembler over `IAuditLogService`,
`IUserServiceRead`, `ITeamService`, `ITeamResourceService`. No DB access,
no cache.

`AuditEvent` and `AuditEventTextualizer` are value types / pure
formatters with no DI dependencies.

---

## Cross-Section Analysis

### Tables Accessed by Multiple Sections (via repository)

After the §15 / `IUserMerge` consolidation and the move to read/write
service splits (`IUserServiceRead`, `ITeamServiceRead`, `ICampServiceRead`,
`IConsentServiceRead`, `ICalendarServiceRead`), most cross-section reads
have been retired. The remaining direct boundary crossings are below.

| Table | Owning Section | Cross-Section Repo Readers/Writers (violations) |
|-------|----------------|------------------------------------------------|
| **Users** | Users | Profiles (`UserEmailRepository` writes `GoogleEmail`/`GoogleEmailStatus`/`Email`; `DuplicateAccountService` via `IUserRepository`), Google Integration (`DriveActivityMonitorRepository` reads `Users`) |
| **EventParticipations** | Users | Profiles (`DuplicateAccountService` via `IUserRepository`) |
| **UserEmails** | Profiles | Tickets (`TicketRepository.GetAllUserEmailLookupEntriesAsync` via `Set<UserEmail>`) |
| **AuditLogEntries** | AuditLog | Google Integration (`DriveActivityMonitorRepository` reads + appends anomalies) |
| **IdentityUserLogins** | Users (ASP.NET Identity) | Google Integration (`DriveActivityMonitorRepository` via `Set<IdentityUserLogin<Guid>>`) |
| **GoogleSyncOutboxEvents** | Google Integration | Teams (`TeamRepository` writes outbox events on team mutations — audited atomicity bridge) |
| **GeneralAvailability** | Shifts (`GeneralAvailabilityService`) | Shifts (`ShiftSignupRepository` reads it for conflict checks) — in-section, service boundary still crossed |

**Resolved since the previous sweep:** Budget→`Teams`, Camp→`Users`,
Calendar→`Teams`, Events→`EventSettings`, and Google Integration→`UserEmails`
(`GoogleAdminService` dropped its `IUserEmailRepository`) are all now
routed through owning-section service interfaces.

### Notable Cross-Section Patterns

1. **`IUserMerge` + read/write splits retired most cross-section
   profile/identity reads.** Account merges fan out over
   `IEnumerable<IUserMerge>`; cross-section reads go through the
   `I*ServiceRead` surfaces. `DuplicateAccountService` still uses direct
   repositories (`IUserRepository`, `IProfileRepository`,
   `IUserEmailRepository`) pending convergence on the same pattern.

2. **Tickets ↔ Profiles email lookup.** `TicketRepository`
   materializes a `UserEmail` projection for attendee matching.
   `IUserEmailService` does not yet expose a bulk lookup; this is the
   cleanest single fix to retire the violation.

3. **Teams ↔ Google outbox.** `TeamRepository` writes
   `GoogleSyncOutboxEvents` so each team mutation is atomic with its
   outbox event. The Google Integration section reads/processes them via
   `IGoogleSyncOutboxRepository`. The atomicity benefit outweighs the
   boundary cost.

4. **DriveActivityMonitor still reaches into Users + AuditLog.**
   `DriveActivityMonitorRepository` reads `Users`, `AuditLogEntries`,
   ASP.NET `IdentityUserLogins`, and its own `SystemSettings`
   (`DriveActivityMonitor:LastRunAt`) key. `GoogleResources` is no longer
   read directly (now via `ITeamResourceService`). The remaining cleanup
   path is to inject `IUserService` / `IAuditLogService`.

5. **SystemSettings has per-key ownership (no single owner service).**
   Each key is owned by the section whose repository accesses it
   (`data-model.md`). The two repositories that touch the table use
   disjoint keys:

   | Key | Owning section | Read by | Written by |
   |-----|----------------|---------|------------|
   | `email_outbox_paused` | Email | `EmailOutboxRepository.GetSendingPausedAsync` | `EmailOutboxRepository.SetSendingPausedAsync` |
   | `DriveActivityMonitor:LastRunAt` | Google Integration | `DriveActivityMonitorRepository` | `DriveActivityMonitorRepository.PersistAnomaliesAsync` |

   There is no cross-section `SystemSettings` access. No
   `ISystemSettingsService` is needed; a third key should be owned by the
   section whose repository reads/writes it.

6. **Singleton `TrackedCache` decorators are now the dominant caching
   pattern.** Ten sections register a Singleton `CachingXService`
   decorator over a keyed-Scoped inner service:
   - `CachingUserService` → `UserInfo` per user (Users + Profiles
     read-model; absorbed the retired `FullProfile` cache).
   - `CachingTeamService` → `TeamInfo` per team (replaces `ActiveTeams`).
   - `CachingCampService` → `CampInfo` per camp + `CampSettingsInfo`
     (replaces `camps_year_{year}` / `CampSettings`).
   - `CachingShiftViewService` → `ShiftUserView` + `ShiftRotaView`.
   - `CachingTicketQueryService` → `TicketOrderInfo` per order (+ per-user
     `IMemoryCache`).
   - `CachingCalendarService` → `CalendarEventInfo` per event (replaces
     `calendar:active-events`).
   - `CachingEventService` → `ApprovedEventView` per event.
   - `CachingLegalDocumentSyncService` → `LegalDocumentInfo` per document.
   - `CachingConsentService` → `UserConsentInfo` per user.
   - `CachingRoleAssignmentService` → `RoleAssignmentRow` per user.
   All are surfaced on `/Admin/CacheStats` via `ICacheStats`, run as
   `IHostedService` warmers, and are evicted through narrow
   `I*Invalidator` interfaces — no per-key `IMemoryCache` coupling in the
   Application layer for these read-models. **Profiles no longer has its
   own decorator** (folded into `UserInfo`).

7. **Notification meters are computed, not queried.**
   `NotificationMeterProvider` reads no tables directly — every counter
   fans out through an owning-service interface call. Cache invalidation
   goes through `INotificationMeterCacheInvalidator`.

8. **HUM analyzers enforce the boundaries at compile time.** Roslyn
   analyzers ratchet the layering rules: `HUM0008` blocks
   `HumansDbContext` in controllers, `HUM0009` blocks `HumansDbContext`
   in Application-layer services. See
   [`code-analysis.md`](code-analysis.md) for the full analyzer list.

---

## Cache Inventory

### All Cache Keys

Per-key `IMemoryCache` entries are sourced from
`src/Humans.Application/CacheKeys.cs` and the invalidator extensions in
`src/Humans.Application/Extensions/MemoryCacheExtensions.cs`. The
`TrackedCache` read-model dictionaries live on the Singleton decorators in
`Humans.Infrastructure/Services/<Section>/` and are surfaced on
`/Admin/CacheStats` via `ICacheStats`.

| Key | TTL | Type | Populated By | Invalidated By |
|-----|-----|------|-------------|----------------|
| `NavBadgeCounts` | 2 min | Static | **NavBadgesViewComponent** | `INavBadgeCacheInvalidator` (FeedbackService, IssuesService, ApplicationDecisionService, RoleAssignmentService) |
| `NotificationBadge:{userId}` | 2 min | Per-User | **NotificationBellViewComponent** | NotificationService, NotificationEmitter, NotificationInboxService |
| `NotificationMeters` | 2 min | Static | NotificationMeterProvider | `INotificationMeterCacheInvalidator` (TeamService, ApplicationDecisionService) |
| `ActiveTeams` | 10 min | Static | _(retired — replaced by `CachingTeamService` `TrackedCache<Guid, TeamInfo>`; key remains in `CacheKeys.Metadata` for invalidator compat)_ | `IActiveTeamsCacheInvalidator` → `ITeamService.InvalidateActiveTeamsCache()` |
| `claims:{userId}` | 60 sec | Per-User | (claims principal factory) | `IRoleAssignmentClaimsCacheInvalidator` (RoleAssignmentService, AccountDeletionService) |
| `shift-auth:{userId}` | 60 sec | Per-User | ShiftManagementService | ShiftManagementService, `IShiftAuthorizationInvalidator` (TeamService, AccountDeletionService) |
| `NavBadge:Voting:{userId}` | 2 min | Per-User | NavBadgesViewComponent, NotificationMeterProvider | `IVotingBadgeCacheInvalidator` (ApplicationDecisionService) |
| `NavBadge:CampLeadJoinRequests:{userId}` | 2 min | Per-User | NotificationMeterProvider | `ICampLeadJoinRequestsBadgeCacheInvalidator` (CampService) |
| `NavBadge:Issues:{userId}` | 2 min | Per-User | IssuesService | `IIssuesBadgeCacheInvalidator` (IssuesService) |
| `Legal:{slug}` | 1 hr | Per-Entity | LegalDocumentService | LegalDocumentService |
| `UserTicketCount:{userId}` | 5 min | Per-User | CachingTicketQueryService | CachingTicketQueryService (targeted Remove) + TTL |
| `UserTicketHoldings:{userId}` | 5 min | Per-User | CachingTicketQueryService | CachingTicketQueryService (targeted Remove) + TTL |
| `TicketEventSummary:{eventId}` | 15 min | Per-Entity | TicketTailorService (Infrastructure) / CachingTicketQueryService | CachingTicketQueryService (`InvalidateVendorEventSummary`) |
| `UserIdsWithTickets` | 5 min | Static | _(reserved in `CacheKeys.Metadata`; per-order data now served from `CachingTicketQueryService` `TrackedCache`)_ | n/a |
| `ValidAttendeeEmails` | 5 min | Static | _(reserved in `CacheKeys.Metadata`; served from orders `TrackedCache`)_ | n/a |
| `TicketDashboardStats` | 5 min | Static | TicketQueryService.GetDashboardStatsAsync (compute — no read-through cache) | n/a (key reserved) |
| `CampContactRateLimit:{userId}:{campId}` | 10 min | Rate Limit | CampContactService | CampContactService |
| `magic_link_used:{tokenPrefix}` | 15 min | Rate Limit | MagicLinkRateLimiter (Infrastructure) | MagicLinkRateLimiter |
| `magic_link_signup:{normalizedEmail}` | 60 sec | Rate Limit | MagicLinkRateLimiter (Infrastructure) | MagicLinkRateLimiter |
| `TrackedCache<Guid, UserInfo>` (CachingUserService) | per-process | Per-User | warm-on-startup + lazy load | `IUserInfoInvalidator` (UserService, ProfileService, ContactFieldService, UserEmailService) + `IUserMerge` fan-out |
| `TrackedCache<Guid, TeamInfo>` (CachingTeamService) | per-process | Per-Entity | warm-on-startup + lazy load | `IActiveTeamsCacheInvalidator` + `IUserMerge` fan-out |
| `TrackedCache<Guid, CampInfo>` + `CampSettingsInfo` (CachingCampService) | per-process | Per-Entity / Static | warm-on-startup + lazy load | `ICampInfoInvalidator` + `IUserMerge` fan-out |
| `TrackedCache<Guid, ShiftUserView>` / `TrackedCache<Guid, ShiftRotaView>` (CachingShiftViewService) | per-process | Per-User / Per-Entity | lazy load | `IShiftViewInvalidator` (ShiftManagementService, ShiftSignupService, GeneralAvailabilityService, VolunteerTrackingService, AccountDeletionService) |
| `TrackedCache<Guid, TicketOrderInfo>` (CachingTicketQueryService) | per-process | Per-Entity | warm-on-startup | `ITicketCacheInvalidator` (TicketSyncService, TicketTransferService, AttendeeContactImportService) |
| `TrackedCache<Guid, CalendarEventInfo>` (CachingCalendarService) | per-process | Per-Entity | warm-on-startup + lazy load | write methods `ReplaceAsync` the touched key |
| `TrackedCache<Guid, ApprovedEventView>` (CachingEventService) | per-process | Per-Entity | lazy warm | `IEventViewInvalidator` |
| `TrackedCache<Guid, LegalDocumentInfo>` (CachingLegalDocumentSyncService) | per-process | Per-Entity | warm-on-startup | `ILegalDocumentCacheInvalidator` |
| `TrackedCache<Guid, UserConsentInfo>` (CachingConsentService) | per-process | Per-User | lazy warm | `IConsentCacheInvalidator` (ConsentService, AccountMergeService) |
| `TrackedCache<Guid, RoleAssignmentRow>` (CachingRoleAssignmentService) | per-process | Per-User | warm-on-startup | `IRoleAssignmentCacheInvalidator` (RoleAssignmentService) |

### Cache Issues / Notes

1. **View components still populate two badge caches** that services
   invalidate. `NavBadgeCounts` (+ `NavBadge:Voting:{userId}`) and
   `NotificationBadge:{userId}` are populated by their respective view
   components (`NavBadgesViewComponent`, `NotificationBellViewComponent`).
   Same backwards pattern as prior sweeps — services know how to
   invalidate but not to recompute.

2. **`UserTicketCount:{userId}` / `UserTicketHoldings:{userId}` rely on
   targeted Remove + TTL.** `CachingTicketQueryService` removes the
   per-user keys on transfer / contact-import / user-merge and otherwise
   lets them expire after 5 min.

3. **`TicketDashboardStats` is compute-only.**
   `TicketQueryService.GetDashboardStatsAsync()` produces the DTO per
   request with no read-through caching. The key remains in
   `CacheKeys.Metadata` for a possible future wrapper.

4. **`NobodiesTeamEmails_All` is fully retired.** The data now flows
   through `IUserEmailService.GetNobodiesTeamEmailsByUserIdsAsync`
   (served from the cached `UserInfo` / repos). The
   `NobodiesEmailBadgeViewComponent` reads through the service and no
   longer touches `IMemoryCache`; the three controller invalidation call
   sites are gone.

5. **Caching decorators live in `Humans.Infrastructure`**, not
   `Humans.Application/Services/`, because they are transparent
   decorators over inner Application-layer services (registered keyed
   `"user-inner"`, `"team-inner"`, `"camp-inner"`, `"shift-view-inner"`,
   `"ticket-query-inner"`, `"calendar-inner"`, `"event-inner"`,
   `"legal-document-sync-inner"`, `"consent-inner"`,
   `"role-assignment-inner"`) and inherit `TrackedCache<TKey, TValue>`
   rather than using `IMemoryCache` for their projection state.

---

## Appendix A: Out-of-Service Database Access

Controllers and view components that inject `HumansDbContext` or
repositories directly, bypassing the service layer. After the `HUM0008`
/ `HUM0009` analyzers shipped and `HumansDbContext` went internal-sealed
(#750), this surface is effectively empty.

### Controllers

Re-audited 2026-05-25. No web controllers inject `HumansDbContext`
directly (the former `DevLoginController` direct-context path is gone —
dev-persona seeding now runs through `DevPersonaSeeder`, which uses
service interfaces and calls `ITeamService.InvalidateActiveTeamsCache()`).
`AdminController` reaches DB diagnostics behind
`IAdminDatabaseDiagnosticsService` / `IAdminDatabaseDiagnosticsRepository`.
All other web controllers go entirely through service interfaces.

### View Components (cache populators)

| Component | Cache Key |
|-----------|-----------|
| **NavBadgesViewComponent** | `NavBadgeCounts` (read/write), `NavBadge:Voting:{userId}` (read/write) |
| **NotificationBellViewComponent** | `NotificationBadge:{userId}` (read/write) |

`NobodiesEmailBadgeViewComponent` no longer populates a cache — it reads
via `IUserService` / `IUserEmailService`. All other view components read
via owning services.

### Background Jobs (Infrastructure)

Jobs live in `Humans.Infrastructure.Jobs` and may use repositories
directly. Mutation-heavy logic funnels into services even from jobs
(e.g. notification cleanup goes via `INotificationRepository` /
`NotificationService`; legal-document sync runs via the cached sync
service and its own repository). Specific jobs and their tables vary;
treat each as an audit item per the section §15 carve-outs.

---

## Appendix B: Out-of-Service Cache Access

Controllers and components that touch `IMemoryCache` directly.

| Controller / Component | Cache Operation | Key |
|------------------------|-----------------|-----|
| **NavBadgesViewComponent** | GetOrCreate | `NavBadgeCounts`, `NavBadge:Voting:{userId}` |
| **NotificationBellViewComponent** | GetOrCreate | `NotificationBadge:{userId}` |

The §15 work has collapsed nearly all out-of-service cache access into the
owning services behind transparent `TrackedCache` decorators. The two
remaining badge view-component populators are the last slice — moving
their recompute logic behind a service interface (so a service both
populates and invalidates) would retire the last out-of-service cache
writes.
