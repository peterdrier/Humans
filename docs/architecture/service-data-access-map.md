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
> `IUserInfoInvalidator`, `IShiftViewInvalidator`,
> `IIssuesBadgeCacheInvalidator`) are resolved via
> `Humans.Infrastructure/Caching/MemoryCacheInvalidators.cs` to the cache keys
> their backing `MemoryCacheExtensions` invalidator hits. Section-decorator
> caches (`TrackedCache<TKey, TValue>` subclasses in
> `src/Humans.Infrastructure/Services/<Section>/Caching*.cs`) are resolved from
> their DI wiring in `src/Humans.Web/Extensions/Sections/*.cs`.
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
`UserEmails`, `CommunicationPreferences`, `AccountMergeRequests`.

> **Change since prior sweep:** the Profiles section no longer ships its own
> caching decorator. `CachingProfileService` and the `FullProfile`
> `TrackedCache` / `IFullProfileInvalidator` seam have been **retired** — all
> denormalized per-user reads now flow through `IUserService.GetUserInfoAsync`
> (the unified User+Profile `UserInfo` read-model owned by `CachingUserService`,
> §15e). Profile-section writes invalidate that read-model via
> `IUserInfoInvalidator.InvalidateAsync`. The Profiles repositories are now
> registered as **Singletons** (`IDbContextFactory` pattern) so the Singleton
> `CachingUserService` can inject them directly.

### ProfileService (Scoped)

Repositories: `IProfileRepository`, `IUserEmailRepository`,
`IContactFieldRepository`, `ICommunicationPreferenceRepository`.

| Table | R/W | Repo |
|-------|-----|------|
| Profiles | R/W | IProfileRepository |
| ContactFields | R/W | IProfileRepository, IContactFieldRepository |
| ProfileLanguages | R/W | IProfileRepository |
| VolunteerHistoryEntries | R/W | IProfileRepository |
| UserEmails | R | IUserEmailRepository |
| CommunicationPreferences | R | ICommunicationPreferenceRepository |

Cross-section reads via `IUserService`, `IAuditLogService`. Implements
`IUserDataContributor` (GDPR) and `IUserMerge` (account-merge fan-out).
No `IMemoryCache` injection. Writes evict the unified User+Profile read-model
via `IUserInfoInvalidator` (replacing the retired `IFullProfileInvalidator`).
Uses `IFileStorage` for profile photos.

### ContactFieldService (Scoped)

Repositories: `IContactFieldRepository`, `IProfileRepository`.

| Table | R/W |
|-------|-----|
| ContactFields | R/W |
| Profiles | R |

Cross-section reads via `ITeamService`, `IRoleAssignmentService`. Holds
request-scoped permission caches (board-member, coordinator, viewer team
ids); no `IMemoryCache`. Invalidates the User+Profile read-model via
`IUserInfoInvalidator`. Implements `IUserMerge`.

### CommunicationPreferenceService (Scoped)

Repository: `ICommunicationPreferenceRepository`.

| Table | R/W |
|-------|-----|
| CommunicationPreferences | R/W |

No cache. Uses `IUnsubscribeTokenProvider` for one-click links. Implements
`IUserMerge`.

### UserEmailService (Scoped)

Repository: `IUserEmailRepository`.

| Table | R/W |
|-------|-----|
| UserEmails | R/W |
| Users | R/W (via `IUserEmailRepository` which holds the only direct EF write to `Users.GoogleEmail` / `Users.GoogleEmailStatus` / `Users.Email`) |

Cross-section calls via `IUserService`, `IUserInfoInvalidator`, plus
ASP.NET `UserManager<User>` and `IServiceProvider` for lazy resolution.
Implements `IUserMerge`. No `IMemoryCache` directly.
**Cross-section design-rule note:** the repository's `Users` writes
overlap the User section — this is the audited bridge for Google email
status updates and is intentional per Profiles §15 design.

### AccountMergeService (Scoped)

Repositories: `IAccountMergeRepository`, `IUserEmailRepository`.

| Table | R/W |
|-------|-----|
| AccountMergeRequests | R/W |
| UserEmails | R/W |

The merge fan-out happens through the `IEnumerable<IUserMerge>` aggregator —
each section's service implements `IUserMerge` and handles its own
owned-table reassignment. Implements `IUserDataContributor`. Direct callers
used: `IUserService`, `IUserInfoInvalidator`, plus the merge aggregator.

Cache: per-section `IUserMerge` implementations invalidate their own
caches; the unified read-model is evicted via `IUserInfoInvalidator` (the
former `IFullProfileInvalidator` is gone).

### DuplicateAccountService (Scoped)

Repositories: `IProfileRepository`, `IUserEmailRepository`,
`IUserRepository`.

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

### AdminHumanListAssembler / EmailProblemsService / PersonSearchFields / PersonSearchMatcher

Read-only DTO assemblers — no repository, no cache. Fan out over
`IProfileService`, `IUserService`, `IUserEmailService`,
`IRoleAssignmentService`, `ITeamService`.

---

## Users

Folder: `src/Humans.Application/Services/Users/`. Owns `Users`,
`UserEmails` cross-bridge (read-through), `EventParticipations`, ASP.NET
`IdentityUserLogins`. The inner `IUserService` registration is wrapped
by `Humans.Infrastructure.Services.Users.CachingUserService` (Singleton
decorator inheriting `TrackedCache<Guid, UserInfo>`) which holds the
canonical `UserInfo` read-model spanning User + Profile sections (added
2026-04-23 via issue #521; consolidated as the sole User+Profile cache
in §15e after `FullProfile` was retired). `CachingUserService` exposes the
budgeted cross-section read surface as `IUserServiceRead`.

### UserService (Scoped — wrapped by CachingUserService Singleton decorator)

Repositories: `IUserRepository`, `IUserEmailRepository`,
`IProfileRepository`, `IContactFieldRepository`,
`ICommunicationPreferenceRepository`.

| Table | R/W | Repo |
|-------|-----|------|
| Users | R/W | IUserRepository |
| UserEmails | R | IUserEmailRepository |
| EventParticipations | R/W | IUserRepository |
| Profiles | R | IProfileRepository |
| ContactFields | R | IContactFieldRepository |
| CommunicationPreferences | R | ICommunicationPreferenceRepository |

The five-repo injection composes the `UserInfo` projection inside
`CachingUserService` — a single cached read-model fanning out from the
inner `UserService` over the User + Profile section repositories.
Implements `IUserDataContributor`, `IUserMerge`.

Cross-section calls via `IAdminAuthorizationService`,
`IUserInfoInvalidator`. No direct `IMemoryCache` — caching is in the
Singleton decorator.

### CachingUserService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, UserInfo>` (`User.UserInfo`, in-process, no `IMemoryCache`) | Per-User | yes | yes | yes (`IUserInfoInvalidator` — fired by UserService/ProfileService writes, by `IUserMerge` participants, and by `UserInfoSaveChangesInterceptor` for Identity-machinery writes) |

Implements `IUserService`, `IUserServiceRead`, `IUserMerge`,
`IUserInfoInvalidator`, and the Infrastructure-internal
`IUserInfoSliceRefresher` (consumed by `UserInfoSaveChangesInterceptor` to
catch OAuth/`UpdateAsync`/`LastLoginAt` writes that bypass the service
surface). Surfaced on `/Admin/CacheStats`.

### AccountProvisioningService (Scoped)

