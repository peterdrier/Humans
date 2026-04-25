# Design Rules

Architectural rules governing how Web, Application, Infrastructure, and Domain interact. **These are target-state rules.** New code must follow them; existing code is migrated incrementally per [Migration Strategy](#15-migration-strategy).

## 1. Layer Responsibilities

Clean Architecture with strict dependency direction. Application depends only on Domain. Infrastructure and Web both depend inward toward Application and Domain. Nothing depends on Web or Infrastructure.

```
Domain  ←  Application  ←  Infrastructure
                       ←  Web
```

| Layer | Contains | Forbidden |
|---|---|---|
| **Domain** | Entities, enums, value objects. No external dependencies. | Services, interfaces, framework references, EF types, DTOs |
| **Application** | Service **interfaces** and **implementations** (business logic), repository and store **interfaces**, DTOs, use cases, authorization handlers | `DbContext`, `Microsoft.EntityFrameworkCore.*`, HTTP types, external SDKs, direct I/O |
| **Infrastructure** | Repository implementations, store implementations, caching decorators, `HumansDbContext`, migrations, external API clients (Google, Stripe, SMTP), background jobs | Controller logic, Razor, HTTP request/response, business rules |
| **Web** | Controllers, views, view models, API endpoints, DI wiring | `DbContext`, direct EF queries, direct cache access for domain data, raw SQL |

The project reference graph (`Humans.Application.csproj` references only `Humans.Domain.csproj`) **structurally enforces** that services in Application cannot import `Microsoft.EntityFrameworkCore`. EF pollution in business logic is a compile error, not a code-review finding.

**Key change from prior rules:** Services now live in `Humans.Application`, not `Humans.Infrastructure`. The old rule ("services own their data access") meant "services inject `DbContext` directly," which conflated business logic with persistence and made "no cross-domain joins" impossible to enforce structurally. The new rule is "services go through their owning repository."

## 2. Service Ownership — The Core Rule

Each service is the exclusive gateway to its data. No component — controller, other service, job, or view component — may bypass the owning service to reach its tables, its cache, or its store.

### 2a. Controllers Cannot Talk to the Database

Controllers call services. Controllers never inject `DbContext`, never write EF queries, never instantiate repositories or stores directly, never access `IMemoryCache` for domain data. Their job is: receive HTTP request → authorize → call service(s) → return response.

**Exception:** `UserManager<User>` / `SignInManager<User>` for ASP.NET Identity operations (login, password, claims) are allowed in controllers since Identity is a framework concern, not a domain service.

### 2b. Services Live in Application, Not Infrastructure

Business services (`ProfileService`, `TeamService`, `BudgetService`, etc.) live in `Humans.Application`. They contain business rules, workflow logic, validation, and orchestration. They **never** import EF types. When they need to load or persist entities, they call their owning repository interface; when they need cached data, they go through their owning store.

Repository **implementations** (the classes that talk to `DbContext`) live in `Humans.Infrastructure`. That is the only project that may touch EF Core.

### 2c. Table Ownership Is Strict and Sectional

Each domain's tables are owned by exactly one service (and that service's repository). **No other service may query, insert, update, or delete rows in tables it does not own.** If `CampService` needs a profile, it calls `IProfileService` — it does not query the `profiles` table, does not instantiate `IProfileRepository`, does not access the Profile section's in-memory cache directly.

### 2d. Cache Ownership Follows Data Ownership

Caching is an internal concern of the owning service. Callers don't know whether data came from memory, the store, or the database — they call the service method and get the result. The mechanism for caching is the **store pattern** (§4) and the **caching decorator** (§5), not raw `IMemoryCache` calls inlined in service methods.

## 3. Repository Layer

Every domain has a narrow, entity-shaped **repository interface** in `Humans.Application/Interfaces/Repositories/` and an EF-backed **implementation** in `Humans.Infrastructure/Repositories/`. The repository is the single point of EF access for its tables.

### 3a. Repository Rules

1. **Entities in, entities out.** Return types are `Profile`, `IReadOnlyList<Profile>`, `IReadOnlyDictionary<Guid, Profile>`, or scalar / id values. Never `IQueryable<T>`, never EF types, never DTOs.
2. **No cross-domain method signatures.** A repository for the Profile domain never takes a `Team`, returns a `User`, or accepts a filter that requires joining another domain's table. If a caller needs a compound shape, a composer at the service layer stitches it from multiple repositories.
3. **Bulk-by-ids is first class.** Every repository exposes a `GetByIdsAsync(IReadOnlyCollection<Guid>)` returning a dictionary. This is what makes in-memory joins (§6) cheap.
4. **`GetAllAsync` exists for store warmup.** At ~500 users it is trivial. Larger datasets would replace it with a streaming shape; at our scale it is strictly cheaper than lazy loading.
5. **No cross-domain navigation properties in return shapes.** `Profile.User` is a cross-domain nav — callers get the FK (`Profile.UserId`) and resolve via `IUserRepository` if they need the User. Aggregate-local navs (`Profile.Languages`) are fine.
6. **No logging of domain events, no audit, no `IClock`, no caching.** Just persistence. Side effects belong to the service.

### 3b. Canonical Repository Shape

```csharp
// Humans.Application/Interfaces/Repositories/IProfileRepository.cs
public interface IProfileRepository
{
    Task<Profile?> GetByIdAsync(Guid profileId, CancellationToken ct = default);
    Task<Profile?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, Profile>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken ct = default);

    Task<int> CountByTierAsync(MembershipTier tier, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetUserIdsByBirthdayMonthAsync(int month, CancellationToken ct = default);

    Task AddAsync(Profile profile, CancellationToken ct = default);
    Task UpdateAsync(Profile profile, CancellationToken ct = default);
    Task DeleteAsync(Guid profileId, CancellationToken ct = default);
}
```

## 4. Store Pattern (In-Memory Entity Cache)

> **Note:** §4–§5 describe the original store + warmup + decorator pattern. **No section uses this pattern any more.** **Profile** migrated to §15 in PR #235 (decorator owns the `ConcurrentDictionary` directly). **Governance** dropped the caching layer entirely in PR for issue #533 — at its traffic level a caching decorator wasn't worth the complexity, so the service talks directly to `IApplicationRepository` and invalidates cross-cutting caches inline. §4–§5 are retained only for historical context. New sections: if caching is warranted, follow §15; if not, use a plain repository + Scoped service.

Every cached domain has a **store** — a dedicated class that owns an in-memory canonical copy of its entities. The store is the *data shape* of the cache; it is separate from the decorator that makes reads transparent.

### 4a. Store Rules

1. **One store per domain.** `IApplicationStore` holds the Governance world. `ITeamStore` holds the Team world. Stores do not share state.
2. **Canonical storage is a dictionary keyed by primary id** (`Dictionary<Guid, Application>`). Secondary indexes are allowed when a specific lookup pattern justifies them; the store keeps them consistent because only the store writes.
3. **Single writer.** Only the owning service writes to the store, and only as part of a successful DB write. The store interface exposes `Upsert(entity)` and `Remove(id)`; the owning service calls these immediately after its repository write returns successfully.
4. **Startup warmup.** Each store loads its full domain on startup via `GetAllAsync()`. At ~500 users this is trivial memory and query cost; it eliminates cache-miss reasoning entirely.
5. **Stores are Infrastructure.** The interface lives in `Humans.Application/Interfaces/Stores/`, the implementation lives in `Humans.Infrastructure/Stores/`.

### 4b. Why a Store, Not Inline `IMemoryCache.GetOrCreateAsync`

The old pattern (`_cache.GetOrCreateAsync($"entity:{id}", ...)` inside a service method) caches *query results*, not entities. `GetById`, `GetByEmail`, and `GetByIds` become three independent cache entries for overlapping data, with three independent invalidation paths and three opportunities for staleness. Under the store pattern, all three are dict lookups over the same canonical entity object, and invalidation is a single `Upsert` call in one place: the owning service's write method.

## 5. Decorator Caching

Services are cached by **wrapping them in a decorator**, not by inlining `IMemoryCache` calls. The decorator is registered via `services.Decorate<IProfileService, CachingProfileService>()` (Scrutor). Callers inject `IProfileService` and get the cached version transparently.

