# Service Data Access Map

Audit of which services access which database tables and cache keys, organized by section.
The goal is to identify cross-section table overlap, duplicated caching, and cache configuration issues.

**Generated:** 2026-08-18

> **Methodology.** Tables are resolved by following each service's injected
> repository interface to its EF-backed implementation in the section's own
> `Data/Repository.cs` (or `Data/Repositories/*.cs`) under `src/Sections/`,
> then mapping the `DbSet<>` (or bare `Set<T>()`) usage to the declaring
> context in that same section's `Data/` folder. **`Humans.Application` and
> `Humans.Infrastructure` are both gone** — `Humans.Infrastructure` was
> deleted at G5 lane 5b-6 and `Humans.Application` was emptied and
> dereferenced at G5 lane 5c (nobodies-collective/Humans#866); every
> repository, `DbContext`, and service now lives under `src/Sections/`, and
> the handful of true cross-section orchestrators that never owned a table
> (Dashboard, the Agent preload augmentor) live in `src/Humans.Web/Services/`
> instead. **Since the per-section DbContext split
> (nobodies-collective/Humans#858) `HumansDbContext` no longer exists at
> all.** Peel 15 (nobodies-collective/Humans#1273, 2026-08-13) merged Users
> and Profiles into a new `UsersDbContext` and **deleted `HumansDbContext`
> outright** — the type, its factory, the model snapshot, and all 288 root
> migration files. Every context below is internal-sealed with its own
> `IDbContextFactory<T>`/direct-injection pattern, against the same
> database/connection. Each peeled context gets its own
> `__EFMigrationsHistory_<Section>` table —
> `UsersDbContext` is the sole exception, carrying forward the original
> unsuffixed `__EFMigrationsHistory` from the deleted root chain (no
> removal migration; the table itself is left in place pending its own
> cleanup PR):
>
> | DbContext | Owns |
> |-----------|------|
> | `UsersDbContext` | `Profiles`, `ContactFields`, `UserEmails`, `VolunteerHistoryEntries`, `AccountMergeRequests`, `CommunicationPreferences`, `ProfileLanguages`, `EventParticipations`, plus the Identity base (`IdentityDbContext<User, IdentityRole<Guid>, Guid>`: `users`/`roles`/`user_roles`/`user_claims`/`user_logins`/`role_claims`/`user_tokens`) — **the successor to `HumansDbContext`** (peel 15, nobodies-collective/Humans#1273, 2026-08-13). Own project, `src/Sections/Humans.Users/`, G5 (Profiles folded in — no separate Profiles project). |
> | `TeamsDbContext` | `Teams`, `TeamMembers`, `TeamJoinRequests`, `TeamJoinRequestStateHistories`, `TeamRoleDefinitions`, `TeamRoleAssignments`, `TeamEarlyEntryGrants` — peeled in nobodies-collective/Humans#1264; owned by the now fully-G5 `src/Sections/Humans.Teams` project (moved off `src/Humans.Application`/`src/Humans.Infrastructure` in G5 batch #3, PR #1280, 2026-08-13); `TeamRepository` injects `IDbContextFactory<TeamsDbContext>` |
> | `AuditLogDbContext` | `AuditLogEntries` — owned by the now fully-G5 `src/Sections/Humans.AuditLog` project (moved in G5 batch #3, PR #1280, 2026-08-13; the first *horizontal* section to go to G5 — see [AuditLog](#auditlog)) |
> | `LegalDbContext` | `LegalDocuments`, `DocumentVersions`, `ConsentRecords` — own project, `src/Sections/Humans.Consent/Data/` (G5). `ConsentRecords` living here (not a separate Consent context) is unchanged from before the peel — Consent has never owned its own DbContext. |
> | `ShiftsDbContext` | `EventSettings`, `Rotas`, `Shifts`, `ShiftSignups`, `ShiftTags`, `RotaShiftTags` (`rota_shift_tags` — the implicit many-to-many mapped by `ShiftTagConfiguration` via `UsingEntity`), `VolunteerEventProfiles`, `GeneralAvailability`, `VolunteerBuildStatuses`, `VolunteerTagPreferences` (own project, `src/Sections/Humans.Shifts/`, G5) |
> | `TicketsDbContext` | `TicketOrders`, `TicketAttendees`, `TicketSyncStates`, `TicketTransferRequests` — owned by the now fully-G5 `src/Sections/Humans.Tickets` project, alongside a `src/Sections/Humans.Tickets.Contracts` leaf and the `src/Sections/Humans.TicketTailor` vendor adapter (G5 batch #3, PR #1280, 2026-08-13; see [Tickets](#tickets)) |
> | `AuthDbContext` | `RoleAssignments` (peeled #1234; own project, `src/Sections/Humans.Auth/`, G5) |
> | `GovernanceDbContext` | `Applications`, `ApplicationStateHistories`, `BoardVotes` (own project, `src/Sections/Humans.Governance/`, G5) |
> | `CampaignsDbContext` | `Campaigns`, `CampaignCodes`, `CampaignGrants` (own project, `src/Sections/Humans.Campaigns/`, G5 since PR #1263, 2026-08-11) |
> | `GoogleIntegrationDbContext` | `GoogleResources`, `GoogleSyncOutboxEvents`, `SyncServiceSettings` (peeled #1236; own project, `src/Sections/Humans.GoogleIntegration/`, G5 — the [Monitor](#monitor) section split out of it this sweep, but owns no table of its own) |
> | `FeedbackDbContext` | `FeedbackReports`, `FeedbackMessages` (own project, `src/Sections/Humans.Feedback/`, G5 since PR #1263, 2026-08-11) |
> | `CityPlanningDbContext` | `CityPlanningSettings`, `CampPolygons`, `CampPolygonHistories` (own project, `src/Sections/Humans.CityPlanning/`, G5 since PR #1263, 2026-08-11) |
> | `BudgetDbContext` | `BudgetYears`, `BudgetGroups`, `BudgetCategories`, `BudgetLineItems`, `BudgetAuditLogs`, `TicketingProjections` (own project, `src/Sections/Humans.Budget/`, G5 since PR #1263, 2026-08-11) |
> | `CampsDbContext` | `Camps`, `CampSeasons`, `CampHistoricalNames`, `CampImages`, `CampSettings`, `CampMembers`, `CampRoleDefinitions`, `CampRoleAssignments` (own project, `src/Sections/Humans.Camps/`, G5) |
> | `GateDbContext` | `GateScanEvents`, `GateSettings`, `GateStaffPins` (own project, `src/Sections/Humans.Gate/`, G5, table names `gate_scan_events` / `gate_settings` / `gate_staff_pins`) |
> | `SystemDbContext` | `DataProtectionKeys` — ASP.NET Data Protection key ring storage, wired directly in `src/Humans.Web/Program.cs`; **no owning Application section, no repository, no service** |
> | `EmailDbContext` | `EmailOutboxMessages` (peeled #1234) |
> | `CalendarDbContext` | `CalendarEvents`, `CalendarEventExceptions` (own project, `src/Sections/Humans.Calendar/`, G5 since PR #1263, 2026-08-11) |
> | `NotificationsDbContext` | `Notifications`, `NotificationRecipients` (own project, `src/Sections/Humans.Notifications/`, G5 since PR #1263, 2026-08-11) |
> | `IssuesDbContext` | `Issues`, `IssueComments` (own project, `src/Sections/Humans.Issues/`, G5 since PR #1263, 2026-08-11) |
> | `SurveysDbContext` | `Surveys`, `SurveyQuestions`, `SurveyQuestionOptions`, `SurveyInvitations`, `SurveyResponses`, `SurveyAnswers` (own project, `src/Sections/Humans.Surveys/`, G5 since PR #1251, 2026-08-10) |
> | `AgentDbContext` | `AgentConversations`, `AgentMessages`, `AgentSettings` (own project, `src/Sections/Humans.Agent/`, G5 since PR #1259, 2026-08-11) |
> | `SystemSettingsDbContext` | `SystemSetting` (own project, `src/Sections/Humans.SystemSettings/`, G5) |
> | `ContainersDbContext` | `Containers`, `ContainerPlacements` (own project, `src/Sections/Humans.Containers/`, G5) |
> | `ExpensesDbContext` | `ExpenseReports`, `ExpenseLines`, `ExpenseAttachments`, `HoldedExpenseOutboxEvents` (own project, `src/Sections/Humans.Expenses/`, G5) |
> | `FinanceDbContext` | `HoldedExpenseDocs`, `HoldedCategoryMap`, `HoldedCreditorContacts`, `HoldedDocSyncStates` (own project, `src/Sections/Humans.Finance/`, G5). **Narrowed this sweep** — the ledger mirror (`HoldedLedgerLines`, its sync state, the chart-of-accounts cache, and the API call log) moved out to the new `HoldedDbContext` below (migration `20260810204942_HoldedMirrorMovesToHoldedSection`). |
> | `HoldedDbContext` | `HoldedLedgerLines`, `HoldedSyncStates`, `HoldedAccounts`, `HoldedApiCalls` — **new section this sweep** (own project, `src/Sections/Humans.Holded/`, G5). The daybook-journal ledger mirror, chart-of-accounts cache, and Holded API call-log/metering, split out of Finance so the two peeled sections that both touch Holded data are structurally isolated from each other. |
> | `EventGuideDbContext` | `EventGuideSettings`, `EventCategories`, `EventVenues`, `Events`, `EventModerationActions`, `EventPreferences`, `EventFavourites` (own project, `src/Sections/Humans.Events/`, G5; the Shifts-owned `EventSettings` / `EventParticipations` tables deliberately stay off this context, despite the name collision) |
> | `StoreDbContext` | `StoreProducts`, `StoreOrders`, `StoreOrderLines`, `StorePayments`, `StoreInvoices`, `StoreTreasurySyncStates` (own project, `src/Sections/Humans.Store/`, G5) |
>
> **Change this sweep (2026-08-18):** G5 (nobodies-collective/Humans#866)
> finished emptying both legacy layers. **`Humans.Application` is now gone**
> (emptied and dereferenced at G5 lane 5c), on top of `Humans.Infrastructure`
> already being gone (lane 5b-6). The six sections still described below as
> living under `src/Humans.Application/Services/<X>/` — Profiles, Users,
> Google Integration, Camps, Shifts, Legal, Consent — moved into their own
> `src/Sections/Humans.<X>/` projects with no table-ownership change (same
> `DbSet`s, new project); Dashboard (`DashboardService`,
> `AdminDashboardService`) has no owned tables and landed in
> `src/Humans.Web/Services/Dashboard/` alongside the other true cross-section
> orchestrators, not in a section project. **Legal and Consent share one
> project** (`src/Sections/Humans.Consent/`) rather than getting one each —
> they always shared `LegalDbContext`. **A new horizontal section,
> [Monitor](#monitor) (`src/Sections/Humans.Monitor/`), split out of Google
> Integration**, taking `DriveActivityMonitorService` /
> `IDriveActivityMonitorService` / `DriveActivityMonitorJob` with it; Google
> Integration keeps the Drive/Directory API clients Monitor calls into. Two
> previously-undocumented services surfaced this sweep:
> `GoogleSyncOutboxProcessor` (Google Integration — the outbox drain,
> `[CrossSectionWrite]`-marked, lifted verbatim out of the deleted
> `Humans.Infrastructure/Jobs/ProcessGoogleSyncOutboxJob`) and
> `NonCompliantMemberSuspension` (Users). The shared cache infrastructure
> (`CacheKeys.cs`, `MemoryCacheExtensions.cs`, `MemoryCacheInvalidators.cs`,
> `TrackedCache<TKey, TValue>`) consolidated into `src/Humans.Interfaces/`
> now that there is no `Humans.Application`/`Humans.Infrastructure` split
> left to straddle — see the note below the DbContext table.
>
> **Change since prior sweep:** two structural moves landed on 2026-08-13.
> (1) **Peel 15 / #858 finale** (PR #1273): Users and Profiles — the last
> two sections still sharing `HumansDbContext` — merged into one new
> `UsersDbContext`, and `HumansDbContext` itself is **deleted**: the type,
> its factory, the model snapshot, and all 288 root migration files. There
> is no removal migration (no snapshot left to shrink); `__EFMigrationsHistory`
> is left in place pending its own cleanup PR. The per-section DbContext
> split from nobodies-collective/Humans#858 is now **complete** — every
> table-owning section has its own context. (2) **G5 batch #3** (PR #1280):
> Tickets, Teams, and AuditLog moved from `src/Humans.Application`/
> `src/Humans.Infrastructure` into their own `src/Sections/Humans.<Section>/`
> projects. Tickets additionally split into three projects — the section
> (`src/Sections/Humans.Tickets`), a contracts leaf
> (`src/Sections/Humans.Tickets.Contracts`), and the TicketTailor vendor
> adapter (`src/Sections/Humans.TicketTailor`, the sole implementation of
> the vendor port) — see [Tickets](#tickets). AuditLog is the first
> *horizontal* section to go to G5. The table-owning half moved first:
> `AuditLogService`/`AuditLogRepository`/`AuditLogDbContext` are in
> `src/Sections/Humans.AuditLog` (+ a `Humans.AuditLog.Contracts` leaf for
> `IAuditLogService`). `AuditViewerService` — which reads `IUserServiceRead`,
> `ITeamServiceRead` and `ITeamResourceService` — stayed in
> `src/Humans.Application/Services/AuditLog/` for one batch and then followed
> in G5 lane 4b-2h (Peter's 2026-08-14 Base-floor decision), landing in
> `src/Sections/Humans.AuditLog/{Contracts,Services,ViewComponents}/`; see
> [AuditLog](#auditlog). None of these three moves changed table ownership —
> same DbSets, new project. Separately, **#992 dropped all 54 cross-section
> EF foreign-key constraints** and **#996 stripped the last 11 cross-section
> EF navigation properties** — cross-section relationships are no longer
> expressible in the EF model at all, so any remaining cross-section table
> read (flagged below) can only be a hand-written query against a foreign
> `DbContext`, never a `.Include()`.
>
> Each peeled context applies its `IEntityTypeConfiguration` classes
> explicitly (no assembly scanning), so a section's model can never
> accrete another section's tables by accident — there is no longer a
> catch-all context that scans the assembly minus peeled namespaces (the
> old `HumansDbContext.PeeledConfigurationNamespaces` property is gone with
> the type). Below, each section header states which DbContext backs its
> tables; per-table `DbContext` notes appear only where a section's tables
> span more than one context.
> The marker-only project `src/Humans.Interfaces/` now holds the shared
> `IApplicationService`, `IRepository`, `IOrchestrator`, `IFanout`, and
> `IInvalidator` marker interfaces (no data-access behavior of its own), plus
> the cache infrastructure that used to straddle the deleted Application/
> Infrastructure split: `CacheKeys.cs`, the invalidator extensions in
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
> Section-decorator `TrackedCache<TKey, TValue>` subclasses now live beside
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
## Dashboard

Folder: `src/Humans.Web/Services/Dashboard/` — moved from
`src/Humans.Application/Services/Dashboard/` at G5 (nobodies-collective/Humans#866);
has no owned tables and never became a section project, so it landed
alongside the other Web-layer cross-section orchestrators instead of under
`src/Sections/`. No owned DB tables.

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

After the §15 / `IUserMerge` consolidation, the
`GoogleAdminService` / `CampRepository.GetCampLeadsAsync` /
`CalendarRepository` / `BudgetRepository` / `EventRepository` /
`ShiftSignupRepository` / `TicketRepository` cleanups, the Profiles
repository consolidation (PRs #810/#811: three Profiles repositories
folded into `IUserRepository`), the #882 / #889 repository
convergences, and the removal of `IShiftSignupRepository`
as a separate interface (signup methods merged into
`IShiftManagementRepository.Signups.cs`), **no cross-section
repository-level table reads remain.**
Because the consolidated `IUserRepository` is now the single owner of every
per-user table across the Users+Profiles section merge, the
previously-tracked `IUserEmailRepository` violations are recategorised —
they are now internal reads/writes of the unified User+Profile owner, not
cross-section violations.

This is now also enforced at the schema level: **#992 dropped all 54
cross-section EF foreign-key constraints** and **#996 stripped the last 11
cross-section EF navigation properties** — a cross-section table read can
no longer be expressed as an `.Include()` even by accident; it would have
to be a hand-written query against a foreign `DbContext`, and the
per-section DbContext peel (see the intro table) means that context
injection isn't even available outside the owning section's repository.

The four previously-tracked cross-section repository reads are all now
**resolved** — each table is owned by exactly one repository, and the
former HUM0025 `[Grandfathered]` markers have been retired:

| Table | Owning Section | Status |
|-------|----------------|--------|
| **GoogleSyncOutboxEvents** | Google Integration | Resolved (#889) — `TeamRepository` no longer writes it; `TeamService` appends via `IGoogleSyncOutboxService` inside a `TransactionScope`. |
| **EventSettings** | Shifts | Resolved (#882) — `VolunteerTrackingRepository` no longer reads it; the `GetEligibleBuildSignupsAsync` / `GetConfirmedShiftsInRangeAsync` reads converged onto `ShiftRepository`. |
| **ShiftSignups** | Shifts | Resolved (#882) — same convergence onto `ShiftRepository`. |
| **SystemSettings** (`SystemSetting`) | SystemSettings | Resolved (#889) — single owner `SystemSettingsRepository`; `EmailOutboxRepository` and `DriveActivityMonitorRepository` no longer touch the table (the latter was deleted). Consumers route through `ISystemSettingsService`. |

### Notable Cross-Section Patterns

1. **`IUserMerge` retired most cross-section profile/identity writes; the
   merge surface now lives in Users (#899).** `AccountMergeService` and
   `DuplicateAccountService` (plus `AccountMergeRepository` and the
   `AccountMergeRequests` table) moved from Profiles into the Users section.
   `AccountMergeService` no longer injects profile-owned repositories
   directly — it fans out over `IEnumerable<IUserMerge>`, with each
   section's service implementing `IUserMerge` to reassign its own owned
   rows. `DuplicateAccountService` was rewritten to **detection-only** (no
   repository, no DB access) — its prior direct `IUserRepository` writes to
   `Users` / `EventParticipations` / `IdentityUserLogins` and the profile
   tables (the §2c cross-section violations called out last sweep) are
   **gone**; it now reads through `IUserService` / `ITeamService` /
   `IRoleAssignmentService` only. With PRs #810/#811 the three Profiles
   repositories collapsed into `IUserRepository`, so what looked like
   cross-section reads/writes between the Profiles and Users sections is now
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
   the Governance pair `IApplicationServiceRead` / `IMembershipCalculatorRead`
   (PR #851). Several of these are the Singleton caching decorators re-cast
   to a narrow surface; the Governance read interfaces are plain
   read-only contracts the section's services implement directly. All keep
   the cross-section coupling minimal and `[SurfaceBudget]`-bounded.

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

5. **Teams ↔ Google outbox now goes through the owning service (#889).**
   `TeamService` appends `GoogleSyncOutboxEvents` via
   `IGoogleSyncOutboxService.AddAsync` / `AddRangeAsync` inside a
   `TransactionScope`, so each team mutation stays atomic with its outbox
   event without `TeamRepository` reaching into the table. The Google
   Integration section owns the table end-to-end (write surface
   `GoogleSyncOutboxService`, read/process via `IGoogleSyncOutboxRepository`).
   The prior cross-section repository write is closed.

6. **DriveActivityMonitor user fallback uses UserInfo; state via SystemSettings
   service (#889).** `DriveActivityMonitorService` — now the [Monitor](#monitor)
   section's own, split out of Google Integration this sweep — resolves Google
   `people/{client_id}` actors through Directory first, then through a per-run
   Google provider-key -> `UserInfo` index from
   `IUserServiceRead.GetAllUserInfosAsync`. Its `IDriveActivityMonitorRepository`
   was deleted; the last-run marker
   (`SystemSettingKeys.DriveActivityMonitorLastRunAt`) is now read/written
   through `ISystemSettingsService` — a §15-compliant cross-section service
   call, not a foreign repository read.

7. **SystemSettings is now owned by a single section/repository (#889).**
   The `SystemSetting` key/value table is owned by the new SystemSettings
   section's `SystemSettingsRepository`; consuming sections route through
   `ISystemSettingsService` rather than touching the table from their own
   repository. This replaces the prior per-key-ownership convention — the
   two well-known keys now flow through the one owner:

   | Key | Consuming section | Routed via |
   |-----|-------------------|------------|
   | `IsEmailSendingPaused` | Email | `EmailOutboxService` → `ISystemSettingsService` |
   | `DriveActivityMonitor:LastRunAt` | Google Integration | `DriveActivityMonitorService` → `ISystemSettingsService` |

   `EmailOutboxRepository` and `DriveActivityMonitorRepository` no longer
   touch `SystemSettings` (the latter is deleted). New keys should be added
   to `SystemSettingKeys` and accessed through `ISystemSettingsService`.

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
    (`HumansDbContext` was retired from these rules' vocabulary when the
    type itself was deleted in peel 15 — they now match "any application
    DbContext" generically, not one named type). See
    [`code-analysis.md`](code-analysis.md) for the full analyzer list.

11. **Provider-based fan-out for derived data.** `IEarlyEntryService`
    aggregates per-user grants over `IEnumerable<IEarlyEntryProvider>`
    implementations (currently Camps, Shifts, and Teams — PR #860).
    `IUserMerge`,
    `IUserDataContributor`, and `IMailerAudience` use the same
    enumerable-injection pattern. This keeps the orchestrator
    section-agnostic; new contributors register a single service
    interface in their section's DI extension.

12. **Surveys section uses cross-section read-split surfaces exclusively
    (#884).** `SurveyService` fans out over `ITeamServiceRead`,
    `IUserServiceRead`, `ITicketServiceRead`, and `IShiftView` for
    audience resolution and display-name stitching — never a foreign
    repository. `IGoogleTranslationService` (GoogleIntegration section)
    is the translation bridge for the admin pre-fill helper. No
    cross-section table reads. There is no `ISurveyServiceRead` — it
    shipped empty in v1, no other section ever consumed it, and it was
    deleted at G5; the section's only outbound contract is the
    single-member `Humans.Surveys.Contracts.ISurveyReminderSender`.

13. **ICalFeed is a pure fan-out orchestrator (#931).** `ICalFeedService`
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
    table (#1066).** `GateService` resolves a scanned barcode by filtering
    the cached `ITicketServiceRead.GetTicketOrdersAsync` projection in
    memory (no new interface method), checks early entry via the cached
    `IEarlyEntryService`, verifies supervisor roles via
    `IRoleAssignmentService`, and pulls the gate-crew roster via
    `IShiftManagementService` — all §15-compliant service-interface calls.
    Its one cross-section **write** — projecting an admit onto the guest's
    `EventParticipations` row as `Attended` (#1081) — goes through the
    owning `IUserService.SetParticipationFromTicketSyncAsync`, so no
    design-rule violation. `IGateRepository` reads/writes only the three
    Gate-owned tables. The section deliberately has **no caching
    decorator**: admission verdicts must be live.

15. **The per-section DbContext split (#858) is now complete — "one
    table, one repository" is a compile/schema-enforced boundary for
    every table-owning section, with no shared context left.** Before the
    split, every table lived in one shared `HumansDbContext`, so a
    cross-section table read was only a code-review catch. Peel 15 (PR
    #1273, 2026-08-13) — Users+Profiles merging into `UsersDbContext` —
    was the **last** peel: `HumansDbContext` is deleted outright, not just
    narrowed. 29 contexts now exist — see the full DbContext table in the
    intro Methodology block above — each mapping **only** its own
    section's tables via explicit `ApplyConfiguration` calls (no assembly
    scanning), so a section's repository cannot accidentally `Set<T>()` a
    foreign table without first injecting that section's
    `IDbContextFactory<T>` — a conspicuous, reviewable change. Combined
    with #992's cross-section FK removal and #996's nav-property removal
    (point 1 above), the boundary is enforced at the schema level too, not
    just at the DI-injection level. The only table-owning context without
    a per-section owner is `SystemDbContext` (`DataProtectionKeys`,
    framework-only, no owning Application section). Every
    context points at the same physical database/connection — this is a
    code-side EF model partition, not a database migration — and each
    peeled context has its own `__EFMigrationsHistory_<Section>` table
    except `UsersDbContext`, which carries forward the original
    unsuffixed `__EFMigrationsHistory` from the deleted root migration
    chain (no removal migration was needed — there was no snapshot left to
    shrink; dropping the old table name is its own follow-up cleanup PR).

---


## Cache Inventory

### All Cache Keys

Sourced from `src/Humans.Interfaces/CacheKeys.cs` and
`src/Humans.Interfaces/Extensions/MemoryCacheExtensions.cs`. TTL/type
classification mirrors `CacheKeys.Metadata` (surfaced on the Admin
`/Debug/CacheStats` page). Note: most section projections are now
`TrackedCache` dictionaries (not `IMemoryCache` keys) and are listed
separately below the key table.

| Key | TTL | Type | Populated By | Invalidated By |
|-----|-----|------|-------------|----------------|
| `FeedbackBadgeCount` | 2 min | Static | **FeedbackService** (`GetActionableCountAsync`) | `INavBadgeCacheInvalidator` (FeedbackService, IssuesService, ApplicationDecisionService, RoleAssignmentService) |
| `NotificationBadge:{userId}` | 2 min | Per-User | **NotificationBellViewComponent** | NotificationService, NotificationEmitter, NotificationInboxService |
| `NotificationMeters` | 2 min | Static | NotificationMeterProvider | `INotificationMeterCacheInvalidator` (TeamService, ApplicationDecisionService) |
| `ActiveTeams` | 10 min | Static | _(retired — replaced by `CachingTeamService` `TrackedCache<Guid, TeamInfo>`; key remains in `CacheKeys.Metadata` for invalidator compat)_ | `IActiveTeamsCacheInvalidator` → `ITeamService.InvalidateActiveTeamsCache()` |
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
| `TrackedCache<Guid, ShiftUserView>` / `TrackedCache<Guid, ShiftRotaView>` | Shifts | `ShiftView.UserView` / `ShiftView.RotaView` | Per-User / Per-Entity | CachingShiftViewService lazy load | `IShiftViewInvalidator` (ShiftManagementService, ShiftSignupService, VolunteerTrackingService, AccountDeletionService) |
| `TrackedCache<Guid, TicketOrderInfo>` | Tickets | `Tickets.Orders` | Per-Entity | CachingTicketQueryService warmup + lazy load | `ITicketCacheInvalidator` |
| `TrackedCache<Guid, CachedUserTicketHoldings>` | Tickets | `Tickets.UserHoldings` | Per-User (5-min freshness in value) | CachingTicketQueryService lazy load | `ITicketCacheInvalidator` |
| `TrackedCache<Guid, CampInfo>` + settings slot | Camps | `Camp.CampInfo` | Per-Entity / Static | CachingCampService warmup + lazy load | `ICampInfoInvalidator` + `IUserMerge` |
| `TrackedCache<Guid, CalendarEventInfo>` | Calendar | `Calendar.Event` | Per-Entity | CachingCalendarService warmup + lazy load | per-event `ReplaceAsync` after delegated write |
| `TrackedCache<Guid, ApprovedEventView>` + category/venue/settings snapshots | Events | `Event.ApprovedEventView` | Per-Entity / Static | CachingEventService warmup + lazy load | `IEventViewInvalidator` (inline per write) |
| `TrackedCache<Guid, UserConsentInfo>` | Consent | `Consent.UserConsentInfo` | Per-User | CachingConsentService lazy load | `IConsentCacheInvalidator` (synchronous per-user evict on submit) |
| `TrackedCache<Guid, LegalDocumentInfo>` | Legal | `Legal.LegalDocumentInfo` | Per-Entity | CachingLegalDocumentSyncService warmup + lazy load | `ILegalDocumentCacheInvalidator.InvalidateAll` (called directly by `LegalDocumentSyncService` after each write — #751) |
| `TrackedCache<Guid, RoleAssignmentRow>` | Auth | `Auth.RoleAssignmentRow` | Per-Entity | CachingRoleAssignmentService warmup + lazy load | `IRoleAssignmentCacheInvalidator.InvalidateAll` (service-level) |
| `TrackedCache<Guid, UserEarlyEntry?>` | Early Entry | `EarlyEntry.UserEarlyEntry` | Per-User (negative-result safe) | CachingEarlyEntryService lazy load | `IEarlyEntryInvalidator.InvalidateUser` / `InvalidateAll` (ShiftManagementService, ShiftSignupService, CampService, TeamService) |

### Cache Issues / Notes

1. **One view component still populates a cache** that services
   invalidate. `NotificationBadge:{userId}` is populated by
   `NotificationBellViewComponent`. This is the same backwards pattern
   called out in prior sweeps — services know how to invalidate but not
   to recompute. (`NavBadgeCounts` is retired — `FeedbackBadgeCount` is
   now owned and populated by `FeedbackService`; `NavBadge:Voting:{userId}`
   is now owned and populated by `ApplicationDecisionService`.
   `NobodiesTeamEmails_All` is gone — replaced by a service method.)

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
   own project**, not in a shared Infrastructure layer — `Humans.Infrastructure`
   is gone, and every decorator listed above moved into its owning section's
   `Services/` (or, for Users, `Data/`) folder at G5. They are transparent
   decorators over an inner Scoped service (registered keyed
   `"user-inner"`, `"team-inner"`, `"shift-view-inner"`,
   `"ticket-query-inner"`, `"camp-inner"`, `"calendar-inner"`,
   `"event-inner"`, `"role-assignment-inner"`, `"legal-document-sync-inner"`,
   `"early-entry-inner"`, plus the Consent inner key) and inherit
   `TrackedCache<TKey, TValue>` (`src/Humans.Interfaces/Interfaces/Caching/TrackedCache.cs`)
   rather than using `IMemoryCache` for their projection state.

---


## Appendix A: Out-of-Service Database Access

Controllers and view components that inject `HumansDbContext` or
repositories directly, bypassing the service layer. After the
`HUM0008` / `HUM0009` analyzers shipped (PR #493, #494), this surface
shrank to a single dev-only path — and that path is now also closed.

### Controllers

None. `DevLoginController`'s previous direct `HumansDbContext` writes
(Camps / CampSeasons / CampLead seeding for dev personas) moved into
`DevPersonaSeeder` (`src/Sections/Humans.Development/Services/DevPersonaSeeder.cs`),
which itself owns no DbContext — every write (`User`/`Profile`/`UserEmail`,
system-team membership, dev barrio camp/season/lead via `ICampService` /
`ICampRoleService`, city-planning team, role assignments, contact fields)
goes through the owning section's service interface per design-rules §2c.
`DevLoginController` now only injects `UserManager<User>`,
`SignInManager<User>`, `IUserEmailService`, and `DevPersonaSeeder`.

`AdminController` is no longer in this list either — its previous direct DB
reads moved behind `IAdminDatabaseDiagnosticsService` (PR #494). All web
controllers (Email, Google, Profile, Board, Budget, CampAdmin, Guest,
Unsubscribe, TeamAdmin, ShiftAdmin, Calendar, Feedback, Issues, Tickets,
Finance, DevLogin, etc.) go entirely through service interfaces.

### View Components (cache populators)

| Component | Cache Key |
|-----------|-----------|
| **NotificationBellViewComponent** | `NotificationBadge:{userId}` (read/write) |

All other view components read via owning services post-§15 audit.
`NavBadgesViewComponent` no longer owns or reads/writes any cache entries
(PR #1010) — `FeedbackBadgeCount` is now owned by `FeedbackService` and
`NavBadge:Voting:{userId}` by `ApplicationDecisionService`. The former
`NobodiesEmailBadgeViewComponent` cache populator was retired along with
the `NobodiesTeamEmails_All` key.

### Background Jobs

Every recurring job now lives in its owning section's `Contracts/` folder —
`src/Humans.Infrastructure/Jobs/` was emptied and deleted at G5 lane 5b-5
(nobodies-collective/Humans#866). Only the DI registration and the roll-call
entry are Shell's, because `UseHumansRecurringJobs` names each job by concrete
type. Mutation-heavy logic funnels into services even from jobs
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
| **GatePinThrottle** (`Humans.Gate/Services/Stores/`, moved this sweep from Web infrastructure; used by `GateController` PIN claim / override) | TryGetValue / Set / Remove | `GatePinFailures:{key}` |
| **GateVendorMirrorLedger** (`Humans.Gate/Services/Stores/`, moved this sweep from Web infrastructure; used by `GateController` and `GateVendorBackfillAdminController`) | TryGetValue / Set (atomic claim) | `GateVendorMirrorSent:{vendorTicketId}` |
| **GateTerminalAccountSeeder** (Web infrastructure) | `InvalidateUserAccess` extension | `ActiveTeams` + `claims:{userId}` + `shift-auth:{userId}` for the kiosk account |

The §15 work continues to push cache populators into the owning service
behind transparent decorators. `NavBadgesViewComponent` no longer injects
`IMemoryCache` (PR #1010) — its badge counts are now owned by
`FeedbackService` (`FeedbackBadgeCount`) and `ApplicationDecisionService`
(`NavBadge:Voting:{userId}`). The previous `NobodiesTeamEmails_All`
invalidation that was scattered across three controllers has already been
retired by moving the lookup into
`IUserEmailService.GetNobodiesTeamEmailsByUserIdsAsync`.
