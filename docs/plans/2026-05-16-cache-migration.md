# Cache Migration Transition Log

Audit performed 2026-05-16 across 11 sections. Each task below is a self-contained unit a parallel agent can pull, work, PR, and close. Sorted by **value/impact**, never by effort.

## Migration context

Three sections already run the canonical cached read-model pattern:

| Section | Projection | Decorator | Interceptor / Invalidator |
|---|---|---|---|
| Users + Profile | `UserInfo` (8 tables) | `CachingUserService` | `UserInfoSaveChangesInterceptor` + decorator-mediated profile writes |
| Teams | `TeamInfo` | `CachingTeamService` | Decorator-mediated only (no interceptor); `IActiveTeamsCacheInvalidator` for cross-section eviction |
| Shifts | `ShiftRotaView` + `ShiftUserView` | `CachingShiftViewService` | Explicit `IShiftViewInvalidator` called from 4 owning services |

The shape of a section's invalidation mechanism is a design call per section, not a universal recipe. Sections with a wide write surface (Camps, Events) should prefer the explicit-invalidator pattern; narrow-write sections (Calendar, Legal) can stay interceptor-only.

## Rules for parallel work

1. **One agent = one task ID.** Claim it by adding `[CLAIMED: <branch-name>]` on the task line. Branches off `origin/main` into a worktree at `.worktrees/<task-id>`.
2. **Stay inside your section's owned tables.** No cross-section table writes. Read other sections' data via their service interfaces only.
3. **`UserInfo` is shared infrastructure — never extend its projection.** If your section needs user data, consume `IUserService.GetUserInfoAsync` at the presentation layer.
4. **`HumansDbContext` interceptor registration is a hot file.** If you add a `SaveChangesInterceptor`, register it in the DbContext options. Last-commit-wins on the registration list; if you merge-conflict, rebase.
5. **Section DI extensions** (`src/Humans.Web/Extensions/Sections/<Section>SectionExtensions.cs`) are isolated per section — safe to edit in parallel.
6. **One PR per task.** Don't stack.
7. Before push: `dotnet build Humans.slnx -v quiet` + `dotnet test Humans.slnx -v quiet`. Architecture tests pin a lot of structure — green or it didn't ship.
8. **Cache size budget**: keep each projection's full-population footprint under ~50 MB at 500-user scale. Document the estimate in the projection record's `<remarks>`.

## Reference implementations

When in doubt, mirror these:

- `src/Humans.Application/UserInfo.cs` — projection record shape
- `src/Humans.Infrastructure/Services/Users/CachingUserService.cs` — full decorator
- `src/Humans.Infrastructure/Services/Teams/CachingTeamService.cs` — decorator without interceptor
- `src/Humans.Infrastructure/Services/Shifts/CachingShiftViewService.cs` — dual-projection decorator with explicit invalidator
- `src/Humans.Infrastructure/Data/UserInfoSaveChangesInterceptor.cs` — interceptor template
- `src/Humans.Infrastructure/HostedServices/UserInfoWarmupHostedService.cs` — eager warmup
- `src/Humans.Application/Interfaces/Shifts/IShiftViewInvalidator.cs` — explicit-invalidator template

---

## Tier 1 — Critical (every-request bypass)

### T-01 · Teams.GetTeamDetailAsync onto TeamInfo cache

**What**: `CachingTeamService.GetTeamDetailAsync` (line 102–106) is a pure `WithInner` pass-through. The inner `TeamService.GetTeamDetailAsync` does a full slug lookup + member stitch + role-definition load from DB on every team page render. This is the largest residual bypass on the entire site.

**Definition of done**:
- `TeamInfo` projection extended with `ChildTeamIds` (or lightweight `ChildTeamRef` records) populated at warm time from the existing `ParentTeamId` index.
- `CachingTeamService.GetTeamDetailAsync` projects from cache: resolve team by slug, walk child teams from the cached parent index, stitch role definitions from `TeamInfo.RoleDefinitions`.
- `TeamService.GetBySlugWithRelationsAsync` and `GetRoleDefinitionsAsync` retained only for the inner write-path code; reads route through the decorator.
- Architecture test or analyzer pins that `GetTeamDetailAsync` does not call the repository when reading.

**Files**: `src/Humans.Application/Interfaces/Teams/ITeamService.cs`, `src/Humans.Infrastructure/Services/Teams/CachingTeamService.cs`, `src/Humans.Application/Services/Teams/TeamService.cs`, `tests/Humans.Application.Tests/Architecture/TeamsArchitectureTests.cs`