### 5a. Decorator Rules

1. **One decorator per service.** `CachingProfileService : IProfileService` wraps the real `ProfileService`.
2. **Reads go through the store.** The decorator asks the store first. With startup warmup, every read is a hit at our scale.
3. **Writes pass through to the inner service.** The inner service writes to the repository and then updates the store. The decorator does not update the store itself — the service does, because only the service knows what the final entity state is after business rules run.
4. **Decorators contain zero business logic.** If the decorator needs to decide anything beyond "is it in the store?", that decision belongs in the service, not the wrapper.

### 5b. The Full Stack

```
Controllers / other services
          ↓ IApplicationDecisionService
CachingApplicationDecisionService (decorator)   [Infrastructure]
          ↓ IApplicationDecisionService
ApplicationDecisionService (business logic)     [Application]
          ↓ IApplicationRepository, IApplicationStore
ApplicationRepository, ApplicationStore         [Infrastructure]
          ↓ DbContext
HumansDbContext                                 [Infrastructure]
```

Three roles, cleanly separated:
- **Repository** talks to EF, nothing else
- **Service** runs business rules and coordinates repository + store writes
- **Decorator** makes caching invisible to callers

## 6. Cross-Domain Joins Are Forbidden

**No EF query may `.Include()` or `.Join()` across a domain boundary.** A Profile query cannot navigate into User, Team, or Campaign. A Team query cannot navigate into Profile or User. A Campaign query cannot navigate into Team members. And so on.

### 6a. Why

Cross-domain joins couple caching and invalidation to the database because no single service owns the joined shape. Nothing upstream can safely cache a Team+Profile join; nothing upstream can safely invalidate it when either side changes. These joins are the single biggest structural barrier to the caching model in §4–§5, and they silently break the table-ownership rule in §2c because the joining service ends up reading columns it does not own.

### 6b. In-Memory Joins Are the Replacement

When a caller needs Team + Profile + User together, the caller (controller, page service, or composer service) asks each owning service for its slice and stitches in memory:

```csharp
// In a controller or composer
var team = await _teamService.GetByIdAsync(teamId, ct);
var userIds = team.Members.Select(m => m.UserId).ToList();
var profiles = await _profileService.GetByUserIdsAsync(userIds, ct);
var users = await _userService.GetByIdsAsync(userIds, ct);

var rows = team.Members.Select(m => new TeamMemberRow(
    UserId:      m.UserId,
    DisplayName: users[m.UserId].DisplayName,
    BurnerName:  profiles[m.UserId].BurnerName,
    Role:        m.Role));
```

Three store reads, no SQL joins, cache ownership intact, each service cachable independently.

### 6c. Cross-Domain Nav Properties

Strip cross-domain navigation properties at the repository and entity boundary:

- ❌ `Profile.User` (nav to User entity in another domain)
- ✅ `Profile.UserId` (FK only)
- ❌ `TeamMember.User` (nav to User)
- ✅ `TeamMember.UserId` (FK only)
- ❌ `CampLead.User`, `ApplicationVote.BoardMember`, etc.
- ✅ The corresponding FKs
- ✅ `Profile.Languages` (aggregate-local collection, fine — same domain)

### 6d. What You Give Up

- **Server-side filter or sort on joined columns** (e.g., "teams ordered by coordinator's city"). At 500 users you filter and sort in memory — cheap.
- **Some EF LINQ elegance.** You write more `Dictionary<Guid, T>` lookups and fewer `Include / ThenInclude` chains.

### 6e. What You Gain

- Cache ownership becomes tractable. Every domain owns its own store and its own invalidation.
- Every table has exactly one writer (its repository) and one cache (its store).
- Missing-`Include` bugs (lazy-load exceptions, over-fetching graphs) stop happening because there are no cross-domain navs to forget.
- The table-ownership rule finally has teeth at query time, not just at write time.

## 7. Decorators vs In-Service Crosscuts

Not every crosscut belongs in a decorator. The decorator pattern works only for concerns that are **mechanical and context-free** — where the wrapper does not need to know *who* is calling or *why*.

| Concern | Pattern | Why |
|---|---|---|
| Caching | Decorator ✅ | Mechanical, context-free |
| Metrics / timing | Decorator ✅ | Mechanical, context-free |
| Retry / circuit breaker (external calls) | Decorator ✅ | Mechanical, context-free |
| Access logging (GDPR "who viewed what") | Decorator ✅ | Mechanical, context-free |
| **Domain audit** (suspended, approved, tier changed) | **In-service**, self-persisting | Needs actor, before/after state, semantic intent |
| **Authorization** | **In-controller** (resource-based handlers, §11) | Needs HTTP identity + resource context |
| **Transactions / unit of work** | **In-repository method** | One repository method = one `SaveChangesAsync`. Compound writes belong in a single repo method, not a service orchestrating multiple repo calls. |

### 7a. Audit Is In-Service and Self-Persisting

Domain audit events — "user X suspended user Y for reason Z" — need the actor, the before/after state, and the semantic intent. A decorator wrapping `SuspendAsync(userId)` has none of that context: it does not know the actor (unless plumbed in), it does not know the old state (unless it re-reads, which is wasteful), and it cannot distinguish a name edit from a suspension from a tier change. So audit stays in-service — the service calls `IAuditLogService.LogAsync(...)` explicitly.

**`IAuditLogService` persists its own entries.** Each `LogAsync` call writes through `IAuditLogRepository.AddAsync`, which opens a fresh `DbContext` via `IDbContextFactory`, adds the entry, and calls `SaveChangesAsync`. Audit is **best-effort**: save failures are logged at error level and swallowed by the service so an audit hiccup never fails the business operation that called it. The audit log table is **append-only per §12** — the repository exposes no update or delete surface.

Consequences:

1. **Call audit *after* the business save**, not before. A business rollback never leaves a ghost audit row because audit hasn't written yet. If the audit save fails after a successful business save, the business change is preserved and the log line explains the missing row.
2. **Audit commits separately from the business change.** The rare failure mode is "business saved, audit did not" — logged loudly, detectable by reconciling row counts, and strictly better than the prior "audit silently vanishes" mode that happened when services moved to repository+factory writes.
3. **Callers do not need to call `SaveChangesAsync`** to flush audit. They also must not expect audit to roll back if a later business step fails.

```csharp
public async Task SuspendAsync(Guid userId, Guid actorId, string reason, CancellationToken ct)
{
    var profile = await _repo.GetByUserIdAsync(userId, ct);
    if (profile is null) return;

    var wasAlreadySuspended = profile.IsSuspended;
    profile.IsSuspended = true;
    await _repo.UpdateAsync(profile, ct);   // business save first
    _store.Upsert(profile);

    await _auditLog.LogAsync(               // then audit (self-persisting)
        AuditAction.ProfileSuspended, nameof(User), userId,
        $"Suspended (was={wasAlreadySuspended}): {reason}",
        actorId);
}
```

**Compound writes that must be atomic** (e.g., season rename + historical-name insert) belong in a single repository method that performs both mutations and one `SaveChangesAsync`. Do not orchestrate multiple repo calls in the service and hope partial failure doesn't strand rows.

If audit calls become noisy across many methods inside one service, the next evolution is **domain events** raised from the entity and handled in Infrastructure — not a decorator.

## 8. Table Ownership Map

Each section's service owns these tables. Cross-service access goes through the service interface, never through direct DB queries, never through another domain's repository or store.

