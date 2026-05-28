# Service Data Access Map

Audit of which services access which database tables and cache keys, organized by section.
The goal is to identify cross-section table overlap, duplicated caching, and cache configuration issues.

**Generated:** 2026-05-28

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
> `IUserInfoInvalidator`, `IShiftViewInvalidator`, `IEarlyEntryInvalidator`,
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
14. [Cantina](#cantina)
15. [Early Entry](#early-entry)
16. [Legal](#legal)
17. [Consent](#consent)
18. [Notifications](#notifications)
19. [Tickets](#tickets)
20. [Budget](#budget)
21. [Campaigns](#campaigns)
22. [Email](#email)
23. [Mailer](#mailer)
24. [Feedback](#feedback)
25. [Issues](#issues)
26. [Events (Event Guide)](#events-event-guide)
27. [Expenses](#expenses)
28. [Finance](#finance)
29. [Store](#store)
30. [Agent](#agent)
31. [Search](#search)
32. [Dashboard](#dashboard)
33. [Gdpr](#gdpr)
34. [AuditLog](#auditlog)
35. [Cross-Section Analysis](#cross-section-analysis)
36. [Cache Inventory](#cache-inventory)
37. [Appendix A: Out-of-Service Database Access](#appendix-a-out-of-service-database-access)
38. [Appendix B: Out-of-Service Cache Access](#appendix-b-out-of-service-cache-access)

---

## Profiles

Folder: `src/Humans.Application/Services/Profiles/`. Owns `Profiles`,
`ContactFields`, `ProfileLanguages`, `VolunteerHistoryEntries`,
`UserEmails`, `CommunicationPreferences`, `AccountMergeRequests`.

> **Change since prior sweep:** The Profiles section's three repositories
> (`IUserEmailRepository`, `IContactFieldRepository`, `IProfileRepository`)
> have been **consolidated into `IUserRepository`** (PRs #810 / #811). The
> single repository now owns all per-user persistence — `Users`, `Profiles`,
> `UserEmails`, `ContactFields`, `ProfileLanguages`,
> `VolunteerHistoryEntries`, `EventParticipations`, and the
> ASP.NET-Identity `IdentityUserLogins` bridge. Services that used to inject
> `IUserEmailRepository` now inject `IUserRepository`. `IProfileService` is
> retired (now `IProfilePictureService` only) and the unified User+Profile
> read-model lives behind `IUserService.GetUserInfoAsync`. The Profiles
> repositories remain **Singletons** (`IDbContextFactory` pattern).

### ProfileService (Scoped — `IProfilePictureService`)

Repository: `IUserRepository` (read-only profile methods - fetches profile row to resolve
storage paths).

| Table | R/W |
|-------|-----|
| Profiles | R (storage-path lookup) |

Cross-section calls via `IUserService` (delegates the content-type DB write).
Uses `IFileStorage` for the picture bytes. No `IMemoryCache`. The picture
content type is written through `IUserService.SetProfilePictureContentTypeAsync`
so the unified `UserInfo` read-model invalidates as a side effect.

### ProfileEditorService (Scoped)

No repository. Per-user serialization wrapper that fans out to
`IUserService.SaveProfileAsync` for the row writes and `IFileStorage` for
the picture file. No `IMemoryCache`.

### ContactFieldService (Scoped)

Repository: `IUserRepository` (profile and contact-field methods).

| Table | R/W |
|-------|-----|
| ContactFields | R/W |
| Profiles | R |

Cross-section reads via `IUserServiceRead`, `ITeamServiceRead`,
`IRoleAssignmentService` (visibility / coordinator-team lookups). Implements
`IUserMerge`. Invalidates the User+Profile read-model via
`IUserInfoInvalidator`. No `IMemoryCache`.

### CommunicationPreferenceService (Scoped)

Repository: `ICommunicationPreferenceRepository`.

| Table | R/W |
|-------|-----|
| CommunicationPreferences | R/W |

Cross-section calls via `IUserServiceRead`, `IAuditLogService`,
`IUnsubscribeTokenProvider` (Infrastructure). Implements `IUserMerge`. No
cache.

### UserEmailService (Scoped)

Repository: `IUserRepository`.

| Table | R/W |
|-------|-----|
| UserEmails | R/W |
| Users | R/W (the only direct EF write to `Users.GoogleEmail` / `Users.GoogleEmailStatus` / `Users.Email`; also a read for `UserEmailWithUser` lookups) |

Cross-section calls via `IUserService`, plus ASP.NET `UserManager<User>` and
`IServiceProvider` for lazy resolution. Implements `IUserMerge`. No
`IMemoryCache` directly.
**Cross-section design-rule note:** `Users` reads/writes overlap the User
section — `IUserRepository` is the consolidated owner post-#810/#811 and
this is the audited bridge for Google email status updates, tracked under
HUM0025 grandfathering as the read-model boundary.

### AccountMergeService (Scoped)

Repositories: `IAccountMergeRepository`, `IUserRepository`.

| Table | R/W |
|-------|-----|
| AccountMergeRequests | R/W |
| UserEmails | R/W (via `IUserRepository`) |

The merge fan-out happens through the `IEnumerable<IUserMerge>` aggregator —
each section's service implements `IUserMerge` and handles its own
owned-table reassignment. Implements `IUserDataContributor`. Cross-section
calls via `IUserService`, `IUserInfoInvalidator`, `IConsentCacheInvalidator`,
plus the merge aggregator.

Cache: per-section `IUserMerge` implementations invalidate their own
caches; the unified read-model is evicted via `IUserInfoInvalidator`.

### DuplicateAccountService (Scoped)

Repository: `IUserRepository` (consolidated post-#810/#811).

| Table | R/W |
|-------|-----|
| Profiles | R/W |
| ContactFields | R/W |
| ProfileLanguages | R/W |
| VolunteerHistoryEntries | R/W |
| UserEmails | R/W |
| Users | R/W |
| EventParticipations | R/W |
| IdentityUserLogins | R/W |

**Cross-section table writes (design-rule violations):** `Users`,
`EventParticipations`, `IdentityUserLogins` are owned by the User section
but written here directly via `IUserRepository`. Tracked under the §15
"merge orchestrator" carve-out — long-term should converge with
`AccountMergeService` on the `IUserMerge` aggregator.

Cross-section calls via `IUserService`, `ITeamService`,
`IRoleAssignmentService`, `IAuditLogService`, `IUserInfoInvalidator`.

### AdminHumanListAssembler / EmailProblemsService / PersonSearchFields

Read-only DTO assemblers — no repository, no cache. Fan out over
`IUserService`, `IUserEmailService`, `IRoleAssignmentService`,
`ITeamService`.

---

## Users

Folder: `src/Humans.Application/Services/Users/`. Owns `Users`,
`UserEmails` cross-bridge (read-through), `EventParticipations`, ASP.NET
`IdentityUserLogins`. The inner `IUserService` registration is wrapped
by `Humans.Infrastructure.Services.Users.CachingUserService` (Singleton
decorator inheriting `TrackedCache<Guid, UserInfo>`) which holds the
canonical `UserInfo` read-model spanning User + Profile sections. The
decorator exposes the budgeted cross-section read surface as
`IUserServiceRead`. Per the in-flight User+Profile section merge,
`IUserService` is absorbing the legacy `IProfileService` surface; the
interface's `[SurfaceBudget]` is intentionally suspended for now.

### UserService (Scoped — wrapped by CachingUserService Singleton decorator)

Repositories: `IUserRepository`, `ICommunicationPreferenceRepository`.

| Table | R/W | Repo |
|-------|-----|------|
| Users | R/W | IUserRepository |
| UserEmails | R | IUserRepository |
| EventParticipations | R/W | IUserRepository |
| IdentityUserLogins | R | IUserRepository |
| Profiles | R/W | IUserRepository |
| ContactFields | R | IUserRepository |
| CommunicationPreferences | R | ICommunicationPreferenceRepository |
| ProfileLanguages | R/W | IUserRepository |
| VolunteerHistoryEntries | R | IUserRepository |

The consolidated `IUserRepository` (post-#810) plus the comms-prefs repo
together compose the `UserInfo` projection inside `CachingUserService` —
a single cached read-model fanning out over the User + Profile section's
unified persistence layer. Implements `IUserDataContributor`,
`IUserMerge`.

Cross-section calls via `IAdminAuthorizationService`. No direct
`IMemoryCache` — caching is in the Singleton decorator.

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

Repository: `IUserRepository`.

| Table | R/W |
|-------|-----|
| Users | R/W |

Idempotent provisioning for import jobs. Uses ASP.NET `UserManager<User>`
for password / identity primitives. Cross-section calls via
`IUserEmailService`, `IUserService`, `IAuditLogService`. No cache.

### AccountDeletionService (Scoped) — `Users/AccountLifecycle/`

No repository. GDPR right-to-deletion orchestrator. Fans out over
`IUserService`, `IUserEmailService`, `ITeamService`,
`IRoleAssignmentService`, `IShiftSignupService`,
`IShiftManagementService`, `ITicketServiceRead`, `IAuditLogService`,
`IEmailService`. Invalidates
`IRoleAssignmentClaimsCacheInvalidator`,
`IShiftAuthorizationInvalidator`, `IShiftViewInvalidator`. Uses
`IFileStorage` for blob cleanup. No cache, no direct DB access — all
writes go through owning services.

### UnsubscribeService (Scoped)

Repository: `IUserRepository` (read-only — token validation).

| Table | R/W |
|-------|-----|
| Users | R |

Calls `ICommunicationPreferenceService` to flip per-category opt-outs;
uses `IDataProtectionProvider` for token validation. No cache.

### UserEmailProviderBackfillService (Scoped)

Repository: `IUserRepository` (consolidated post-#810/#811).

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

No repository injected. Cross-section calls via `IUserService`,
`IApplicationDecisionService`, `IEmailService`, `INotificationService`,
`ISystemTeamSync`, `IMembershipCalculator`, `IAuditLogService`,
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

No repository. Fans out over `IUserService`, `INotificationService`,
`INotificationInboxService`, `IAuditLogService`, `IHumansMetrics`. No
direct DB access, no cache. All `Profile.State` writes go through
`IUserService` (the unified user/profile write surface) which invalidates
the unified User+Profile read-model downstream.

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

Cross-section calls via `IUserService`, `IRoleAssignmentService`,
`IEmailService`, `IUserEmailService`, `INotificationService`,
`ISystemTeamSync`, `IAuditLogService`, `IHumansMetrics`. Implements
`IUserDataContributor`, `IUserMerge`.

### MembershipCalculator (Scoped)

No repository. Pure read computation over `IUserService`,
`IMembershipQuery`, `ILegalDocumentSyncService`, `IConsentService`
(resolved lazily via `IServiceProvider` to break a DI cycle), and
`IClock`. No DB access, no cache.

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

The inner `IRoleAssignmentService` is wrapped by
`Humans.Infrastructure.Services.Auth.CachingRoleAssignmentService`
(Singleton decorator inheriting `TrackedCache<Guid, RoleAssignmentRow>`,
issue #749). The full `role_assignments` row set is held in memory so
cross-section reads (`GetActiveCountsByRoleAsync`, `GetActiveForUserAsync`)
derive at any clock instant without a query. Invalidation is service-level:
the inner service's writes call `IRoleAssignmentCacheInvalidator.InvalidateAll()`
directly (single writer, so no EF interceptor needed).

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
`TeamRoleAssignments`, `TeamRoleDefinitions`. Also writes
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
| `TrackedCache<Guid, TeamInfo>` (`Team.TeamInfo`, in-process, no `IMemoryCache`) | Per-Entity (warmed on startup) | yes | yes | yes (via `IActiveTeamsCacheInvalidator`, called from `IUserMerge` flows and direct mutation paths) |

Implements `ITeamService`, `ITeamServiceRead`, `IUserMerge`. Replaces the
previous `ActiveTeams` `IMemoryCache` entry — the `TeamInfo` dictionary is
the canonical source. Surfaced on `/Admin/CacheStats`.

### TeamPageService / TeamPageSummaryMapper / TeamDirectoryBuilder

Read-only assemblers — no repository, no cache. Fan out over
`ITeamService`, `IUserService`, `ITeamResourceService`,
`IShiftManagementService`.

---

## Google Integration

Folder: `src/Humans.Application/Services/GoogleIntegration/`. Owns
`SyncServiceSettings`, `GoogleSyncOutboxEvents`, and `GoogleResources`
(the latter via `TeamResourceService`; the outbox is appended atomically
by `TeamRepository`, Google Integration is the read/process owner).
`Users.GoogleEmail` / `GoogleEmailStatus` writes go through
`IUserService` / `IUserEmailService` per §15.

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
`IUserEmailService`, `ISyncSettingsService`, `IAuditLogService`,
`IGoogleRemovalNotificationService`, `IGoogleGroupSyncScheduler`. No
direct DB access, no cache.

### GoogleAdminService (Scoped)

No repository. **Migrated to the §15 pattern (issue #554)** — no DbContext
or repository dependency; all cross-section data access routes through the
owning services. Cross-section calls via `IGoogleWorkspaceUserService`,
`IGoogleSyncService`, `ITeamService`, `ITeamResourceService`,
`IUserService`, `IUserEmailService`, `IAuditLogService`, plus
`ILogger<GoogleAdminService>`. No cache.

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

### DriveActivityMonitorService (Scoped)

Repository: `IDriveActivityMonitorRepository`.

| Table | R/W |
|-------|-----|
| GoogleResources | R (via `ITeamResourceService`) |
| Users / IdentityUserLogins | R (via `IUserServiceRead.GetAllUserInfosAsync` / `UserInfo.ExternalLogins`) |
| SystemSettings | R/W (key `DriveActivityMonitor:LastRunAt`) |

Google `people/{id}` fallback resolution goes through the Users read-model:
the service builds a per-run Google provider-key -> `UserInfo` index from
`IUserServiceRead.GetAllUserInfosAsync` and uses `UserInfo.Email`. The
repository owns only the `SystemSettings` key plumbing. Audit-log writes go
through `IAuditLogService`.

Cross-section calls via `IGoogleDriveActivityClient`,
`ITeamResourceService`, `IUserServiceRead`, `IAuditLogService`. No cache.

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

> **Change since prior sweep:** `ICampRoleRepository` has been
> **consolidated into `ICampRepository`** (PR #809) via a `.Roles.cs` partial.
> `CampRoleService` now injects `ICampRepository` directly. The Camps
> section is back to a single repository owning all of its tables. The
> earlier `GetCampLeadsAsync` cross-section read of `Users` remains retired,
> and `CampService` continues to implement `IEarlyEntryProvider`.

### CampService (Scoped — wrapped by CachingCampService Singleton decorator)

Repository: `ICampRepository`.

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

Cross-section calls via `IUserServiceRead`, `IAuditLogService`,
`ISystemTeamSync`, `IFileStorage`, `INotificationEmitter`, plus
`Lazy<ICampRoleService>` to break a DI cycle. Implements
`IUserDataContributor`, `IUserMerge`, `IEarlyEntryProvider`. The inner
service no longer touches `IMemoryCache` directly — all caching lives in
the decorator.

### CachingCampService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, CampInfo>` (`Camp.CampInfo`, warmed on startup) | Per-Entity | yes | yes | yes (`ICampInfoInvalidator.InvalidateCampAsync`, wholesale `RefreshAll` for cross-cutting writes) |
| `CampSettingsInfo` single slot (no `IMemoryCache`) | Static | yes | yes | yes (`ICampInfoInvalidator.InvalidateSettingsAsync`) |

Implements `ICampService`, `ICampServiceRead`, `IUserMerge`,
`ICampInfoInvalidator`. Surfaced on `/Admin/CacheStats`.

### CampRoleService (Scoped)

Repository: `ICampRepository`.

| Table | R/W |
|-------|-----|
| CampRoleDefinitions | R/W |
| CampRoleAssignments | R/W |
| CampMembers | R |
| Camps | R (via repo helper) |

Cross-section calls via `ICampService`, `IUserService`,
`IAuditLogService`, `INotificationEmitter`. No `IMemoryCache`.

### CampContactService (Scoped)

No repository. Rate-limited contact relay. Cross-section calls via
`IEmailService`, `IAuditLogService`, `INotificationEmitter`,
`IUserService`, `ICampService`.

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
`CalendarEvents`, `CalendarEventExceptions`. The inner `ICalendarService`
is wrapped by `Humans.Infrastructure.Services.Calendar.CachingCalendarService`
(Singleton decorator inheriting `TrackedCache<Guid, CalendarEventInfo>`,
warmed on startup). The decorator exposes the cross-section read surface
as `ICalendarServiceRead`; writes delegate to the inner service then
refresh the affected event row.

> **Change since prior sweep:** `CalendarRepository` no longer joins the
> `Teams` table — the previous cross-section read has been retired and the
> service stitches team names via `ITeamServiceRead` at the application
> layer.

### CalendarService (Scoped — wrapped by CachingCalendarService Singleton decorator)

Repository: `ICalendarRepository`.

| Table | R/W |
|-------|-----|
| CalendarEvents | R/W |
| CalendarEventExceptions | R/W |

Cross-section calls via `ITeamService`, `IAuditLogService`,
`ICalendarOccurrenceExpander`.

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

> **Change since prior sweep:** `IShiftManagementRepository` and
> `IShiftSignupRepository` are now backed by **one concrete partial class
> `ShiftRepository`** (PR #806; `.Management.cs` + `.Signups.cs` partials).
> The two interfaces are preserved so service-layer injection patterns and
> the management/signup split remain visible at the call site, but
> persistence-side ownership has converged. `IGeneralAvailabilityRepository`
> was previously folded into `IVolunteerTrackingRepository`. Some shared
> Shifts-internal reads (`EventSettings`, `ShiftSignups` from
> `VolunteerTrackingRepository`) carry `[Grandfathered("HUM0025", …)]` while
> the read surfaces converge.

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
`IShiftViewInvalidator`, `IEarlyEntryInvalidator`, plus `IServiceProvider`
for cycle-breaking. Implements `IShiftAuthorizationInvalidator`,
`IUserMerge`. Also exposes the Cantina-gating predicates
(`HasQualifyingCantinaSignupAsync`, `GetOnSiteUserIdsForDayAsync`).

### ShiftSignupService (Scoped)

Repositories: `IShiftSignupRepository`, `IVolunteerTrackingRepository`.

| Table | R/W |
|-------|-----|
| ShiftSignups | R/W |
| Shifts | R (via repo) |
| Rotas | R (via repo) |
| VolunteerEventProfiles | R/W (via repo) |
| VolunteerTagPreferences | R (via repo) |
| GeneralAvailability | R (via `IVolunteerTrackingRepository`, GDPR export) |

Cross-section calls via `IShiftManagementService`, `IBurnSettingsService`,
`IMembershipCalculator`, `IAuditLogService`, `INotificationService`,
`IAdminAuthorizationService`, `IShiftViewInvalidator`,
`IEarlyEntryInvalidator`, `IServiceProvider`. Implements
`IUserDataContributor`, `IUserMerge`. No `IMemoryCache`.

### GeneralAvailabilityService (Scoped)

Repository: `IVolunteerTrackingRepository`.

| Table | R/W |
|-------|-----|
| GeneralAvailability | R/W |

Cross-section calls via `IShiftViewInvalidator`. Implements `IUserMerge`.
No `IMemoryCache`.

### VolunteerTrackingService (Scoped)

Repositories: `IVolunteerTrackingRepository`, `IShiftManagementRepository`.

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
| ShiftSignups | R (via repo: confirmed shifts in range) |
| Shifts | R (via repo) |
| Rotas | R (via repo) |

Cross-section calls via `IShiftManagementService`, `IUserServiceRead`.
Implements `IVolunteerTrackingExportService`, `IEarlyEntryProvider`
(contributes confirmed-build-shift early-entry rows). No cache, no
direct DB access beyond the export query.

### ShiftViewService (Scoped — wrapped by CachingShiftViewService Singleton decorator)

Repositories: `IShiftManagementRepository`, `IShiftSignupRepository`,
`IVolunteerTrackingRepository`.

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
| `TrackedCache<Guid, ShiftUserView>` (`ShiftView.UserView`, in-process, no `IMemoryCache`) | Per-User | yes | yes | yes (via `IShiftViewInvalidator.InvalidateUser`) |
| `TrackedCache<Guid, ShiftRotaView>` (`ShiftView.RotaView`, in-process, no `IMemoryCache`) | Per-Entity | yes | yes | yes (via `IShiftViewInvalidator.InvalidateRota`) |

Implements `IShiftView`, `IShiftViewInvalidator`. Resolves the inner
Scoped `IShiftView` via `IServiceScopeFactory` to honour scope rules.
Both cache instances are surfaced on `/Admin/CacheStats`.

### BurnSettingsService (Scoped)

Repository: `IShiftManagementRepository` (read-only — fetches `EventSettings`).

| Table | R/W |
|-------|-----|
| EventSettings | R |

Read-only adapter mapping `EventSettings` → `BurnSettingsInfo` DTO at the
section boundary (#719). Exposes `IBurnSettingsService` for cross-section
consumers that need active-event metadata without coupling to the full
shifts surface. No cache (single active row, cold path).

### RotaCoordinatorMessageService (Scoped)

Repositories: `IShiftSignupRepository`, `IShiftManagementRepository`.

| Table | R/W |
|-------|-----|
| ShiftSignups | R |
| Rotas | R (via repos) |
| Shifts | R (via repos) |
| EventSettings | R (via `IShiftManagementRepository.GetActiveEventSettingsAsync` — team-level dispatch path) |

Cross-section calls via `ITeamServiceRead`, `IUserServiceRead`,
`IEmailService`, `IAuditLogService`. Implements per-rota
(`SendRotaMessageAsync`) and team-level (`SendTeamRotasMessageAsync`,
PR #795) dispatch — groups active signups by user across one or many
rotas and enqueues one personalised email per recipient via the outbox.
No cache.

### WorkloadService (Scoped) — `Shifts/Workload/`

Repository: `IShiftManagementRepository`.

| Table | R/W |
|-------|-----|
| EventSettings | R |
| Shifts | R (via repo) |
| Rotas | R (cached via `IShiftView`) |

Cross-section calls via `IShiftView` (cached), `ITeamService` (team
projections for role-period estimates), `IUserServiceRead`. No own
cache — relies on the per-rota `ShiftView.RotaView` eviction to refresh.

### EarlyEntryCapacityCalculator / ShiftEarlyEntryProjection / TeamPalette

Stateless calculators / projections — no DI dependencies, no DB access.

---

## Cantina

Folder: `src/Humans.Application/Services/Cantina/`. Owns no DB tables —
orchestrator only. Dietary data moved to `Profile` and is read through
the unified `UserInfo` read-model.

### CantinaRosterService (Scoped)

No repository. Cross-section reads via `IShiftManagementService`
(on-site cohort per day, via `GetOnSiteUserIdsForDayAsync` and active
`EventSettings`) and `IUserServiceRead` (cached `UserInfo` + `ProfileInfo`
for dietary preference, allergies, intolerances). Implements
`ICantinaRosterService`. No direct DB access, no cache.

`MedicalConditions` is intentionally never read here — the cantina plans
around food, not medical history.

---

## Early Entry

Folder: `src/Humans.Application/Services/EarlyEntry/`. Owns no DB tables —
fan-out orchestrator over per-section `IEarlyEntryProvider`
implementations. The inner `IEarlyEntryService` is wrapped by
`Humans.Infrastructure.Services.EarlyEntry.CachingEarlyEntryService`
(Singleton decorator inheriting `TrackedCache<Guid, UserEarlyEntry?>`).

### EarlyEntryService (Scoped, keyed `"early-entry-inner"` — inner of CachingEarlyEntryService)

No repository. Injects `IEnumerable<IEarlyEntryProvider>` — every section
that grants early entry implements this and contributes its rows.
Current providers: Camps (`CampService` — camp-lead grants), Shifts
(`VolunteerTrackingExportService` — confirmed build-shift grants).
Sequential fan-out (providers share the scoped `HumansDbContext` and EF
is not thread-safe). No direct DB access, no cache.

### CachingEarlyEntryService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, UserEarlyEntry?>` (`EarlyEntry.UserEarlyEntry`, lazy, no warmup) | Per-User (caches negative result) | yes | yes | yes (`IEarlyEntryInvalidator.InvalidateUser` / `InvalidateAll`, fired from Shifts and Camps writes) |

Implements `IEarlyEntryService`, `IEarlyEntryInvalidator`. `GetRosterAsync`
always delegates to the inner service (admin roster needs live data);
`GetForUserAsync` is cached per-user (including the no-EE negative result
since most users have no EE). Resolves the keyed Scoped inner via
`IServiceScopeFactory`. Surfaced on `/Admin/CacheStats`.

---

## Legal

Folder: `src/Humans.Application/Services/Legal/`. Owns `LegalDocuments`,
`DocumentVersions`. The inner `ILegalDocumentSyncService` is wrapped by
`Humans.Infrastructure.Services.Legal.CachingLegalDocumentSyncService`
(Singleton decorator inheriting `TrackedCache<Guid, LegalDocumentInfo>`,
warmed on startup, with a version-id → document-id index). It caches the
global active-document set behind the every-page consent-banner read and
the per-version lookup, invalidated wholesale after any persisted
`legal_documents` / `document_versions` write via
`LegalDocumentSaveChangesInterceptor` → `ILegalDocumentCacheInvalidator.InvalidateAll`.

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
The inner `IConsentService` is wrapped by
`Humans.Infrastructure.Services.Consent.CachingConsentService` (Singleton
decorator inheriting `TrackedCache<Guid, UserConsentInfo>`, lazy / no
startup warmup). It caches the per-user set of consented document-version
ids (with the account-merge source-id chain resolved at load) and
**synchronously** evicts the affected user on `SubmitConsentAsync` before
returning, so the next-page consent-banner check never observes a stale
"still required" entry. It exposes the cross-section read surface as
`IConsentServiceRead`.

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

Cross-section calls via `IUserServiceRead`, `IGoogleSyncService`,
`ITeamServiceRead`, `ITicketSyncService`, `IApplicationDecisionService`,
`ICampService`. **No direct DB access** — every counter fans out through
an owning-service interface call.

### NotificationRecipientResolver (Scoped)

No repository. Fan-out over `ITeamServiceRead`, `IRoleAssignmentService`.
No DB access, no cache.

---

## Tickets

Folder: `src/Humans.Application/Services/Tickets/`. Owns `TicketOrders`,
`TicketAttendees`, `TicketSyncStates`, `TicketTransferRequests`.

The read path is split: `TicketQueryService` is the **inner** read service,
registered keyed under `CachingTicketQueryService.InnerServiceKey`
(`"ticket-query-inner"`), and is wrapped by the Singleton
`CachingTicketQueryService` decorator. The decorator is the registered
`ITicketService`, the budgeted cross-section `ITicketServiceRead`, and the
`ITicketCacheInvalidator`. External sections inject `ITicketServiceRead`
(two-method surface: `GetTicketOrdersAsync` + `GetUserTicketHoldingsAsync`)
rather than the full `ITicketService`. Tickets caching is entirely
`TrackedCache`-based: an orders slice (`Tickets.Orders`, warmed on startup)
and a user-holdings slice (`Tickets.UserHoldings`, lazy with a 5-minute
freshness deadline embedded in the cached value). The only `IMemoryCache`
key the section still uses is `TicketEventSummary:{eventId}`.

### TicketQueryService (Scoped, keyed `"ticket-query-inner"` — inner of CachingTicketQueryService)

Repository: `ITicketRepository`.

| Table | R/W |
|-------|-----|
| TicketOrders | R |
| TicketAttendees | R |
| TicketSyncStates | R |

The inner service holds no cache — invalidation methods are no-ops on the
inner; `CachingTicketQueryService` intercepts. Cross-section calls via
`IBudgetService`, `ICampaignService`, `IUserService`, `IUserEmailService`,
`ITeamServiceRead` (read-split surface), `IShiftManagementService`.
Implements `IUserDataContributor` (the GDPR contributor is the inner, one
per section).

> **Change since prior sweep:** `TicketRepository` no longer reads
> `UserEmails` directly (PR #802) — the prior `GetAllUserEmailLookupEntriesAsync`
> projection has been retired. Email-to-user matching for ticket sync now
> routes through `IUserServiceRead.GetAllUserInfosAsync` (see
> `TicketSyncService.BuildEmailLookupAsync`). The cross-section
> design-rule violation on `UserEmails` is closed.

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
`IUserServiceRead`, `IUserService`, `ICampaignService`,
`IShiftManagementService`, `ITicketCacheInvalidator`. Implements
`ITicketSyncService`, `IUserMerge`. `BuildEmailLookupAsync` builds the
verified-email → user-id map by fanning out over `IUserServiceRead.GetAllUserInfosAsync`
(replaces the prior `TicketRepository` projection — PR #802).

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

No repository. Tickets→Budget bridge — reads paid ticket sales through
`ITicketServiceRead.GetTicketOrdersAsync` (the cached read surface) and
delegates every `BudgetLineItem` / `TicketingProjection` write to
`IBudgetService`. Holds no DB access of its own.

> **Change since prior sweep:** the dedicated `ITicketingBudgetRepository`
> / `TicketingBudgetRepository` were **removed** (PR #815) — the
> Budget/Tickets read surface folded into `ITicketRepository` and this
> service now reads orders via the cached `ITicketServiceRead` instead of
> a direct repository. There is no longer any `TicketOrders` read here.

Cross-section calls via `ITicketServiceRead`, `IBudgetService`, plus
`IClock` and `ILogger`. Aggregates paid orders into weekly ticketing
actuals which it hands to `IBudgetService.SyncTicketingActualsAsync`.
Implements `ITicketingBudgetService`. No cache.

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

> **Change since prior sweep:** `BudgetRepository` no longer reads
> `Teams` directly — the previous cross-section join has been retired and
> team labels are stitched at the service layer via `ITeamService`.

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

### Audience definitions (`IMailerAudience`)

Audience-membership computation classes under `Mailer/Audiences/`:
`HasShiftAudience`, `HasShiftSetupAudience`, `HasShiftEventAudience`,
`HasShiftStrikeAudience`, `HasTicketAudience`, `MarketingAudience`,
`MarketingNoTicketAudience`, `TicketNoShiftsAudience`, `MailerAudienceBase`,
`HasShiftInPeriodAudienceBase`.
No repository; compute over read-split / section service interfaces
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
`ITeamService`, `IEmailService`, `INotificationService`,
`IAuditLogService`, `IHostEnvironment`. Implements `IUserDataContributor`,
`IUserMerge`. `IMemoryCache` is injected only as the substrate for
`INavBadgeCacheInvalidator`.

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
`EventModerationActions`, `EventPreferences`, `EventFavourites`.

> **Change since prior sweep:** `EventRepository` no longer reads
> `EventSettings` (the Shifts-owned table) directly — active-event
> discovery has been migrated to a service-layer call. The inner
> `IEventService` is wrapped by
> `Humans.Infrastructure.Services.Events.CachingEventService` (Singleton
> decorator). It owns four split projections — a per-event
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

Cross-section calls limited to `IClock` (plus owning-service lookups for
active-event scoping). Implements `IUserDataContributor`. The inner
service has no `IMemoryCache`.

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
`IUserService`, `IAuditLogService`, `IHoldedClient`,
`IHoldedFinanceService` (Finance section — creditor balance exposure to
expense submitters per PR #791). Implements `IUserDataContributor`. No
`IMemoryCache`.

### SepaPaymentFileBuilder

Stateless builder — formats SEPA XML payment files. No DI dependencies
beyond `SepaConfig` options. No DB access.

---

## Finance

Folder: `src/Humans.Application/Services/Finance/`. Owns `HoldedExpenseDocs`,
`HoldedCategoryMap`, `HoldedSyncStates`, `HoldedCreditorBalances`,
`HoldedPayments`. Section added since prior sweep to consolidate the
Holded accounting-system integration (provisioning, purchase-doc sync,
actuals computation, creditor balance polling).

### HoldedFinanceService (Scoped)

Repository: `IHoldedRepository`.

| Table | R/W |
|-------|-----|
| HoldedCategoryMap | R/W |
| HoldedExpenseDocs | R/W |
| HoldedCreditorBalances | R/W |
| HoldedPayments | R/W |
| HoldedSyncStates | R/W |

Cross-section calls via `IBudgetService` (full `IBudgetService` — should
narrow to an `IBudgetServiceRead` via the section read/write split once
the surface stabilises), `IHoldedClient` (Infrastructure). Implements
`IHoldedFinanceService`. No `IMemoryCache`.

### HoldedMatcher

Stateless matcher — pairs Holded docs against budget categories. No DI
dependencies beyond pure data shaping, no DB access.

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
`ITeamServiceRead` (team-order counterparty surface, #816),
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

Repository: `IAgentRepository` (read-only window queries for the admin
status report).

| Table | R/W |
|-------|-----|
| AgentConversations | R |
| AgentMessages | R |

Cross-section calls via `IAgentSettingsService`, `IAgentRateLimitStore`,
`IAgentRetentionRunStore`, `IAgentAnthropicBalanceProvider`. Read-only
assembler for `/Agent/Admin/Status` — one 30-day projection, all
sub-windows computed in memory. No cache.

### AgentPricing

Static class — hard-coded per-1M-token Anthropic pricing for agent spend
estimates. No DI, no DB access.

### AgentSettingsService / AgentPromptAssembler / AgentToolDispatcher / AgentUserSnapshotProvider / AgentAbuseDetector (Infrastructure)

Live under `src/Humans.Infrastructure/Services/Agent/`. The settings
service is the only one that touches `AgentSettings` directly (via
`AgentRepository.GetAgentSettingsAsync` / `UpsertAgentSettingsAsync`).
The others are stateless adapters or fan-out over public service
interfaces (`ITeamService`, `IUserService`, `IRoleAssignmentService`,
`ICampService`, `IShiftView`, etc.) for the agent's tool-dispatch and
user-snapshot surfaces. No `IMemoryCache`.

### AnthropicClient (Infrastructure)

Outbound API client over `AnthropicOptions`. No DB access, no cache.

---

## Search

Folder: `src/Humans.Application/Services/Search/`. No owned DB tables.

### SearchService (Scoped)

No repository. Pure read-aggregation over `IUserServiceRead`,
`ITeamServiceRead`, `ICampServiceRead`, `IShiftManagementService`,
`IEventService`, plus `IConfiguration` for the events feature flag. No
DB access, no cache. All search results come from the cached UserInfo /
TeamInfo / CampInfo / event projections.

---

## Dashboard

Folder: `src/Humans.Application/Services/Dashboard/`. No owned DB tables.

### DashboardService (Scoped)

No repository. Read-only fan-out over `IMembershipCalculator`,
`IApplicationDecisionService`, `IShiftManagementService`, `IShiftView`,
`ITicketServiceRead`, `IUserServiceRead`, `ITeamServiceRead`. Uses
`TicketVendorSettings`. No DB access, no cache.

### AdminDashboardService (Scoped)

No repository. Fan-out over `IUserServiceRead`, `IMembershipCalculator`,
`IApplicationDecisionService`, `IShiftManagementService`, `IShiftView`.
No DB access, no cache.

---

## Gdpr

Folder: `src/Humans.Application/Services/Gdpr/`. No owned DB tables —
the export orchestrator runs over per-section `IUserDataContributor`
fan-out.

### GdprExportService (Scoped)

No repository. Injects `IEnumerable<IUserDataContributor>` — every
section that owns per-user tables implements this and contributes its
slice. Current contributors (per design-rules §8a): Profiles
(`AccountMergeService`), Users (`UserService`), Auth
(`RoleAssignmentService`), Governance (`ApplicationDecisionService`),
Camps (`CampService`), Shifts (`ShiftSignupService`), Tickets
(`TicketQueryService` — the keyed inner), Notifications
(`NotificationInboxService`), AuditLog (`AuditLogService`), Budget
(`BudgetService`), Campaigns (`CampaignService`), Feedback
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
`IUserService`, `ITeamService`, `ITeamResourceService`. No DB access,
no cache.

`AuditEvent` and `AuditEventTextualizer` are value types / pure
formatters with no DI dependencies.

---

## Cross-Section Analysis

### Tables Accessed by Multiple Sections (via repository)

After the §15 / `IUserMerge` consolidation, the
`GoogleAdminService` / `CampRepository.GetCampLeadsAsync` /
`CalendarRepository` / `BudgetRepository` / `EventRepository` /
`ShiftSignupRepository` / `TicketRepository` cleanups, and the
recent Profiles repository consolidation (PRs #810/#811: three
Profiles repositories folded into `IUserRepository`), only a handful
of cross-section table reads remain. Because the consolidated
`IUserRepository` is now the single owner of every per-user table
across the Users+Profiles section merge, the previously-tracked
`IUserEmailRepository` violations are recategorised — they are now
internal reads/writes of the unified User+Profile owner, not
cross-section violations.

| Table | Owning Section | Cross-Section Repo Readers (violations) |
|-------|----------------|-----------------------------------------|
| **GoogleSyncOutboxEvents** | Google Integration | Teams (`TeamRepository` writes outbox events on team mutations) |
| **EventSettings** | Shifts | Shifts internal (`VolunteerTrackingRepository` reads it alongside `ShiftRepository`; HUM0025 grandfathered, scoped to Shifts internals) |
| **ShiftSignups** | Shifts | Shifts internal (same — `VolunteerTrackingRepository` reads alongside `ShiftRepository`; HUM0025 grandfathered) |
| **SystemSettings** | per-key (see Pattern #7) | shared by `EmailOutboxRepository` (Email-owned keys) and `DriveActivityMonitorRepository` (Google-owned keys); disjoint keys — HUM0025 grandfathered |

### Notable Cross-Section Patterns

1. **`IUserMerge` retired most cross-section profile/identity writes.**
   `AccountMergeService` no longer injects profile-owned repositories
   directly — it fans out over `IEnumerable<IUserMerge>`, with each
   section's service implementing `IUserMerge` to reassign its own owned
   rows. `DuplicateAccountService` still uses the consolidated
   `IUserRepository` directly pending convergence on the same pattern.
   With PRs #810/#811 the three Profiles repositories collapsed into
   `IUserRepository`, so what looked like cross-section reads/writes
   between the Profiles and Users sections is now internal to the
   unified Users+Profiles section owner.

2. **Read/write surface split (read-split interfaces).** Several sections
   now expose a budgeted cross-section read interface that external sections
   inject instead of the full service: `IUserServiceRead`,
   `ITeamServiceRead`, `ICalendarServiceRead`, `IConsentServiceRead`,
   `ICampServiceRead`, and `ITicketServiceRead`. These are the Singleton
   caching decorators re-cast to a narrow surface, keeping the
   cross-section coupling minimal and `[SurfaceBudget]`-bounded.

3. **`IProfileService` retired into `IUserService`.** The Profile-section
   service surface has been folded into `IUserService` as part of the
   Users+Profile section merge (`IUserService` is absorbing the legacy
   `IProfileService` methods over several PRs; the interface's
   `[SurfaceBudget]` is intentionally suspended during the merge).
   `ProfileEditorService` and `ContactFieldService` remain in
   `Services/Profiles/` as section-internal collaborators; the only
   Application-layer service named `ProfileService` is now a thin
   `IProfilePictureService` implementation for picture-bytes IO.

4. **Tickets ↔ Profiles email lookup retired (PR #802).**
   `TicketRepository` no longer projects `UserEmail` rows directly.
   `TicketSyncService.BuildEmailLookupAsync` fans out over
   `IUserServiceRead.GetAllUserInfosAsync` and synthesises the
   verified-email → user-id map from the cached `UserInfo` slices.
   `UserEmails` is now read only by the consolidated `IUserRepository`
   itself (post-#810/#811) for `UserInfo` projection — internal to the
   unified Users+Profiles owner, no longer a cross-section reach.

5. **Teams ↔ Google outbox.** `TeamRepository` writes
   `GoogleSyncOutboxEvents` so each team mutation is atomic with its
   outbox event. The Google Integration section reads/processes them
   via `IGoogleSyncOutboxRepository`. The atomicity benefit outweighs
   the boundary cost.

6. **DriveActivityMonitor user fallback uses UserInfo.**
   `DriveActivityMonitorService` resolves Google `people/{client_id}` actors
   through Directory first, then through a per-run Google provider-key ->
   `UserInfo` index from `IUserServiceRead.GetAllUserInfosAsync`. The
   repository owns only its `SystemSettings` key.

7. **SystemSettings has per-key ownership (no single owner service).**
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

8. **Cached read-models have displaced almost all per-key `IMemoryCache`
   entries.** Singleton decorators inheriting `TrackedCache<TKey, TValue>`
   now own the canonical projections across most sections:
   - `CachingUserService` → `UserInfo` per user (Users + Profiles
     unified read-model).
   - `CachingTeamService` → `TeamInfo` per team (replaced `ActiveTeams`).
   - `CachingShiftViewService` → `ShiftView.UserView` + `ShiftView.RotaView`.
   - `CachingTicketQueryService` → `Tickets.Orders` + `Tickets.UserHoldings`.
   - `CachingCampService` → `CampInfo` per camp + settings slot
     (replaced `camps_year_{year}` / `CampSettings`).
   - `CachingCalendarService` → `CalendarEventInfo` per event
     (replaced `calendar:active-events`).
   - `CachingEventService` → `ApprovedEventView` + category/venue/settings
     snapshots.
   - `CachingConsentService` → `UserConsentInfo` per user.
   - `CachingLegalDocumentSyncService` → `LegalDocumentInfo` per document.
   - `CachingRoleAssignmentService` → `RoleAssignmentRow` set.
   - `CachingEarlyEntryService` → `UserEarlyEntry?` per user (new — caches
     negative results too).
   All are surfaced on `/Admin/CacheStats` via `ICacheStats` and evicted
   through narrow `I*Invalidator` interfaces (or EF
   `SaveChangesInterceptor`s for Legal / User-Identity writes) — no direct
   `IMemoryCache` coupling in the Application layer.

9. **Notification meters are computed, not queried.**
   `NotificationMeterProvider` reads no tables directly — every counter
   fans out through an owning-service interface call (`IUserServiceRead`,
   `ITeamServiceRead`, `IApplicationDecisionService`, `ITicketSyncService`,
   `IGoogleSyncService`, `ICampService`). Cache invalidation goes through
   `INotificationMeterCacheInvalidator`.

10. **HUM analyzers enforce the boundaries at compile time.** Roslyn
    analyzers ratchet the layering rules: `HUM0008` blocks
    `HumansDbContext` in controllers, `HUM0009` blocks `HumansDbContext`
    in Application-layer services. See
    [`code-analysis.md`](code-analysis.md) for the full analyzer list.

11. **Provider-based fan-out for derived data.** `IEarlyEntryService`
    aggregates per-user grants over `IEnumerable<IEarlyEntryProvider>`
    implementations (currently Camps and Shifts). `IUserMerge`,
    `IUserDataContributor`, and `IMailerAudience` use the same
    enumerable-injection pattern. This keeps the orchestrator
    section-agnostic; new contributors register a single service
    interface in their section's DI extension.

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
| `CampContactRateLimit:{userId}:{campId}` | 10 min | Rate Limit | CampContactService | CampContactService |
| `magic_link_used:{tokenPrefix}` | 15 min | Rate Limit | MagicLinkRateLimiter (Infrastructure) | MagicLinkRateLimiter |
| `magic_link_signup:{normalizedEmail}` | 60 sec | Rate Limit | MagicLinkRateLimiter (Infrastructure) | MagicLinkRateLimiter |

> **Retired `IMemoryCache` keys** (now `TrackedCache` projections or
> removed entirely): `camps_year_{year}` and `CampSettings` (→
> `CachingCampService`), `calendar:active-events` (→
> `CachingCalendarService`), `NobodiesTeamEmails_All` (replaced by
> `IUserEmailService.GetNobodiesTeamEmailsByUserIdsAsync` service method).
> These were removed from `CacheKeys.cs` / `CacheKeys.Metadata`.

### Section Decorator Caches (`TrackedCache`, not `IMemoryCache`)

| Cache | Section | Key | Type | Populated By | Invalidated By |
|-------|---------|-----|------|-------------|----------------|
| `TrackedCache<Guid, UserInfo>` | Users (+Profiles) | `User.UserInfo` | Per-User | CachingUserService warmup + lazy load | `IUserInfoInvalidator` + `IUserMerge` + `UserInfoSaveChangesInterceptor` |
| `TrackedCache<Guid, TeamInfo>` | Teams | `Team.TeamInfo` | Per-Entity | CachingTeamService warmup + lazy load | `IActiveTeamsCacheInvalidator` + `IUserMerge` |
| `TrackedCache<Guid, ShiftUserView>` / `TrackedCache<Guid, ShiftRotaView>` | Shifts | `ShiftView.UserView` / `ShiftView.RotaView` | Per-User / Per-Entity | CachingShiftViewService lazy load | `IShiftViewInvalidator` (ShiftManagementService, ShiftSignupService, GeneralAvailabilityService, VolunteerTrackingService, AccountDeletionService) |
| `TrackedCache<Guid, TicketOrderInfo>` | Tickets | `Tickets.Orders` | Per-Entity | CachingTicketQueryService warmup + lazy load | `ITicketCacheInvalidator` |
| `TrackedCache<Guid, CachedUserTicketHoldings>` | Tickets | `Tickets.UserHoldings` | Per-User (5-min freshness in value) | CachingTicketQueryService lazy load | `ITicketCacheInvalidator` |
| `TrackedCache<Guid, CampInfo>` + settings slot | Camps | `Camp.CampInfo` | Per-Entity / Static | CachingCampService warmup + lazy load | `ICampInfoInvalidator` + `IUserMerge` |
| `TrackedCache<Guid, CalendarEventInfo>` | Calendar | `Calendar.Event` | Per-Entity | CachingCalendarService warmup + lazy load | per-event `ReplaceAsync` after delegated write |
| `TrackedCache<Guid, ApprovedEventView>` + category/venue/settings snapshots | Events | `Event.ApprovedEventView` | Per-Entity / Static | CachingEventService warmup + lazy load | `IEventViewInvalidator` (inline per write) |
| `TrackedCache<Guid, UserConsentInfo>` | Consent | `Consent.UserConsentInfo` | Per-User | CachingConsentService lazy load | `IConsentCacheInvalidator` (synchronous per-user evict on submit) |
| `TrackedCache<Guid, LegalDocumentInfo>` | Legal | `Legal.LegalDocumentInfo` | Per-Entity | CachingLegalDocumentSyncService warmup + lazy load | `ILegalDocumentCacheInvalidator.InvalidateAll` (via `LegalDocumentSaveChangesInterceptor`) |
| `TrackedCache<Guid, RoleAssignmentRow>` | Auth | `Auth.RoleAssignmentRow` | Per-Entity | CachingRoleAssignmentService warmup + lazy load | `IRoleAssignmentCacheInvalidator.InvalidateAll` (service-level) |
| `TrackedCache<Guid, UserEarlyEntry?>` | Early Entry | `EarlyEntry.UserEarlyEntry` | Per-User (negative-result safe) | CachingEarlyEntryService lazy load | `IEarlyEntryInvalidator.InvalidateUser` / `InvalidateAll` (ShiftManagementService, ShiftSignupService, CampService) |

### Cache Issues / Notes

1. **View components still populate two caches** that services
   invalidate. `NavBadgeCounts` and `NotificationBadge:{userId}` are
   populated by `NavBadgesViewComponent` and `NotificationBellViewComponent`
   respectively. This is the same backwards pattern called out in prior
   sweeps — services know how to invalidate but not to recompute.
   (`NobodiesTeamEmails_All` is gone — replaced by a service method.)

2. **Ticket user holdings are tracked, not `IMemoryCache` keys.**
   `CachingTicketQueryService` keeps user holdings in `Tickets.UserHoldings`,
   a `TrackedCache` keyed by user id. Transfer, contact import, account merge,
   and full ticket sync paths clear the affected tracked entries through
   `ITicketCacheInvalidator`; stale entries also reload after the 5-minute
   freshness deadline stored in the tracked value.

3. **`TicketDashboardStats` is invalidation-only, not read-through.**
   `TicketQueryService.GetDashboardStatsAsync()` is the canonical
   producer of the `TicketDashboardStats` DTO — invoked directly by
   `TicketController.Index` per request (passing through the decorator), with
   no read-through caching. The cache key (`CacheKeys.TicketDashboardStats`)
   is kept so a future caching wrapper can be added without changing the
   cache-stats classification.

4. **`CachingEarlyEntryService` caches negative results.** Most users have
   no early entry, so the `EarlyEntry.UserEarlyEntry` tracked cache stores
   the `null` outcome too — otherwise every page render for the no-EE
   majority would re-fan-out across the provider chain.

5. **Caching decorators live in `Humans.Infrastructure`**, not
   `Humans.Application/Services/`, because they are transparent
   decorators over inner Application-layer services (registered keyed
   `"user-inner"`, `"team-inner"`, `"shift-view-inner"`,
   `"ticket-query-inner"`, `"camp-inner"`, `"calendar-inner"`,
   `"event-inner"`, `"role-assignment-inner"`, `"legal-document-sync-inner"`,
   `"early-entry-inner"`, plus the Consent inner key) and inherit
   `TrackedCache<TKey, TValue>` rather than using `IMemoryCache` for
   their projection state.

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
Feedback, Issues, Tickets, Finance, etc.) go entirely through service
interfaces.

### View Components (cache populators)

| Component | Cache Key |
|-----------|-----------|
| **NavBadgesViewComponent** | `NavBadgeCounts` (read/write), `NavBadge:Voting:{userId}` (read/write) |
| **NotificationBellViewComponent** | `NotificationBadge:{userId}` (read/write) |

All other view components read via owning services post-§15 audit. The
former `NobodiesEmailBadgeViewComponent` cache populator was retired
along with the `NobodiesTeamEmails_All` key.

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
| **NavBadgesViewComponent** | GetOrCreate | `NavBadgeCounts`, `NavBadge:Voting:{userId}` |
| **NotificationBellViewComponent** | GetOrCreate | `NotificationBadge:{userId}` |

The §15 work continues to push cache populators into the owning service
behind transparent decorators. The remaining view-component populators
are the next slice; collapsing them into owning-section services would
retire the last out-of-service cache writes. The previous
`NobodiesTeamEmails_All` invalidation that was scattered across three
controllers has already been retired by moving the lookup into
`IUserEmailService.GetNobodiesTeamEmailsByUserIdsAsync`.
