# Service Data Access Map

Audit of which services access which database tables and cache keys, organized by section.
The goal is to identify cross-section table overlap, duplicated caching, and cache configuration issues.

**Generated:** 2026-04-25

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
> `IFullProfileInvalidator`) are resolved to the cache keys their backing
> `MemoryCacheExtensions` invalidator hits.
>
> At ~500-user single-server scale this map is diagnostic, not gating —
> **cross-section table reads are flagged as design-rule violations per
> [`design-rules.md` §"Services own their data"](design-rules.md)**, but
> serve as a backlog rather than a blocker.

---

## Table of Contents

1. [Profile](#profile)
2. [Users](#users)
3. [Onboarding](#onboarding)
4. [Governance](#governance)
5. [Auth](#auth)
6. [Teams](#teams)
7. [Google Integration](#google-integration)
8. [Camps](#camps)
9. [City Planning](#city-planning)
10. [Calendar](#calendar)
11. [Shifts](#shifts)
12. [Legal](#legal)
13. [Consent](#consent)
14. [Notifications](#notifications)
15. [Tickets](#tickets)
16. [Budget](#budget)
17. [Campaigns](#campaigns)
18. [Email](#email)
19. [Feedback](#feedback)
20. [Dashboard](#dashboard)
21. [Gdpr](#gdpr)
22. [AuditLog](#auditlog)
23. [Cross-Section Analysis](#cross-section-analysis)
24. [Cache Inventory](#cache-inventory)
25. [Appendix A: Out-of-Service Database Access](#appendix-a-out-of-service-database-access)
26. [Appendix B: Out-of-Service Cache Access](#appendix-b-out-of-service-cache-access)

---

## Profile

Folder: `src/Humans.Application/Services/Profile/`. Owns `Profiles`,
`ContactFields`, `ProfileLanguages`, `VolunteerHistoryEntries`,
`UserEmails`, `CommunicationPreferences`, `AccountMergeRequests`. The
`IProfileService` registration is wrapped by
`Humans.Infrastructure.Services.Profiles.CachingProfileService` (Singleton
decorator) which holds the canonical `FullProfile` dictionary.

### ProfileService (Scoped — wrapped by CachingProfileService Singleton decorator)

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

Cross-section reads via owning interfaces (no direct table access):
`IUserService`, `IRoleAssignmentService`, `ITeamService`,
`ITicketQueryService`, `IConsentService`, `ICampaignService`,
`IApplicationDecisionService`, `IMembershipCalculator`,
`IOnboardingEligibilityQuery`. No `IMemoryCache` injection; cache lives
in `CachingProfileService` (`_byUserId` `ConcurrentDictionary<Guid, FullProfile>`).

### CachingProfileService (Singleton, Infrastructure)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `_byUserId` `ConcurrentDictionary<Guid, FullProfile>` (in-process, no `IMemoryCache`) | Per-User | yes | yes | yes (per-user evict + warmup hosted service) |

### ContactFieldService (Scoped)

Repositories: `IContactFieldRepository`, `IProfileRepository`.

| Table | R/W |
|-------|-----|
| ContactFields | R/W |
| Profiles | R |

Cross-section reads via `ITeamService`, `IRoleAssignmentService`. Holds
request-scoped permission caches (board-member, coordinator, viewer team
ids); no `IMemoryCache`.

### ContactService (Scoped)

No repository — coordination service over `IUserService`,
`IUserEmailService`, `ICommunicationPreferenceService`,
`IAuditLogService`, plus ASP.NET `UserManager<User>`. No direct table
access, no cache.

### CommunicationPreferenceService (Scoped)

Repository: `ICommunicationPreferenceRepository`.

| Table | R/W |
|-------|-----|
| CommunicationPreferences | R/W |

No cache. Uses `IUnsubscribeTokenProvider` for one-click links.

### UserEmailService (Scoped)

Repository: `IUserEmailRepository`.

| Table | R/W |
|-------|-----|
| UserEmails | R/W |
| Users | R/W (via `IUserEmailRepository` which holds the only direct EF write to `Users.GoogleEmail`/`GoogleEmailStatus`/`Email`) |

Cross-section calls via `IAccountMergeService`, `IUserService`,
`ITeamService`, `IEmailService`, `IFullProfileInvalidator`. No
`IMemoryCache` directly. **Cross-section design-rule note:** the repo's
`Users` writes overlap the User section — this is the audited bridge for
Google email status updates and is intentional per Profile §15 design.

### AccountMergeService (Scoped)

Repositories: `IAccountMergeRepository`, `IProfileRepository`,
`IUserEmailRepository`, `IUserRepository`.

| Table | R/W |
|-------|-----|
| AccountMergeRequests | R/W |
| Profiles | R/W |
| ContactFields | R/W (via `IProfileRepository`) |
| ProfileLanguages | R/W (via `IProfileRepository`) |
| VolunteerHistoryEntries | R/W (via `IProfileRepository`) |
| UserEmails | R/W |
| Users | R/W |
| EventParticipations | R/W (via `IUserRepository`) |
| IdentityUserLogins | R/W (via `IUserRepository.MigrateExternalLoginsAsync`) |

**Cross-section table writes (design-rule violations):** `Users`,
`EventParticipations`, `IdentityUserLogins` are owned by the User section
but written here directly via `IUserRepository`. Tracked under the §15
"merge orchestrator" carve-out.

Cache: invalidates the canonical `FullProfile` cache via
`IFullProfileInvalidator`; ends role assignments via
`IRoleAssignmentService` (which invalidates nav-badge + claims caches).

### DuplicateAccountService (Scoped)

Repositories: `IProfileRepository`, `IUserEmailRepository`,
`IUserRepository`.

| Table | R/W |
|-------|-----|
| Profiles | R/W |
| ContactFields | R/W |
| ProfileLanguages | R/W |
| VolunteerHistoryEntries | R/W |
| UserEmails | R/W |
| Users | R/W |
| EventParticipations | R/W (via `IUserRepository`) |
| IdentityUserLogins | R/W (via `IUserRepository`) |

Same cross-section profile vs user split as AccountMergeService;
invalidates `FullProfile` via `IFullProfileInvalidator`.

---

## Users

Folder: `src/Humans.Application/Services/Users/`. Owns `Users`,
`EventParticipations`, ASP.NET `IdentityUserLogins` (via `Set<>`).

### UserService (Scoped)

Repositories: `IUserRepository`, `IUserEmailRepository`.

| Table | R/W |
|-------|-----|
| Users | R/W |
| UserEmails | R |
| EventParticipations | R/W |
| IdentityUserLogins | R/W (via `IUserRepository.RemoveExternalLoginsAsync` / `MigrateExternalLoginsAsync`) |

Cross-section calls via `IProfileService`, `IShiftManagementService`,
`IShiftSignupService`, `IRoleAssignmentService`, `ITeamService`. Cache
invalidation via `IFullProfileInvalidator`,
`IRoleAssignmentClaimsCacheInvalidator`,
`IShiftAuthorizationInvalidator`. Resolves
`IServiceProvider` for the dependency-cycle-resolution pattern
documented in `design-rules.md §15`.

### AccountProvisioningService (Scoped)

Repositories: `IUserRepository`, `IUserEmailRepository`.

| Table | R/W |
|-------|-----|
| Users | R/W |
| UserEmails | R/W |

No cache.

### UnsubscribeService (Scoped)

Repository: `IUserRepository`.

| Table | R/W |
|-------|-----|
| Users | R |

Calls `ICommunicationPreferenceService` to flip per-category opt-outs;
uses `IDataProtectionProvider` for token validation. No cache.

---

## Onboarding

Folder: `src/Humans.Application/Services/Onboarding/`. Orchestrator
section — owns no DB tables, holds no `IMemoryCache` injection.

### OnboardingService (Scoped)

No repository injected. Cross-section calls via `IProfileService`,
`IUserService`, `IRoleAssignmentService`, `ISystemTeamSync`,
`IApplicationDecisionService`, `IMembershipCalculator`,
`INotificationService`, `INotificationInboxService`, `IEmailService`.
No `IMemoryCache`. State changes flow through the owning services so
cache invalidation happens at the boundary they each own.

---

## Governance

Folder: `src/Humans.Application/Services/Governance/`. Owns
`Applications`, `BoardVotes`.

### ApplicationDecisionService (Scoped)

Repository: `IApplicationRepository`.

| Table | R/W |
|-------|-----|
| Applications | R/W |
| BoardVotes | R/W (removed for GDPR after decision) |

| Cache (via invalidators) | Invalidate |
|-------------------------|------------|
| `NavBadgeCounts` (`INavBadgeCacheInvalidator`) | yes |
| `NotificationMeters` (`INotificationMeterCacheInvalidator`) | yes |
| `NavBadge:Voting:{userId}` (`IVotingBadgeCacheInvalidator`) | yes (per voter) |

Cross-section calls via `IProfileService.SetMembershipTierAsync`,
`IRoleAssignmentService` (term assignments), `ISystemTeamSync`
(provisioning), `INotificationService`, `IEmailService`. No direct
profile/role-assignment table writes — all routed through owning
services.

### MembershipCalculator (Scoped)

No repository. Pure read computation over `IConsentService`,
`ILegalDocumentSyncService`, `IProfileService`, `IRoleAssignmentService`,
`ITeamService`, `IUserService`, `ISystemTeamSync`. No DB access, no
cache.

### MembershipQuery (Scoped)

No repository. Read-only fan-out over `IRoleAssignmentService`,
`ITeamService`, `ISystemTeamSync`. No DB access, no cache.

---

## Auth

Folder: `src/Humans.Application/Services/Auth/`. Owns `RoleAssignments`.

### RoleAssignmentService (Scoped)

Repository: `IRoleAssignmentRepository`.

| Table | R/W |
|-------|-----|
| RoleAssignments | R/W |

| Cache (via invalidators) | Invalidate |
|-------------------------|------------|
| `NavBadgeCounts` (`INavBadgeCacheInvalidator`) | yes |
| `claims:{userId}` (`IRoleAssignmentClaimsCacheInvalidator`) | yes |

Cross-section calls via `IUserService`, `ISystemTeamSync`,
`IAuditLogService`. Implements `IUserDataContributor` for GDPR exports.

### MagicLinkService (Scoped)

Repository: `IUserRepository`. Reads/updates auth-related fields on
`Users`.

| Table | R/W |
|-------|-----|
| Users | R/W |

| Cache Key | TTL | Type |
|-----------|-----|------|
| `magic_link_used:{tokenPrefix}` | 15 min | Rate limit / replay prevention |
| `magic_link_signup:{normalizedEmail}` | 60 sec | Rate limit |

Cross-section calls via `IUserEmailService`, `IEmailService`,
`IMagicLinkRateLimiter`, `IMagicLinkUrlBuilder`,
`IUnsubscribeTokenProvider`.

**Cross-section table read (design-rule violation):** repository writes to
`Users` (User section). MagicLinkService is the only Auth service that
mutates `Users`; consider a dedicated `IUserService.UpdateLoginAsync` to
remove the boundary.

---

## Teams

Folder: `src/Humans.Application/Services/Teams/`. Owns `Teams`,
`TeamMembers`, `TeamJoinRequests`, `TeamRoleAssignments`,
`TeamRoleDefinition`, `GoogleResources` (via `TeamResourceService`),
`GoogleSyncOutboxEvents` for team-mutation outbox writes.

### TeamService (Scoped)

Repository: `ITeamRepository`.

| Table | R/W |
|-------|-----|
| Teams | R/W |
| TeamMembers | R/W |
| TeamJoinRequests | R/W |
| TeamRoleAssignments | R/W |
| TeamRoleDefinition | R/W |
| GoogleSyncOutboxEvents | R/W (outbox events emitted on team mutations) |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `ActiveTeams` (`ConcurrentDictionary<Guid, CachedTeam>`) | 10 min | yes | yes | yes |
| `NotificationMeters` (`INotificationMeterCacheInvalidator`) | 2 min | | | yes |
| `shift-auth:{userId}` (`IShiftAuthorizationInvalidator`) | 60 sec | | | yes |

Cross-section calls via `IRoleAssignmentService`, `IUserService`,
`IShiftManagementService` (lazy via `IServiceProvider` to break cycle),
`ITeamResourceService`, `ISystemTeamSync`, `IEmailService`,
`IAuditLogService`. **Cross-section table read (design-rule violation):**
`GoogleSyncOutboxEvents` is owned by the Google Integration section but
written directly by `ITeamRepository.AddOutboxEventAsync` so team
mutations are atomic with their outbox event. Acceptable per the Google
Integration section's outbox design but worth surfacing.

### TeamPageService (Scoped)

No repository. Read-only fan-out over `IUserService`,
`IProfileService`, `IShiftManagementService`, `ITeamService`,
`ITeamResourceService`. No DB access, no cache.

### TeamResourceService (Scoped)

Repository: `IGoogleResourceRepository`.

| Table | R/W |
|-------|-----|
| GoogleResources | R/W |

Sole owner of `google_resources`. All consumers call
`ITeamResourceService` read methods rather than touching
`DbSet<GoogleResource>`; ownership is enforced by
`scripts/check-google-resource-ownership.sh`. Cross-section calls via
`ITeamService`, `IRoleAssignmentService`, `IGoogleSyncService`,
`ITeamResourceGoogleClient`, `IAuditLogService`. No cache.

---

## Google Integration

Folder: `src/Humans.Application/Services/GoogleIntegration/`. Owns
`GoogleResources` (via `TeamResourceService` in Teams),
`GoogleSyncOutboxEvents`, `SyncServiceSettings`. `Users` writes for
`GoogleEmail`/`GoogleEmailStatus` happen through `IUserService` /
`IUserEmailService` per §15.

### GoogleWorkspaceSyncService (Scoped)

Repositories: `IGoogleResourceRepository`, `IGoogleSyncOutboxRepository`.

| Table | R/W |
|-------|-----|
| GoogleResources | R/W |
| GoogleSyncOutboxEvents | R/W |

Cross-section calls via `IUserService`, `ITeamService`,
`ITeamResourceService`, `IUserEmailService`, `ISyncSettingsService`,
`IAuditLogService`, `IGoogleDirectoryClient`,
`IGoogleDrivePermissionsClient`, `IGoogleGroupMembershipClient`,
`IGoogleGroupProvisioningClient`, `ITeamResourceGoogleClient`. Lazy
`IServiceProvider` resolution for parallel/per-batch scope creation. No
`IMemoryCache`.

### GoogleAdminService (Scoped)

Repositories: `IUserEmailRepository`, `IUserRepository`. (Direct
repository injection — bypasses owning services.)

| Table | R/W |
|-------|-----|
| UserEmails | R/W |
| Users | R/W |

**Cross-section table writes (design-rule violation):** `Users` and
`UserEmails` are owned by the User and Profile sections respectively.
Direct repo injection bypasses `IUserService`/`IUserEmailService`. Should
route through the owning services.

Cross-section calls via `ITeamService`, `ITeamResourceService`,
`IGoogleSyncService`, `IGoogleWorkspaceUserService`, `IUserService`,
`IUserEmailService`, `IAuditLogService`. No cache.

### GoogleWorkspaceUserService (Scoped)

No repository, no DB access. Pure Google Directory API wrapper
(`IWorkspaceUserDirectoryClient`). No cache.

### EmailProvisioningService (Scoped)

No repository injected — orchestrates `IUserService`,
`IUserEmailService`, `IProfileService`, `ITeamService`,
`IGoogleWorkspaceUserService`, `INotificationService`, `IEmailService`,
`IAuditLogService`. No DB access, no cache.

### SyncSettingsService (Scoped)

Repository: `ISyncSettingsRepository`.

| Table | R/W |
|-------|-----|
| SyncServiceSettings | R/W |

No cache.

### DriveActivityMonitorService (Scoped)

Repository: `IDriveActivityMonitorRepository`.

| Table | R/W |
|-------|-----|
| GoogleResources | R |
| AuditLogEntries | R/W |
| SystemSettings | R/W |
| Users | R |
| IdentityUserLogins | R |

**Cross-section table reads (design-rule violation):** `Users` (User
section), `IdentityUserLogins` (Auth/Identity), `AuditLogEntries`
(AuditLog section), `SystemSettings` (no clear owner — see Cross-Section
Analysis). The repo bundles them because the Drive Activity reconciler
needs to correlate Drive events to user PeopleIds and SystemSettings
holds the watermark cursor; should split into owning-service calls.

Cross-section calls via `ITeamResourceService`,
`IGoogleDriveActivityClient`. No cache.

---

## Camps

Folder: `src/Humans.Application/Services/Camps/`. Owns `Camps`,
`CampSeasons`, `CampLeads`, `CampHistoricalNames`, `CampImages`,
`CampSettings`, `CampMembers`.

### CampService (Scoped)

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

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `camps_year_{year}` | 5 min | yes | yes | yes |
| `CampSettings` | 5 min | yes | yes | yes |
| `NavBadge:CampLeadJoinRequests:{userId}` (`ICampLeadJoinRequestsBadgeCacheInvalidator`) | 2 min | | | yes (per lead) |

Cross-section calls via `IUserService`, `ISystemTeamSync`,
`IAuditLogService`, `ICampImageStorage`. Implements
`IUserDataContributor` for GDPR exports.

### CampContactService (Scoped)

No repository — pure messaging/audit orchestrator.

| Cache Key | TTL | Type |
|-----------|-----|------|
| `CampContactRateLimit:{userId}:{campId}` | 10 min | Rate limit |

Cross-section calls via `IEmailService`, `IAuditLogService`.

---

## City Planning

Folder: `src/Humans.Application/Services/CityPlanning/`. Owns
`CampPolygons`, `CampPolygonHistories`, `CityPlanningSettings`.

### CityPlanningService (Scoped)

Repository: `ICityPlanningRepository`.

| Table | R/W |
|-------|-----|
| CampPolygons | R/W |
| CampPolygonHistories | R/W |
| CityPlanningSettings | R/W |

Cross-section calls via `ICampService` (camp/season reads),
`IProfileService`, `ITeamService`, `IUserService`. No direct cross-section
table reads — all routed through owning services. No cache.

---

## Calendar

Folder: `src/Humans.Application/Services/Calendar/`. Owns
`CalendarEvents`, `CalendarEventExceptions`.

### CalendarService (Scoped)

Repository: `ICalendarRepository`.

| Table | R/W |
|-------|-----|
| CalendarEvents | R/W |
| CalendarEventExceptions | R/W |

| Cache Key | TTL | Type |
|-----------|-----|------|
| `calendar:active-events` | short-TTL marker | Static (per `design-rules §15f` request-acceleration carve-out) |

Cross-section calls via `ITeamService` (in-memory team-name join per
§6b), `IAuditLogService`. No cross-section table reads.

---

## Shifts

Folder: `src/Humans.Application/Services/Shifts/`. Owns `Rotas`,
`Shifts`, `ShiftSignups`, `ShiftTags`, `VolunteerEventProfiles`,
`VolunteerTagPreferences`, `EventSettings`, `GeneralAvailability`.

### ShiftManagementService (Scoped)

Repository: `IShiftManagementRepository`. Implements
`IShiftAuthorizationInvalidator`.

| Table | R/W |
|-------|-----|
| Rotas | R/W |
| Shifts | R/W |
| ShiftSignups | R |
| ShiftTags | R/W |
| VolunteerEventProfiles | R/W |
| VolunteerTagPreferences | R/W |
| EventSettings | R/W |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `shift-auth:{userId}` | 60 sec | yes | yes | yes |

Cross-section calls via `ITeamService`, `IRoleAssignmentService`,
`IUserService`, `ITicketQueryService`, `IAuditLogService`. Lazy
`IServiceProvider` resolution for cycle-breaking.

### ShiftSignupService (Scoped)

Repository: `IShiftSignupRepository`.

| Table | R/W |
|-------|-----|
| ShiftSignups | R/W |
| Shifts | R |
| Rotas | R/W |
| VolunteerEventProfiles | R/W |
| VolunteerTagPreferences | R |
| GeneralAvailability | R |

**Cross-section table read (design-rule violation):**
`GeneralAvailability` is owned by `GeneralAvailabilityService`. The
repository reads it for shift conflict checks; should route through
`IGeneralAvailabilityService`.

Cross-section calls via `IShiftManagementService`, `ITeamService`,
`INotificationService`, `IAuditLogService`. Lazy `IServiceProvider`.
Implements `IUserDataContributor`. No cache.

### GeneralAvailabilityService (Scoped)

Repository: `IGeneralAvailabilityRepository`.

| Table | R/W |
|-------|-----|
| GeneralAvailability | R/W |

No cache. No cross-section deps beyond `IClock`.

---

## Legal

Folder: `src/Humans.Application/Services/Legal/`. Owns `LegalDocuments`,
`DocumentVersions`.

### LegalDocumentService (Scoped)

No repository — wraps `IGitHubLegalDocumentConnector` for live document
fetches.

| Cache Key | TTL | Read | Write |
|-----------|-----|------|-------|
| `Legal:{slug}` | 1 hr (30 sec on failure) | yes | yes |

### LegalDocumentSyncService (Scoped)

Repository: `ILegalDocumentRepository`.

| Table | R/W |
|-------|-----|
| LegalDocuments | R/W |
| DocumentVersions | R/W |

Cross-section calls via `IProfileService` (re-consent reminders),
`INotificationService`, `IGitHubLegalDocumentConnector`. No cache.

### AdminLegalDocumentService (Scoped)

Repository: `ILegalDocumentRepository`.

| Table | R/W |
|-------|-----|
| LegalDocuments | R/W |
| DocumentVersions | R/W |

Cross-section calls via `ITeamService`, `ILegalDocumentSyncService`. No
cache.

---

## Consent

Folder: `src/Humans.Application/Services/Consent/`. Owns
`ConsentRecords` (append-only — DB triggers prevent UPDATE/DELETE).

### ConsentService (Scoped)

Repository: `IConsentRepository`.

| Table | R/W |
|-------|-----|
| ConsentRecords | R/W (INSERT-only enforced by trigger) |

Cross-section calls via `IProfileService`, `IOnboardingService` (lazy
via `IServiceProvider` for cycle-breaking), `IMembershipCalculator`,
`ILegalDocumentSyncService`, `INotificationInboxService`,
`ISystemTeamSync`. Implements `IUserDataContributor`. No cache.

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

| Cache Key | Invalidate |
|-----------|------------|
| `NotificationBadge:{userId}` | yes (per recipient) |

Cross-section calls via `INotificationRecipientResolver`,
`ICommunicationPreferenceService`.

### NotificationEmitter (Scoped)

Repository: `INotificationRepository`. Companion to
`NotificationService` for batch emit.

| Table | R/W |
|-------|-----|
| Notifications | R/W |
| NotificationRecipients | R/W |

| Cache Key | Invalidate |
|-----------|------------|
| `NotificationBadge:{userId}` | yes (single-recipient direct emits) |

Cross-section calls via `INotificationRecipientResolver`,
`ICommunicationPreferenceService`, `IRoleAssignmentService`,
`ITeamService`.

### NotificationInboxService (Scoped)

Repository: `INotificationRepository`.

| Table | R/W |
|-------|-----|
| Notifications | R/W |
| NotificationRecipients | R/W |

| Cache Key | Invalidate |
|-----------|------------|
| `NotificationBadge:{userId}` | yes |

Cross-section calls via `IUserService`. Implements
`IUserDataContributor`.

### NotificationRecipientResolver (Scoped)

No repository, no DB access. Resolves recipient sets from
`IRoleAssignmentService`, `ITeamService`. No cache.

### NotificationMeterProvider (Scoped)

No repository — aggregates counts via owning services.

| Cache Key | TTL | Read | Write |
|-----------|-----|------|-------|
| `NotificationMeters` | 2 min | yes | yes |
| `NavBadge:Voting:{userId}` | 2 min | yes | yes |
| `NavBadge:CampLeadJoinRequests:{userId}` | 2 min | yes | yes |

Cross-section calls via `IProfileService`, `IUserService`,
`ITeamService`, `IApplicationDecisionService`, `ITicketSyncService`,
`IGoogleSyncService`, `ICampService`. No direct table reads — fully
fan-out aggregator after the §15 migration.

---

## Tickets

Folder: `src/Humans.Application/Services/Tickets/`. Owns `TicketOrders`,
`TicketAttendees`, `TicketSyncStates`. Vendor HTTP calls live in
`Humans.Infrastructure.Services.TicketTailorService` (out-of-section).

### TicketQueryService (Scoped)

Repository: `ITicketRepository`.

| Table | R/W |
|-------|-----|
| TicketOrders | R |
| TicketAttendees | R |
| TicketSyncStates | R |
| UserEmails | R (via `ITicketRepository.GetAllUserEmailLookupEntriesAsync`, used to match attendees) |

**Cross-section table read (design-rule violation):** `UserEmails`
(Profile section). The repository materializes a lookup-entry projection
from `UserEmails` for attendee matching; should route through
`IUserEmailService` (and potentially expose a bulk lookup method there).

Cross-section calls via `IUserService`, `IUserEmailService`,
`ITeamService`, `ICampaignService`, `IBudgetService`,
`IShiftManagementService`, `IProfileService`. Implements
`IUserDataContributor`.

| Cache Key | TTL | Read | Write |
|-----------|-----|------|-------|
| `UserTicketCount:{userId}` | 5 min | yes | yes |
| `UserIdsWithTickets` | 5 min | yes | yes |
| `ValidAttendeeEmails` | 5 min | yes | yes |

### TicketSyncService (Scoped)

Repository: `ITicketRepository`.

| Table | R/W |
|-------|-----|
| TicketOrders | R/W |
| TicketAttendees | R/W |
| TicketSyncStates | R/W |
| UserEmails | R (via ticket repo's email-lookup projection) |

| Cache Key | Invalidate |
|-----------|------------|
| `TicketEventSummary:{eventId}` | yes |
| `TicketDashboardStats` | yes (via `InvalidateTicketCaches`) |
| `UserIdsWithTickets` | yes (via `InvalidateTicketCaches`) |
| `ValidAttendeeEmails` | yes (via `InvalidateTicketCaches`) |

Cross-section calls via `IUserService`, `ICampaignService`,
`IShiftManagementService`, `ITicketVendorService`, `IStripeService`. No
direct cross-section table writes — `event_participations` and
`campaign_grants` updates route through `IUserService` and
`ICampaignService` respectively per §15.

### TicketingBudgetService (Scoped)

Repositories: `ITicketingBudgetRepository`. (`IBudgetService` also
injected for cross-section budget mutations.)

| Table | R/W |
|-------|-----|
| TicketOrders | R |

Cross-section budget writes (BudgetLineItems, TicketingProjections) now
route through `IBudgetService` rather than directly. No cache.

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

Cross-section calls via `ITeamService` for permission resolution.
Implements `IUserDataContributor`. No cache.

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

Cross-section calls via `IUserService`, `IUserEmailService`,
`ITeamService`, `INotificationService`,
`ICommunicationPreferenceService`, `IEmailService`. Email outbox
enqueues now route through `IEmailService` (no direct
`EmailOutboxMessages` writes from this service after §15). Implements
`IUserDataContributor`. No cache.

---

## Email

Folder: `src/Humans.Application/Services/Email/`. Owns
`EmailOutboxMessages`. `OutboxEmailService` is the public facade
(`IEmailService` impl) that enqueues; `EmailOutboxService` owns persisted
outbox processing. SMTP transport / rendering live in Infrastructure.

### OutboxEmailService (Scoped, registered as `IEmailService`)

Repository: `IEmailOutboxRepository`.

| Table | R/W |
|-------|-----|
| EmailOutboxMessages | R/W |
| SystemSettings | R (via outbox repo's diagnostic/cursor reads) |

Cross-section calls via `IUserEmailService`,
`ICommunicationPreferenceService`, `IEmailBodyComposer`,
`IEmailRenderer`, `IEmailTransport`. No cache.

### EmailOutboxService (Scoped)

Repository: `IEmailOutboxRepository`.

| Table | R/W |
|-------|-----|
| EmailOutboxMessages | R/W |
| SystemSettings | R/W (cursor / `ProcessEmailOutboxJob` watermark) |

**Cross-section table read/write (design-rule violation):**
`SystemSettings` has no clear owner; the outbox repo touches it for the
processor's last-run watermark. Tracked under the SystemSettings
ownership question.

No cache.

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
`IAuditLogService`. Implements `IUserDataContributor`.

---

## Dashboard

Folder: `src/Humans.Application/Services/Dashboard/`. Read-only
aggregator — owns no DB tables.

### DashboardService (Scoped)

No repository. Pure fan-out over `IProfileService`, `IUserService`,
`ITeamService`, `IShiftManagementService`, `IShiftSignupService`,
`ITicketQueryService`, `IMembershipCalculator`,
`IApplicationDecisionService`. No DB access, no cache.

---

## Gdpr

Folder: `src/Humans.Application/Services/Gdpr/`. Aggregator —
fan-out over every section's `IUserDataContributor` to build the export.

### GdprExportService (Scoped)

No repository. Iterates `IEnumerable<IUserDataContributor>` registered by
sections (Profile, Auth, AuditLog, Camp, Campaign, Tickets, Shifts,
Notifications, etc.). No DB access, no cache.

---

## AuditLog

Folder: `src/Humans.Application/Services/AuditLog/`. Owns
`AuditLogEntries` (append-only per design-rules §12).

### AuditLogService (Scoped)

Repository: `IAuditLogRepository` (registered Singleton, factory-based).

| Table | R/W |
|-------|-----|
| AuditLogEntries | R/W (append-only) |
| Users | R (display-name lookup for UI rendering) |
| Teams | R (team-name lookup for UI rendering) |

**Cross-section table reads (design-rule violation, deliberate):** the
audit-log UI needs actor/subject display names without pulling
controllers into `DbContext`. The repository exposes
`GetUserDisplayNamesAsync` / `GetTeamNamesAsync` projections; consider
routing through `IUserService.GetDisplayNamesAsync` /
`ITeamService.GetTeamNamesByIdsAsync` to remove the boundary.

Implements `IUserDataContributor`. No cache.

---

## Cross-Section Analysis

### Tables Accessed by Multiple Sections (via repository)

After the §15 migration moved most cross-section access through owning
service interfaces, only a few repositories still reach across
boundaries directly. These are the remaining design-rule violations.

| Table | Owning Section | Cross-Section Repo Readers (violations) |
|-------|----------------|-----------------------------------------|
| **Users** | Users | Profile (`AccountMergeService`, `DuplicateAccountService` via `IUserRepository`), GoogleIntegration (`GoogleAdminService` via `IUserRepository`), Auth (`MagicLinkService` via `IUserRepository`), AuditLog (display lookup), Google (`DriveActivityMonitorRepository`) |
| **UserEmails** | Profile | GoogleIntegration (`GoogleAdminService` via `IUserEmailRepository`), Tickets (`TicketRepository` via `Set<UserEmail>` for attendee email lookup), Auth (`MagicLinkService` reads `UserEmail` via `IUserRepository`), Profile (`AccountMergeService`/`DuplicateAccountService` via `IUserEmailRepository`) |
| **EventParticipations** | Users | Profile (`AccountMergeService`, `DuplicateAccountService` via `IUserRepository`) |
| **IdentityUserLogins** | Auth/Identity | Profile (`AccountMergeService`, `DuplicateAccountService` via `IUserRepository.MigrateExternalLoginsAsync`/`RemoveExternalLoginsAsync`), GoogleIntegration (`DriveActivityMonitorRepository`) |
| **AuditLogEntries** | AuditLog | GoogleIntegration (`DriveActivityMonitorRepository`) |
| **GoogleSyncOutboxEvents** | GoogleIntegration | Teams (`TeamRepository` writes outbox events on team mutations) |
| **GeneralAvailability** | Shifts (GeneralAvailabilityService) | Shifts (`ShiftSignupRepository` reads it for conflict checks) |
| **SystemSettings** | (no clear owner — see below) | GoogleIntegration (`DriveActivityMonitorRepository`), Email (`EmailOutboxRepository`) |

### Notable Cross-Section Patterns

1. **Profile↔User Identity coupling.** `AccountMergeService` /
   `DuplicateAccountService` write `Users`, `EventParticipations`, and
   `IdentityUserLogins` directly via `IUserRepository`. This is the §15
   "merge orchestrator" carve-out — the merge needs all
   user-identity-linked tables in one transaction. Long-term it should
   move to a `IUserService.MergeAccountsAsync` orchestrator.

2. **Tickets ↔ Profile email lookup.** `TicketRepository`
   materializes a `UserEmail` projection (`UserEmailLookupEntry`) for
   attendee matching. `IUserEmailService` does not yet expose a bulk
   lookup; this is the cleanest single fix.

3. **Teams ↔ Google outbox.** `TeamRepository` writes
   `GoogleSyncOutboxEvents` so each team mutation is atomic with its
   outbox event. The Google Integration section reads/processes them
   via `IGoogleSyncOutboxRepository`. The atomicity benefit outweighs
   the boundary cost.

4. **DriveActivityMonitor reaches into four sections.**
   `DriveActivityMonitorRepository` reads `Users`, `IdentityUserLogins`,
   `AuditLogEntries`, `SystemSettings`. The cleanup path is to inject
   the owning services and reduce the repo to `GoogleResources`-only
   reads.

5. **SystemSettings has no owner.** Two repositories
   (`DriveActivityMonitorRepository`, `EmailOutboxRepository`) write
   their cursors/watermarks to `SystemSettings`. There is no
   `ISystemSettingsService` — this is a known gap.

6. **CachingProfileService is the canonical Profile cache.** Per §15
   the legacy `ApprovedProfiles` `IMemoryCache` entry is gone; the
   `_byUserId` `ConcurrentDictionary<Guid, FullProfile>` inside the
   Singleton decorator is now the single source of cached profile
   truth, evicted via `IFullProfileInvalidator`.

7. **Notification meters are computed, not queried.**
   `NotificationMeterProvider` no longer reads any tables directly —
   every counter fans out through an owning-service interface call
   (`IProfileService`, `IUserService`, `ITeamService`,
   `IApplicationDecisionService`, `ITicketSyncService`,
   `IGoogleSyncService`, `ICampService`). Cache invalidation goes
   through `INotificationMeterCacheInvalidator`.

---

## Cache Inventory

### All Cache Keys

Sourced from `src/Humans.Application/CacheKeys.cs` and
`src/Humans.Application/Extensions/MemoryCacheExtensions.cs`.

| Key | TTL | Type | Populated By | Invalidated By |
|-----|-----|------|-------------|----------------|
| `NavBadgeCounts` | 2 min | Static | **NavBadgesViewComponent** | `INavBadgeCacheInvalidator` (FeedbackService, ApplicationDecisionService, RoleAssignmentService, OnboardingService via cross-cuts) |
| `NotificationBadge:{userId}` | 2 min | Per-User | **NotificationBellViewComponent** | NotificationService, NotificationEmitter, NotificationInboxService |
| `NotificationMeters` | 2 min | Static | NotificationMeterProvider | `INotificationMeterCacheInvalidator` (TeamService, ApplicationDecisionService) |
| `ActiveTeams` | 10 min | Static | TeamService (`ConcurrentDictionary<Guid, CachedTeam>` projection) | TeamService, `IActiveTeamsCacheInvalidator` |
| `claims:{userId}` | 60 sec | Per-User | (claims principal factory) | `IRoleAssignmentClaimsCacheInvalidator` (RoleAssignmentService, UserService) |
| `shift-auth:{userId}` | 60 sec | Per-User | ShiftManagementService | ShiftManagementService, `IShiftAuthorizationInvalidator` (TeamService, UserService) |
| `NavBadge:Voting:{userId}` | 2 min | Per-User | NotificationMeterProvider | `IVotingBadgeCacheInvalidator` (ApplicationDecisionService) |
| `NavBadge:CampLeadJoinRequests:{userId}` | 2 min | Per-User | NotificationMeterProvider | `ICampLeadJoinRequestsBadgeCacheInvalidator` (CampService) |
| `camps_year_{year}` | 5 min | Per-Entity | CampService | CampService |
| `CampSettings` | 5 min | Static | CampService | CampService |
| `Legal:{slug}` | 1 hr | Per-Entity | LegalDocumentService | LegalDocumentService |
| `UserTicketCount:{userId}` | 5 min | Per-User | TicketQueryService | (TTL-based only) |
| `UserIdsWithTickets` | 5 min | Static | TicketQueryService | TicketSyncService (`InvalidateTicketCaches`) |
| `ValidAttendeeEmails` | 5 min | Static | TicketQueryService | TicketSyncService (`InvalidateTicketCaches`) |
| `TicketEventSummary:{eventId}` | 15 min | Per-Entity | TicketTailorService (Infrastructure) | TicketSyncService |
| `TicketDashboardStats` | 5 min | Static | (unknown populator — see below) | TicketSyncService |
| `NobodiesTeamEmails_All` | 2 min | Static | **NobodiesEmailBadgeViewComponent** | GoogleController |
| `CampContactRateLimit:{userId}:{campId}` | 10 min | Rate Limit | CampContactService | CampContactService |
| `magic_link_used:{tokenPrefix}` | 15 min | Rate Limit | MagicLinkService | MagicLinkService |
| `magic_link_signup:{normalizedEmail}` | 60 sec | Rate Limit | MagicLinkService | MagicLinkService |
| `calendar:active-events` | short-TTL | Static (request-acceleration) | CalendarService | CalendarService |
| (`_byUserId` in CachingProfileService) | per-process | Per-User | CachingProfileService warmup + lazy load | `IFullProfileInvalidator` (Profile section + cross-section call sites) |

### Cache Issues / Notes

1. **View components still populate two caches** that services
   invalidate. `NavBadgeCounts` is populated by `NavBadgesViewComponent`
   and `NotificationBadge:{userId}` by `NotificationBellViewComponent`,
   both invalidated through the section invalidator interfaces. This is
   the same backwards pattern as before §15 — services know how to
   invalidate but not to recompute.

2. **`UserTicketCount:{userId}` has no explicit invalidation.** Per-user
   ticket counts cache for 5 min and rely on TTL only;
   `InvalidateTicketCaches` deliberately skips per-user keys at
   ~500-user scale per the comment in `MemoryCacheExtensions`.

3. **`TicketDashboardStats` populator unidentified in service layer.**
   `TicketSyncService` invalidates the key but no service writes it; it
   is presumably written by a controller or view component (see
   Appendix A).

4. **`NobodiesTeamEmails_All`** is still populated by
   `NobodiesEmailBadgeViewComponent` and invalidated by
   `GoogleController`. No service involvement — should move into the
   Google or Email section.

5. **CachingProfileService is in `Humans.Infrastructure`**, not
   `Humans.Application/Services/`, because it is a transparent
   decorator over the inner Application-layer `ProfileService`
   (registered keyed `"profile-inner"`) and uses a plain
   `ConcurrentDictionary` rather than `IMemoryCache` for the
   `FullProfile` projection.

---

## Appendix A: Out-of-Service Database Access

Controllers and view components that inject `HumansDbContext` or
repositories directly, bypassing the service layer. After the §15
migration most controllers go through services, but a few legacy
direct-DB call sites remain.

### Controllers (still needing audit pass)

| Controller | Notes |
|------------|-------|
| **AdminController** | Migration metadata reads, Hangfire lock table SQL. Legitimate infrastructure. |
| **DevLoginController** | Camps/CampSeasons/CampLeads seeding for dev. Legitimate dev-only path. |
| **EmailController** | `SystemSettings` read/write for outbox cursor (no `ISystemSettingsService` exists). |
| **GoogleController** | Invalidates `NobodiesTeamEmails_All`. |
| **ProfileController**, **BoardController**, **BudgetController**, **CampAdminController**, **GuestController**, **UnsubscribeController** | Re-audit needed against current code; existing direct-DB queries from before §15 may have moved into services. |

### View Components (cache populators)

| Component | Cache Key |
|-----------|-----------|
| **NavBadgesViewComponent** | `NavBadgeCounts` (read/write) |
| **NotificationBellViewComponent** | `NotificationBadge:{userId}` (read/write) |
| **NobodiesEmailBadgeViewComponent** | `NobodiesTeamEmails_All` (read/write) |
| **AuditLogViewComponent**, **MyGoogleResourcesViewComponent**, **ProfileCardViewComponent** | Read via owning services after §15 audit. |

### Background Jobs (Infrastructure)

Jobs live in `Humans.Infrastructure.Jobs` and may use repositories
directly. Mutation-heavy logic should funnel into services even from
jobs. Specific jobs and their tables vary; treat each as an audit item
per the Profile §15 / Teams §15 / Google §15 carve-outs.

---

## Appendix B: Out-of-Service Cache Access

Controllers and components that touch `IMemoryCache` directly.

| Controller / Component | Cache Operation | Key |
|------------------------|-----------------|-----|
| **GoogleController** | Remove | `NobodiesTeamEmails_All` |
| **DevLoginController** | Remove | `ActiveTeams`, `claims:{userId}` (via `InvalidateUserAccess`) |
| **NavBadgesViewComponent** | GetOrCreate | `NavBadgeCounts` |
| **NotificationBellViewComponent** | GetOrCreate | `NotificationBadge:{userId}` |
| **NobodiesEmailBadgeViewComponent** | TryGetValue / Set | `NobodiesTeamEmails_All` |

The §15 work continues to push cache populators into the owning service
behind transparent decorators. The remaining view-component populators
are the next slice.