| Section | Service(s) | Owned Tables |
|---------|-----------|--------------|
| **Profiles** | `ProfileService`, `ContactFieldService`, `ContactService`, `UserEmailService`, `CommunicationPreferenceService`, `AccountMergeService`, `DuplicateAccountService` | `profiles`, `contact_fields`, `user_emails`, `communication_preferences`, `volunteer_history_entries`, `account_merge_requests` |
| **Users/Identity** | `UserService`, `AccountProvisioningService`, `UnsubscribeService` | `AspNetUsers`, `AspNetUserClaims`, `AspNetUserLogins`, `AspNetUserTokens`, `AspNetRoles` (legacy), `AspNetUserRoles` (legacy), `event_participations` |
| **Teams** | `TeamService`, `TeamPageService`, `TeamResourceService` | `teams`, `team_members`, `team_join_requests`, `team_join_request_state_histories`, `team_role_definitions`, `team_role_assignments`, `team_pages`, `google_resources` |
| **Auth** | `RoleAssignmentService`, `MagicLinkService` | `role_assignments` |
| **Governance** | `ApplicationDecisionService` | `applications`, `application_state_histories`, `board_votes` |
| **Legal & Consent** | `LegalDocumentService`, `AdminLegalDocumentService`, `LegalDocumentSyncService`, `ConsentService` | `legal_documents`, `document_versions`, `consent_records` |
| **Onboarding** | `OnboardingService` | *(no owned tables — orchestrates Profiles, Legal, Teams, Governance)* |
| **Camps** | `CampService`, `CampContactService` | `camps`, `camp_seasons`, `camp_leads`, `camp_images`, `camp_historical_names`, `camp_settings` |
| **City Planning** | `CityPlanningService` | `city_planning_settings`, `camp_polygons`, `camp_polygon_histories` |
| **Calendar** | `CalendarService` | `calendar_events`, `calendar_event_exceptions` |
| **Shifts** | `ShiftManagementService`, `ShiftSignupService`, `GeneralAvailabilityService` | `rotas`, `shifts`, `shift_signups`, `event_settings`, `general_availabilities`, `volunteer_event_profiles`, `shift_tags`, `volunteer_tag_preferences` |
| **Budget** | `BudgetService` | `budget_years`, `budget_groups`, `budget_categories`, `budget_line_items`, `budget_audit_logs`, `ticketing_projections` |
| **Tickets** | `TicketQueryService`, `TicketSyncService`, `TicketingBudgetService` | `ticket_orders`, `ticket_attendees`, `ticket_sync_states` |
| **Campaigns** | `CampaignService` | `campaigns`, `campaign_codes`, `campaign_grants` |
| **Google Integration** | `GoogleSyncService`, `GoogleAdminService`, `GoogleWorkspaceSyncService`, `GoogleWorkspaceUserService`, `DriveActivityMonitorService`, `SyncSettingsService`, `EmailProvisioningService` | `sync_service_settings`, `google_sync_outbox_events` |
| **Email** | `EmailOutboxService`, `OutboxEmailService`, `EmailService` | `email_outbox_messages`; owns `system_settings` key `email_outbox_paused` |
| **Feedback** | `FeedbackService` | `feedback_reports`, `feedback_messages` |
| **Notifications** | `NotificationService`, `NotificationInboxService`, `NotificationMeterProvider` | `notifications`, `notification_recipients` |
| **Audit Log** | `AuditLogService` | `audit_log_entries` |

**`system_settings` is per-key ownership.** Each key belongs to its consuming section's repository — there is no single cross-cutting owner. Currently-tracked keys: `email_outbox_paused` (Email), `DriveActivityMonitor:LastRunAt` (Google Integration).

**Admin is not a section.** The `/Admin/*` controllers are a nav holder for admin-only actions that live in other sections (outbox pause in Email, suspend/merge/purge in Profiles, sync settings in Google Integration, role assignments in Auth, legal-doc management in Legal & Consent). Services referenced from `AdminController` belong to their owning section, not to Admin.

See [`docs/architecture/dependency-graph.md`](dependency-graph.md) for the full directed dependency graph with current vs target edges and circular dependency analysis.

### 8a. User-Scoped Sections Must Contribute to the GDPR Export

Every section whose owned tables hold per-user rows MUST implement `IUserDataContributor` (`Humans.Application.Interfaces.Gdpr`) so the GDPR Article 15 data export (`IGdprExportService`) can assemble a complete document without any cross-section database reads. The orchestrator injects `IEnumerable<IUserDataContributor>`, fans out one call per contributor, and merges the returned slices into the JSON document the user downloads from `/Profile/Me/DownloadData`.

Adding a new user-scoped section to §8 above requires four coupled steps — all four, in any order, before the PR can land:

1. Add the new section-name constants to `GdprExportSections` (`Humans.Application.Interfaces.Gdpr`).
2. Make the owning service implement `IUserDataContributor` and return its own slice. A contributor reads only its own section's tables — cross-section data flows through other contributors, not through `Include` chains. Collection slices must always return the shaped list (empty when the user has no records); `null` data is reserved for single-object sections whose entity doesn't exist for this user.
3. Register the service in `InfrastructureServiceCollectionExtensions` using the forwarding pattern so the same scoped instance serves both the primary interface and `IUserDataContributor`:

   ```csharp
   services.AddScoped<MyNewService>();
   services.AddScoped<IMyNewService>(sp => sp.GetRequiredService<MyNewService>());
   services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<MyNewService>());
   ```

4. Add the concrete service type to `GdprExportDependencyInjectionTests.ExpectedContributorTypes` — the enforced view of the §8 rows that hold user-scoped data.

The architecture test suite in `GdprExportDependencyInjectionTests.cs` enforces every step automatically:

- `EverySectionServiceMustImplementIUserDataContributor` — each listed type really implements the interface.
- `EveryIUserDataContributorInInfrastructureIsExpected` — every `IUserDataContributor` found via reflection in `Humans.Infrastructure` is in the expected list (catches new contributors that forget the list).
- `EveryExpectedContributorIsRegisteredInInfrastructure` — every listed type has a DI registration.
- `EveryIUserDataContributorFactoryForwardsToAnExpectedConcreteType` — each forwarding factory resolves to a distinct expected concrete type, so a duplicated or mis-wired factory can't silently drop a section.
- `GdprExportServiceIsRegistered` — the orchestrator itself is registered.

**Uncaught case (convention, not test):** if a new user-scoped section is added to §8 but its owning service never implements `IUserDataContributor` at all, reflection finds nothing to enumerate and the suite passes vacuously. The four-step list above is the prose-level guardrail — reviewers should reject any §8 edit that adds a user-scoped row without touching `ExpectedContributorTypes` in the same PR.

See [`docs/features/gdpr-export.md`](../features/gdpr-export.md) for the JSON output shape, the contributor table, and a worked example of adding a new section.

## 9. Cross-Service Communication

When a service needs data from another section, it calls that section's public service interface via constructor injection. Repositories and stores are never crossed — only the public `I{Section}Service` interface.

```csharp
// CORRECT — CampService needs profiles, asks ProfileService
public class CampService(
    ICampRepository campRepository,
    ICampStore campStore,
    IProfileService profileService) : ICampService
{
    public async Task<CampDetailDto> GetCampDetailAsync(Guid campId, CancellationToken ct)
    {
        var camp = await campRepository.GetByIdAsync(campId, ct);
        if (camp is null) return null;

        var leadProfiles = await profileService.GetByUserIdsAsync(camp.LeadUserIds, ct);
        return BuildDto(camp, leadProfiles);
    }
}
```

Wrong patterns — each violates an invariant somewhere:

```csharp
// WRONG — reaches into another domain's repository
public class CampService(ICampRepository repo, IProfileRepository profileRepo) : ICampService { ... }

// WRONG — uses IDbContextFactory to query another domain's tables directly
public class CampService(ICampRepository repo, IDbContextFactory<HumansDbContext> factory) : ICampService
{
    public async Task<CampDetailDto> GetCampDetailAsync(Guid campId, CancellationToken ct)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var leadProfiles = await ctx.Profiles.Where(...).ToListAsync(ct); // ← Profile section's table
        ...
    }
}

// WRONG — direct DbContext access (impossible by project graph once migrated)
public class CampService(HumansDbContext db) : ICampService { ... }

// WRONG — cross-domain .Include
var camp = await db.Camps.Include(c => c.Leads).ThenInclude(l => l.Profile).FirstAsync(...);
```

### Rules

- Cross-service calls are **by id or small parameter set** — `GetByUserIdAsync(Guid)`, `GetByIdsAsync(IReadOnlyCollection<Guid>)`. Never a raw predicate that pushes another domain's schema knowledge into the caller.
- Services return **DTOs or domain entities** — never `IQueryable`, never cross-domain entity graphs.
- Circular dependencies are resolved by extracting a shared interface or using an orchestrating service (e.g., `OnboardingService` orchestrates Profiles + Legal + Teams).