Repositories: `IUserRepository`, `IUserEmailRepository` (via
`IUserEmailService`), `IProfileRepository` (via `IProfileService`).

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
`IShiftManagementService`, `IProfileService`, `ITicketServiceRead`,
`IAuditLogService`, `IEmailService`. Invalidates
`IRoleAssignmentClaimsCacheInvalidator`,
`IShiftAuthorizationInvalidator`, `IShiftViewInvalidator`. No cache, no
direct DB access — all writes go through owning services.

### UnsubscribeService (Scoped)

Repository: `IUserRepository`.

| Table | R/W |
|-------|-----|
| Users | R |

Calls `ICommunicationPreferenceService` to flip per-category opt-outs;
uses `IDataProtectionProvider` for token validation. No cache.

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
`IUserService`, `IApplicationDecisionService`, `IEmailService`,
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
invalidates the unified User+Profile read-model downstream.

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

Cross-section calls via `IUserService`, `IProfileService`,
`IRoleAssignmentService`, `IEmailService`, `IUserEmailService`,
`INotificationService`, `ISystemTeamSync`, `IAuditLogService`,
`IHumansMetrics`. Implements `IUserDataContributor`, `IUserMerge`.

### MembershipCalculator (Scoped)

No repository. Pure read computation over `IProfileService`,
`IMembershipQuery`, `IUserService`, `ILegalDocumentSyncService`,
`IConsentService` (resolved lazily via `IServiceProvider` to break a
DI cycle), and `IClock`. No DB access, no cache.

### MembershipQuery (Scoped)

No repository. Read-only fan-out over `IRoleAssignmentService`,
`ITeamService`. No DB access, no cache.

### GovernanceIndexService (Scoped)

No repository. Read-only assembly of the governance index view over
`IApplicationDecisionService`, `ILegalDocumentService`, `IUserService`.
No DB access, no cache.

---

## Auth

Folder: `src/Humans.Application/Services/Auth/`. Owns `RoleAssignments`.