**Coordination**: Owns Teams section exclusively. No conflict with T-02.

---

### T-02 · GuideRoleResolver direct repo bypass → cache read

**What**: `GuideRoleResolver.IsAnyActiveCoordinatorAsync` injects `ITeamRepository` directly and issues `db.TeamMembers.AnyAsync(...)` on every request that needs guide-role context (every authenticated nav render). Trivially answerable from the existing `TeamInfo` cache: `teamsById.Values.Any(t => t.Members.Any(m => m.UserId == userId && m.Role == Coordinator))`.

**Definition of done**:
- `GuideRoleResolver` injects `ITeamService` instead of `ITeamRepository`.
- `IsAnyActiveCoordinatorAsync` answers from cached `TeamInfo` snapshot.
- Architecture test pins no direct `ITeamRepository` injection outside Teams section.

**Files**: `src/Humans.Infrastructure/Services/GuideRoleResolver.cs`, test files for guide-role resolution.

**Coordination**: No conflict.

---

## Tier 2 — High (new caches in sections about to get busy)

### T-03 · Build cache for Events section (PR #539 follow-up)

**What**: Events was merged 2026-05-15 with explicit "no caching decorator" rationale. Read load is expected to spike with the public CORS API (`/api/events/events`). Build the split cache: per-event `ApprovedEventView`, flat `EventCategoryView` list, flat `EventVenueView` list, `EventGuideSettingsView` singleton.

**Definition of done**:
- Four projection records in `src/Humans.Application/Interfaces/Events/`.
- `CachingEventService` decorator + warmup hosted service.
- `SaveChangesInterceptor` watching `guide_events`, `event_categories`, `guide_shared_venues`, `guide_settings`; or — if write surface grows — an explicit `IEventViewInvalidator` called from `EventService` mutation methods.
- In-memory filter logic for `GetApprovedEventsAsync` (campId, venueId, categoryId, q params).
- `GetAllEventsForDashboardAsync` (moderator-only, must show fresh pending count) stays direct DB.
- Update `docs/sections/Events.md` Architecture block — flip the "No caching decorator" rationale.

**Files**: `src/Humans.Application/Interfaces/Events/IEventService.cs`, `src/Humans.Application/Services/Events/EventService.cs`, new `src/Humans.Infrastructure/Services/Events/CachingEventService.cs`, new interceptor, new warmup hosted service, `src/Humans.Web/Extensions/Sections/EventsSectionExtensions.cs`, `docs/sections/Events.md`.

