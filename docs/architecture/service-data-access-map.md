# Service Data Access Map

Audit of which services access which database tables and cache keys, organized by section.
The goal is to identify cross-section table overlap, duplicated caching, and cache configuration issues.

**Generated:** 2026-04-10

---

## Table of Contents

1. [Profiles & Accounts](#profiles--accounts)
2. [Onboarding & Governance](#onboarding--governance)
3. [Teams](#teams)
4. [Google Integration](#google-integration)
5. [Camps & City Planning](#camps--city-planning)
6. [Shifts](#shifts)
7. [Legal & Consent](#legal--consent)
8. [Notifications](#notifications)
9. [Tickets](#tickets)
10. [Budget](#budget)
11. [Campaigns](#campaigns)
12. [Email](#email)
13. [Feedback](#feedback)
14. [Admin & Infrastructure](#admin--infrastructure)
15. [Cross-Section Analysis](#cross-section-analysis)
16. [Cache Inventory](#cache-inventory)
17. [Appendix A: Out-of-Service Database Access](#appendix-a-out-of-service-database-access)
18. [Appendix B: Out-of-Service Cache Access](#appendix-b-out-of-service-cache-access)

---

## Profiles & Accounts

### ProfileService (Scoped)

| Table | R/W |
|-------|-----|
| Profiles | R/W |
| Users | R/W |
| Applications | R/W |
| CampaignGrants | R |
| EventSettings | R |
| TicketSyncStates | R |
| TicketOrders | R |
| TicketAttendees | R |
| TeamRoleAssignments | R/W |
| TeamMembers | R |
| VolunteerHistoryEntries | R |
| ContactFields | R |
| CommunicationPreferences | R |
| ConsentRecords | R |
| VolunteerEventProfiles | R/W |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `UserProfile:{userId}` | 2 min | yes | yes | yes |
| `ApprovedProfiles` | 10 min | yes | yes | yes |
| `NavBadgeCounts` | 2 min | | | yes |
| `NotificationMeters` | 2 min | | | yes |
| `ActiveTeams` | 10 min | | | yes |
| `claims:{userId}` | 60 sec | | | yes |

### ContactFieldService (Scoped)

| Table | R/W |
|-------|-----|
| Profiles | R |
| ContactFields | R/W |

No distributed cache. Uses request-scoped in-memory fields for permission checks (board member, coordinator, team IDs) to avoid N+1.

### ContactService (Scoped)

| Table | R/W |
|-------|-----|
| Users | R/W |
| UserEmails | R/W |
| CommunicationPreferences | R |

No cache.

### AccountProvisioningService (Scoped)

| Table | R/W |
|-------|-----|
| Users | R/W |
| UserEmails | R/W |

No cache.

### AccountMergeService (Scoped)

| Table | R/W |
|-------|-----|
| AccountMergeRequests | R/W |
| Users | R/W |
| RoleAssignments | R/W |
| IdentityUserLogins | R/W |
| UserEmails | R/W |
| Profiles | R/W |
| ContactFields | R/W |
| VolunteerHistoryEntries | R/W |

| Cache Key | Invalidate |
|-----------|------------|
| `ApprovedProfiles` | yes |
| `ActiveTeams` (via RemoveMemberFromAllTeamsCache) | yes |

### DuplicateAccountService (Scoped)

| Table | R/W |
|-------|-----|
| Users | R |
| UserEmails | R/W |
| Profiles | R/W |
| TeamMembers | R |
| RoleAssignments | R |
| IdentityUserLogins | R/W |
| ContactFields | R/W |
| VolunteerHistoryEntries | R/W |

| Cache Key | Invalidate |
|-----------|------------|
| `ApprovedProfiles` | yes |
| `ActiveTeams` (via RemoveMemberFromAllTeamsCache) | yes |

### MembershipCalculator (Scoped)

| Table | R/W |
|-------|-----|
| Profiles | R |
| TeamMembers | R |
| RoleAssignments | R |
| LegalDocuments | R |
| DocumentVersions | R |
| ConsentRecords | R |
| Users | R |

No cache. Pure computation.

### VolunteerHistoryService (Scoped)

| Table | R/W |
|-------|-----|
| VolunteerHistoryEntries | R/W |
| Profiles | R |

| Cache Key | Invalidate |
|-----------|------------|
| `ApprovedProfiles` | yes (updates entry in bulk cache) |

---

## Onboarding & Governance

### OnboardingService (Scoped)

| Table | R/W |
|-------|-----|
| Profiles | R/W |
| Users | R |
| Applications | R |
| BoardVotes | R/W |
| RoleAssignments | R |
| TeamMembers | R |
| UserEmails | R/W |

| Cache Key | Invalidate |
|-----------|------------|
| `NavBadgeCounts` | yes |
| `NotificationMeters` | yes |
| `UserProfile:{userId}` | yes |
| `ApprovedProfiles` | yes (add/remove) |
| `NavBadge:Voting:{userId}` | yes |
| `UserAccess:{userId}` (composite) | yes |

### ApplicationDecisionService (Scoped)

| Table | R/W |
|-------|-----|
| Applications | R/W |
| BoardVotes | R/W (removed for GDPR after decision) |
| Profiles | R/W (MembershipTier update) |

| Cache Key | Invalidate |
|-----------|------------|
| `NavBadgeCounts` | yes |
| `NotificationMeters` | yes |
| `NavBadge:Voting:{userId}` | yes (per voter) |

### RoleAssignmentService (Scoped)

| Table | R/W |
|-------|-----|
| RoleAssignments | R/W |

| Cache Key | Invalidate |
|-----------|------------|
| `NavBadgeCounts` | yes |
| `claims:{userId}` | yes |

---

## Teams

### TeamService (Scoped)

| Table | R/W |
|-------|-----|
| Teams | R/W |
| TeamMembers | R/W |
| TeamJoinRequests | R/W |
| TeamRoleDefinitions | R/W |
| TeamRoleAssignments | R/W |
| Users | R |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `ActiveTeams` | 10 min | yes | yes | yes |
| `shift-auth:{userId}` | 60 sec | | | yes |
| `NotificationMeters` | 2 min | | | yes |

### TeamPageService (Scoped)

| Table | R/W |
|-------|-----|
| Users | R |
| Teams | R |
| Rotas | R |

No direct cache. Delegates to other services.

### TeamResourceService (Scoped)

| Table | R/W |
|-------|-----|
| GoogleResources | R/W |

No cache.

### TeamResourcePersistence (static helper, not DI-registered)

| Table | R/W |
|-------|-----|
| GoogleResources | R/W |

No cache.

---

## Google Integration

### GoogleWorkspaceSyncService (Scoped)

| Table | R/W |
|-------|-----|
| GoogleResources | R/W |
| TeamMembers | R |
| Teams | R |
| Users | R/W |
| GoogleSyncOutboxEvents | R/W |
| SyncServiceSettings | R |

No direct cache. Uses `IDbContextFactory` + `SemaphoreSlim(5)` for parallel safe DB access.

### GoogleAdminService (Scoped)

| Table | R/W |
|-------|-----|
| UserEmails | R/W |
| Users | R/W |
| Teams | R |
| TeamMembers | R |
| GoogleResources | R |

No cache.

### GoogleWorkspaceUserService (Scoped)

No database access. Pure Google Directory API wrapper.

### EmailProvisioningService (Scoped)

| Table | R/W |
|-------|-----|
| Users | R/W |

No cache. Delegates to UserEmailService for email record creation.

### SyncSettingsService (Scoped)

| Table | R/W |
|-------|-----|
| SyncServiceSettings | R/W |

No cache.

### DriveActivityMonitorService (Scoped)

| Table | R/W |
|-------|-----|
| GoogleResources | R |
| SystemSettings | R/W |
| IdentityUserLogins | R |
| Users | R |

Per-invocation in-memory dictionary for people ID resolution (not IMemoryCache).

---

## Camps & City Planning

### CampService (Scoped)

| Table | R/W |
|-------|-----|
| Camps | R/W |
| CampSeasons | R/W |
| CampLeads | R/W |
| CampHistoricalNames | R/W |
| CampImages | R/W |
| CampSettings | R/W |
| AuditLogEntries | W |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `camps_year_{year}` | 5 min | yes | yes | yes |
| `CampSettings` | 5 min | yes | yes | yes |

### CampContactService (Scoped)

| Table | R/W |
|-------|-----|
| AuditLogEntries | W |

| Cache Key | TTL | Type |
|-----------|-----|------|
| `CampContactRateLimit:{userId}:{campId}` | 10 min | Rate limit |

### CityPlanningService (Scoped)

| Table | R/W |
|-------|-----|
| CampPolygons | R/W |
| CampSeasons | R/W |
| Camps | R |
| CampPolygonHistories | R/W |
| Profiles | R |
| Teams | R |
| TeamMembers | R |
| CampLeads | R |
| CityPlanningSettings | R/W |
| CampSettings | R |

No cache.

---

## Shifts

### ShiftManagementService (Scoped)

| Table | R/W |
|-------|-----|
| Teams | R |
| TeamRoleAssignments | R |
| TeamMembers | R |
| RoleAssignments | R |
| EventSettings | R/W |
| Rotas | R/W |
| Shifts | R/W |
| ShiftSignups | R |
| ShiftTags | R/W |
| VolunteerTagPreferences | R/W |

| Cache Key | TTL | Read | Write |
|-----------|-----|------|-------|
| `shift-auth:{userId}` | 60 sec | yes | yes |

### ShiftSignupService (Scoped)

| Table | R/W |
|-------|-----|
| ShiftSignups | R/W |
| Shifts | R |
| Rotas | R/W |
| EventSettings | R |
| Teams | R |
| AuditLogEntries | W |
| Users | R |

No cache.

### GeneralAvailabilityService (Scoped)

| Table | R/W |
|-------|-----|
| GeneralAvailability | R/W |
| Users | R |

No cache.

---

## Legal & Consent

### LegalDocumentService (Scoped)

No database access. Fetches documents from GitHub.

| Cache Key | TTL | Read | Write |
|-----------|-----|------|-------|
| `Legal:{slug}` | 1 hr (30 sec on failure) | yes | yes |

### LegalDocumentSyncService (Scoped)

| Table | R/W |
|-------|-----|
| LegalDocuments | R/W |
| DocumentVersions | R/W |
| Profiles | R |

No cache.

### AdminLegalDocumentService (Scoped)

| Table | R/W |
|-------|-----|
| LegalDocuments | R/W |
| Teams | R |
| DocumentVersions | R/W |

No cache.

### ConsentService (Scoped)

| Table | R/W |
|-------|-----|
| LegalDocuments | R |
| DocumentVersions | R |
| ConsentRecords | R/W |
| Profiles | R |

No cache.

---

## Notifications

### NotificationService (Scoped)

| Table | R/W |
|-------|-----|
| CommunicationPreferences | R |
| Teams | R |
| TeamMembers | R |
| RoleAssignments | R |
| Notifications | W |
| NotificationRecipients | W |

| Cache Key | Invalidate |
|-----------|------------|
| `NotificationBadge:{userId}` | yes (per affected user) |

### NotificationInboxService (Scoped)

| Table | R/W |
|-------|-----|
| NotificationRecipients | R/W |
| Notifications | R/W |

| Cache Key | Invalidate |
|-----------|------------|
| `NotificationBadge:{userId}` | yes |

### NotificationMeterProvider (Scoped)

| Table | R/W |
|-------|-----|
| Profiles | R |
| Users | R |
| GoogleSyncOutboxEvents | R |
| TeamJoinRequests | R |
| TicketSyncStates | R |
| Applications | R |

| Cache Key | TTL | Read | Write |
|-----------|-----|------|-------|
| `NotificationMeters` | 2 min | yes | yes |
| `NavBadge:Voting:{userId}` | 2 min | yes | yes |

### CommunicationPreferenceService (Scoped)

| Table | R/W |
|-------|-----|
| CommunicationPreferences | R/W |

No cache.

---

## Tickets

### TicketQueryService (Scoped)

| Table | R/W |
|-------|-----|
| TicketAttendees | R |
| TicketOrders | R |
| UserEmails | R |
| TeamMembers | R |
| Campaigns | R |
| TicketSyncStates | R |

| Cache Key | TTL | Read | Write |
|-----------|-----|------|-------|
| `UserTicketCount:{userId}` | 5 min | yes | yes |
| `UserIdsWithTickets` | 5 min | yes | yes |

### TicketSyncService (Scoped)

| Table | R/W |
|-------|-----|
| TicketSyncStates | R/W |
| UserEmails | R |
| TicketOrders | R/W |
| TicketAttendees | R/W |
| CampaignGrants | R/W |
| Campaigns | R |

| Cache Key | Invalidate |
|-----------|------------|
| `TicketEventSummary:{eventId}` | yes |
| `TicketDashboardStats` | yes |
| `UserIdsWithTickets` | yes |

### TicketTailorService (Transient via HttpClient factory)

No database access. External API client.

| Cache Key | TTL | Read | Write |
|-----------|-----|------|-------|
| `TicketEventSummary:{eventId}` | 15 min | yes | yes |

### TicketingBudgetService (Scoped)

| Table | R/W |
|-------|-----|
| TicketOrders | R |
| BudgetGroups | R |
| BudgetCategories | R |
| BudgetLineItems | R/W |
| TicketingProjections | R/W |

No cache.

---

## Budget

### BudgetService (Scoped)

| Table | R/W |
|-------|-----|
| BudgetYears | R/W |
| BudgetGroups | R/W |
| BudgetCategories | R/W |
| BudgetLineItems | R/W |
| TicketingProjections | R/W |
| BudgetAuditLogs | R/W |
| Teams | R |
| TeamMembers | R |
| TeamRoleAssignments | R |

No cache.

---

## Campaigns

### CampaignService (Scoped)

| Table | R/W |
|-------|-----|
| Campaigns | R/W |
| CampaignGrants | R/W |
| CampaignCodes | R/W |
| Teams | R |
| Users | R |
| TeamMembers | R |
| EmailOutboxMessages | R/W |

No cache.

---

## Email

### OutboxEmailService (Scoped)

| Table | R/W |
|-------|-----|
| UserEmails | R |
| EmailOutboxMessages | W |

No cache.

### EmailOutboxService (Scoped)

| Table | R/W |
|-------|-----|
| EmailOutboxMessages | R/W |

No cache.

### UserEmailService (Scoped)

| Table | R/W |
|-------|-----|
| UserEmails | R/W |
| AccountMergeRequests | R/W |
| Users | R/W |

No cache.

### SmtpEmailService / SmtpEmailTransport / EmailRenderer (Scoped)

No database access. No cache. Pure transport and rendering.

---

## Feedback

### FeedbackService (Scoped)

| Table | R/W |
|-------|-----|
| FeedbackReports | R/W |
| FeedbackMessages | R/W |
| Users | R |
| Teams | R |

| Cache Key | Invalidate |
|-----------|------------|
| `NavBadgeCounts` | yes |

---

## Admin & Infrastructure

### AuditLogService (Scoped)

| Table | R/W |
|-------|-----|
| AuditLogEntries | R/W |
| GoogleResources | R (Include for sync logs) |

No cache. Entries are added but not saved; caller controls atomicity.

### MagicLinkService (Scoped)

| Table | R/W |
|-------|-----|
| UserEmails | R |
| Users | R/W |

| Cache Key | TTL | Type |
|-----------|-----|------|
| `magic_link_used:{tokenPrefix}` | 15 min | Rate limit / replay prevention |
| `magic_link_signup:{email}` | 60 sec | Rate limit |

### HumansMetricsService (Singleton)

| Table | R/W |
|-------|-----|
| Users | R |
| Profiles | R |
| Applications | R |
| RoleAssignments | R |
| Teams | R |
| TeamJoinRequests | R |
| GoogleResources | R |
| LegalDocuments | R |
| GoogleSyncOutboxEvents | R |

Uses `IDbContextFactory` (correct for singleton). Auto-refreshes snapshot every 60 seconds.
No IMemoryCache usage; maintains internal volatile snapshot for OpenTelemetry gauges.

### TrackingMemoryCache (Singleton)

No database access. Decorator around IMemoryCache that tracks hit/miss statistics and active entry counts per key prefix. ConcurrentDictionary for stats.

---

## Cross-Section Analysis

### Tables Accessed by Multiple Sections

This is the core finding. Tables listed are accessed by 3+ sections (excluding AuditLogEntries which is write-only from many services).

| Table | Sections |
|-------|----------|
| **Users** | Profiles, Onboarding, Teams, Google, Camps (City Planning), Shifts, Notifications, Tickets, Email, Feedback, Admin/Metrics |
| **Profiles** | Profiles, Onboarding, Camps (City Planning), Legal (Sync), Notifications (Meters), Admin/Metrics |
| **TeamMembers** | Profiles, Teams, Google (Sync, Admin), Camps (City Planning), Shifts, Notifications, Tickets, Budget, Campaigns, Admin/Metrics |
| **Teams** | Teams, Camps (City Planning), Shifts, Legal (Admin), Notifications, Budget, Campaigns, Feedback, Admin/Metrics |
| **RoleAssignments** | Onboarding (Governance), Shifts, Notifications, Profiles (MembershipCalc), Admin/Metrics |
| **Applications** | Profiles, Onboarding, Governance, Notifications (Meters), Admin/Metrics |
| **UserEmails** | Profiles (Accounts), Email, Google (Admin), Tickets (Sync), Magic Link |
| **GoogleResources** | Teams (Resources), Google (Sync, Admin, Activity), Notifications (Meters), Admin/Metrics |
| **CommunicationPreferences** | Notifications, Profiles (export), Contact |
| **TicketOrders** | Tickets (Query, Sync), Ticketing Budget, Profiles (hold date) |
| **LegalDocuments / DocumentVersions** | Legal (all 3 services), Consent, MembershipCalculator |
| **ConsentRecords** | Consent, MembershipCalculator, Profiles (export) |
| **CampaignGrants** | Campaigns, Tickets (Sync), Profiles |
| **EmailOutboxMessages** | Email (Outbox, OutboxService), Campaigns |

### Notable Cross-Section Overlaps

1. **TeamMembers is the most cross-cut table** (11 sections). Nearly every section needs to check team membership for authorization or filtering. This is somewhat mitigated by the `ActiveTeams` cache which includes member lists.

2. **ProfileService reaches into Tickets and Events** to compute hold dates (`TicketOrders`, `TicketAttendees`, `EventSettings`, `TicketSyncStates`). This creates a dependency from Profiles into Tickets that could be extracted into a method on TicketQueryService.

3. **TicketingBudgetService reads TicketOrders and writes BudgetLineItems** — bridging Tickets and Budget. This is the intended design (sync actuals from tickets into budget).

4. **CampaignGrants are written by CampaignService but also updated by TicketSyncService** (marking redemption). Two different sections mutate the same table for different reasons.

5. **MembershipCalculator reads across Legal, Consent, Teams, and Profiles** to compute membership status. This is inherently cross-cutting but is read-only.

6. **NotificationMeterProvider is a wide reader** — reads from Profiles, Users, GoogleSyncOutboxEvents, TeamJoinRequests, TicketSyncStates, and Applications. This is expected for an aggregation service.

---

## Cache Inventory

### All Cache Keys

| Key | TTL | Type | Populated By | Invalidated By |
|-----|-----|------|-------------|----------------|
| `NavBadgeCounts` | 2 min | Static | **NavBadgesViewComponent** | ProfileService, OnboardingService, FeedbackService, ApplicationDecisionService, RoleAssignmentService |
| `NotificationBadge:{userId}` | 2 min | Per-User | **NotificationBellViewComponent** | NotificationService, NotificationInboxService |
| `NotificationMeters` | 2 min | Static | NotificationMeterProvider | ProfileService, OnboardingService, ApplicationDecisionService, TeamService |
| `ApprovedProfiles` | 10 min | Static | ProfileService | ProfileService, OnboardingService, VolunteerHistoryService, AccountMergeService, DuplicateAccountService |
| `ActiveTeams` | 10 min | Static | TeamService | TeamService, ProfileService, AccountMergeService, DuplicateAccountService |
| `UserProfile:{userId}` | 2 min | Per-User | ProfileService | ProfileService, OnboardingService |
| `NavBadge:Voting:{userId}` | 2 min | Per-User | NotificationMeterProvider | OnboardingService, ApplicationDecisionService |
| `claims:{userId}` | 60 sec | Per-User | (unknown populator) | RoleAssignmentService, ProfileService |
| `shift-auth:{userId}` | 60 sec | Per-User | ShiftManagementService | TeamService |
| `camps_year_{year}` | 5 min | Per-Entity | CampService | CampService |
| `CampSettings` | 5 min | Static | CampService | CampService |
| `Legal:{slug}` | 1 hr | Per-Entity | LegalDocumentService | LegalDocumentService |
| `UserTicketCount:{userId}` | 5 min | Per-User | TicketQueryService | (TTL-based only) |
| `UserIdsWithTickets` | 5 min | Static | TicketQueryService | TicketSyncService |
| `TicketEventSummary:{eventId}` | 15 min | Per-Entity | TicketTailorService | TicketSyncService |
| `TicketDashboardStats` | 5 min | Static | (unknown populator) | TicketSyncService |
| `NobodiesTeamEmails_All` | 2 min | Static | **NobodiesEmailBadgeViewComponent** | GoogleController |
| `CampContactRateLimit:{userId}:{campId}` | 10 min | Rate Limit | CampContactService | CampContactService |
| `magic_link_used:{tokenPrefix}` | 15 min | Rate Limit | MagicLinkService | MagicLinkService |
| `magic_link_signup:{email}` | 60 sec | Rate Limit | MagicLinkService | MagicLinkService |

### Cache Issues Found

**1. View components populate caches that services invalidate**

`NavBadgeCounts` is populated by `NavBadgesViewComponent` (which does direct DB queries) but invalidated by 5 different services. `NotificationBadge:{userId}` is the same pattern with `NotificationBellViewComponent`. `NobodiesTeamEmails_All` is populated by `NobodiesEmailBadgeViewComponent` but invalidated by `GoogleController`.

This means the DB queries that compute these values live in view components, not services. The services only know how to remove the key, not recompute it. This is backwards — the computation should be in a service, and the view component should call the service.

**2. ApprovedProfiles is a shared mutable ConcurrentDictionary**

Multiple services add to or remove from the `ApprovedProfiles` bulk cache entry. On a cache miss, ProfileService loads all approved profiles and rebuilds the dictionary. Between a miss and the rebuild, individual add/remove operations from other services (OnboardingService, VolunteerHistoryService, etc.) could race. At current scale (~500 users) this is low-risk but architecturally fragile.

**3. UserTicketCount has no explicit invalidation**

Per-user ticket counts are cached for 5 minutes with TTL-based expiry only. After a ticket sync, `UserIdsWithTickets` is invalidated but individual `UserTicketCount:{userId}` entries are not. A user could see stale ticket counts for up to 5 minutes after sync.

**4. TicketDashboardStats invalidated but populator unclear**

`TicketSyncService` invalidates `TicketDashboardStats` but the code that populates it wasn't found in the service layer audit. It may be populated in a controller or view component (see Appendix A).

**5. NobodiesTeamEmails_All is entirely outside the service layer**

The cache is populated by a view component and invalidated by a controller. No service is involved. The DB query (via `IUserEmailService`) and cache management should both move to a service.

---

## Table Ownership Map

The principle: **each table has one owning service, and all other access goes through that service's API.**
Read access through the owning service lets it serve from cache. Write access through the owning service lets it invalidate correctly.

### Proposed Ownership

| Table(s) | Owner | Current Violations (services accessing directly) |
|----------|-------|--------------------------------------------------|
| **Profiles**, ContactFields, VolunteerHistoryEntries, ProfileLanguages, VolunteerEventProfiles | ProfileService | ApplicationDecisionService (writes MembershipTier), OnboardingService (R/W approval/suspension), AccountMergeService (R/W anonymize), DuplicateAccountService (R/W anonymize), NotificationMeterProvider (R counts), CityPlanningService (R display name), HumansMetricsService (R counts) |
| **Users** | (Identity — UserManager) | Touched by nearly every section. Most reads are for display names or email. Google services write GoogleEmail/GoogleEmailStatus. |
| **UserEmails** | UserEmailService | AccountMergeService (R/W), AccountProvisioningService (R/W), GoogleAdminService (R/W), OnboardingService (R/W purge), TicketSyncService (R match), MagicLinkService (R), OutboxEmailService (R) |
| **Teams**, TeamMembers, TeamJoinRequests, TeamRoleDefinitions, TeamRoleAssignments | TeamService | Read by 11 sections for auth/membership checks. SystemTeamSyncJob writes TeamMembers and TeamRoleAssignments directly. BudgetService reads TeamRoleAssignments for auth. |
| **Applications**, BoardVotes | OnboardingService / ApplicationDecisionService | ProfileService (R/W — creates applications on profile save), NotificationMeterProvider (R), HumansMetricsService (R) |
| **RoleAssignments** | RoleAssignmentService | MembershipCalculator (R), NotificationService (R), ShiftManagementService (R), AccountMergeService (R/W end roles), HumansMetricsService (R) |
| **GoogleResources** | TeamResourceService | GoogleWorkspaceSyncService (R/W provision), GoogleAdminService (R), DriveActivityMonitorService (R), NotificationMeterProvider (R), HumansMetricsService (R) |
| **GoogleSyncOutboxEvents** | GoogleWorkspaceSyncService | NotificationMeterProvider (R), HumansMetricsService (R) |
| **SyncServiceSettings** | SyncSettingsService | Clean — no violations |
| **LegalDocuments**, DocumentVersions | LegalDocumentSyncService / AdminLegalDocumentService | MembershipCalculator (R), ConsentService (R) |
| **ConsentRecords** | ConsentService | MembershipCalculator (R) |
| **Notifications**, NotificationRecipients | NotificationService / NotificationInboxService | Clean — no violations |
| **CommunicationPreferences** | CommunicationPreferenceService | NotificationService (R), ContactService (R) |
| **Camps**, CampSeasons, CampLeads, CampHistoricalNames, CampImages, CampSettings | CampService | CityPlanningService (R/W CampSeasons, R others) |
| **CampPolygons**, CampPolygonHistories, CityPlanningSettings | CityPlanningService | Clean — no violations |
| **EventSettings**, Rotas, Shifts, ShiftSignups, ShiftTags, VolunteerTagPreferences | ShiftManagementService / ShiftSignupService | Clean internally (ShiftSignup reads Shifts/Rotas but they're same section) |
| **GeneralAvailability** | GeneralAvailabilityService | Clean — no violations |
| **TicketOrders**, TicketAttendees, TicketSyncStates | TicketSyncService / TicketQueryService | ProfileService (R for hold dates), TicketingBudgetService (R orders for actuals) |
| **BudgetYears**, BudgetGroups, BudgetCategories, BudgetLineItems, BudgetAuditLogs, TicketingProjections | BudgetService | TicketingBudgetService (R/W line items and projections) |
| **Campaigns**, CampaignGrants, CampaignCodes | CampaignService | TicketSyncService (R/W CampaignGrants — marks redemption), ProfileService (R CampaignGrants) |
| **FeedbackReports**, FeedbackMessages | FeedbackService | Clean — no violations |
| **EmailOutboxMessages** | OutboxEmailService / EmailOutboxService | CampaignService (R/W — creates outbox entries) |
| **AuditLogEntries** | AuditLogService | Clean — write-only, always called through the service |
| **AccountMergeRequests** | AccountMergeService | UserEmailService (R/W — creates merge requests on email conflict) |
| **SystemSettings** | (no owner — shared key-value store) | DriveActivityMonitorService (R/W), ProcessEmailOutboxJob (R/W), EmailController (R/W) |

### Violations Summary

**Write violations (most critical — break cache invalidation):**

| Violator | Table Written | Owner |
|----------|--------------|-------|
| ApplicationDecisionService | Profiles (MembershipTier) | ProfileService |
| OnboardingService | Profiles (approval/suspension fields) | ProfileService |
| AccountMergeService | Profiles, ContactFields, VolunteerHistoryEntries | ProfileService |
| DuplicateAccountService | Profiles, ContactFields, VolunteerHistoryEntries | ProfileService |
| ProfileService | Applications | OnboardingService |
| SystemTeamSyncJob | TeamMembers, TeamRoleAssignments | TeamService |
| ProcessAccountDeletionsJob | TeamRoleAssignments, ContactFields, VolunteerHistoryEntries | TeamService, ProfileService |
| TicketSyncService | CampaignGrants | CampaignService |
| TicketingBudgetService | BudgetLineItems, TicketingProjections | BudgetService |
| CityPlanningService | CampSeasons | CampService |
| CampaignService | EmailOutboxMessages | OutboxEmailService |
| AccountMergeService | RoleAssignments | RoleAssignmentService |
| GoogleWorkspaceSyncService | Users (GoogleEmailStatus) | Identity/UserManager |

**Read violations (less critical but prevent caching):**

Too many to list individually. The pattern is: services query tables they don't own to get display names, check membership, count records, or verify authorization. The most common reads across boundaries are:
- TeamMembers (11 sections check membership)
- Users (display names, email)
- Profiles (status checks, counts)
- RoleAssignments (authorization checks)

---

## Prioritized Fix Plan

Strategy: start from the **least-intertwined services** (island sections) and work toward the heavily cross-cut core. Each phase makes the next one easier because it reduces the number of cross-boundary accesses.

### Phase 1 — Island Services (no inbound violations)

These services own their tables cleanly and no other service writes to them. The work is purely moving the *read* queries from other services into these services' APIs, then calling the service instead.

**1a. FeedbackService**
- Currently reads Users (display name) and Teams (name) directly
- Add lookup methods or accept display names from callers
- Effort: trivial

**1b. GeneralAvailabilityService**
- Reads Users for display names
- Same pattern as above
- Effort: trivial

**1c. CommunicationPreferenceService**
- NotificationService and ContactService read CommunicationPreferences directly
- Add `GetPreferencesForUserAsync(userId)` / `IsOptedOutAsync(userId, category)` if not already present
- Effort: small

**1d. SyncSettingsService**
- Already clean. No work needed.

**1e. ConsentService**
- MembershipCalculator reads ConsentRecords directly
- Add a method like `GetConsentedVersionIdsAsync(userId)` or `GetConsentMapForUsersAsync(userIds)`
- Effort: small

### Phase 2 — Self-Contained Sections with Minor Boundary Reads

**2a. Legal (LegalDocumentSyncService / AdminLegalDocumentService)**
- MembershipCalculator reads LegalDocuments/DocumentVersions for required doc lists
- Add `GetRequiredDocumentVersionsAsync(teamId?)` to the legal service
- Effort: small

**2b. CampService ← CityPlanningService**
- CityPlanningService reads AND writes CampSeasons
- CityPlanningService should call CampService methods for season data and mutations (e.g., `GetSeasonAsync`, `UpdateSeasonPlacementDatesAsync`)
- Effort: moderate — CityPlanning has several CampSeason write paths

**2c. Shifts (ShiftManagementService / ShiftSignupService)**
- Reads Teams/TeamMembers for authorization
- Reads RoleAssignments for coordinator checks
- Wire through TeamService and RoleAssignmentService APIs instead of direct queries
- Effort: moderate (shift-auth cache already helps, but the underlying queries still bypass)

### Phase 3 — Ticket/Budget/Campaign Triangle

These three sections have write violations across each other.

**3a. TicketSyncService → CampaignService**
- TicketSyncService writes CampaignGrants (marks RedeemedAt)
- Add `MarkGrantRedeemedAsync(code, redeemedAt)` to CampaignService
- Effort: small

**3b. TicketingBudgetService → BudgetService**
- Writes BudgetLineItems and TicketingProjections directly
- Add methods to BudgetService: `UpsertAutoLineItemsAsync(categoryId, items)`, `UpdateProjectionAsync(groupId, actuals)`
- Effort: moderate — the weekly line-item upsert logic is non-trivial

**3c. CampaignService → OutboxEmailService**
- Creates EmailOutboxMessages directly
- Should call OutboxEmailService to enqueue
- Effort: small

**3d. ProfileService → TicketQueryService**
- ProfileService reads TicketOrders/TicketAttendees/TicketSyncStates for hold date
- Add `GetEventHoldStatusAsync(userId)` to TicketQueryService
- Effort: small

### Phase 4 — Google Integration

**4a. GoogleWorkspaceSyncService**
- Reads TeamMembers, Teams directly → call TeamService
- Writes Users.GoogleEmailStatus → call a method on an identity/account service
- Effort: moderate — sync service is complex and uses IDbContextFactory for parallelism

**4b. GoogleAdminService**
- Reads/writes UserEmails → call UserEmailService
- Reads TeamMembers → call TeamService
- Effort: moderate

### Phase 5 — Governance and Onboarding

**5a. ApplicationDecisionService → ProfileService**
- Writes `profile.MembershipTier` directly on approval
- ProfileService should expose `UpdateMembershipTierAsync(userId, tier)`
- Effort: small code change but important for cache correctness

**5b. ProfileService → OnboardingService**
- ProfileService creates/updates Applications on profile save
- This is a write violation in the wrong direction (Profiles writing to Governance)
- Move application creation to ApplicationDecisionService, call it from ProfileService
- Effort: moderate — tangled in the profile save flow

**5c. OnboardingService → ProfileService**
- Writes approval/suspension/consent-check fields on Profiles directly
- Should call ProfileService methods for these mutations
- This is the single biggest source of cache-busting for ApprovedProfiles
- Effort: moderate-large — OnboardingService has many paths that mutate profile state

### Phase 6 — Core Cross-Cutting (TeamMembers, Users, Profiles)

**6a. TeamMembers read access**
- 11 sections read TeamMembers for authorization checks
- TeamService already has `ActiveTeams` cache with member lists
- Add/expose `IsUserMemberOfTeamAsync`, `GetUserTeamIdsAsync`, `GetTeamMemberUserIdsAsync` methods that serve from cache
- Many callers can switch to these without touching the DB
- Effort: moderate — many call sites to update, but each one is mechanical

**6b. Users read access (display names, email)**
- Nearly universal. Many services just need `DisplayName` or `Email`
- Consider a lightweight `IUserLookupService` (or methods on an existing service) that serves from a cached dictionary
- Effort: moderate — mechanical but widespread

**6c. Profiles read access**
- ProfileService's `ApprovedProfiles` cache already holds what most callers need
- Expose read methods that serve from cache: `GetApprovedProfileAsync(userId)`, `GetProfileCountsAsync()`
- Redirect NotificationMeterProvider, HumansMetricsService, etc. to use ProfileService instead of querying the table
- Effort: moderate

### Phase 7 — Jobs and Account Operations

**7a. SystemTeamSyncJob → TeamService**
- Writes TeamMembers and TeamRoleAssignments directly
- Needs `AddMemberToTeamAsync` / `RemoveMemberFromTeamAsync` methods on TeamService
- Effort: large — this is the most complex job in the system

**7b. ProcessAccountDeletionsJob → ProfileService, TeamService**
- Anonymizes profiles, removes contact fields, volunteer history, role assignments directly
- Should orchestrate through the owning services
- Effort: large — touches many tables across sections

**7c. AccountMergeService / DuplicateAccountService → ProfileService, RoleAssignmentService**
- Write to Profiles (anonymize), RoleAssignments (end), ContactFields, VolunteerHistoryEntries
- Should call owning services for each mutation
- Effort: moderate-large

### Phase 8 — View Components and Controllers

After services own their data properly, move the view component DB queries and cache population into the owning services:

- NavBadgesViewComponent → new method on a service (or NotificationMeterProvider)
- NotificationBellViewComponent → NotificationInboxService
- NobodiesEmailBadgeViewComponent → a Google/Email service
- ProfileCardViewComponent → ProfileService
- AuditLogViewComponent → AuditLogService
- MyGoogleResourcesViewComponent → TeamResourceService

Controller DB access should follow the same pattern — each controller call goes through a service.

### Summary

| Phase | Scope | Effort | Impact |
|-------|-------|--------|--------|
| 1 | Island services (Feedback, GA, CommPrefs, Consent) | Small | Low risk, establishes pattern |
| 2 | Self-contained sections (Legal, Camps←CityPlanning, Shifts) | Small-Moderate | Removes first write violations |
| 3 | Ticket/Budget/Campaign triangle | Moderate | Fixes cross-section writes |
| 4 | Google integration | Moderate | Reduces sync complexity |
| 5 | Governance/Onboarding ↔ Profiles | Moderate-Large | Fixes ApprovedProfiles cache reliability |
| 6 | Core cross-cutting (TeamMembers, Users, Profiles reads) | Moderate | Enables proper caching everywhere |
| 7 | Jobs and account operations | Large | Removes last direct DB mutations |
| 8 | View components and controllers | Moderate | Completes the service layer boundary |

---

## Appendix A: Out-of-Service Database Access

Controllers and view components that inject `HumansDbContext` or `IMemoryCache` directly, bypassing the service layer.

### Controllers

| Controller | Tables Read | Tables Written |
|------------|------------|----------------|
| **AdminController** | Users, Profiles, TicketOrders, TicketAttendees, database metadata (migrations) | Hangfire lock table (ExecuteSqlRaw) |
| **ProfileController** | ProfileLanguages, UserEmails, EmailOutboxMessages, TeamMembers, CampaignGrants, GoogleSyncOutboxEvents, TicketOrders | ProfileLanguages, UserEmails |
| **BoardController** | Users, Teams | |
| **BudgetController** | BudgetLineItems | |
| **EmailController** | EmailOutboxMessages, SystemSettings | SystemSettings |
| **GuestController** | TicketOrders, TicketAttendees, Users | |
| **CampAdminController** | Camps | |
| **DevLoginController** | Camps, CampSeasons, CampLeads | Camps, CampSeasons, CampLeads |
| **UnsubscribeController** | (via injected DbContext) | |
| **GoogleController** | | |

### View Components

| Component | Tables Read | Cache Keys |
|-----------|------------|------------|
| **NavBadgesViewComponent** | Profiles, Applications | `NavBadgeCounts` (read/write) |
| **NotificationBellViewComponent** | NotificationRecipients | `NotificationBadge:{userId}` (read/write) |
| **AuditLogViewComponent** | Users, Teams | |
| **MyGoogleResourcesViewComponent** | TeamMembers | |
| **ProfileCardViewComponent** | Users, Profiles, ProfileLanguages | |
| **NobodiesEmailBadgeViewComponent** | | `NobodiesTeamEmails_All` (read/write) |

### Background Jobs

Jobs are in Infrastructure so this is a softer violation, but complex mutation logic should live in services.

| Job | Tables Read | Tables Written | Cache |
|-----|------------|----------------|-------|
| **SystemTeamSyncJob** | TeamMembers, Teams, Profiles, Applications, Users, RoleAssignments, GoogleResources | TeamMembers, TeamRoleAssignments | ActiveTeams, ApprovedProfiles |
| **ProcessAccountDeletionsJob** | Users, ContactFields, VolunteerHistoryEntries | Users (anonymize), ContactFields, VolunteerHistoryEntries, TeamRoleAssignments, ShiftSignups, VolunteerEventProfiles | ApprovedProfiles |
| **SendAdminDailyDigestJob** | Users, Profiles, TeamMembers, TeamJoinRequests, Applications, GoogleSyncOutboxEvents, TicketSyncStates, RoleAssignments | | |
| **SendBoardDailyDigestJob** | AuditLogEntries, Users, Applications, Profiles, TeamJoinRequests, TeamMembers, RoleAssignments, BoardVotes | | |
| **SuspendNonCompliantMembersJob** | Users | | UserProfile invalidation |
| **ProcessGoogleSyncOutboxJob** | GoogleSyncOutboxEvents, Users, Teams, GoogleResources | GoogleSyncOutboxEvents | |
| **CleanupNotificationsJob** | Notifications | Notifications | |
| **CleanupEmailOutboxJob** | EmailOutboxMessages | EmailOutboxMessages | |
| **ProcessEmailOutboxJob** | SystemSettings, EmailOutboxMessages, CampaignGrants | SystemSettings, EmailOutboxMessages | |
| **TermRenewalReminderJob** | Applications | Applications | |
| **SendReConsentReminderJob** | Users | Users | |
| **SyncLegalDocumentsJob** | TeamMembers, ConsentRecords, Users | | |

---

## Appendix B: Out-of-Service Cache Access

Controllers that directly manipulate cache keys.

| Controller/Component | Cache Operation | Key |
|---------------------|-----------------|-----|
| **GoogleController** | Remove | `NobodiesTeamEmails_All` |
| **ProfileController** | Remove | (various profile-related keys) |
| **TeamAdminController** | Remove | (team-related keys) |
| **DevLoginController** | Invalidate | `ApprovedProfiles`, `UserAccess` |
| **NavBadgesViewComponent** | GetOrCreate | `NavBadgeCounts` |
| **NotificationBellViewComponent** | GetOrCreate | `NotificationBadge:{userId}` |
| **NobodiesEmailBadgeViewComponent** | TryGetValue/Set | `NobodiesTeamEmails_All` |
