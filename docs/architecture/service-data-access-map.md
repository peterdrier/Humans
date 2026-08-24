# Service Data Access Map

Audit of which services access which database tables and cache keys, organized by section.
The goal is to identify cross-section table overlap, duplicated caching, and cache configuration issues.

> **Methodology.** Tables are resolved by following each service's injected
> repository interface to its EF-backed implementation in the section's own
> `Data/Repository.cs` (or `Data/Repositories/*.cs`) under `src/Sections/`,
> then mapping the `DbSet<>` (or bare `Set<T>()`) usage to the declaring
> context in that same section's `Data/` folder. Every repository,
> `DbContext`, and service lives under `src/Sections/`; the handful of true
> cross-section orchestrators that own no table (Dashboard, the Agent
> preload augmentor) live in `src/Humans.Web/Services/` instead. There is no
> shared `HumansDbContext` — every context below is internal-sealed with its
> own `IDbContextFactory<T>`/direct-injection pattern, against the same
> database/connection. Each context gets its own `__EFMigrationsHistory_<Section>`
> table, except `UsersDbContext`, which carries forward the original
> unsuffixed `__EFMigrationsHistory` table (dropping the old name is a
> pending cleanup):
>
> | DbContext | Owns |
> |-----------|------|
> | `UsersDbContext` | `Profiles`, `ContactFields`, `UserEmails`, `VolunteerHistoryEntries`, `AccountMergeRequests`, `CommunicationPreferences`, `ProfileLanguages`, `EventParticipations`, plus the Identity base (`IdentityDbContext<User, IdentityRole<Guid>, Guid>`: `users`/`roles`/`user_roles`/`user_claims`/`user_logins`/`role_claims`/`user_tokens`). Own project, `src/Sections/Humans.Users/` (Profiles folded in — no separate Profiles project). |
> | `TeamsDbContext` | `Teams`, `TeamMembers`, `TeamJoinRequests`, `TeamJoinRequestStateHistories`, `TeamRoleDefinitions`, `TeamRoleAssignments`, `TeamEarlyEntryGrants` — owned by `src/Sections/Humans.Teams`; `TeamRepository` injects `IDbContextFactory<TeamsDbContext>` |
> | `AuditLogDbContext` | `AuditLogEntries` — owned by `src/Sections/Humans.AuditLog` (a *horizontal* section — see [AuditLog](../../src/Sections/Humans.AuditLog/Docs/data-access.md)) |
> | `LegalDbContext` | `LegalDocuments`, `DocumentVersions`, `ConsentRecords` — own project, `src/Sections/Humans.Consent/Data/`. `ConsentRecords` lives here (not a separate Consent context) — Consent has never owned its own DbContext. |
> | `ShiftsDbContext` | `EventSettings`, `Rotas`, `Shifts`, `ShiftSignups`, `ShiftTags`, `RotaShiftTags` (`rota_shift_tags` — the implicit many-to-many mapped by `ShiftTagConfiguration` via `UsingEntity`), `VolunteerEventProfiles`, `GeneralAvailability`, `VolunteerBuildStatuses`, `VolunteerTagPreferences` (own project, `src/Sections/Humans.Shifts/`) |
> | `TicketsDbContext` | `TicketOrders`, `TicketAttendees`, `TicketSyncStates`, `TicketTransferRequests` — owned by `src/Sections/Humans.Tickets`, alongside a `src/Sections/Humans.Tickets.Contracts` leaf and the `src/Sections/Humans.TicketTailor` vendor adapter (see [Tickets](../../src/Sections/Humans.Tickets/Docs/data-access.md)) |
> | `AuthDbContext` | `RoleAssignments` (own project, `src/Sections/Humans.Auth/`) |
> | `GovernanceDbContext` | `Applications`, `ApplicationStateHistories`, `BoardVotes` (own project, `src/Sections/Humans.Governance/`) |
> | `CampaignsDbContext` | `Campaigns`, `CampaignCodes`, `CampaignGrants` (own project, `src/Sections/Humans.Campaigns/`) |
> | `GoogleIntegrationDbContext` | `GoogleResources`, `GoogleSyncOutboxEvents`, `SyncServiceSettings` (own project, `src/Sections/Humans.GoogleIntegration/`; owns no table beyond these — see [Monitor](../../src/Sections/Humans.Monitor/Docs/data-access.md) for the related horizontal section) |
> | `FeedbackDbContext` | `FeedbackReports`, `FeedbackMessages` (own project, `src/Sections/Humans.Feedback/`) |
> | `CityPlanningDbContext` | `CityPlanningSettings`, `CampPolygons`, `CampPolygonHistories` (own project, `src/Sections/Humans.CityPlanning/`) |
> | `BudgetDbContext` | `BudgetYears`, `BudgetGroups`, `BudgetCategories`, `BudgetLineItems`, `BudgetAuditLogs`, `TicketingProjections` (own project, `src/Sections/Humans.Budget/`) |
> | `CampsDbContext` | `Camps`, `CampSeasons`, `CampHistoricalNames`, `CampImages`, `CampSettings`, `CampMembers`, `CampRoleDefinitions`, `CampRoleAssignments` (own project, `src/Sections/Humans.Camps/`) |
> | `GateDbContext` | `GateScanEvents`, `GateSettings`, `GateStaffPins` (own project, `src/Sections/Humans.Gate/`, table names `gate_scan_events` / `gate_settings` / `gate_staff_pins`) |
> | `SystemDbContext` | `DataProtectionKeys` — ASP.NET Data Protection key ring storage, wired directly in `src/Humans.Web/Program.cs`; **no owning Application section, no repository, no service** |
> | `EmailDbContext` | `EmailOutboxMessages` |
> | `CalendarDbContext` | `CalendarEvents`, `CalendarEventExceptions` (own project, `src/Sections/Humans.Calendar/`) |
> | `NotificationsDbContext` | `Notifications`, `NotificationRecipients` (own project, `src/Sections/Humans.Notifications/`) |
> | `IssuesDbContext` | `Issues`, `IssueComments` (own project, `src/Sections/Humans.Issues/`) |
> | `SurveysDbContext` | `Surveys`, `SurveyQuestions`, `SurveyQuestionOptions`, `SurveyInvitations`, `SurveyResponses`, `SurveyAnswers` (own project, `src/Sections/Humans.Surveys/`) |
> | `AgentDbContext` | `AgentConversations`, `AgentMessages`, `AgentSettings` (own project, `src/Sections/Humans.Agent/`) |
> | `SettingsDbContext` | `Setting`, `EventSettings` — tables `system_settings` and `settings_event` (own project, `src/Sections/Humans.Settings/`) |
> | `ContainersDbContext` | `Containers`, `ContainerPlacements`, `ContainerImages` (own project, `src/Sections/Humans.Containers/`) |
> | `ExpensesDbContext` | `ExpenseReports`, `ExpenseLines`, `ExpenseAttachments`, `HoldedExpenseOutboxEvents` (own project, `src/Sections/Humans.Expenses/`) |
> | `FinanceDbContext` | `HoldedExpenseDocs`, `HoldedCategoryMap`, `HoldedCreditorContacts`, `HoldedDocSyncStates` (own project, `src/Sections/Humans.Finance/`). The ledger mirror (`HoldedLedgerLines`, its sync state, the chart-of-accounts cache, and the API call log) lives in the separate `HoldedDbContext` below. |
> | `HoldedDbContext` | `HoldedLedgerLines`, `HoldedSyncStates`, `HoldedAccounts`, `HoldedApiCalls` (own project, `src/Sections/Humans.Holded/`). The daybook-journal ledger mirror, chart-of-accounts cache, and Holded API call-log/metering — split out of Finance so the two sections that both touch Holded data stay structurally isolated from each other. |
> | `EventGuideDbContext` | `EventGuideSettings`, `EventCategories`, `EventVenues`, `Events`, `EventModerationActions`, `EventPreferences`, `EventFavourites` (own project, `src/Sections/Humans.Events/`; the Shifts-owned `EventSettings` / `EventParticipations` tables deliberately stay off this context, despite the name collision) |
> | `StoreDbContext` | `StoreProducts`, `StoreOrders`, `StoreOrderLines`, `StorePayments`, `StoreInvoices`, `StoreTreasurySyncStates` (own project, `src/Sections/Humans.Store/`) |
>
> Each context applies its `IEntityTypeConfiguration` classes explicitly (no
> assembly scanning), so a section's model can never accrete another
> section's tables by accident. Below, each section header states which
> DbContext backs its tables; per-table `DbContext` notes appear only where
> a section's tables span more than one context.
>
> The marker-only project `src/Humans.Base/` holds the shared
> `IApplicationService`, `IRepository`, `IOrchestrator`, `IFanout`, and
> `IInvalidator` marker interfaces (no data-access behavior of its own), plus
> the cache infrastructure: `CacheKeys.cs`, the invalidator extensions in
> `Extensions/MemoryCacheExtensions.cs`, `Caching/MemoryCacheInvalidators.cs`,
> and the `TrackedCache<TKey, TValue>` base class
> (`Interfaces/Caching/TrackedCache.cs`) that every section-owned Singleton
> caching decorator below inherits. Cross-cutting invalidator interfaces
> (`INavBadgeCacheInvalidator`,
> `INotificationMeterCacheInvalidator`, `IVotingBadgeCacheInvalidator`,
> `IRoleAssignmentClaimsCacheInvalidator`, `IActiveTeamsCacheInvalidator`,
> `ICampLeadJoinRequestsBadgeCacheInvalidator`, `IShiftAuthorizationInvalidator`,
> `IUserInfoInvalidator`, `IShiftViewInvalidator`, `IEarlyEntryInvalidator`,
> `IIssuesBadgeCacheInvalidator`) resolve via `MemoryCacheInvalidators.cs` to
> the cache keys their backing `MemoryCacheExtensions` invalidator hits.
> Section-decorator `TrackedCache<TKey, TValue>` subclasses live beside
> their inner service in each section's own `Services/` (or `Data/`) folder —
> e.g. `CachingUserService` is `src/Sections/Humans.Users/Data/CachingUserService.cs`
> — and are resolved from their DI wiring in
> each section's own `Section.cs` (`ISection.Register`). `src/Humans.Web/Extensions/Sections/` retains only `AdminSectionExtensions` and `AuthSectionExtensions`, neither of which registers a decorator.
>
> At ~500-user single-server scale this map is diagnostic, not gating —
> **cross-section table reads are flagged as design-rule violations per
> [`design-rules.md` §"Services own their data"](design-rules.md)**, but
> serve as a backlog rather than a blocker.