**Coordination**: Stop-gap `EventSettings` cross-read (issue #719) survives — cache `TimeZoneId` at warm time; document the stale-on-EventSettings-edit window.

---

### T-04 · Build two-layer cache for Legal & Consent

**What**: Every-page consent banner read currently flows through `ConsentService.GetConsentedVersionIdsAsync` + `LegalDocumentSyncService.GetActiveRequiredDocumentsForTeamsAsync`. Cache as two layers:
- **Global**: `LegalDocumentInfo[]` of active+required docs (rare writes — admin publish or sync job).
- **Per-user**: `UserConsentInfo` with `IReadOnlySet<Guid> ConsentedVersionIds` (one invalidation per user acknowledgement).

**Definition of done**:
- Two projection records.
- Caching decorator(s) wrapping `IConsentService` and `ILegalDocumentSyncService` (or merge into one section-level decorator).
- **Synchronous invalidation on `ConsentService.SubmitConsentAsync`** — the cache must be flushed before the response redirects, or the next-page consent-banner check serves stale "still required" data.
- Merge-chain resolution (`IUserService.GetMergedSourceIdsAsync`) moves into warm/invalidate logic, not at read time.
- `ConsentArchitectureTests` updated to permit the decorator while keeping the impl free of `IMemoryCache`.

**Files**: `src/Humans.Application/Interfaces/Consent/IConsentService.cs`, `src/Humans.Application/Interfaces/Legal/ILegalDocumentSyncService.cs`, `src/Humans.Application/Services/Consent/ConsentService.cs`, `src/Humans.Application/Services/Legal/LegalDocumentSyncService.cs`, new decorators, new warmup, `tests/Humans.Application.Tests/Architecture/ConsentArchitectureTests.cs`.

**Coordination**: `ProfileInfo.ConsentCheckStatus` (already on `UserInfo`) is the CC review annotation, not the gate. Boundary is clear; do not relocate.

---

## Tier 3 — Medium (drain existing bypasses + remaining new caches)

### T-05 · Drain `IProfileService.GetByUserIdsAsync` callers onto `UserInfo.Profile`

**What**: Roughly ten callers still hit the profile repo for fields fully projected in `UserInfo.Profile`. Replace with `IUserService.GetUserInfosAsync` / `GetUserInfoAsync`.

Known sites (from User+Profile retrospective):
- `ProfileController.BuildAdminHumansAsync` (the most-redundant — already loads `UserInfo`, then re-fetches `Profile`)
- `FeedbackController`, `FeedbackService`
- `ExpensesController`
- `AuditViewerService`
- `MembershipCalculator`
- `GoogleGroupSyncService`
- `SystemTeamSyncJob` (3 sites)
- `DuplicateAccountService`

**Definition of done**:
- All sites listed switched to `UserInfo` reads.
- `IProfileService.GetByUserIdsAsync` either deleted, or visibility-restricted to ProfileService-internal use (analyzer test pins the restriction).
- Architecture test pins no `IProfileService.GetByUserIdsAsync` injection in non-Profile sections.

**Files**: each listed caller; `src/Humans.Application/Interfaces/Profiles/IProfileService.cs`; tests.

**Coordination**: Lane is safe — each caller is in a different section's service. If two agents claim two callers, no merge conflict beyond the `IProfileService.cs` interface deletion (which only one task closes).

---

### T-06 · Build cache for Camps section

**What**: All five `*Info` records (`CampInfo`, `CampSeasonInfo`, `CampSettingsInfo`, `CampLeadInfo`, `CampRoleDefinitionInfo`, `CampRoleAssignmentInfo`) are already shaped in `ICampService.cs` / `ICampRoleService.cs`. 18+ cross-section callers already consume them. The decorator + singleton + interceptor + warmup is mechanical.

**Definition of done**:
- `CachingCampService` decorator + warmup.
- Resolve the `CampInfo.Leads = null` vs populated semantic — preferred path: always populate leads in the cached projection; deprecate the separate `GetCampsWithLeadsForYearAsync` method.
- **`EeGrantedCount` cross-table invalidation**: `SaveChangesInterceptor` watches both `camp_seasons` AND `camp_members.HasEarlyEntry` changes. Document this in the projection record's `<remarks>`.
- Year-keyed sub-views (`GetCampsForYearAsync`) implemented as filtered snapshot of the canonical per-camp cache, not separate cache entries.
- `IsUserCampLeadAsync` (4 auth-handler callers) answered from cache, not DB.

**Files**: `src/Humans.Application/Services/Camps/CampService.cs`, new `src/Humans.Infrastructure/Services/Camps/CachingCampService.cs`, new interceptor, new warmup, `src/Humans.Web/Extensions/Sections/CampsSectionExtensions.cs`, `docs/sections/Camps.md`.

**Coordination**: Strip the inline 5-min `IMemoryCache` in `CampService` once the decorator lands.

---

### T-07 · Tickets — lift ad-hoc IMemoryCache to decorator, close merge gap

**What**: `TicketQueryService` already has ad-hoc `IMemoryCache` with 5-min TTLs and intent-named invalidators (`InvalidateAfterTransfer`, `InvalidateAfterContactImport`). Lift to the canonical decorator shape; close the `TicketSyncService.ReassignAsync` (account-merge) invalidation gap *before* the decorator goes live.

**Definition of done**:
- `CachingTicketQueryService` decorator with `TicketOrderInfo` + `TicketAttendeeInfo` projections (per-order keyed dict; attendees embedded).
- `ReassignAsync` (account merge fold) invalidates buyer + ticket-holder cache entries.
- Sync-job invalidation hook preserved (currently `_cache.InvalidateTicketCaches()` after `SyncOrdersAndAttendeesAsync`).
- Per-user `UserTicketCount` short-TTL pattern preserved as a separate concern, not absorbed into the main cache.
- `TicketingProjection` (Budget-section forecast) is **not** this cache — leave it alone.

**Files**: `src/Humans.Application/Services/Tickets/TicketQueryService.cs`, `src/Humans.Application/Services/Tickets/TicketSyncService.cs`, `src/Humans.Application/Services/Tickets/TicketTransferService.cs`, new `src/Humans.Infrastructure/Services/Tickets/CachingTicketQueryService.cs`, `src/Humans.Web/Extensions/Sections/TicketsSectionExtensions.cs`.

**Coordination**: None.

**2026-05-24 PR #757 note**: the ticket-order projection cache remains live under
the §15 keyed-inner pattern. `CachingTicketQueryService` warms
`TicketOrderInfo` through the normal `ITicketQueryService.GetTicketOrderInfosAsync`
read method; it does not inject `ITicketRepository` and does not expose a
cache-named `ForCache` method.

---

### T-08 · Build cache for Calendar section

**What**: Two reads (`GetOccurrencesInWindowAsync`, `GetEventByIdAsync`), five writes. Cache the raw `CalendarEvent` rows (with `Exceptions` collection); expansion stays in the service.

**Definition of done**:
- `CalendarEventInfo` projection keyed by event id.
- `CachingCalendarService` decorator with snapshot-scan for window queries (analog of `CachingShiftViewService.InvalidateShift`).
- Single `InvalidateEvent(Guid eventId)` covers all 5 write paths, including `CalendarEventException` upserts (which evict the parent event entry).
- Document at the decorator call site that exception writes evict the *parent* event, not the exception row.

**Files**: `src/Humans.Application/Services/Calendar/CalendarService.cs`, new decorator, `src/Humans.Web/Extensions/Sections/CalendarSectionExtensions.cs`.

**Coordination**: When the iCal feed endpoint lands (no current code path; `User.ICalToken` exists on UserInfo but is unused), this cache will absorb its read load — design with that in mind.

**2026-05-24 PR #757 note**: the Calendar cache remains live under the §15
keyed-inner pattern. `CachingCalendarService` exposes the DTO-only
`ICalendarServiceRead` surface for reads and warms `CalendarEventInfo` through
normal inner service methods (`GetAllEventInfosAsync` / `GetEventInfoAsync`);
it does not inject `ICalendarRepository` and does not expose cache-named
`ForCache` methods.

---

### T-09 · Drain Shifts bypass callers onto IShiftView

**What**: Migrate the hottest residual bypasses identified in the Shifts retrospective. Defers were tracked in issue #720.

Sites to migrate:
- `ShiftVolunteerSearchBuilder` — currently issues N direct DB queries in a voluntell search loop. Switch to bulk `IShiftView.GetUsersAsync`.
- `AgentUserSnapshotProvider`, `AgentToolDispatcher` — switch from `IShiftSignupService.GetByUserAsync` to `IShiftView.GetUserAsync`.
- `ProfileController` + `DashboardService` — switch `IShiftManagementService.GetVolunteerTagPreferencesAsync` to `IShiftView` field.

**Definition of done**:
- Each caller listed switched to `IShiftView`.
- Architecture test pins no `IShiftSignupService` injection in non-Shifts sections (or, narrower: pins no `IShiftSignupService.GetByUserAsync` external calls).

**Files**: `src/Humans.Application/Services/Shifts/ShiftVolunteerSearchBuilder.cs`, `src/Humans.Infrastructure/Agent/AgentUserSnapshotProvider.cs`, `src/Humans.Infrastructure/Agent/AgentToolDispatcher.cs`, `src/Humans.Web/Controllers/ProfileController.cs`, `src/Humans.Web/Services/DashboardService.cs`.

**Coordination**: T-10 is the other Shifts-bypass task; split between the two so they don't conflict.

---

### T-10 · Shifts — migrate ShiftsController + ViewComponent bypasses [CLAIMED: refactor/cache-t10-shifts-legacy]

**What**: Remaining legacy bypasses noted in the retrospective:
- `ShiftsController` (×2) and `ShiftSignupsViewComponent` — `IShiftSignupService.GetByUserAsync`.
- `ShiftsController` — `IGeneralAvailabilityService.GetByUserAsync`.
- `ShiftBrowsePageBuilder` — `IShiftManagementService.GetVolunteerTagPreferencesAsync`.

**Definition of done**:
- All listed sites switched to `IShiftView`.
- Issue #720 closed or its scope narrowed.

**Files**: `src/Humans.Web/Controllers/ShiftsController.cs`, `src/Humans.Web/ViewComponents/ShiftSignupsViewComponent.cs`, `src/Humans.Web/Services/ShiftBrowsePageBuilder.cs`.

**Coordination**: Pairs with T-09. Pick disjoint file sets.

---

## Tier 4 — Polish

### T-11 · Drain `ProfileController.GetUserAsync` / `FindByIdAsync` to claim extraction

**What**: ~10 sites in `ProfileController` call `_userManager.GetUserAsync(User)` or `FindByIdAsync(id)` purely to extract `user.Id`, then discard the entity. Each fires an Identity UserStore DB hit. Replace with `User.GetUserId()` claim extraction.

**Definition of done**: every such site uses the claim helper. `User` entity is loaded only when subsequent code reads non-id fields (rare in `ProfileController`).

**Files**: `src/Humans.Web/Controllers/ProfileController.cs`.

---

### T-12 · Remove `Profile.ProfilePictureData` zombie column

**What**: PR #576 stopped writes to the obsolete `ProfilePictureData` byte column. The column itself still exists on `Profile.cs`, `ProfileSaveRequest`, `ProfileService.SaveProfileAsync` (×2), `DevPersonaSeeder`, `OnboardingWidgetController`, and the migration tooling around it. A future untracked `Profile` load could pull megabytes into memory.

**Definition of done**: column dropped from the domain entity. EF migration to drop the database column. All migration tooling (`ProfilePictureMigrationAdminController`) removed if migration is complete in QA + prod.

**Files**: `src/Humans.Domain/Entities/Profile.cs`, `src/Humans.Application/Services/Profiles/ProfileService.cs`, related controllers, new EF migration.

**Coordination**: Requires confirmation that QA + prod databases have already been migrated (verify with Peter before committing the column drop).

---

### T-13 · Teams polish — SearchAsync + GetMyTeamMembershipsAsync

**What**:
- `TeamService.SearchAsync` delegates to `_repo.SearchAsync` (DB). The cache holds `Name`, `Slug`, `IsHidden`, `IsActive` — can answer fully in-memory.
- `GetMyTeamMembershipsAsync` does three DB round trips (`GetUserTeamsAsync` + `GetActiveChildIdsByParentsAsync` + `GetPendingCountsByTeamIdsAsync`). Project all three from `TeamInfo` if possible; otherwise reduce to one query.

**Definition of done**: both methods served fully from cache (or, for membership pending counts, document why the count requires a live query).

**Files**: `src/Humans.Application/Services/Teams/TeamService.cs`, `src/Humans.Infrastructure/Services/Teams/CachingTeamService.cs`.

**Coordination**: Doesn't conflict with T-01 (different methods).

---

### T-14 · Add null-guard to Shifts `InvalidateRota` cascade

**What**: `CachingShiftViewService.InvalidateRota` walks the user cache snapshot and evicts users whose `Signups[].Shift?.RotaId == rotaId`. If `ShiftSignup.Shift` or `Shift.RotaId` is null on a cached row, that user entry silently stays stale. Add a guard or an assert; surface the case in logs if it ever fires.

**Definition of done**: explicit null-handling + log line; or a unit test pinning the invariant that `Shift.Rota` is always populated on cached signups.

**Files**: `src/Humans.Infrastructure/Services/Shifts/CachingShiftViewService.cs`, related test.

---

## Skip — explicit no-cache decisions

### Notifications
The badge-count cache (`CacheKeys.NotificationBadgeCounts(userId)`) is already implemented via `IMemoryCache` with write-through eviction across 16 emitter call sites and a 2-min TTL backstop. A `ConcurrentDictionary` singleton is the wrong shape for high-churn per-user data. **No work warranted.**

### Governance / Applications
Term-expiration semantics are time-based (`TermExpiresAt`), not event-based — a §15 event-driven cache would serve "still active" stale forever until the next write triggers eviction. Combined with the low write volume (~tens per year) and the existing nav-badge `IMemoryCache` covering the only ambient hot reads, the cache is not worth the complexity. **No work warranted unless and until the term-expiry invalidation problem has a clean solution.**

---

## Suggested parallelization

8 agents at peak, claim by tier:

| Wave | Tasks | Notes |
|---|---|---|
| Wave 1 | T-01, T-02, T-03, T-04 | Highest-value items; all in disjoint sections |
| Wave 2 | T-05, T-06, T-07, T-08 | After Wave 1 ships; T-05 can split across 2–3 agents along caller boundaries |
| Wave 3 | T-09, T-10, T-11, T-12, T-13, T-14 | Polish; cheap; pair up however |

Cross-wave coordination: only the `HumansDbContext` interceptor registration list is a shared file. Each new interceptor adds one line; resolve any conflict by rebasing the loser.