## 10. Cross-Cutting Services

Some services are used across all sections. They own their own tables but are injected everywhere.

| Service | Purpose | Owned Tables |
|---------|---------|--------------|
| `RoleAssignmentService` | Temporal role memberships (Auth section) — the gateway for all role queries | `role_assignments` |
| `AuditLogService` | Append-only audit trail for user actions and sync operations | `audit_log_entries` |
| `EmailOutboxService` | Queue and track transactional emails | `email_outbox_messages` |
| `NotificationService` | In-app notifications | *(transient)* |

These are standalone services, not embedded in section services. Any service or controller can call `IAuditLogService.LogAsync(...)` to record an action, or `IRoleAssignmentService.HasActiveRoleAsync(...)` to check a role. They follow the same repository + store + decorator pattern as any other service.

## 11. Authorization Pattern

Authorization uses **ASP.NET Core resource-based authorization** — one pattern, everywhere.

### How it works

Controllers call `IAuthorizationService.AuthorizeAsync(User, resource, requirement)`. Authorization handlers contain the logic. Services are auth-free — they trust the caller.

```csharp
// Controller — authorize, then call service
var authResult = await _authorizationService.AuthorizeAsync(User, category, BudgetOperationRequirement.Edit);
if (!authResult.Succeeded) return Forbid();
await _budgetService.DeleteLineItemAsync(id);
```

### Existing handlers

| Handler | Requirement | Resource | Purpose |
|---------|-------------|----------|---------|
| `TeamAuthorizationHandler` | `TeamOperationRequirement` | `Team` | Coordinator/manager/admin checks |
| `BudgetAuthorizationHandler` | `BudgetOperationRequirement` | `BudgetCategory` | Finance role + coordinator checks |
| `CampAuthorizationHandler` | `CampOperationRequirement` | `Camp` | Lead/CampAdmin checks |
| `RoleAssignmentAuthorizationHandler` | `RoleAssignmentOperationRequirement` | `string` (role name) | Who can assign which roles |
| `IsActiveMemberHandler` | `IsActiveMemberRequirement` | — | Membership gate |
| `HumanAdminOnlyHandler` | `HumanAdminOnlyRequirement` | — | Admin profile operations |

### Rules

- **No `isPrivileged` booleans.** Don't pass auth decisions as parameters to services. If the controller maps it wrong, the service silently does the wrong thing.
- **No inline `IsInRole` chains in controllers** for resource-scoped checks. Use the handler. `[Authorize(Roles = ...)]` is still fine for simple route-level role gates.
- **Services are auth-free.** They don't check roles, don't inject `IHttpContextAccessor`, don't receive boolean privilege flags. Authorization happens before the service is called.
- **New sections need a handler.** When adding a new section with resource-scoped auth, add a `*OperationRequirement` + `*AuthorizationHandler` pair. Don't invent a new pattern.

## 12. Immutable Entity Rules

Some entities are append-only. They have database triggers or application-level enforcement preventing UPDATE and DELETE.

| Entity | Table | Constraint |
|--------|-------|------------|
| `ConsentRecord` | `consent_records` | DB triggers block UPDATE and DELETE |
| `AuditLogEntry` | `audit_log_entries` | Append-only by convention |
| `BudgetAuditLog` | `budget_audit_logs` | Append-only by convention |
| `CampPolygonHistory` | `camp_polygon_histories` | Append-only by convention |
| `ApplicationStateHistory` | `application_state_histories` | Append-only by convention |
| `TeamJoinRequestStateHistory` | `team_join_request_state_histories` | Append-only by convention |

**Rule:** Never add UPDATE or DELETE logic for append-only entities. New state = new row. Repository interfaces for these domains expose `AddAsync` and `GetX` methods but no `UpdateAsync` or `DeleteAsync`.

## 13. Google Resource Ownership

All Google Drive resources are on **Shared Drives** (never My Drive). Google integration is managed by dedicated services:

- `GoogleSyncService` — syncs team membership to Drive/Groups
- `GoogleAdminService` — admin operations on Google Workspace
- `GoogleWorkspaceUserService` — user provisioning
- `SyncSettingsService` — per-service sync mode (None/AddOnly/AddAndRemove)

**No other service queries Google resources directly.** If a section needs to know about a team's Google resources, it asks `ITeamResourceService`. The guardrail script `scripts/check-google-resource-ownership.sh` enforces this at CI time.

## 14. DTO and ViewModel Boundary

- **Domain entities** live in `Humans.Domain`. They are mutable, have identity, and carry invariants. Entities never reference EF types.
- **DTOs** live in `Humans.Application`. They are read-optimized shapes for specific use cases (admin tables, API responses, view data). Services return DTOs when the shape is call-specific and the entity does not match; they return entities when the caller needs the full aggregate.
- **ViewModels** live in `Humans.Web` (or are inlined in controllers). Controllers map DTOs or entities to view models for Razor.
- **Domain entities should not leak into Razor views** when a DTO would provide better separation. Simple 1:1 cases are acceptable; anything that would have required `.Include` for navigation in the old model is not.
- **View components** are part of Web. They call services, not repositories or stores.

## 15. Profile Section Pattern — Canonical Cache-Collapse Architecture