---


## Per-Section Maps

Each section's table/cache map lives in its own project at
`src/Sections/Humans.<Section>/Docs/data-access.md` — regenerated per-section so a change
inside one section only touches that section's file. This global file keeps only what is
genuinely cross-section: the Dashboard orchestrator (no project of its own), the
cross-section analysis, the cache inventory, and the out-of-service access appendices.

---
## Web Platform Services

### AdminDatabaseDiagnosticsService (Scoped — `src/Humans.Web/Services/`)

Repository: `IAdminDatabaseDiagnosticsRepository` (`src/Humans.Web/Repositories/`) —
raw diagnostics over the database (migration-history status across every section's
`__EFMigrationsHistory*` table, Hangfire lock clearing). No owning section, no owned
application tables. Cross-section reads via `IUserServiceRead` and `ITicketServiceRead`
(audience segmentation). No `IMemoryCache`.

## Dashboard

Folder: `src/Humans.Web/Services/Dashboard/` — has no owned tables and is
not a section project, so it lives alongside the other Web-layer
cross-section orchestrators instead of under `src/Sections/`. No owned DB
tables.

### DashboardService (Scoped)

No repository. Read-only fan-out over `IMembershipCalculatorRead`,
`IApplicationServiceRead`, `IShiftManagementService`, `IShiftView`,
`ITicketServiceRead`, `IUserServiceRead`, `ITeamServiceRead`. Uses
`TicketVendorSettings`. No DB access, no cache.