> **Change since prior sweep:** the inner `IRoleAssignmentService` is now
> wrapped by `Humans.Infrastructure.Services.Auth.CachingRoleAssignmentService`
> (Singleton decorator inheriting `TrackedCache<Guid, RoleAssignmentRow>`,
> issue #749). The full `role_assignments` row set is held in memory so
> cross-section reads (`GetActiveCountsByRoleAsync`, `GetActiveForUserAsync`)
> derive at any clock instant without a query. Invalidation is service-level:
> the inner service's writes call `IRoleAssignmentCacheInvalidator.InvalidateAll()`
> directly (single writer, so no EF interceptor needed).

### RoleAssignmentService (Scoped — wrapped by CachingRoleAssignmentService Singleton decorator)

Repository: `IRoleAssignmentRepository`.

| Table | R/W |
|-------|-----|
| RoleAssignments | R/W |

| Cache (via invalidators) | Invalidate |
|-------------------------|------------|
| `Auth.RoleAssignmentRow` TrackedCache (`IRoleAssignmentCacheInvalidator`) | yes (wholesale flush on write) |
| `NavBadgeCounts` (`INavBadgeCacheInvalidator`) | yes |
| `claims:{userId}` (`IRoleAssignmentClaimsCacheInvalidator`) | yes |

Cross-section calls via `IUserService`, `ISystemTeamSync`,
`IAuditLogService`. Implements `IUserDataContributor` for GDPR exports
and `IUserMerge` for account merges.

### CachingRoleAssignmentService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, RoleAssignmentRow>` (`Auth.RoleAssignmentRow`, in-process, no `IMemoryCache`) | Per-Entity (warmed on startup) | yes | yes | yes (wholesale, via `IRoleAssignmentCacheInvalidator.InvalidateAll`) |

Implements `IRoleAssignmentService`, `IRoleAssignmentCacheInvalidator`.
Resolves the keyed Scoped inner per-call via `IServiceScopeFactory`.
Surfaced on `/Admin/CacheStats`.

### MagicLinkService (Scoped)

No repository. Uses ASP.NET `UserManager<User>` plus `IUserEmailService`,
`IEmailService`, `IMagicLinkRateLimiter`, `IMagicLinkUrlBuilder`,
`IUnsubscribeTokenProvider`. No direct `IMemoryCache` —
rate-limit/replay sentinels are owned by `IMagicLinkRateLimiter`
(Infrastructure) which writes `magic_link_used:{tokenPrefix}` and
`magic_link_signup:{normalizedEmail}` into `IMemoryCache`.

### AdminAuthorizationService (Scoped)

Repository: `IRoleAssignmentRepository`.

| Table | R/W |
|-------|-----|
| RoleAssignments | R |

Read-only — answers "is this user a board member / coordinator / admin"
for cross-section authorization checks. Cycle-safe (does not pull
`IAuthorizationService`). No cache (reads route through the inner repo;
hot reads can migrate to the cached row set incrementally).

---

## Teams

Folder: `src/Humans.Application/Services/Teams/`. Owns `Teams`,
`TeamMembers`, `TeamJoinRequests`, `TeamJoinRequestStateHistories`,
`TeamRoleAssignments`, `TeamRoleDefinitions`. Also owns the
`GoogleResources` table via `TeamResourceService`, and writes
`GoogleSyncOutboxEvents` atomically on team mutations (cross-section
bridge — Google Integration is the read owner). The inner `ITeamService`
registration is wrapped by
`Humans.Infrastructure.Services.Teams.CachingTeamService` (Singleton
decorator inheriting `TrackedCache<Guid, TeamInfo>`); it exposes the
budgeted cross-section read surface as `ITeamServiceRead`.

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
| `TrackedCache<Guid, TeamInfo>` (in-process, no `IMemoryCache`) | Per-Entity | yes | yes | yes (via `IActiveTeamsCacheInvalidator`, called from `IUserMerge` flows and direct mutation paths) |

Implements `ITeamService`, `ITeamServiceRead`, `IUserMerge`. Replaces the
previous `ActiveTeams` `IMemoryCache` entry — the `TeamInfo` dictionary is
the canonical source. Surfaced on `/Admin/CacheStats`.

### TeamPageService (Scoped)

No repository. Read-only fan-out over `ITeamService`,
`IProfileService`, `ITeamResourceService`, `IShiftManagementService`,
`IUserService`. No DB access, no cache.

### TeamResourceService (Scoped)

Repository: `IGoogleResourceRepository`.

| Table | R/W |
|-------|-----|
| GoogleResources | R/W |

Sole owner of `google_resources`. All consumers call
`ITeamResourceService` read methods rather than touching
`DbSet<GoogleResource>`; ownership is enforced by
`scripts/check-google-resource-ownership.sh` and the
`Architecture.GoogleResourceOwnership` analyzer. Cross-section calls via
`ITeamService`, `ITeamResourceGoogleClient`, `IGoogleDrivePermissionsClient`,
`IAuditLogService`, plus `IServiceProvider` to break a DI cycle. No
cache.

### TeamDirectoryBuilder

Stateless helper used by `TeamPageService` — no DI dependencies beyond
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
`ITeamService`, `ITeamResourceService`, `IUserEmailService`,
`ISyncSettingsService`, `IAuditLogService`, `IGoogleDirectoryClient`,
`IGoogleDrivePermissionsClient`, `IGoogleGroupSync` (sync orchestrator),
`ITeamResourceGoogleClient`, `IGoogleRemovalNotificationService`. Lazy
`IServiceProvider` resolution for parallel/per-batch scope creation. No
`IMemoryCache`.

### GoogleGroupSyncService (Scoped)

No repository directly — operates over external clients and the
in-process `IEnumerable<IGoogleGroupMembershipSource>` (currently only
`TeamService`). Cross-section calls via `IGoogleGroupMembershipClient`,
`IGoogleGroupProvisioningClient`, `ITeamResourceGoogleClient`,
`ITeamResourceService`, `ITeamService`, `IUserService`,
`IUserEmailService`, `IProfileService`, `ISyncSettingsService`,
`IAuditLogService`, `IGoogleRemovalNotificationService`,
`IGoogleGroupSyncScheduler`. No direct DB access, no cache.

### GoogleAdminService (Scoped)

Repository: `IUserEmailRepository`.

| Table | R/W |
|-------|-----|
| UserEmails | R/W (via `IUserEmailRepository`) |

Read-mostly admin facade. Fans out over `IGoogleWorkspaceUserService`,
`IGoogleSyncService`, `IUserService`, `IUserEmailService`,
`IAuditLogService`, `IEmailService`. No cache.

**Cross-section reads (design-rule violations):** the `IUserEmailRepository`
injection is direct rather than through `IUserEmailService`. Tracked as
part of the §15 cleanup backlog — preserved while the Google admin tool
surface stabilises.

### GoogleWorkspaceUserService (Scoped)

No repository. Thin facade over `IWorkspaceUserDirectoryClient`
(Infrastructure). No DB access, no cache.

### EmailProvisioningService (Scoped)

No repository. Wraps `IGoogleAdminService` + `IUserEmailService` +
`IAuditLogService` to provision Google Workspace mailboxes. No direct DB
access, no cache.

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
| GoogleResources | R (via repo) |
| AuditLogEntries | R (via repo) |
| Users | R (via repo) |
| SystemSettings | R/W (key `DriveActivityMonitor:LastRunAt`) |

**Cross-section reads (design-rule violations):** the
`DriveActivityMonitorRepository` queries `AuditLogEntries` and `Users`
directly. The cleanup path is to inject `IAuditLogService` /
`IUserService` and reduce the repo to `GoogleResources`-only reads.

Cross-section calls via `IGoogleDriveActivityClient`,
`ITeamResourceService`. No cache.

### GoogleRemovalNotificationService (Scoped)

No repository. Wraps `IUserEmailService` + `IUserService` +
`IEmailService` to send notifications when access is removed. No direct
DB access, no cache.

---

## Camps

Folder: `src/Humans.Application/Services/Camps/`. Owns `Camps`,
`CampSeasons`, `CampLeads`, `CampHistoricalNames`, `CampImages`,
`CampSettings`, `CampMembers`, `CampRoleDefinitions`,
`CampRoleAssignments`.

> **Change since prior sweep:** the inner `ICampService` is now wrapped by
> `Humans.Infrastructure.Services.Camps.CachingCampService` (Singleton
> decorator inheriting `TrackedCache<Guid, CampInfo>` plus a single-slot
> `CampSettingsInfo`). This **replaces** the legacy `camps_year_{year}` and
> `CampSettings` `IMemoryCache` keys — year-keyed reads are now filtered
> snapshots of the warm per-camp dict. Writes delegate to the inner service
> then invalidate via `ICampInfoInvalidator`.

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

| Cache (via invalidators) | Invalidate |
|-------------------------|------------|
| `Camp.CampInfo` TrackedCache + settings slot (`ICampInfoInvalidator`) | yes |
| `NavBadge:CampLeadJoinRequests:{userId}` (`ICampLeadJoinRequestsBadgeCacheInvalidator`) | yes |

**Cross-section table read (design-rule violation):** `ICampRepository`
reads `Users` directly (via `_dbContext.Users.AsNoTracking()` in
`GetCampLeadsAsync`). Tracked as a §15 cleanup item — should fan out
through `IUserService`.

Cross-section calls via `IUserService`, `IAuditLogService`,
`ISystemTeamSync`, `IFileStorage`, `INotificationEmitter`, plus
`Lazy<ICampRoleService>` to break a DI cycle. Implements
`IUserDataContributor`, `IUserMerge`. The inner service no longer touches
`IMemoryCache` directly — all caching lives in the decorator.

### CachingCampService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, CampInfo>` (`Camp.CampInfo`, warmed on startup) | Per-Entity | yes | yes | yes (`ICampInfoInvalidator.InvalidateCampAsync`, wholesale `RefreshAll` for cross-cutting writes) |
| `CampSettingsInfo` single slot (no `IMemoryCache`) | Static | yes | yes | yes (`ICampInfoInvalidator.InvalidateSettingsAsync`) |

Implements `ICampService`, `IUserMerge`, `ICampInfoInvalidator`. Surfaced
on `/Admin/CacheStats`.

### CampRoleService (Scoped)

Repository: `ICampRoleRepository`.

| Table | R/W |
|-------|-----|
| CampRoleDefinitions | R/W |
| CampRoleAssignments | R/W |
| CampMembers | R |
| Camps | R |

Cross-section calls via `ICampService`, `IUserService`,
`IAuditLogService`, `INotificationEmitter`. No `IMemoryCache`.

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

Cross-section calls via `ICampService`, `IAuditLogService`,
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

Cross-section calls via `ICampService`, `ITeamService`, `IUserService`.
Uses `CityPlanningOptions`. No `IMemoryCache`.

---

## Calendar

Folder: `src/Humans.Application/Services/Calendar/`. Owns
`CalendarEvents`, `CalendarEventExceptions`.

> **Change since prior sweep:** the inner `ICalendarService` is now wrapped
> by `Humans.Infrastructure.Services.Calendar.CachingCalendarService`
> (Singleton decorator inheriting `TrackedCache<Guid, CalendarEventInfo>`,
> warmed on startup). This **replaces** the legacy `calendar:active-events`
> `IMemoryCache` key. The decorator exposes the cross-section read surface as
> `ICalendarServiceRead`; writes delegate to the inner service then refresh
> the affected event row.

### CalendarService (Scoped — wrapped by CachingCalendarService Singleton decorator)

Repository: `ICalendarRepository`.

| Table | R/W |
|-------|-----|
| CalendarEvents | R/W |
| CalendarEventExceptions | R/W |
| Teams | R (via `ICalendarRepository` for team-scoped event lookups) |

**Cross-section table read (design-rule violation):** `CalendarRepository`
joins `Teams` directly. The decorator additionally resolves team names via
`ITeamServiceRead` when expanding occurrences; could move the repo join to
`ITeamService` lookup, preserved for query efficiency.

Cross-section calls via `ITeamService`, `IAuditLogService`.

### CachingCalendarService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, CalendarEventInfo>` (`Calendar.Event`, warmed on startup) | Per-Entity | yes | yes | yes (per-event `ReplaceAsync` after each delegated write) |

Implements `ICalendarService`, `ICalendarServiceRead`. Resolves the keyed
Scoped inner per-call; resolves `ITeamServiceRead` for occurrence team
names. Surfaced on `/Admin/CacheStats`.

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
Implements `IShiftAuthorizationInvalidator`, `IUserMerge`.

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

**Cross-section table read (design-rule note):** the repository reads
`GeneralAvailability` (owned by `IGeneralAvailabilityService` in the
same section) for conflict detection. In-section, but worth flagging
because the two services nominally own different tables.

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
which caches both projection types per-entity (per-user view and
per-rota view). Service-keyed as `"shift-view-inner"` so the decorator
can resolve it without self-recursion.

### CachingShiftViewService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, ShiftUserView>` (in-process, no `IMemoryCache`) | Per-User | yes | yes | yes (via `IShiftViewInvalidator.InvalidateUser`) |
| `TrackedCache<Guid, ShiftRotaView>` (in-process, no `IMemoryCache`) | Per-Entity | yes | yes | yes (via `IShiftViewInvalidator.InvalidateRota`) |

Implements `IShiftView`, `IShiftViewInvalidator`. Resolves the inner
Scoped `IShiftView` via `IServiceScopeFactory` to honour scope rules.
Both cache instances are surfaced on `/Admin/CacheStats`.

### EarlyEntryCapacityCalculator

Stateless calculator — no DI dependencies, no DB access.

---

## Legal

Folder: `src/Humans.Application/Services/Legal/`. Owns `LegalDocuments`,
`DocumentVersions`.

> **Change since prior sweep:** the inner `ILegalDocumentSyncService` is now
> wrapped by `Humans.Infrastructure.Services.Legal.CachingLegalDocumentSyncService`
> (Singleton decorator inheriting `TrackedCache<Guid, LegalDocumentInfo>`,
> warmed on startup, with a version-id → document-id index). It caches the
> global active-document set behind the every-page consent-banner read and
> the per-version lookup, invalidated wholesale after any persisted
> `legal_documents` / `document_versions` write via
> `LegalDocumentSaveChangesInterceptor` → `ILegalDocumentCacheInvalidator.InvalidateAll`.

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

Cross-section calls via `INotificationService`, `IUserService`,
`IGitHubLegalDocumentConnector`. The inner service has no `IMemoryCache`;
caching lives in the decorator. Periodic background sync of legal
documents from the legal-internal repo.

### CachingLegalDocumentSyncService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, LegalDocumentInfo>` (`Legal.LegalDocumentInfo`, warmed on startup, + version-id index) | Per-Entity | yes | yes (warm/load) | yes (wholesale via `ILegalDocumentCacheInvalidator.InvalidateAll`, fired by `LegalDocumentSaveChangesInterceptor`) |

Implements `ILegalDocumentSyncService`, `ILegalDocumentCacheInvalidator`.
Surfaced on `/Admin/CacheStats`.

### AdminLegalDocumentService (Scoped)

Repository: `ILegalDocumentRepository`.

| Table | R/W |
|-------|-----|
| LegalDocuments | R/W |
| DocumentVersions | R/W |

Admin-only mutation surface. Cross-section calls via
`ILegalDocumentSyncService`, `ITeamService`. Uses `GitHubSettings`. No
`IMemoryCache`. Writes are picked up by the interceptor-driven cache flush.

---

## Consent

Folder: `src/Humans.Application/Services/Consent/`. Owns `ConsentRecords`.

> **Change since prior sweep:** the inner `IConsentService` is now wrapped by
> `Humans.Infrastructure.Services.Consent.CachingConsentService` (Singleton
> decorator inheriting `TrackedCache<Guid, UserConsentInfo>`, lazy / no
> startup warmup, T-04). It caches the per-user set of consented
> document-version ids (with the account-merge source-id chain resolved at
> load) and **synchronously** evicts the affected user on
> `SubmitConsentAsync` before returning, so the next-page consent-banner
> check never observes a stale "still required" entry. It exposes the
> cross-section read surface as `IConsentServiceRead`.

### ConsentService (Scoped — wrapped by CachingConsentService Singleton decorator)

Repository: `IConsentRepository`.

| Table | R/W |
|-------|-----|
| ConsentRecords | R/W |

Cross-section calls via `IOnboardingService`,
`ILegalDocumentSyncService`, `INotificationInboxService`,
`ISystemTeamSync`, `IUserService`, `IHumansMetrics`, plus
`IServiceProvider` for cycle-breaking. Implements `IUserDataContributor`.
The inner service has no `IMemoryCache`; caching lives in the decorator.

### CachingConsentService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, UserConsentInfo>` (`Consent.UserConsentInfo`, lazy, no warmup) | Per-User | yes (consented-version-set reads) | yes (lazy load) | yes (synchronous per-user evict on submit; via `IConsentCacheInvalidator`) |

Implements `IConsentService`, `IConsentServiceRead`,
`IConsentCacheInvalidator`. Richer record reads (dashboards, history,
record counts) pass through to the inner service. Surfaced on
`/Admin/CacheStats`.

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
`IClock`. Implements `IUserMerge`.

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
(`TeamService`, `CampService`, `CampRoleService`, `CampContactService`)
that have a single-recipient dispatch already targeted. Cross-section
calls via `ICommunicationPreferenceService`.

### NotificationInboxService (Scoped)

Repository: `INotificationRepository`.

| Table | R/W |
|-------|-----|
| Notifications | R |
| NotificationRecipients | R/W (read state, dismissal) |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `NotificationBadge:{userId}` | 2 min | | | yes (on read/dismiss) |

Cross-section calls via `IUserService`. Implements `IUserDataContributor`.

### NotificationMeterProvider (Scoped)

No repository. Pure read-aggregation over owning services.

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `NotificationMeters` | 2 min | yes | yes | (per `INotificationMeterCacheInvalidator` callers) |
| `NavBadge:Voting:{userId}` | 2 min | yes | yes | (per `IVotingBadgeCacheInvalidator`) |
| `NavBadge:CampLeadJoinRequests:{userId}` | 2 min | yes | yes | (per `ICampLeadJoinRequestsBadgeCacheInvalidator`) |

Cross-section calls via `IProfileService`, `IUserService`,
`IGoogleSyncService`, `ITeamService`, `ITicketSyncService`,
`IApplicationDecisionService`, `ICampService`. **No direct DB access** —
every counter fans out through an owning-service interface call.

### NotificationRecipientResolver (Scoped)

No repository. Fan-out over `ITeamService`, `IRoleAssignmentService`.
No DB access, no cache.

---

## Tickets

Folder: `src/Humans.Application/Services/Tickets/`. Owns `TicketOrders`,
`TicketAttendees`, `TicketSyncStates`, `TicketTransferRequests`.

> **Change since prior sweep (PR #744 — "ticket read service and tracked
> caches"):** the read path is now split. `TicketQueryService` is the **inner**
> read service, registered keyed under
> `CachingTicketQueryService.InnerServiceKey` (`"ticket-query-inner"`), and is
> wrapped by the Singleton `CachingTicketQueryService` decorator. The decorator
> is the registered `ITicketService`, the budgeted cross-section
> `ITicketServiceRead`, and the `ITicketCacheInvalidator`. External sections
> inject `ITicketServiceRead` (two-method surface:
> `GetTicketOrdersAsync` + `GetUserTicketHoldingsAsync`) rather than the full
> `ITicketService`. Tickets caching is entirely `TrackedCache`-based now: an
> orders slice (`Tickets.Orders`, warmed on startup) and a user-holdings slice
> (`Tickets.UserHoldings`, lazy with a 5-minute freshness deadline embedded in
> the cached value). The only `IMemoryCache` key the section still uses is
> `TicketEventSummary:{eventId}`.

### TicketQueryService (Scoped, keyed `"ticket-query-inner"` — inner of CachingTicketQueryService)

Repository: `ITicketRepository`.

| Table | R/W |
|-------|-----|
| TicketOrders | R |
| TicketAttendees | R |
| TicketSyncStates | R |
| UserEmails | R (via `ITicketRepository` projections — attendee/email matching for ticket counts) |

The inner service holds no cache — invalidation methods are no-ops on the
inner; `CachingTicketQueryService` intercepts. Cross-section calls via
`IBudgetService`, `ICampaignService`, `IUserService`, `IUserEmailService`,
`ITeamServiceRead` (read-split surface), `IShiftManagementService`.
Implements `IUserDataContributor` (the GDPR contributor is the inner, one
per section).

**Cross-section table read (design-rule violation):** `TicketRepository`
materialises `UserEmail` projections for attendee matching. `IUserEmailService`
does not yet expose a bulk lookup; this is the cleanest single fix to retire
the violation.

### CachingTicketQueryService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, TicketOrderInfo>` (`Tickets.Orders`, warmed on startup) | Per-Entity | yes | yes (warm + lazy) | `ITicketCacheInvalidator` (clear-all on transfer / contact-import / merge / sync) |
| `TrackedCache<Guid, CachedUserTicketHoldings>` (`Tickets.UserHoldings`, lazy, 5-min freshness inside value) | Per-User | yes | yes (lazy load) | `ITicketCacheInvalidator` (per-user evict on transfer/merge; clear-all on contact import) |
| `TicketEventSummary:{eventId}` (`IMemoryCache`) | 15 min | (removed by `InvalidateVendorEventSummary`) | | `ITicketCacheInvalidator.InvalidateVendorEventSummary` |

Implements `ITicketService`, `ITicketServiceRead`, `ITicketCacheInvalidator`,
`IHostedService` (its `StartAsync` warms the orders slice). Resolves the keyed
Scoped inner per-call via `IServiceScopeFactory`. Both `TrackedCache`
instances are surfaced on `/Admin/CacheStats`.
`GetDashboardStatsAsync` is a straight pass-through to the inner (compute-only,
no read-through cache — see `TicketDashboardStats` note in the Cache Inventory).

### TicketSyncService (Scoped)

Repositories: `ITicketRepository`, `ITicketTransferRepository`.

| Table | R/W |
|-------|-----|
| TicketOrders | R/W |
| TicketAttendees | R/W |
| TicketSyncStates | R/W |
| TicketTransferRequests | R |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `TicketEventSummary:{eventId}` (via `ITicketCacheInvalidator.InvalidateVendorEventSummary`) | 15 min | | | yes (per event) |
| `Tickets.Orders` / `Tickets.UserHoldings` tracked slices (via `ITicketCacheInvalidator`) | per-process | | | yes |

Cross-section calls via `ITicketVendorService`, `IStripeService`,
`IUserService`, `ICampaignService`, `IShiftManagementService`,
`ITicketCacheInvalidator`. Implements `ITicketSyncService`, `IUserMerge`.

### TicketTransferService (Scoped)

Repositories: `ITicketRepository`, `ITicketTransferRepository`.

| Table | R/W |
|-------|-----|
| TicketOrders | R |
| TicketAttendees | R/W |
| TicketTransferRequests | R/W |

Cross-section calls via `IUserService`, `IUserEmailService`,
`IEmailService`, `IAuditLogService`. Invalidates ticket caches via
`ITicketCacheInvalidator` (`InvalidateAfterTransfer`). No `IMemoryCache`
directly.

### TicketingBudgetService (Scoped)

Repository: `ITicketingBudgetRepository`.

| Table | R/W |
|-------|-----|
| TicketOrders | R |

Cross-section calls via `IBudgetService`. Aggregates ticket revenue
into a budget-side projection. Implements `ITicketingBudgetService`. No cache.

### AttendeeContactImportService (Scoped)

Repository: `ITicketRepository`.

| Table | R/W |
|-------|-----|
| TicketAttendees | R |

Cross-section calls via `IUserEmailService`, `IAccountProvisioningService`,
`IUserService`, `IShiftManagementService`, `ITicketCacheInvalidator`,
`IAuditLogService`. Imports attendee contact data into the system; clears
ticket caches via `InvalidateAfterContactImport`. No `IMemoryCache` directly.

### OnsiteRosterService (Scoped)

No repository. "Who's onsite" roster orchestrator (#736). Pure read
orchestration over `IUserServiceRead`, `IShiftManagementService`,
`ICampService`, `ITeamServiceRead`, `IRoleAssignmentService`. Implements
`IOnsiteRosterService`, `IApplicationService`. No direct DB access, no cache.

`TicketAttendeeOwnership` is a stateless helper (current-owner predicate),
no DI dependencies.

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
| Teams | R (via `IBudgetRepository` — read-only join for team-scoped budget views) |

**Cross-section table read (design-rule violation):** `BudgetRepository`
reads `Teams` directly. The repository joins `Teams` for display; could
fan out through `ITeamService.GetAsync` but the join is read-only.

Cross-section calls via `ITeamService`, `IUserService`. Implements
`IUserDataContributor`. No `IMemoryCache`.

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

Cross-section calls via `ITeamService`, `IUserEmailService`,
`IUserService`, `INotificationService`, `ICommunicationPreferenceService`,
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
sections' services.

### MailerImportService (Scoped)

No repository. Cross-section calls via `IMailerLiteService` (external),
`IUserEmailService`, `IUserService`, `IAccountProvisioningService`,
`ICommunicationPreferenceService`, `IAuditLogService`. Inbound import
slice — reads MailerLite subscribers and provisions matching accounts.
No DB access, no cache.

### MailerAudienceSyncService (Scoped)

No repository. Cross-section calls via `IMailerLiteService`,
`IUserEmailService`, `IAuditLogService`, plus
`IEnumerable<IMailerAudience>` (audience definitions). Outbound slice —
pushes computed audiences back to MailerLite groups. No DB access, no
cache.

### Audience definitions (`IMailerAudience`)

Audience-membership computation classes under `Mailer/Audiences/`
(`HasShiftAudience`, `MarketingAudience`, `MailerAudienceBase`). No
repository; compute over read-split / section service interfaces
(`ITicketServiceRead`, `IShiftSignupService`, `IShiftManagementService`,
etc.). No direct DB access, no cache.

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
`ITeamService`, `IProfileService`, `IEmailService`,
`INotificationService`, `IAuditLogService`, `IHostEnvironment`.
Implements `IUserDataContributor`, `IUserMerge`. `IMemoryCache` is
injected only as the substrate for `INavBadgeCacheInvalidator`.

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
`IAuditLogService`, `IHostEnvironment`. Implements `IUserDataContributor`.

---

## Events (Event Guide)

Folder: `src/Humans.Application/Services/Events/`. Owns `Events`,
`EventGuideSettings`, `EventCategories`, `EventVenues`,
`EventModerationActions`, `EventPreferences`, `EventFavourites`. (Note:
`EventSettings` is the historical shifts/event configuration table, owned
by Shifts — see "cross-section read" below.)

> **Change since prior sweep (T-03):** the inner `IEventService` is now
> wrapped by `Humans.Infrastructure.Services.Events.CachingEventService`
> (Singleton decorator). It owns four split projections — a per-event
> `TrackedCache<Guid, ApprovedEventView>` (`Event.ApprovedEventView`) plus
> flat snapshots for categories, venues, and the guide-settings singleton.
> Writes delegate to the inner service then invalidate the affected slice
> inline (no `SaveChangesInterceptor` — all `event_*` writes flow through
> `IEventService` by design, enforced by the
> `Only_EventRepository_Writes_Event_DbSets` architecture test).

### EventService (Scoped, keyed `"event-inner"` — inner of CachingEventService)

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
| EventSettings | R (via `IEventRepository.GetActiveCampEventsAsync` / `GetActiveEventSettingsAsync` — read-only lookup of the active Shifts event for event-guide scoping) |

Cross-section calls limited to `IClock`. Implements
`IUserDataContributor`. The inner service has no `IMemoryCache`.

**Cross-section table read (design-rule violation, audited):**
`EventRepository` reads `EventSettings` (owned by Shifts) for active-event
discovery. Could route through `IShiftManagementService.GetActiveAsync`
to retire the boundary crossing.

### CachingEventService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, ApprovedEventView>` (`Event.ApprovedEventView`) | Per-Entity | yes | yes | yes (per-slice, inline after delegated write; via `IEventViewInvalidator`) |
| Flat `EventCategoryView` list | Static | yes | yes | yes |
| Flat `EventVenueView` list | Static | yes | yes | yes |
| `EventGuideSettingsView` singleton | Static | yes | yes | yes |

Implements `IEventService`, `IEventViewInvalidator`, `IHostedService`
(`StartAsync` warms all four projections). The moderator-only
`GetAllEventsForDashboardAsync` passes through to the inner service (needs a
fresh pending count; the cache only holds approved events). Only the event
projection is surfaced on `/Admin/CacheStats`.

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
`IUserService`, `IProfileService`, `IAuditLogService`, `IHoldedClient`
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

### AgentSettingsService / AgentPromptAssembler / AgentToolDispatcher / AgentUserSnapshotProvider / AgentAbuseDetector (Infrastructure)

Live under `src/Humans.Infrastructure/Services/Agent/`. The settings
service is the only one that touches `AgentSettings` directly (via
`AgentRepository.GetAgentSettingsAsync` / `UpsertAgentSettingsAsync`).
The others are stateless adapters or fan-out over public service
interfaces (`ITeamService`, `IUserService`, `IProfileService`,
`IRoleAssignmentService`, `ICampService`, `IShiftView`, etc.) for the
agent's tool-dispatch and user-snapshot surfaces. No `IMemoryCache`.

### AnthropicClient (Infrastructure)

Outbound API client over `AnthropicOptions`. No DB access, no cache.

---

## Search

Folder: `src/Humans.Application/Services/Search/`. No owned DB tables.

### SearchService (Scoped)

No repository. Pure read-aggregation over `IProfileService`,
`ITeamService`, `ICampService`, `IShiftManagementService`. No DB access,
no cache. All search results come from the cached UserInfo / TeamInfo /
camp / shift projections.

---

## Dashboard

Folder: `src/Humans.Application/Services/Dashboard/`. No owned DB tables.

### DashboardService (Scoped)

No repository. Read-only fan-out over `IProfileService`,
`IMembershipCalculator`, `IApplicationDecisionService`,
`IShiftManagementService`, `IShiftView`, `ITicketServiceRead`,
`IUserService`, `ITeamService`. Uses `TicketVendorSettings`. No DB
access, no cache.

### AdminDashboardService (Scoped)

No repository. Fan-out over `IUserService`, `IMembershipCalculator`,
`IApplicationDecisionService`. No DB access, no cache.

---

## Gdpr

Folder: `src/Humans.Application/Services/Gdpr/`. No owned DB tables —
the export orchestrator runs over per-section `IUserDataContributor`
fan-out.

### GdprExportService (Scoped)

No repository. Injects `IEnumerable<IUserDataContributor>` — every
section that owns per-user tables implements this and contributes its
slice. Current contributors (per design-rules §8a): Profiles
(`ProfileService`), Users (`UserService`), Auth (`RoleAssignmentService`),
Governance (`ApplicationDecisionService`), Camps (`CampService`), Shifts
(`ShiftSignupService`), Tickets (`TicketQueryService` — the keyed inner),
Notifications (`NotificationInboxService`), AuditLog (`AuditLogService`),
Budget (`BudgetService`), Campaigns (`CampaignService`), Feedback
(`FeedbackService`), Issues (`IssuesService`), Events (`EventService`),
Expenses (`ExpenseReportService`), Agent (`AgentService`), Teams
(`TeamService`), Consent (`ConsentService`). No direct DB access, no cache.

---

## AuditLog

Folder: `src/Humans.Application/Services/AuditLog/`. Owns
`AuditLogEntries`.

### AuditLogService (Scoped)

Repository: `IAuditLogRepository`.

| Table | R/W |
|-------|-----|
| AuditLogEntries | R/W |

Cross-section calls via `IUserService`. Implements `IUserDataContributor`.
No `IMemoryCache`.

### AuditViewerService (Scoped)

No repository. Read-only view assembler over `IAuditLogService`,
`IProfileService`, `ITeamService`, `ITeamResourceService`. No DB access,
no cache.

`AuditEvent` and `AuditEventTextualizer` are value types / pure
formatters with no DI dependencies.

---

## Cross-Section Analysis

### Tables Accessed by Multiple Sections (via repository)

After the §15 / `IUserMerge` consolidation, only a handful of
repositories still reach across boundaries directly. These are the
remaining design-rule violations.

| Table | Owning Section | Cross-Section Repo Readers (violations) |
|-------|----------------|-----------------------------------------|
| **Users** | Users | Profiles (`UserEmailRepository` writes `GoogleEmail`/`GoogleEmailStatus`/`Email`; `DuplicateAccountService` via `IUserRepository`), Google Integration (`DriveActivityMonitorRepository`), Camps (`CampRepository.GetCampLeadsAsync`) |
| **UserEmails** | Profiles | Google Integration (`GoogleAdminService` via `IUserEmailRepository`), Tickets (`TicketRepository` `UserEmail` projections for attendee matching) |
| **EventParticipations** | Users | Profiles (`DuplicateAccountService` via `IUserRepository`) |
| **AuditLogEntries** | AuditLog | Google Integration (`DriveActivityMonitorRepository`) |
| **GoogleSyncOutboxEvents** | Google Integration | Teams (`TeamRepository` writes outbox events on team mutations) |
| **GeneralAvailability** | Shifts (`GeneralAvailabilityService`) | Shifts (`ShiftSignupRepository` reads it for conflict checks) — in-section, but service boundary still crossed |
| **Teams** | Teams | Budget (`BudgetRepository`), Calendar (`CalendarRepository`), Google Integration (`GoogleResourceRepository`) |
| **EventSettings** | Shifts | Events (`EventRepository.GetActiveCampEventsAsync` / `GetActiveEventSettingsAsync`) |
| **SystemSettings** | per-key (see SystemSettings ownership table below) | not a violation when the owning section's repo touches its own key |

### Notable Cross-Section Patterns

1. **`IUserMerge` retired most cross-section profile/identity writes.**
   `AccountMergeService` no longer injects `IUserRepository` /
   `IProfileRepository` directly — it fans out over
   `IEnumerable<IUserMerge>`, with each section's service implementing
   `IUserMerge` to reassign its own owned rows. `DuplicateAccountService`
   still uses direct repositories pending convergence on the same pattern.

2. **Read/write surface split (read-split interfaces).** Several sections
   now expose a budgeted cross-section read interface that external sections
   inject instead of the full service: `IUserServiceRead`,
   `ITeamServiceRead`, `ICalendarServiceRead`, `IConsentServiceRead`, and
   `ITicketServiceRead` (PR #744). These are the Singleton caching
   decorators re-cast to a narrow surface, keeping the cross-section
   coupling minimal and `[SurfaceBudget]`-bounded.

3. **Tickets ↔ Profiles email lookup.** `TicketRepository`
   materializes `UserEmail` projections for attendee matching.
   `IUserEmailService` does not yet expose a bulk lookup; this is the
   cleanest single fix to retire the violation.

4. **Teams ↔ Google outbox.** `TeamRepository` writes
   `GoogleSyncOutboxEvents` so each team mutation is atomic with its
   outbox event. The Google Integration section reads/processes them
   via `IGoogleSyncOutboxRepository`. The atomicity benefit outweighs
   the boundary cost.

5. **DriveActivityMonitor reaches into three sections.**
   `DriveActivityMonitorRepository` reads `Users`, `AuditLogEntries`,
   `SystemSettings` (its own `DriveActivityMonitor:LastRunAt` key). The
   cleanup path is to inject `IUserService` / `IAuditLogService` and
   reduce the repo to `GoogleResources` + `SystemSettings`-only writes.

6. **SystemSettings has per-key ownership (no single owner service).**
   Each key is owned by the section whose repository accesses it; this
   is the established convention per [`data-model.md`](data-model.md)
   ("Each key belongs to its consuming section's repository"). Two
   repositories touch the table today and they touch disjoint keys:

   | Key | Owning section | Read by | Written by |
   |-----|----------------|---------|------------|
   | `email_outbox_paused` | Email | `EmailOutboxRepository.GetSendingPausedAsync` | `EmailOutboxRepository.SetSendingPausedAsync` |
   | `DriveActivityMonitor:LastRunAt` | Google Integration | `DriveActivityMonitorRepository.GetLastRunTimestampAsync` | `DriveActivityMonitorRepository.PersistAnomaliesAsync` |

   Because `EmailOutboxRepository` only reads/writes its own
   Email-owned key and `DriveActivityMonitorRepository` only
   reads/writes its own Google-owned key, there is no cross-section
   `SystemSettings` access. No `ISystemSettingsService` is needed; if a
   third key is added, it should be owned by the section whose
   repository reads/writes it.

7. **Cached read-models have displaced almost all per-key `IMemoryCache`
   entries.** Singleton decorators inheriting `TrackedCache<TKey, TValue>`
   now own the canonical projections across most sections:
   - `CachingUserService` → `UserInfo` per user (Users + Profiles
     unified read-model; the old `FullProfile` cache and
     `CachingProfileService` were **retired** in §15e).
   - `CachingTeamService` → `TeamInfo` per team (replaced `ActiveTeams`).
   - `CachingShiftViewService` → `ShiftUserView` + `ShiftRotaView`.
   - `CachingTicketQueryService` → `Tickets.Orders` + `Tickets.UserHoldings`
     (PR #744).
   - `CachingCampService` → `CampInfo` per camp + settings slot
     (replaced `camps_year_{year}` / `CampSettings`).
   - `CachingCalendarService` → `CalendarEventInfo` per event
     (replaced `calendar:active-events`).
   - `CachingEventService` → `ApprovedEventView` + category/venue/settings
     snapshots.
   - `CachingConsentService` → `UserConsentInfo` per user.
   - `CachingLegalDocumentSyncService` → `LegalDocumentInfo` per document.
   - `CachingRoleAssignmentService` → `RoleAssignmentRow` set (issue #749).
   All are surfaced on `/Admin/CacheStats` via `ICacheStats` and evicted
   through narrow `I*Invalidator` interfaces (or EF
   `SaveChangesInterceptor`s for Legal / User-Identity writes) — no direct
   `IMemoryCache` coupling in the Application layer.

8. **Notification meters are computed, not queried.**
   `NotificationMeterProvider` reads no tables directly — every counter
   fans out through an owning-service interface call (`IProfileService`,
   `IUserService`, `ITeamService`, `IApplicationDecisionService`,
   `ITicketSyncService`, `IGoogleSyncService`, `ICampService`). Cache
   invalidation goes through `INotificationMeterCacheInvalidator`.

9. **HUM analyzers enforce the boundaries at compile time.** Roslyn
   analyzers ratchet the layering rules: `HUM0008` blocks
   `HumansDbContext` in controllers, `HUM0009` blocks `HumansDbContext`
   in Application-layer services. See
   [`code-analysis.md`](code-analysis.md) for the full analyzer list.

---

## Cache Inventory

### All Cache Keys

Sourced from `src/Humans.Application/CacheKeys.cs` and
`src/Humans.Application/Extensions/MemoryCacheExtensions.cs`. TTL/type
classification mirrors `CacheKeys.Metadata` (surfaced on the Admin
`/Admin/CacheStats` page). Note: most section projections are now
`TrackedCache` dictionaries (not `IMemoryCache` keys) and are listed
separately below the key table.

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
| `Legal:{slug}` | 1 hr | Per-Entity | LegalDocumentService (GitHub-source read-through) | LegalDocumentService |
| `TicketEventSummary:{eventId}` | 15 min | Per-Entity | TicketTailorService (Infrastructure) / TicketSyncService | TicketSyncService, `ITicketCacheInvalidator.InvalidateVendorEventSummary` |
| `TicketDashboardStats` | 5 min | Static | TicketQueryService.GetDashboardStatsAsync (compute — no read-through cache; key reserved for future wrapper) | (reserved cache-stats key) |
| `NobodiesTeamEmails_All` | 2 min | Static | **NobodiesEmailBadgeViewComponent** | TeamAdminController, GoogleController, ProfileController |
| `CampContactRateLimit:{userId}:{campId}` | 10 min | Rate Limit | CampContactService | CampContactService |
| `magic_link_used:{tokenPrefix}` | 15 min | Rate Limit | MagicLinkRateLimiter (Infrastructure) | MagicLinkRateLimiter |
| `magic_link_signup:{normalizedEmail}` | 60 sec | Rate Limit | MagicLinkRateLimiter (Infrastructure) | MagicLinkRateLimiter |

> **Retired `IMemoryCache` keys** (now `TrackedCache` projections):
> `camps_year_{year}` and `CampSettings` (→ `CachingCampService`),
> `calendar:active-events` (→ `CachingCalendarService`). These were removed
> from `CacheKeys.cs` / `CacheKeys.Metadata`.

### Section Decorator Caches (`TrackedCache`, not `IMemoryCache`)

| Cache | Section | Key | Type | Populated By | Invalidated By |
|-------|---------|-----|------|-------------|----------------|
| `TrackedCache<Guid, UserInfo>` | Users (+Profiles) | `User.UserInfo` | Per-User | CachingUserService warmup + lazy load | `IUserInfoInvalidator` + `IUserMerge` + `UserInfoSaveChangesInterceptor` |
| `TrackedCache<Guid, TeamInfo>` | Teams | (TeamInfo dict) | Per-Entity | CachingTeamService lazy load | `IActiveTeamsCacheInvalidator` + `IUserMerge` |
| `TrackedCache<Guid, ShiftUserView>` / `TrackedCache<Guid, ShiftRotaView>` | Shifts | (ShiftView dicts) | Per-User / Per-Entity | CachingShiftViewService lazy load | `IShiftViewInvalidator` (ShiftManagementService, ShiftSignupService, GeneralAvailabilityService, VolunteerTrackingService, AccountDeletionService) |
| `TrackedCache<Guid, TicketOrderInfo>` | Tickets | `Tickets.Orders` | Per-Entity | CachingTicketQueryService warmup + lazy load | `ITicketCacheInvalidator` |
| `TrackedCache<Guid, CachedUserTicketHoldings>` | Tickets | `Tickets.UserHoldings` | Per-User (5-min freshness in value) | CachingTicketQueryService lazy load | `ITicketCacheInvalidator` |
| `TrackedCache<Guid, CampInfo>` + settings slot | Camps | `Camp.CampInfo` | Per-Entity / Static | CachingCampService warmup + lazy load | `ICampInfoInvalidator` + `IUserMerge` |
| `TrackedCache<Guid, CalendarEventInfo>` | Calendar | `Calendar.Event` | Per-Entity | CachingCalendarService warmup + lazy load | per-event `ReplaceAsync` after delegated write |
| `TrackedCache<Guid, ApprovedEventView>` + category/venue/settings snapshots | Events | `Event.ApprovedEventView` | Per-Entity / Static | CachingEventService warmup + lazy load | `IEventViewInvalidator` (inline per write) |
| `TrackedCache<Guid, UserConsentInfo>` | Consent | `Consent.UserConsentInfo` | Per-User | CachingConsentService lazy load | `IConsentCacheInvalidator` (synchronous per-user evict on submit) |
| `TrackedCache<Guid, LegalDocumentInfo>` | Legal | `Legal.LegalDocumentInfo` | Per-Entity | CachingLegalDocumentSyncService warmup + lazy load | `ILegalDocumentCacheInvalidator.InvalidateAll` (via `LegalDocumentSaveChangesInterceptor`) |
| `TrackedCache<Guid, RoleAssignmentRow>` | Auth | `Auth.RoleAssignmentRow` | Per-Entity | CachingRoleAssignmentService warmup + lazy load | `IRoleAssignmentCacheInvalidator.InvalidateAll` (service-level) |

### Cache Issues / Notes

1. **View components still populate three caches** that services
   invalidate. `NavBadgeCounts`, `NotificationBadge:{userId}`, and
   `NobodiesTeamEmails_All` are populated by their respective view
   components. This is the same backwards pattern called out in prior
   sweeps — services know how to invalidate but not to recompute.

2. **Ticket user holdings are tracked, not `IMemoryCache` keys.**
   `CachingTicketQueryService` keeps user holdings in `Tickets.UserHoldings`,
   a `TrackedCache` keyed by user id. Transfer, contact import, account merge,
   and full ticket sync paths clear the affected tracked entries through
   `ITicketCacheInvalidator`; stale entries also reload after the 5-minute
   freshness deadline stored in the tracked value. (PR #744.)

3. **`TicketDashboardStats` is invalidation-only, not read-through.**
   `TicketQueryService.GetDashboardStatsAsync()` is the canonical
   producer of the `TicketDashboardStats` DTO — invoked directly by
   `TicketController.Index` per request (passing through the decorator), with
   no read-through caching. The cache key (`CacheKeys.TicketDashboardStats`)
   is kept so a future caching wrapper can be added without changing the
   cache-stats classification.

4. **`NobodiesTeamEmails_All`** is populated by
   `NobodiesEmailBadgeViewComponent` and invalidated by three
   controllers (`TeamAdminController`, `GoogleController`,
   `ProfileController`). No service involvement — should move into the
   owning section behind a service interface.

5. **Caching decorators live in `Humans.Infrastructure`**, not
   `Humans.Application/Services/`, because they are transparent
   decorators over inner Application-layer services (registered keyed
   `"user-inner"`, `"team-inner"`, `"shift-view-inner"`,
   `"ticket-query-inner"`, `"camp-inner"`, `"calendar-inner"`,
   `"event-inner"`, `"role-assignment-inner"`, `"legal-document-sync-inner"`,
   plus the Consent inner key) and inherit `TrackedCache<TKey, TValue>`
   rather than using `IMemoryCache` for their projection state. The
   `FullProfile` decorator (`CachingProfileService`) was removed; Profiles
   no longer ships a decorator.

---

## Appendix A: Out-of-Service Database Access

Controllers and view components that inject `HumansDbContext` or
repositories directly, bypassing the service layer. After the
`HUM0008` / `HUM0009` analyzers shipped (PR #493, #494), this surface
shrank to a single dev-only path.

### Controllers

Controllers with direct `HumansDbContext` / repository injection:

| Controller | Notes |
|------------|-------|
| **DevLoginController** | Injects `HumansDbContext`. Camps / CampSeasons / CampLeads seeding for dev personas. Legitimate dev-only path; the controller is conditionally registered only when `DevAuth__Enabled=true`. |

`AdminController` is no longer in this list — its previous direct DB
reads moved behind `IAdminDatabaseDiagnosticsService` (PR #494). All
other web controllers (Email, Google, Profile, Board, Budget,
CampAdmin, Guest, Unsubscribe, TeamAdmin, ShiftAdmin, Calendar,
Feedback, Issues, Tickets, etc.) go entirely through service
interfaces. Some controllers still touch `IMemoryCache` directly for
the `NobodiesTeamEmails_All` invalidation — that's tracked under
Appendix B, not here.

### View Components (cache populators)

| Component | Cache Key |
|-----------|-----------|
| **NavBadgesViewComponent** | `NavBadgeCounts` (read/write), `NavBadge:Voting:{userId}` (read/write) |
| **NotificationBellViewComponent** | `NotificationBadge:{userId}` (read/write) |
| **NobodiesEmailBadgeViewComponent** | `NobodiesTeamEmails_All` (read/write) |

All other view components read via owning services post-§15 audit.

### Background Jobs (Infrastructure)

Jobs live in `Humans.Infrastructure.Jobs` and may use repositories
directly. Mutation-heavy logic funnels into services even from jobs
(e.g. `CleanupNotificationsJob` calls `INotificationRepository` via
`NotificationService`; `LegalDocumentSyncService` runs via Hangfire and
goes through its own repository). Specific jobs and their tables vary;
treat each as an audit item per the section §15 carve-outs.

---

## Appendix B: Out-of-Service Cache Access

Controllers and components that touch `IMemoryCache` directly.

| Controller / Component | Cache Operation | Key |
|------------------------|-----------------|-----|
| **TeamAdminController** | Remove | `NobodiesTeamEmails_All` |
| **GoogleController** | Remove | `NobodiesTeamEmails_All` |
| **ProfileController** | Remove (×11 call sites — email edits, primary changes, deletes, etc.) | `NobodiesTeamEmails_All` |
| **NavBadgesViewComponent** | GetOrCreate | `NavBadgeCounts`, `NavBadge:Voting:{userId}` |
| **NotificationBellViewComponent** | GetOrCreate | `NotificationBadge:{userId}` |
| **NobodiesEmailBadgeViewComponent** | TryGetValue / Set | `NobodiesTeamEmails_All` |

The §15 work continues to push cache populators into the owning service
behind transparent decorators. The remaining view-component populators
plus the `NobodiesTeamEmails_All` invalidation scattered across three
controllers are the next slice — collapsing them into an
`INobodiesTeamEmailsService` (or `IGoogleService` extension) would
retire the last out-of-service cache writes.