The Profile section is the **reference implementation** for the target caching architecture (completed in PR #235, 2026-04-20). All future section migrations that warrant a caching layer follow this pattern. The original §4/§5 store-and-decorator spec was superseded during Profile migration; §15 documents the final, code-verified shape.

> **Governance** previously used the §4/§5 pattern but dropped its caching layer entirely (issue #533): the section is low-traffic enough that DB reads per request are fine, so `ApplicationDecisionService` talks directly to `IApplicationRepository` and invalidates cross-cutting caches (`INavBadgeCacheInvalidator`, `INotificationMeterCacheInvalidator`, `IVotingBadgeCacheInvalidator`) inline after successful writes. Not every section needs §15 — reach for it only when traffic or bulk-read patterns justify an in-memory projection.

### 15a. Four-Layer Stack

```
Controller / View Component
  ↓ I<Section>Service                               [Application interface]
Caching<Section>Service   (optional decorator)      [Infrastructure — Singleton]
  ↓ keyed resolve via IServiceScopeFactory
<Section>Service          (inner, keyed)            [Application — Scoped]
  ↓ repositories + cross-section service interfaces
<Section>Repository                                 [Infrastructure — Singleton via IDbContextFactory]
  ↓ IDbContextFactory<HumansDbContext>              [Singleton — creates short-lived contexts per method]
```

The decorator is "optional" in the sense that removing it leaves the system fully functional — the inner service implements every method against the DB. The decorator is a pure performance optimization layered on top.

### 15b. Repository Rules

Repositories are registered as **Singleton** because they inject `IDbContextFactory<HumansDbContext>` rather than `HumansDbContext` directly. Every method creates and disposes its own short-lived context:

```csharp
public async Task<Profile?> GetByUserIdReadOnlyAsync(Guid userId, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    return await ctx.Profiles
        .AsNoTracking()
        .Include(p => p.VolunteerHistory)
        // ...
        .FirstOrDefaultAsync(p => p.UserId == userId, ct);
}
```

This is the Microsoft-endorsed pattern for Singleton services that need DB access: `DbContext` is not thread-safe, holds a live connection, and accumulates tracked entities — it must be short-lived. `IDbContextFactory` creates lightweight, isolated contexts on demand without the overhead of scope-factory indirection.

**Read method naming convention:**
- `*ReadOnlyAsync` — `AsNoTracking()`, returns detached entities; used for reads that don't need to mutate.
- `*ForMutationAsync` — tracking enabled, returns attached entities; used when the caller will mutate the entity and call `UpdateAsync` on the same repository in the same method.

### 15c. Application Service (Inner) Rules

The application service (`ProfileService`, `ContactFieldService`, etc.) lives in `Humans.Application.Services.Profile`. It:

- Injects repository interfaces, never `DbContext`.
- Never imports `IMemoryCache` or any caching abstraction — it is completely cache-unaware.
- Is registered as **Scoped** and **keyed** under `CachingProfileService.InnerServiceKey` (`"profile-inner"`) so the Singleton decorator can resolve fresh instances per-call without self-resolution.
- Implements **every** read method against the DB, including snapshot-based search/filter methods that the decorator may accelerate from its dict. Removing the decorator must leave the system fully functional. The base service must **never** return empty results for a method "so the decorator can override" — that would make correctness depend on the decorator being present.
- Shared filter/search logic lives as **`public static`** helpers on the service class (e.g., `ProfileService.SearchApprovedUsersFromSnapshot`, `ProfileService.GetFilteredHumansFromSnapshotAsync`). The base service builds its snapshot from the repository; the decorator passes `_byUserId.Values`. Same output, two speeds.
- Sub-aggregates belong to the parent section. CV entries are in `FullProfile.CVEntries` and are written through `IProfileService.SaveCVEntriesAsync`. There is no separate `IVolunteerHistoryService`. The parent repository owns the reconcile logic (`IProfileRepository.ReconcileCVEntriesAsync`).

DI registration for the inner service:

```csharp
// Inner: Scoped + keyed, so the Singleton decorator can resolve it per-call.
services.AddKeyedScoped<IProfileService, ProfileService>(CachingProfileService.InnerServiceKey);

// Forward the concrete type for IUserDataContributor resolution.
services.AddScoped<ProfileService>(sp =>
    (ProfileService)sp.GetRequiredKeyedService<IProfileService>(CachingProfileService.InnerServiceKey));
services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ProfileService>());
```

### 15d. Caching Decorator Rules

`CachingProfileService` is a **Singleton** that owns a private `ConcurrentDictionary<Guid, FullProfile> _byUserId`. The dict persists across HTTP requests. There is no separate `IProfileStore` interface, no store class, no `IMemoryCache` for canonical domain data. A narrow `FullProfileWarmupHostedService` populates the dict once at startup — see **Warming** below.

**Constructor:**

```csharp
public sealed class CachingProfileService : IProfileService, IFullProfileInvalidator
{
    public const string InnerServiceKey = "profile-inner";

    private readonly IProfileRepository _profileRepository;
    private readonly IUserEmailRepository _userEmailRepository;
    private readonly IServiceScopeFactory _scopeFactory;

    private readonly ConcurrentDictionary<Guid, FullProfile> _byUserId = new();

    public CachingProfileService(
        IProfileRepository profileRepository,
        IUserEmailRepository userEmailRepository,
        IServiceScopeFactory scopeFactory) { ... }
}
```

`IProfileRepository` and `IUserEmailRepository` are injected directly because they are also Singleton (`IDbContextFactory`-based). All Scoped dependencies (inner `IProfileService`, `IUserService`, `INavBadgeCacheInvalidator`, `INotificationMeterCacheInvalidator`) are resolved per-call via `IServiceScopeFactory` to avoid the captured-scoped-dependency anti-pattern:

```csharp
await using var scope = _scopeFactory.CreateAsyncScope();
var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
```

**Reads:** `ValueTask<FullProfile?> GetFullProfileAsync(Guid userId, CancellationToken ct = default)`. Dict hit completes synchronously with zero allocation; cold path wraps the inner call and populates the dict.

**Writes:** delegate to the inner service, then call `RefreshEntryAsync(userId, ct)`. `RefreshEntryAsync` reloads directly from repositories, re-stitches the `FullProfile`, and upserts the dict. If the profile or user no longer exists, the dict entry is removed.

**Warming:** eager at startup. `FullProfileWarmupHostedService` (registered via `AddHostedService`) calls `CachingProfileService.WarmAllAsync` during host start, enumerating all users and populating `_byUserId` before the app takes traffic. Bulk reads (`GetBirthdayProfilesAsync`, `GetApprovedProfilesWithLocationAsync`, `GetFilteredHumansAsync`, `SearchApprovedUsersAsync`) then read the warm dict directly — no runtime gate, no fully-warm flag. If warmup fails (e.g., DB unreachable at startup) the hosted service logs at Error and the host continues to start; individual per-user reads will lazy-populate the dict via `GetFullProfileAsync`. Warmup is an optimization, not a correctness requirement.

**Static helpers:** methods like `GetBirthdayProfilesAsync` and `SearchApprovedUsersAsync` are served synchronously from `_byUserId.Values` via `ProfileService.GetBirthdayProfilesFromSnapshot` / `ProfileService.SearchApprovedUsersFromSnapshot` — no inner call, no scope.

DI registration for the decorator:

```csharp
// Singleton decorator — dict persists across requests.
services.AddSingleton<CachingProfileService>();
services.AddSingleton<IProfileService>(sp => sp.GetRequiredService<CachingProfileService>());

// CRITICAL: IFullProfileInvalidator must alias the same Singleton instance.
// Two separate instances would split the dict and cause divergence.
services.AddSingleton<IFullProfileInvalidator>(sp =>
    sp.GetRequiredService<CachingProfileService>());
```

### 15e. IFullProfileInvalidator — One-Way Cross-Section Signal

`IFullProfileInvalidator` (defined in `Humans.Application/Interfaces/`) exposes a single method:

```csharp
Task InvalidateAsync(Guid userId, CancellationToken ct = default);
```

Implemented by `CachingProfileService`. External sections inject `IFullProfileInvalidator` when their writes make the cached FullProfile view stale. The decorator reloads-or-removes the entry via `RefreshEntryAsync`. External code never mutates the dict directly.

**CRITICAL:** `IFullProfileInvalidator` must resolve to the **same Singleton instance** as `IProfileService`. Both registrations point to the single `CachingProfileService` instance. If two instances were created, the dict would diverge and invalidations would be silently lost.

### 15f. Projection DTO Naming

| Type | Name |
|------|------|
| Projection DTO | `Full<Section>` (e.g., `FullProfile`) |
| Read method | `GetFull<Section>Async` (e.g., `GetFullProfileAsync`) |
| Invalidator interface | `IFull<Section>Invalidator` (e.g., `IFullProfileInvalidator`) |
| Sub-aggregate collections | Natural plural on the DTO (e.g., `CVEntries` — not `VolunteerHistory`) |

Old names that no longer exist: `CachedProfile`, `IProfileStore`, `ProfileStore`, `ProfileStoreWarmupHostedService`, `IVolunteerHistoryService`, `VolunteerHistoryService`.

### 15g. Known Deferrals

**§15 NEW-B — Cross-section ShiftAuthorization cache staleness.** ~~`RequestDeletionAsync` on Profile no longer invalidates the `ShiftAuthorization` cache (`shift-auth:{userId}`, 60s TTL) that the old `InvalidateUserCaches(userId)` bundle covered. This is tolerable at ~500-user scale given the short TTL and the context (the user is being deleted). Resolution: when Shifts migrates to the §15 pattern, it should subscribe to `IFullProfileInvalidator` (or an equivalent `IShiftAuthorizationInvalidator`) to clear its own cache on profile changes.~~ **Resolved 2026-04-24** — `ShiftManagementService` now implements `IShiftAuthorizationInvalidator`, exposed so Profile / User / Team writes call `Invalidate(userId)` directly. `UserService.AnonymizeExpiredAccountAsync` invalidates on account anonymization, and `TeamService` invalidates on role-assignment changes that toggle shift-coordinator privilege. Cross-section dependency direction is Shifts → (nothing); callers push the signal, the cache owner stays decoupled.

**`OnboardingService.SetConsentCheckPendingIfEligibleAsync` / `PurgeHumanAsync`** do not invalidate the `FullProfile` dict. Pre-existing behavior (they did not invalidate the old cache either). To be addressed after the §15 migration settles.

### 15h. Migration Rules During the Transition

1. **New sections must comply.** New features use the §15 pattern from day one. Create the repository and decorator the same day you create the service — do not accrue "migrate later" debt.
2. **Touch-and-clean within scope.** When modifying an existing service for unrelated reasons, don't scope-creep into a full §15 migration. Fix the immediate issue; migrate the section in a dedicated session.
3. **Don't half-migrate a section.** If you start extracting a repository, finish the full stack in one session. A half-migrated section is worse than either extreme.
4. **EF migration review still applies.** Schema changes still go through the EF migration reviewer agent — the repository layer does not change what migrations look like.

### 15i. Known Current Violations (as of 2026-04-23)

- **1 business service** still lives in `Humans.Infrastructure/Services/` and injects `HumansDbContext` directly: `HumansMetricsService`. All other files in that folder are connectors, stubs, or renderers that correctly stay in Infrastructure. Target: 0. Migration progress by section:
  - **Governance** — migrated 2026-04-15 in PR #503.
  - **Profiles** — fully migrated 2026-04-20 in PR #235 — `ProfileService`, `ContactFieldService`, `ContactService`, `UserEmailService`, `CommunicationPreferenceService` now live in `Humans.Application.Services.Profile`. (`VolunteerHistoryService` was folded into `ProfileService`/`IProfileRepository`; it no longer exists as a separate service.)
  - **User** — migrated 2026-04-21 in PR #243 — `UserService` now lives in `Humans.Application.Services.Users`, goes through `IUserRepository`, and invalidates `IFullProfileInvalidator` on writes that change FullProfile-visible fields. **No caching decorator** was added for the User section: User is ~500 rows with no hot bulk-read path, so a dict cache isn't warranted (same rationale as Governance's decorator removal in #242). Option A is documented in `docs/superpowers/specs/2026-04-21-issue-511-user-migration.md`.
  - **City Planning** — migrated 2026-04-22 in PR #543 — `CityPlanningService` now lives in `Humans.Application.Services.CityPlanning`, goes through `ICityPlanningRepository`, and routes cross-section reads (camps, teams, profiles, users) through the owning service interfaces. Cross-domain `.Include(h => h.ModifiedByUser)` on `CampPolygonHistories` replaced with a batched `IUserService.GetByIdsAsync` lookup. Option A (no decorator) — admin-facing, low-traffic.
  - **Audit Log** — migrated 2026-04-22 for issue #552 — `AuditLogService` now lives in `Humans.Application.Services.AuditLog`, goes through `IAuditLogRepository`, and persists each entry immediately (auto-saved per call) rather than relying on a shared-scope `SaveChanges` from the caller. The repository is append-only per §12 — no update or delete surface — enforced by `AuditLogArchitectureTests.IAuditLogRepository_HasNoUpdateOrDeleteMethods`. Option A (no decorator) — writes are scattered across every section (~96 call sites) and reads are admin-only.
  - **Budget** — migrated 2026-04-22 in issue #544; flipped to §15b Singleton + `IDbContextFactory` in issue #572 (2026-04-23). `BudgetService` lives in `Humans.Application.Services.Budget`, goes through `IBudgetRepository`, and calls `ITeamService` (via two narrow new methods `GetBudgetableTeamsAsync` and `GetEffectiveBudgetCoordinatorTeamIdsAsync`) for cross-domain team reads. `IBudgetRepository` exposes atomic per-method operations — no public `SaveChangesAsync`, no `FindForMutationAsync`-returns-tracked surface. Composite operations (e.g., `CreateYearWithScaffoldAsync`, `SyncTicketingActualsAsync`, `RefreshTicketingProjectionsAsync`) perform all their work inside one repository method so the transaction boundary is preserved inside a single `DbContext` lifetime. No caching decorator — Budget is admin-only, low-traffic. `BudgetAuditLog.ActorUser` and `BudgetCategory.Team` cross-domain navs are `[Obsolete]`-marked; `BudgetLineItem.ResponsibleTeam` is still read by the Finance CategoryDetail view and deferred to a nav-strip follow-up.
  - **Campaigns** — migrated 2026-04-22 in issue #546 — `CampaignService` now lives in `Humans.Application.Services.Campaigns`, goes through `ICampaignRepository`, and routes cross-section reads via `ITeamService.GetTeamMembersAsync` and the new `IUserEmailService.GetNotificationTargetEmailsAsync(IReadOnlyCollection<Guid>)`. No caching decorator. Cross-domain navs `CampaignGrant.User` and `Campaign.CreatedByUser` are `[Obsolete]`-marked; the `TicketQueryService.GetCodeTrackingDataAsync` code-tracking page still reads `grant.User.DisplayName` inside `#pragma warning disable CS0618` blocks — migration follow-up lands when Tickets moves to Application.
  - **Camps** — migrated 2026-04-22 for issue #542 — `CampService` now lives in `Humans.Application.Services.Camps`, goes through `ICampRepository`, routes lead display-names through `IUserService`, and delegates filesystem I/O to `ICampImageStorage`. **No caching decorator**: the ~100-row camp list uses short-TTL `IMemoryCache` in-service per §15f.
  - **Email** — migrated 2026-04-22 in PR for issue #548 — `EmailOutboxService` and `OutboxEmailService` now live in `Humans.Application.Services.Email`, go through `IEmailOutboxRepository`, and route the verified-email-by-recipient lookup through `IUserEmailService` so `user_emails` stays behind its owning service. Two new connector abstractions (`IEmailBodyComposer`, `IImmediateOutboxProcessor`) keep `IHostEnvironment`/`EmailSettings` and Hangfire out of the Application layer. No caching decorator — outbox is a sequential queue drain, not a hot-path read shape.
  - **Feedback** — migrated 2026-04-22 in issue #549 — `FeedbackService` now lives in `Humans.Application.Services.Feedback`, goes through `IFeedbackRepository`, and resolves reporter / assignee / resolver display names + effective email via `IUserService`, `IUserEmailService.GetNotificationTargetEmailsAsync`, and `ITeamService.GetTeamNamesByIdsAsync`. Cross-domain `.Include()` chains on `FeedbackReport.User`, `.ResolvedByUser`, `.AssignedToUser`, `.AssignedToTeam`, and `FeedbackMessage.SenderUser` are gone from the service (4→0). Navs are `[Obsolete]`-marked and populated in-memory by the service (§6b "in-memory join") so controllers and views continue to read `report.User.DisplayName` etc. under `#pragma warning disable CS0618`. Nav-strip follow-up lands as part of the wider User-entity nav cleanup. No caching decorator — Feedback is admin-review-only and low-traffic.
  - **Auth** — migrated 2026-04-22 in issue #551 — `RoleAssignmentService` and `MagicLinkService` now live in `Humans.Application.Services.Auth`. `RoleAssignmentService` goes through `IRoleAssignmentRepository`, stitches assignee / creator display names via `IUserService`, and invalidates the per-user claims cache via `IRoleAssignmentClaimsCacheInvalidator` + the nav-badge cache via `INavBadgeCacheInvalidator`. `MagicLinkService` owns no tables; verified-email lookup routes through `IUserEmailService.FindVerifiedEmailWithUserAsync`, and Data-Protection / URL / replay / signup-cooldown state sits behind Infrastructure-side `IMagicLinkUrlBuilder` + `IMagicLinkRateLimiter` (same shape as `CommunicationPreferenceService` + `IUnsubscribeTokenProvider`). Cross-domain navs on `RoleAssignment` (`User`, `CreatedByUser`) are `[Obsolete]`-marked and populated in-memory; controllers + the two daily-digest jobs that still read them do so under `#pragma warning disable CS0618`. No caching decorator — Auth writes are rare (handful of admin events per month).
  - **Teams** — fully migrated 2026-04-23 under umbrella #540 (sub-task #540a landed last). `TeamService` now lives in `Humans.Application.Services.Teams`, goes through `ITeamRepository` for all owned-table access, and routes every cross-section read through the public service interface (`IUserService` for display-name/profile-picture stitching, `IRoleAssignmentService` for role checks, `IShiftManagementService` for active-event + pending-signup-count lookups, `ITeamResourceService` for Drive resource summaries, `IEmailService`/`ISystemTeamSync` for out-of-band side effects). Cross-domain `.Include(...User)` chains in the service are gone (5→0). Cross-domain navs (`TeamMember.User`, `TeamJoinRequest.User`, `TeamJoinRequest.ReviewedByUser`, `TeamRoleAssignment.AssignedByUser`, `TeamJoinRequestStateHistory.ChangedByUser`) are `[Obsolete]`-marked per §6c; the service populates them in-memory (§6b) for controllers/views/models that still read them under `#pragma warning disable CS0618` blocks. The nav-strip follow-up lands with the wider User-entity nav cleanup. Option A (no separate caching decorator): the section keeps the existing short-TTL `IMemoryCache` projection at `CacheKeys.ActiveTeams` (10-minute TTL, in-service, same precedent as Camps — §15i Camps entry). The decorator split can be layered on later without changing the `ITeamService` surface if profiling warrants it. Sub-tasks #540b (`TeamPageService`) and #540c (`TeamResourceService`) landed earlier in this umbrella. §15i transitional #2 (`TeamService → UserEmailService → AccountMergeService → TeamService`) is no longer active — the lazy `IServiceProvider` resolution for `IEmailService` inside `TeamService` is still in place solely to break the cycle with `UserService → TeamService → EmailService → UserEmailService → UserService`, and goes away when `AccountMergeService` migrates to `Humans.Application.Services.Profile` (tracked separately).
  - **Notifications** — migrated 2026-04-22 for issue #550 — `NotificationService`, `NotificationInboxService`, and `NotificationMeterProvider` now live in `Humans.Application.Services.Notifications`, go through `INotificationRepository` for their owned tables (`notifications`, `notification_recipients`), and reach every other section's data via its public service interface. `NotificationMeterProvider` was the biggest clean-up: it previously read `Profiles`, `Users`, `GoogleSyncOutboxEvents`, `TeamJoinRequests`, `TicketSyncStates`, and `Applications` directly; it now aggregates count methods added to `IProfileService`, `IUserService`, `IGoogleSyncService`, `ITeamService`, `ITicketSyncService`, and `IApplicationDecisionService`. `IRoleAssignmentService.GetActiveUserIdsForRoleAsync` was added so `NotificationService.SendToRoleAsync` doesn't query `role_assignments`. `CleanupNotificationsJob` also moves through the repository. Option A (no caching decorator) — dispatch is fire-and-forget and reads are cached at the view-component layer via short-TTL `IMemoryCache`.
  - **Onboarding** — migrated in PR #285 (issue #553) — `OnboardingService` now lives in `Humans.Application.Services.Onboarding`. It owns no tables and orchestrates Profiles, Legal, and Teams via their public service interfaces.
  - **Shifts** — fully migrated 2026-04-25 under umbrella #541. `ShiftManagementService`, `ShiftSignupService`, and `GeneralAvailabilityService` live in `Humans.Application.Services.Shifts`, going through `IShiftManagementRepository`, `IShiftSignupRepository`, and `IGeneralAvailabilityRepository`. Cross-domain navs `Rota.Team`, `ShiftSignup.User` / `EnrolledByUser` / `ReviewedByUser`, `VolunteerEventProfile.User`, and `VolunteerTagPreference.User` are **deleted** — Shifts-owned entities expose only their FK (`TeamId`, `UserId`, etc.). EF configurations now use the typed-FK form (`HasOne<Team>().WithMany().HasForeignKey(r => r.TeamId)`) — no nav reference, no `#pragma warning disable CS0618` left in the section. Cross-domain `.Include(Rota.Team)` / `.Include(ShiftSignup.User)` / `.Include(ShiftSignup.ReviewedByUser)` inside `ShiftSignupRepository` are stripped — the repo now returns ID-only graphs and consumers resolve display fields via `ITeamService.GetTeamNamesByIdsAsync` (or `GetByIdsWithParentsAsync` when slug is needed) and `IUserService.GetByIdsAsync`. The `ShiftAdminViewModel` carries an `IReadOnlyDictionary<Guid, User> Users` populated by the controller; `ShiftAdmin/Index.cshtml` reads from it via `Model.Users.GetValueOrDefault(...)`. The `ShiftManagementService.GetRotaByIdAsync` / `GetRotasByDepartmentAsync` / `GetShiftByIdAsync` in-memory nav-stitching is gone — those methods are now thin pass-throughs to the repo. §15 NEW-B (ShiftAuthorization cache invalidation on profile mutation) is resolved — `ShiftManagementService` implements `IShiftAuthorizationInvalidator`, and `UserService.AnonymizeExpiredAccountAsync` + `TeamService` role-assignment writes call `Invalidate(userId)` to clear the 60 s `shift-auth:{userId}` cache. **Option A** (no separate caching decorator): the section keeps the existing short-TTL `IMemoryCache` entries (`shift-auth:{userId}` at 60 s, coordinator-dashboard at 5 min) in-service per §15f. Per-service architecture tests (`ShiftManagementArchitectureTests`, `ShiftSignupArchitectureTests`, `GeneralAvailabilityArchitectureTests`) pin namespace + no-DbContext-ctor + repository-dep invariants, plus `ShiftsOwnedEntities_HaveNoCrossDomainNavigationProperties` assertion that `User`/`Team` navs are not reintroduced.
  - **Tickets (partial, #545)** — `TicketQueryService`, `TicketSyncService`, and `TicketingBudgetService` now live in `Humans.Application.Services.Tickets`, going through `ITicketRepository` and `ITicketingBudgetRepository`. The Ticket Tailor API side of `TicketSyncService` is structurally separated via the `ITicketVendorService` connector (PR #277). Pending upstream promotion to nobodies-collective/Humans.
  - **Google Workspace (fully migrated, #554 / #574 / #575 / #576)** — all Google Integration business services now live in `Humans.Application.Services.GoogleIntegration`: `GoogleAdminService`, `GoogleWorkspaceUserService`, `DriveActivityMonitorService`, `SyncSettingsService`, and `EmailProvisioningService` (PR #267, issue #289); and `GoogleWorkspaceSyncService` (§15 Part 2b, issue #575, 2026-04-23). **Part 1 of #554 (2026-04-23):** `IGoogleSyncOutboxRepository` extracted — `google_sync_outbox_events` behind a dedicated repository for the count queries used by `NotificationMeterProvider`, `HumansMetricsService`, `SendAdminDailyDigestJob`, and `GoogleWorkspaceSyncService.GetFailedSyncEventCountAsync`. **Part 2a (issue #574, PR #302):** SDK bridge interfaces extracted — `IGoogleDirectoryClient`, `IGoogleDrivePermissionsClient`, `IGoogleGroupMembershipClient`, `IGoogleGroupProvisioningClient` — with real Google-backed implementations and dev-mode stubs in `Humans.Infrastructure/Services/GoogleWorkspace/`. **Part 2b (issue #575):** `GoogleWorkspaceSyncService` moved to `Humans.Application.Services.GoogleIntegration`; it now reads Google via the four Part 2a bridges + `ITeamResourceGoogleClient`, reads cross-section DB state through sibling service interfaces (`ITeamService` for team/member graph via two new methods `GetActiveMembersForTeamsAsync` + `GetActiveChildMembersByParentIdsAsync`, `IUserService` for User rows incl. new `SetGoogleEmailStatusAsync`, `IUserEmailService.MatchByEmailsAsync` for extra-email identity, `IGoogleResourceRepository` for narrow `google_resources` writes), and lazy-resolves `ITeamResourceService` via `IServiceProvider` to break the construction cycle with `TeamResourceService`. Non-sensitive options (Domain / CustomerId / TeamFoldersParentId / GroupSettings) live on a new Application-layer `GoogleWorkspaceOptions`; credential-sensitive `GoogleWorkspaceSettings` stays in Infrastructure and both bind to the same `GoogleWorkspace` appsettings section. Parallel-sync DbContext-factory plumbing retired — the bridge clients carry their own concurrency-safe state and the old `DbSemaphore` is no longer needed. **Part 2c (issue #576, 2026-04-23):** the three remaining direct-DbContext consumers were flipped onto the repository surface: `ProcessGoogleSyncOutboxJob` now injects `IGoogleSyncOutboxRepository` + `IGoogleResourceRepository` + `IUserService` + `ITeamService` (the outbox repo grew a `GetProcessingBatchAsync` / `MarkProcessedAsync` / `MarkPermanentlyFailedAsync` / `IncrementRetryAsync` processor surface); `GoogleController.SyncOutbox` routes the admin dashboard read through `IGoogleSyncOutboxRepository.GetRecentAsync` + sibling services for user/team/resource display; and `TeamService` — the only remaining Application-layer writer of `GoogleSyncOutboxEvent` — already delegates every `outboxEvent` insert to `TeamRepository.AddMemberWithOutboxAsync` / `ApproveRequestWithMemberAsync` / `MarkMemberLeftWithOutboxAsync` (kept inside the Teams transaction boundary per §6d). The Google Workspace section now has zero non-repository direct `DbSet<GoogleSyncOutboxEvent>` / `DbSet<GoogleResource>` / `DbSet<SyncServiceSettings>` reads or writes across Application + Web layers.
  - **Calendar** — migrated 2026-04-23 for issue #569 — `CalendarService` now lives in `Humans.Application.Services.Calendar`, goes through `ICalendarRepository`, and routes owning-team display names through `ITeamService.GetTeamNamesByIdsAsync` (§6b in-memory join). Cross-domain `.Include(e => e.OwningTeam)` is gone; `CalendarEvent.OwningTeam` nav is `[Obsolete]`-marked and the EF configuration references it under `#pragma warning disable CS0618` to keep the FK + cascade behavior wired. **No caching decorator** — short-TTL `IMemoryCache` (`calendar:active-events`) stays in-service per §15f. The `calendar_events` / `calendar_event_exceptions` tables are now listed under a new **Calendar** row in §8.
- **Cross-domain `.Include()` calls** are now gone from the Application layer — the Application layer is clean (0 `.Include()` calls across all Application-layer services). The two remaining `.Include(Team)` reads inside `TeamRepository.GetActiveMembersForTeamsAsync` and `GetActiveChildMembersByParentIdsAsync` hydrate the aggregate-local `TeamMember.Team` nav only (not cross-domain). Target: 0 everywhere.
  - Includes fully removed from the service layer in these sections:
    - Profile
    - Governance
    - City Planning
    - Campaigns
    - Camps
    - Feedback
    - Auth
    - Teams (all three services — `TeamService`, `TeamPageService`, `TeamResourceService`)
    - Notifications
    - Onboarding
    - Shifts
    - Tickets
    - Google Workspace (all services — `GoogleWorkspaceSyncService` moved to the Application layer in §15 Part 2b / #575; cross-domain includes retired in favour of sibling-service batched reads)
    - Calendar
  - **User section's PR #243 landed the Application-layer move but deferred the nav-strip** — cross-domain nav reads via `user.UserEmails`, `user.Profile`, `user.TeamMemberships`, `user.GetEffectiveEmail()` still exist in `TeamService`, `GoogleWorkspaceSyncService` (Application layer; reads `TeamMember.User` after `ITeamService` stitches it in-memory, under `#pragma warning disable CS0618`), `SendBoardDailyDigestJob`, `SyncLegalDocumentsJob`, `SystemTeamSyncJob`, `SuspendNonCompliantMembersJob`, `ProfileController`. Tracked as a follow-up.
- **29 repositories** exist today. Target: one per domain (~20 total, some sections need two):
  - `AccountMergeRepository`
  - `ApplicationRepository`
  - `AuditLogRepository`
  - `BudgetRepository`
  - `CalendarRepository`
  - `CampaignRepository`
  - `CampRepository`
  - `CityPlanningRepository`
  - `CommunicationPreferenceRepository`
  - `ConsentRepository`
  - `ContactFieldRepository`
  - `DriveActivityMonitorRepository`
  - `EmailOutboxRepository`
  - `FeedbackRepository`
  - `GeneralAvailabilityRepository`
  - `GoogleResourceRepository`
  - `GoogleSyncOutboxRepository`
  - `LegalDocumentRepository`
  - `NotificationRepository`
  - `ProfileRepository`
  - `RoleAssignmentRepository`
  - `ShiftManagementRepository`
  - `ShiftSignupRepository`
  - `SyncSettingsRepository`
  - `TeamRepository`
  - `TicketRepository`
  - `TicketingBudgetRepository`
  - `UserEmailRepository`
  - `UserRepository`
- **0 stores** exist today. `IApplicationStore` was retired when Governance dropped its caching layer (issue #533). Target: replaced by §15 `ConcurrentDictionary`-in-decorator where a cache is warranted; no separate store type.
- **1 caching decorator** exists today (`CachingProfileService` — §15 pattern). Governance, User, and Camps sections operate without one. Target: every migrated section that needs caching uses the §15 pattern. Not every section needs caching.
- **Inline `IMemoryCache.GetOrCreateAsync`** still scattered across services for non-profile caches (nav badge, notification meter, role-assignment claims, shift auth, camps-for-year, camp settings). These are short-TTL request-acceleration caches, not canonical domain data caches, and are appropriate for `IMemoryCache`. Canonical domain data caches (full entity projections) must use the §15 pattern.
- **Cross-domain navigation properties**. Target: stripped at the entity boundary, FK-only everywhere.
  - Still declared:
    - `User.Profile`, `User.UserEmails`, `User.TeamMemberships`, `User.RoleAssignments`, `User.Applications`, `User.ConsentRecords`, `User.CommunicationPreferences`, `User.GetEffectiveEmail()` — PR #243 deferred the strip to an isolated follow-up PR because ~15 call sites need service-routing migration (and a new `IUserEmailService.GetNotificationEmailAsync` surface).
    - `TeamMember.User`, `Camp.CreatedByUser`, `CampSeason.ReviewedByUser`, etc. — still declared freely.
  - Stripped:
    - Profile-section: `Profile.User`, `UserEmail.User`, `CommunicationPreference.User`.
    - Email-section: `EmailOutboxMessage.User`.
    - `CampLead.User` (issue #542) — lead display data routes through `IUserService`.

**External connectors (API bridge pattern).** External SDKs (Google, Stripe, SMTP/IMAP, Octokit, etc.) sit behind Application-layer interfaces so `Humans.Application` never references the SDK assembly. The concrete implementation lives in `Humans.Infrastructure/Services/` (or a subfolder) and is the only code that imports the SDK namespaces. Connectors own no database tables — side-effects that need persistence write through the owning section's repository (e.g., Stripe fee values land on `TicketOrder`, written by `ITicketRepository`).
- **Stripe** (issue #556, 2026-04-22): `IStripeService` in `Humans.Application.Interfaces`, `StripeService` in `Humans.Infrastructure.Services`. The bridge is structurally enforced (`Humans.Application.csproj` does not reference `Stripe.net`) and additionally covered by `StripeConnectorArchitectureTests` (SDK types cannot leak onto the interface surface).
- **Google Workspace** (pre-§15): resources are on Shared Drives only; all SDK access goes through the dedicated services listed in §13. Extracted connectors so far:
  - `ITeamResourceGoogleClient` (PR #274) — Teams→Drive linking.
  - `IWorkspaceUserDirectoryClient` (issue #554) — @nobodies.team account lifecycle.
  - `IGoogleDriveActivityClient` (issue #554) — Drive Activity v2 permission-change monitoring.
  - `IGoogleGroupMembershipClient`, `IGoogleGroupProvisioningClient`, `IGoogleDrivePermissionsClient`, `IGoogleDirectoryClient` (§15 Part 2a, issue #574) — SDK bridge surface consumed by the Application-layer `GoogleWorkspaceSyncService` (§15 Part 2b, issue #575). Real implementations in `Humans.Infrastructure/Services/GoogleWorkspace/`; stubs (in-memory fakes) registered when no service-account credentials are configured. Application-layer service never imports `Google.Apis.*`.
- **Email** (PR #266): `IEmailBodyComposer` (Application) renders the message; `IImmediateOutboxProcessor` (Infrastructure) drives MailKit/SMTP. The body-composer is SDK-free so Application-layer services can build messages without pulling MailKit in.
- **Ticket vendor** (PR #277): `ITicketVendorService` (Application), concrete `TicketTailorService` / `StubTicketVendorService` (Infrastructure). `TicketVendorSettings` lives in `Humans.Application.Configuration` so the Application-layer `TicketSyncService` can read non-sensitive fields without reaching into Infrastructure.

Controllers with direct `DbContext` access (violation of §2a, tracked separately):
- `AdminController`, `ProfileController`, `GoogleController`, `DevLoginController` (dev-only, low priority).