### AdminDashboardService (Scoped)

No repository. Fan-out over `IUserServiceRead`, `IMembershipCalculatorRead`,
`IApplicationServiceRead`, `IShiftManagementService`, `IShiftView`.
No DB access, no cache.

---


## Cross-Section Analysis

### Tables Accessed by Multiple Sections (via repository)

**No cross-section repository-level table reads remain.** The consolidated
`IUserRepository` is the single owner of every per-user table across the
Users+Profiles section merge, so what might look like `IUserEmailRepository`
cross-section access is internal reads/writes of the unified User+Profile
owner, not a cross-section violation.

This is enforced at the schema level too: cross-section EF foreign-key
constraints and navigation properties are absent from the model
entirely — a cross-section table read can no longer be expressed as an
`.Include()` even by accident; it would have to be a hand-written query
against a foreign `DbContext`, and the per-section DbContext split (see the
intro table) means that context injection isn't even available outside the
owning section's repository.

Every table is owned by exactly one repository; there are no HUM0025
`[Grandfathered]` markers left for cross-section repository reads:

| Table | Owning Section | Routed via |
|-------|----------------|--------|
| **GoogleSyncOutboxEvents** | Google Integration | `TeamService` appends via `IGoogleSyncOutboxService` inside a `TransactionScope`. |
| **EventSettings** | Shifts | `ShiftRepository`. |
| **ShiftSignups** | Shifts | `ShiftRepository`. |
| **Settings** (`system_settings`, `settings_event`) | Settings | Single owner `Repository`; consumers route through `ISettingsService`. `settings_event` has no consumers yet — it is populated by `/Settings/Admin/Carry` and read by nothing until the sections are pointed at it (nobodies-collective/Humans#1104). |

### Notable Cross-Section Patterns

1. **`IUserMerge` retired most cross-section profile/identity writes; the
   merge surface lives in Users.** `AccountMergeService` and
   `DuplicateAccountService` (plus `AccountMergeRepository` and the
   `AccountMergeRequests` table) live in the Users section.
   `AccountMergeService` does not inject profile-owned repositories
   directly — it fans out over `IEnumerable<IUserMerge>`, with each
   section's service implementing `IUserMerge` to reassign its own owned
   rows. `DuplicateAccountService` is **detection-only** (no
   repository, no DB access) — it reads through `IUserService` /
   `ITeamService` / `IRoleAssignmentService` only. The three Profiles
   repositories are collapsed into `IUserRepository`, so what looked like
   cross-section reads/writes between the Profiles and Users sections is
   internal to the unified Users+Profiles section owner.

2. **Read/write surface split (read-split interfaces).** Several sections
   now expose a budgeted cross-section read interface that external sections
   inject instead of the full service: `IUserServiceRead`,
   `ITeamServiceRead`, `ICalendarServiceRead`, `IConsentServiceRead`,
   `ICampServiceRead`, `ITicketServiceRead`, `IGoogleSyncServiceRead`
   (consumed by `NotificationMeterProvider` for failed-sync-event counts),
   `ICampaignServiceRead` (consumed by `TicketQueryService`),
   `IEventServiceRead` (consumed by `CampEventsViewComponent`
   for the camp detail page's events card),
   `ICityPlanningServiceRead` (exposes `GetSettingsAsync` /
   `GetRegistrationInfoAsync` / `IsCityPlanningTeamMemberAsync` — current
   consumers are Web-layer controllers/handlers, no Application-layer
   service consumer yet), and
   the Governance pair `IApplicationServiceRead` / `IMembershipCalculatorRead`.
   Several of these are the Singleton caching decorators re-cast
   to a narrow surface; the Governance read interfaces are plain
   read-only contracts the section's services implement directly. All keep
   the cross-section coupling minimal and `[SurfaceBudget]`-bounded.

3. **`IProfileService` retired into `IUserService`.** The Profile-section
   service surface is folded into `IUserService` as part of the
   Users+Profile section merge; the interface's `[SurfaceBudget]` is
   intentionally suspended during the merge.
   `ProfileEditorService` and `ContactFieldService` remain in
   `Services/Profiles/` as section-internal collaborators; the only
   Application-layer service named `ProfileService` is a thin
   `IProfilePictureService` implementation for picture-bytes IO.

4. **Tickets ↔ Profiles email lookup.**
   `TicketRepository` does not project `UserEmail` rows directly.
   `TicketSyncService.BuildEmailLookupAsync` fans out over
   `IUserServiceRead.GetAllUserInfosAsync` and synthesises the
   verified-email → user-id map from the cached `UserInfo` slices.
   `UserEmails` is read only by the consolidated `IUserRepository`
   for `UserInfo` projection — internal to the unified Users+Profiles owner,
   not a cross-section reach.

5. **Teams ↔ Google outbox goes through the owning service.**
   `TeamService` appends `GoogleSyncOutboxEvents` via
   `IGoogleSyncOutboxService.AddAsync` / `AddRangeAsync` inside a
   `TransactionScope`, so each team mutation stays atomic with its outbox
   event without `TeamRepository` reaching into the table. The Google
   Integration section owns the table end-to-end (write surface
   `GoogleSyncOutboxService`, read/process via `IGoogleSyncOutboxRepository`).

6. **DriveActivityMonitor user fallback uses UserInfo; state via the Settings
   service.** `DriveActivityMonitorService` — the
   [Monitor](../../src/Sections/Humans.Monitor/Docs/data-access.md)
   section's own — resolves Google
   `people/{client_id}` actors through Directory first, then through a per-run
   Google provider-key -> `UserInfo` index from
   `IUserServiceRead.GetAllUserInfosAsync`. It has no repository; the
   last-run marker
   (`SettingKeys.DriveActivityMonitorLastRunAt`) is read/written
   through `ISettingsService` — a §15-compliant cross-section service
   call, not a foreign repository read.

7. **Settings' tables are owned by a single section/repository.**
   `system_settings` (key/value) and `settings_event` (the app-wide event
   values) are both owned by the Settings section's `Repository`; consuming
   sections route through `ISettingsService` rather than touching the tables
   from their own repository:

   | Key | Consuming section | Routed via |
   |-----|-------------------|------------|
   | `IsEmailSendingPaused` | Email | `EmailOutboxService` → `ISettingsService` |
   | `DriveActivityMonitor:LastRunAt` | Google Integration | `DriveActivityMonitorService` → `ISettingsService` |

   New keys should be added to `SettingKeys` and accessed through
   `ISettingsService`. Both of those keys move to their own sections' settings
   later; the shared key/value store is not where new per-section state belongs.

8. **Cached read-models cover almost all per-key `IMemoryCache`
   entries.** Singleton decorators inheriting `TrackedCache<TKey, TValue>`
   own the canonical projections across most sections:
   - `CachingUserService` → `UserInfo` per user (Users + Profiles
     unified read-model).
   - `CachingTeamService` → `TeamInfo` per team (replaced `ActiveTeams`).
   - `CachingShiftViewService` → `ShiftView.UserView` + `ShiftView.RotaView`.
   - `CachingTicketQueryService` → `Tickets.Orders` + `Tickets.UserHoldings`.
   - `CachingCampService` → `CampInfo` per camp + settings slot.
   - `CachingCalendarService` → `CalendarEventInfo` per event.
   - `CachingEventService` → `ApprovedEventView` + category/venue/settings
     snapshots.
   - `CachingConsentService` → `UserConsentInfo` per user.
   - `CachingLegalDocumentSyncService` → `LegalDocumentInfo` per document.
   - `CachingRoleAssignmentService` → `RoleAssignmentRow` set.
   - `CachingEarlyEntryService` → `UserEarlyEntry?` per user (caches
     negative results too).
   All are surfaced on `/Debug/CacheStats` via `ICacheStats` and evicted
   through narrow `I*Invalidator` interfaces (or EF
   `SaveChangesInterceptor`s for Legal / User-Identity writes) — no direct
   `IMemoryCache` coupling in the Application layer.

9. **Notification meters are computed, not queried.**
   `NotificationMeterProvider` reads no tables directly — every counter
   fans out through an owning-service interface call (`IUserServiceRead`,
   `ITeamServiceRead`, `IApplicationServiceRead`, `ITicketSyncService`,
   `IGoogleSyncServiceRead`, `ICampServiceRead`). Cache invalidation goes
   through `INotificationMeterCacheInvalidator`.

10. **HUM analyzers enforce the boundaries at compile time.** Roslyn
    analyzers ratchet the layering rules: `HUM0008` blocks any per-section
    application `DbContext` in controllers, `HUM0009` blocks one
    in Application-layer services that don't implement `IRepository`
    (matching "any application DbContext" generically, not one named type).
    See [`code-analysis.md`](code-analysis.md) for the full analyzer list.

11. **Provider-based fan-out for derived data.** `IEarlyEntryService`
    aggregates per-user grants over `IEnumerable<IEarlyEntryProvider>`
    implementations (currently Camps, Shifts, and Teams).
    `IUserMerge`,
    `IUserDataContributor`, and `IMailerLiteAudience` use the same
    enumerable-injection pattern. This keeps the orchestrator
    section-agnostic; new contributors register a single service
    interface in their section's DI extension.

12. **Surveys section uses cross-section read-split surfaces exclusively.**
    `SurveyService` fans out over `ITeamServiceRead`,
    `IUserServiceRead`, `ITicketServiceRead`, and `IShiftView` for
    audience resolution and display-name stitching — never a foreign
    repository. `IGoogleTranslationService` (GoogleIntegration section)
    is the translation bridge for the admin pre-fill helper. No
    cross-section table reads. There is no `ISurveyServiceRead` — no other
    section consumes one; the section's only outbound contract is the
    single-member `Humans.Surveys.Contracts.ISurveyReminderSender`.

13. **ICalFeed is a pure fan-out orchestrator.** `ICalFeedService`
    owns no repository and touches no table directly. Token validation
    routes through `IUserServiceRead.GetUserInfoAsync` (the
    `CachingUserService` TrackedCache — no DB round-trip on cache hit).
    Shift and Event items are contributed by `ShiftSignupService` and
    `EventService` respectively, each reading their own owned tables through
    their own repositories — `ShiftsDbContext` for Shifts,
    `EventGuideDbContext` for Events. The sequential (non-parallel) fan-out
    pattern mirrors `GdprExportService` and `EarlyEntryService`: each
    contributor uses its own DbContext instance, but EF
    `DbContext`/`IDbContextFactory` usage is not thread-safe within a single
    async flow, so contributors still run one at a time.

14. **Gate composes cached cross-section reads and never touches a foreign
    table.** `GateService` resolves a scanned barcode by filtering
    the cached `ITicketServiceRead.GetTicketOrdersAsync` projection in
    memory (no new interface method), checks early entry via the cached
    `IEarlyEntryService`, verifies supervisor roles via
    `IRoleAssignmentService`, and pulls the gate-crew roster via
    `IShiftManagementService` — all §15-compliant service-interface calls.
    Its one cross-section **write** — projecting an admit onto the guest's
    `EventParticipations` row as `Attended` — goes through the
    owning `IUserService.SetParticipationFromTicketSyncAsync`, so no
    design-rule violation. `IGateRepository` reads/writes only the three
    Gate-owned tables. The section deliberately has **no caching
    decorator**: admission verdicts must be live.

15. **The per-section DbContext split is complete — "one
    table, one repository" is a compile/schema-enforced boundary for
    every table-owning section, with no shared context left.** 29 contexts
    exist — see the full DbContext table in the intro Methodology block
    above — each mapping **only** its own section's tables via explicit
    `ApplyConfiguration` calls (no assembly scanning), so a section's
    repository cannot accidentally `Set<T>()` a foreign table without
    first injecting that section's `IDbContextFactory<T>` — a conspicuous,
    reviewable change. Combined with the cross-section FK and nav-property
    removal (point 1 above), the boundary is enforced at the schema level
    too, not just at the DI-injection level. The only table-owning context
    without a per-section owner is `SystemDbContext` (`DataProtectionKeys`,
    framework-only, no owning Application section). Every
    context points at the same physical database/connection — this is a
    code-side EF model partition, not a database migration — and each
    context has its own `__EFMigrationsHistory_<Section>` table
    except `UsersDbContext`, which carries forward the original
    unsuffixed `__EFMigrationsHistory` (dropping the old table name is a
    pending cleanup).

---


## Cache Inventory

### All Cache Keys

Sourced from `src/Humans.Base/CacheKeys.cs` and
`src/Humans.Base/Extensions/MemoryCacheExtensions.cs`. TTL/type
classification mirrors `CacheKeys.Metadata` (surfaced on the Admin
`/Debug/CacheStats` page). Note: most section projections are
`TrackedCache` dictionaries (not `IMemoryCache` keys) and are listed
separately below the key table.

| Key | TTL | Type | Populated By | Invalidated By |
|-----|-----|------|-------------|----------------|
| `FeedbackBadgeCount` | 2 min | Static | **FeedbackService** (`GetActionableCountAsync`) | `INavBadgeCacheInvalidator` (FeedbackService, IssuesService, ApplicationDecisionService, RoleAssignmentService) |
| `NotificationBadge:{userId}` | 2 min | Per-User | **NotificationBellViewComponent** | NotificationService, NotificationEmitter, NotificationInboxService |
| `NotificationMeters` | 2 min | Static | NotificationMeterProvider | `INotificationMeterCacheInvalidator` (TeamService, ApplicationDecisionService) |
| `ActiveTeams` | 10 min | Static | _(unused — `CachingTeamService`'s `TrackedCache<Guid, TeamInfo>` is the live cache; key remains in `CacheKeys.Metadata` for invalidator compat)_ | `IActiveTeamsCacheInvalidator` → `ITeamService.InvalidateActiveTeamsCache()` |
| `claims:{userId}` | 60 sec | Per-User | (claims principal factory) | `IRoleAssignmentClaimsCacheInvalidator` (RoleAssignmentService, AccountDeletionService) |
| `shift-auth:{userId}` | 60 sec | Per-User | ShiftManagementService | ShiftManagementService, `IShiftAuthorizationInvalidator` (TeamService, AccountDeletionService) |
| `NavBadge:Voting:{userId}` | 2 min | Per-User | **ApplicationDecisionService** (`GetUnvotedApplicationCountAsync`) | `IVotingBadgeCacheInvalidator` (ApplicationDecisionService) |
| `NavBadge:CampLeadJoinRequests:{userId}` | 2 min | Per-User | NotificationMeterProvider | `ICampLeadJoinRequestsBadgeCacheInvalidator` (CampService) |
| `NavBadge:Issues:{userId}` | 2 min | Per-User | IssuesService | `IIssuesBadgeCacheInvalidator` (IssuesService) |
| `Legal:{slug}` | 1 hr | Per-Entity | LegalDocumentService (GitHub-source read-through) | LegalDocumentService |
| `TicketEventSummary:{eventId}` | 15 min | Per-Entity | TicketTailorService (`Humans.TicketTailor`) / TicketSyncService | TicketSyncService, `ITicketCacheInvalidator.InvalidateVendorEventSummary` |
| `TicketDashboardStats` | 5 min | Static | TicketQueryService.GetDashboardStatsAsync (compute — no read-through cache; key reserved for future wrapper) | (reserved cache-stats key) |
| `CampContactRateLimit:{userId}:{campId}` | 10 min | Rate Limit | CampContactService | CampContactService |
| `magic_link_used:{tokenPrefix}` | 15 min | Rate Limit | MagicLinkRateLimiter (`Humans.Auth`) | MagicLinkRateLimiter |
| `magic_link_signup:{normalizedEmail}` | 60 sec | Rate Limit | MagicLinkRateLimiter (`Humans.Auth`) | MagicLinkRateLimiter |
| `GateLoginFailures:{sourceIp}` | 1 min window | Rate Limit | GateLoginThrottle (Web) | GateLoginThrottle (reset on success) |
| `GatePinFailures:{key}` | 15 min lockout after 5 failures | Rate Limit | GatePinThrottle (Web) | GatePinThrottle (only a correct PIN clears it) |
| `GateVendorMirrorSent:{vendorTicketId}` | 24 hr | Dedupe claim | GateVendorMirrorLedger (Web) | expiry only |

> The three `Gate*` keys are held by Web-layer helper singletons
> (`src/Humans.Web/Services/`), not `CacheKeys.cs` /
> `CacheKeys.Metadata` — they never appear on `/Debug/CacheStats`.

### Section Decorator Caches (`TrackedCache`, not `IMemoryCache`)

| Cache | Section | Key | Type | Populated By | Invalidated By |
|-------|---------|-----|------|-------------|----------------|
| `TrackedCache<Guid, UserInfo>` | Users (+Profiles) | `User.UserInfo` | Per-User | CachingUserService warmup + lazy load | `IUserInfoInvalidator` + `IUserMerge` + `UserInfoSaveChangesInterceptor` |
| `TrackedCache<Guid, TeamInfo>` | Teams | `Team.TeamInfo` | Per-Entity | CachingTeamService warmup + lazy load | `IActiveTeamsCacheInvalidator` + `IUserMerge` |
| `TrackedCache<Guid, ShiftUserView>` / `TrackedCache<Guid, ShiftRotaView>` | Shifts | `ShiftView.UserView` / `ShiftView.RotaView` | Per-User / Per-Entity | CachingShiftViewService lazy load | `IShiftViewInvalidator` (ShiftManagementService, ShiftSignupService, VolunteerTrackingService, AccountDeletionService) |
| `TrackedCache<Guid, TicketOrderInfo>` | Tickets | `Tickets.Orders` | Per-Entity | CachingTicketQueryService warmup + lazy load | `ITicketCacheInvalidator` |
| `TrackedCache<Guid, CachedUserTicketHoldings>` | Tickets | `Tickets.UserHoldings` | Per-User (5-min freshness in value) | CachingTicketQueryService lazy load | `ITicketCacheInvalidator` |
| `TrackedCache<Guid, CampInfo>` + settings slot | Camps | `Camp.CampInfo` | Per-Entity / Static | CachingCampService warmup + lazy load | `ICampInfoInvalidator` + `IUserMerge` |
| `TrackedCache<Guid, CalendarEventInfo>` | Calendar | `Calendar.Event` | Per-Entity | CachingCalendarService warmup + lazy load | per-event `ReplaceAsync` after delegated write |
| `TrackedCache<Guid, ApprovedEventView>` + category/venue/settings snapshots | Events | `Event.ApprovedEventView` | Per-Entity / Static | CachingEventService warmup + lazy load | `IEventViewInvalidator` (inline per write) |
| `TrackedCache<Guid, UserConsentInfo>` | Consent | `Consent.UserConsentInfo` | Per-User | CachingConsentService lazy load | `IConsentCacheInvalidator` (synchronous per-user evict on submit) |
| `TrackedCache<Guid, LegalDocumentInfo>` | Legal | `Legal.LegalDocumentInfo` | Per-Entity | CachingLegalDocumentSyncService warmup + lazy load | `ILegalDocumentCacheInvalidator.InvalidateAll` (called directly by `LegalDocumentSyncService` after each write) |
| `TrackedCache<Guid, RoleAssignmentRow>` | Auth | `Auth.RoleAssignmentRow` | Per-Entity | CachingRoleAssignmentService warmup + lazy load | `IRoleAssignmentCacheInvalidator.InvalidateAll` (service-level) |
| `TrackedCache<Guid, UserEarlyEntry?>` | Early Entry | `EarlyEntry.UserEarlyEntry` | Per-User (negative-result safe) | CachingEarlyEntryService lazy load | `IEarlyEntryInvalidator.InvalidateUser` / `InvalidateAll` (ShiftManagementService, ShiftSignupService, CampService, TeamService) |

### Cache Issues / Notes

1. **One view component still populates a cache** that services
   invalidate. `NotificationBadge:{userId}` is populated by
   `NotificationBellViewComponent` — a backwards pattern: services know how
   to invalidate but not to recompute. `FeedbackBadgeCount` is
   owned and populated by `FeedbackService`; `NavBadge:Voting:{userId}`
   is owned and populated by `ApplicationDecisionService`.

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

5. **Caching decorators live beside their inner service in each section's
   own project**, not in a shared Infrastructure layer. Every decorator
   listed above lives in its owning section's `Services/` (or, for Users,
   `Data/`) folder. They are transparent
   decorators over an inner Scoped service (registered keyed
   `"user-inner"`, `"team-inner"`, `"shift-view-inner"`,
   `"ticket-query-inner"`, `"camp-inner"`, `"calendar-inner"`,
   `"event-inner"`, `"role-assignment-inner"`, `"legal-document-sync-inner"`,
   `"early-entry-inner"`, plus the Consent inner key) and inherit
   `TrackedCache<TKey, TValue>` (`src/Humans.Base/Interfaces/Caching/TrackedCache.cs`)
   rather than using `IMemoryCache` for their projection state.

---


## Appendix A: Out-of-Service Database Access

Controllers and view components that inject a `DbContext` or
repositories directly, bypassing the service layer.

### Controllers

None. Every write (`User`/`Profile`/`UserEmail`,
system-team membership, dev barrio camp/season/lead via `ICampService` /
`ICampRoleService`, city-planning team, role assignments, contact fields)
goes through the owning section's service interface per design-rules §2c.
`DevLoginController` injects `UserManager<User>`,
`SignInManager<User>`, `IUserEmailService`, and `DevPersonaSeeder`
(`src/Sections/Humans.Development/Services/DevPersonaSeeder.cs`, which
itself owns no DbContext). `AdminController`'s direct DB reads go behind
`IAdminDatabaseDiagnosticsService`. All web
controllers (Email, Google, Profile, Board, Budget, CampAdmin, Guest,
Unsubscribe, TeamAdmin, ShiftAdmin, Calendar, Feedback, Issues, Tickets,
Finance, DevLogin, etc.) go entirely through service interfaces.

### View Components (cache populators)

| Component | Cache Key |
|-----------|-----------|
| **NotificationBellViewComponent** | `NotificationBadge:{userId}` (read/write) |

All other view components read via owning services. `NavBadgesViewComponent`
owns no cache entries — `FeedbackBadgeCount` is owned by `FeedbackService`
and `NavBadge:Voting:{userId}` by `ApplicationDecisionService`.

### Background Jobs

Every recurring job lives in its owning section's `Contracts/` folder. Only
the DI registration and the roll-call entry are Shell's, because
`UseHumansRecurringJobs` names each job by concrete type. Mutation-heavy
logic funnels into services even from jobs
(e.g. `CleanupNotificationsJob` calls `INotificationRepository` via
`NotificationService`; `LegalDocumentSyncService` runs via Hangfire and
goes through its own repository). Specific jobs and their tables vary;
treat each as an audit item per the section §15 carve-outs.

---


## Appendix B: Out-of-Service Cache Access

Controllers and components that touch `IMemoryCache` directly.

| Controller / Component | Cache Operation | Key |
|------------------------|-----------------|-----|
| **NotificationBellViewComponent** | GetOrCreate | `NotificationBadge:{userId}` |
| **GateLoginThrottle** (Web infrastructure, used by the gate-terminal sign-in) | TryGetValue / Set / Remove | `GateLoginFailures:{sourceIp}` |
| **GatePinThrottle** (`Humans.Gate/Services/Stores/`; used by `GateController` PIN claim / override) | TryGetValue / Set / Remove | `GatePinFailures:{key}` |
| **GateVendorMirrorLedger** (`Humans.Gate/Services/Stores/`; used by `GateController` and `GateVendorBackfillAdminController`) | TryGetValue / Set (atomic claim) | `GateVendorMirrorSent:{vendorTicketId}` |
| **GateTerminalAccountSeeder** (`Humans.Tickets/Services/`) | `InvalidateUserAccess` extension | `ActiveTeams` + `claims:{userId}` + `shift-auth:{userId}` for the kiosk account |

The §15 work continues to push cache populators into the owning service
behind transparent decorators. `NavBadgesViewComponent` does not inject
`IMemoryCache` — its badge counts are owned by
`FeedbackService` (`FeedbackBadgeCount`) and `ApplicationDecisionService`
(`NavBadge:Voting:{userId}`).
