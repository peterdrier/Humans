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

Each domain's tables are owned by exactly one service (and that service's repository). **No other service may query, insert, update, or delete rows in tables it does not own.** If `CampService` needs a profile, it calls `IProfileService` — it does not query the `profiles` table, does not instantiate `IProfileRepository`, does not read from `IProfileStore`.

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

Every cached domain has a **store** — a dedicated class that owns an in-memory canonical copy of its entities. The store is the *data shape* of the cache; it is separate from the decorator that makes reads transparent.

### 4a. Store Rules

1. **One store per domain.** `IProfileStore` holds the Profile world. `ITeamStore` holds the Team world. Stores do not share state.
2. **Canonical storage is a dictionary keyed by primary id** (`Dictionary<Guid, Profile>`). Secondary indexes (e.g., `Dictionary<string, Guid>` for email → id) are allowed when a specific lookup pattern justifies them; the store keeps them consistent because only the store writes.
3. **Single writer.** Only the owning service writes to the store, and only as part of a successful DB write. The store interface exposes `Upsert(entity)` and `Remove(id)`; the owning service calls these immediately after its repository write returns successfully.
4. **Startup warmup.** Each store loads its full domain on startup via `IProfileRepository.GetAllAsync()`. At ~500 users this is trivial memory and query cost; it eliminates cache-miss reasoning entirely.
5. **Stores are Infrastructure.** The interface lives in `Humans.Application/Interfaces/Stores/`, the implementation lives in `Humans.Infrastructure/Stores/`.

### 4b. Why a Store, Not Inline `IMemoryCache.GetOrCreateAsync`

The old pattern (`_cache.GetOrCreateAsync($"profile:{id}", ...)` inside a service method) caches *query results*, not entities. `GetById`, `GetByEmail`, and `GetByIds` become three independent cache entries for overlapping data, with three independent invalidation paths and three opportunities for staleness. Under the store pattern, all three are dict lookups over the same canonical `Profile` object, and invalidation is a single `Upsert` call in one place: the owning service's write method.

## 5. Decorator Caching

Services are cached by **wrapping them in a decorator**, not by inlining `IMemoryCache` calls. The decorator is registered via `services.Decorate<IProfileService, CachingProfileService>()` (Scrutor). Callers inject `IProfileService` and get the cached version transparently.

### 5a. Decorator Rules

1. **One decorator per service.** `CachingProfileService : IProfileService` wraps the real `ProfileService`.
2. **Reads go through the store.** The decorator asks `IProfileStore` first. With startup warmup, every read is a hit at our scale.
3. **Writes pass through to the inner service.** The inner service writes to the repository and then updates the store. The decorator does not update the store itself — the service does, because only the service knows what the final entity state is after business rules run.
4. **Decorators contain zero business logic.** If the decorator needs to decide anything beyond "is it in the store?", that decision belongs in the service, not the wrapper.

### 5b. The Full Stack

```
Controllers / other services
          ↓ IProfileService
CachingProfileService (decorator)       [Infrastructure]
          ↓ IProfileService
ProfileService (business logic)         [Application]
          ↓ IProfileRepository, IProfileStore
ProfileRepository, ProfileStore         [Infrastructure]
          ↓ DbContext
HumansDbContext                         [Infrastructure]
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
| **Domain audit** (suspended, approved, tier changed) | **In-service** | Needs actor, before/after state, semantic intent, same transaction as the write |
| **Authorization** | **In-controller** (resource-based handlers, §11) | Needs HTTP identity + resource context |
| **Transactions / unit of work** | **In-service** | Must wrap the whole business operation |

### 7a. Audit Is In-Service

Domain audit events — "user X suspended user Y for reason Z" — need the actor, the before/after state, the semantic intent, and must commit in the same unit of work as the mutation. A decorator wrapping `SuspendAsync(userId)` has none of that context: it does not know the actor (unless plumbed in), it does not know the old state (unless it re-reads, which is wasteful), and it cannot distinguish a name edit from a suspension from a tier change.

Services call `IAuditLogService.LogAsync(...)` explicitly inside the business method:

```csharp
public async Task SuspendAsync(Guid userId, Guid actorId, string reason, CancellationToken ct)
{
    var profile = await _repo.GetByUserIdAsync(userId, ct);
    if (profile is null) return;

    var wasAlreadySuspended = profile.IsSuspended;
    profile.IsSuspended = true;
    await _repo.UpdateAsync(profile, ct);
    _store.Upsert(profile);

    await _auditLog.LogAsync(new AuditEntry(
        Actor:   actorId,
        Subject: userId,
        Action:  "ProfileSuspended",
        Before:  wasAlreadySuspended,
        After:   true,
        Reason:  reason), ct);
}
```

If audit calls become noisy across many methods inside one service, the next evolution is **domain events** raised from the entity and handled in Infrastructure — not a decorator.

## 8. Table Ownership Map

Each section's service owns these tables. Cross-service access goes through the service interface, never through direct DB queries, never through another domain's repository or store.

| Section | Service(s) | Owned Tables |
|---------|-----------|--------------|
| **Profiles** | `ProfileService`, `ContactFieldService`, `ContactService`, `UserEmailService`, `CommunicationPreferenceService`, `VolunteerHistoryService` | `profiles`, `contact_fields`, `user_emails`, `communication_preferences`, `volunteer_history_entries` |
| **Users/Identity** | `UserService`, `AccountProvisioningService`, `DuplicateAccountService`, `AccountMergeService` | `AspNetUsers` (users), `AspNetUserClaims`, `AspNetUserLogins`, `AspNetUserTokens` |
| **Teams** | `TeamService`, `TeamPageService`, `TeamResourceService` | `teams`, `team_members`, `team_join_requests`, `team_join_request_state_histories`, `team_role_definitions`, `team_role_assignments`, `team_pages` |
| **Auth** | `RoleAssignmentService` | `role_assignments` |
| **Governance** | `ApplicationDecisionService` | `applications`, `application_state_histories`, `board_votes` |
| **Legal & Consent** | `LegalDocumentService`, `AdminLegalDocumentService`, `ConsentService` | `legal_documents`, `document_versions`, `consent_records` |
| **Onboarding** | `OnboardingService` | *(no owned tables — orchestrates Profiles, Legal, Teams)* |
| **Camps** | `CampService`, `CampContactService` | `camps`, `camp_seasons`, `camp_leads`, `camp_images`, `camp_historical_names`, `camp_settings` |
| **City Planning** | `CityPlanningService` | `city_planning_settings`, `camp_polygons`, `camp_polygon_histories` |
| **Shifts** | `ShiftManagementService`, `ShiftSignupService`, `GeneralAvailabilityService` | `rotas`, `shifts`, `shift_signups`, `event_settings`, `general_availabilities`, `volunteer_event_profiles` |
| **Budget** | `BudgetService` | `budget_years`, `budget_groups`, `budget_categories`, `budget_line_items`, `budget_audit_logs`, `ticketing_projections` |
| **Tickets** | `TicketQueryService`, `TicketSyncService`, `TicketingBudgetService` | `ticket_orders`, `ticket_attendees`, `ticket_sync_states` |
| **Campaigns** | `CampaignService` | `campaigns`, `campaign_codes`, `campaign_grants` |
| **Team Resources** | `TeamResourceService` | `google_resources` |
| **Google Integration** | `GoogleSyncService`, `GoogleAdminService`, `GoogleWorkspaceUserService`, `DriveActivityMonitorService`, `SyncSettingsService`, `EmailProvisioningService` | `sync_service_settings` |
| **Email** | `EmailOutboxService`, `EmailService` | `email_outbox_messages` |
| **Feedback** | `FeedbackService` | `feedback_reports` |
| **Notifications** | `NotificationService`, `NotificationInboxService` | *(in-memory / transient)* |
| **Audit** | `AuditLogService` | `audit_log_entries` |
| **System Settings** | *(accessed via owning services)* | `system_settings` |
| **Unsubscribe** | `UnsubscribeService` | *(operates on User.UnsubscribedFromCampaigns via UserService)* |

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

// WRONG — reaches into another domain's store
public class CampService(ICampRepository repo, IProfileStore profileStore) : ICampService { ... }

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

## 15. Migration Strategy

This is the target. Existing code violates most of it. Migration is **per-domain, one at a time** — no big-bang rewrite.

### 15a. Migration Phases

> **Practice spike completed 2026-04-15:** **Governance** (`ApplicationDecisionService`) is the first section to land the full repo/store/decorator pattern end-to-end — see PR #503 and [`docs/superpowers/plans/2026-04-15-governance-migration.md`](../superpowers/plans/2026-04-15-governance-migration.md). Use that PR as the **reference template** for every subsequent section migration. Profile remains Step 0 in the original migration sequence (below) but the mechanics are now proven on a smaller target.

1. **Step 0 — Spike on Profile.** Land the full pattern on one domain to validate the shape: create `IProfileRepository` + `ProfileRepository`, create `IProfileStore` + `ProfileStore`, move `ProfileService` from `Humans.Infrastructure` to `Humans.Application`, create `CachingProfileService` decorator, wire DI. Verify build and smoke test. This is the architectural proof — if it feels wrong, bail before touching more domains.
2. **Step 1 — Quarantine direct access to the spiked domain.** Replace every `_dbContext.Profiles.*` call in other services with `IProfileService` calls. Delete every `.Include(x => x.Profile)` / `.Include(x => x.User)` in other domains; replace with in-memory stitching per §6b. At the end of this step, `DbContext.Profiles` is referenced in exactly one file: `ProfileRepository.cs`.
3. **Step 2 — Repeat per domain, highest-blast-radius first.** Priority order (driven by cross-domain `.Include` count and fan-in, not alphabet): User, Team, RoleAssignment, then Campaign, Application, Consent, then the long tail.
4. **Step 3 — Tail domains.** Audit log, outbox, sync settings, and other low-traffic domains get the full pattern for consistency. These are mechanical and low-risk.
5. **Step 4 — Structural enforcement.** Architecture test or CI grep that fails if (a) any service lives in `Humans.Infrastructure/Services/`, or (b) any file outside a `*Repository.cs` references `DbContext.<tablename>`, or (c) any `.Include()` navigates across domain boundaries.

### 15b. Migration Rules During the Transition

1. **New code must comply.** New features use the target pattern from day one, even in domains that have not been migrated yet. That means creating the repository + store + decorator for a new domain the same day you create its service.
2. **Touch-and-clean within scope.** When modifying an existing service for unrelated reasons, don't scope-creep into a full repository migration. Fix the immediate bug; migrate the domain in a dedicated session.
3. **Don't half-migrate a domain.** If you start extracting `IProfileRepository`, finish the full stack (repo + store + decorator + caller updates) in one session. A half-migrated domain where some callers use the new service and others still `.Include()` directly is worse than either extreme.
4. **EF migration review still applies.** Schema changes still go through the EF migration reviewer agent — the repository layer does not change what migrations look like, just who calls them.

### 15c. Known Current Violations (as of 2026-04-17)

- **35 services** live in `Humans.Infrastructure/Services/` and inject `HumansDbContext` directly. Target: 0 (all business services move to `Humans.Application`). **Governance:** migrated 2026-04-15 in PR #503. **Profiles:** migrated 2026-04-16 in #504 — 6 services (`ProfileService`, `ContactFieldService`, `ContactService`, `UserEmailService`, `CommunicationPreferenceService`, `VolunteerHistoryService`) now live in `Humans.Application.Services.Profile`. **Users:** migrated 2026-04-17 in #511 — `UserService` now lives in `Humans.Application.Services.Users`.
- **~20 cross-domain `.Include()` calls** across **~12 services**. Biggest offenders: `OnboardingService` (2 remaining after #511), `GoogleWorkspaceSyncService` (3 remaining), `FeedbackService` (4 — all inbound navs owned by Feedback section). The User-side Profile/UserEmails/TeamMemberships/RoleAssignments includes are gone after #511. Target: 0.
- **7 repositories** exist today (`ApplicationRepository`, `ProfileRepository`, `ContactFieldRepository`, `UserEmailRepository`, `CommunicationPreferenceRepository`, `VolunteerHistoryRepository`, `UserRepository`). Target: one per domain (~20 total).
- **3 stores** exist today (`ApplicationStore`, `ProfileStore`, `UserStore`). Target: one per cached domain (~15–20).
- **3 caching decorators** exist today (`CachingApplicationDecisionService`, `CachingProfileService`, `CachingUserService`). Target: one per migrated service.
- **Inline `IMemoryCache.GetOrCreateAsync`** still scattered across services. Governance, Profiles, and Users extracted invalidations to cross-cutting interfaces and the decorator pattern. Target: replaced by decorator + store pattern everywhere.
- **Cross-domain navigation properties** (`CampLead.User`, etc.) are still used on some entities. **Governance**, **Profile-section**, and **User-side cross-domain navs** (`User.Profile`, `User.UserEmails`, `User.TeamMemberships`, `User.RoleAssignments`, `User.CommunicationPreferences`, `User.Applications`, `User.ConsentRecords`) have been stripped. The inbound navs (`TeamMember.User`, `RoleAssignment.User`, `Feedback.User`, `CampaignGrant.User`) remain until their owning sections migrate. Target: stripped at the entity boundary, FK-only.
- **Transitional §2c violations from #511 (User migration):**
  - `OnboardingService.ApproveVolunteerAsync`/`SuspendAsync`/`UnsuspendAsync` read/write `DbContext.Profiles` directly because calling `IProfileService.{ApproveVolunteer,Suspend,Unsuspend}Async` from OnboardingService would create a DI cycle (ProfileService already depends on IOnboardingService). Fixed when OnboardingService itself migrates. (`SuspendNonCompliantMembersJob` was fixed up in the same PR — it's a leaf caller, no cycle, so it now goes through `IProfileService.SuspendAsync`.)
  - `TeamService` → `IUserEmailService` was fixed in #511 by extracting `IAccountMergeRequestRepository` from `IAccountMergeService`, breaking the `TeamService` → `UserEmailService` → `AccountMergeService` → `TeamService` cycle.
  - `GoogleWorkspaceSyncService` → `ITeamService` was also addressed in #511: rather than extract an interface, the single `ITeamService.GetUserTeamsAsync` usage in `RestoreUserToAllTeamsAsync` was replaced with a direct `DbContext.TeamMembers` read — consistent with GWS's three other direct TeamMembers queries (lines 204, 227, 685). The lazy `IServiceProvider` getter was removed. GWS's remaining cross-section DbContext reads are transitional until the Google integration section itself migrates.
  - `AccountMergeService.RejectAsync` line ~180 single-email removal via `DbContext.UserEmails.Remove(pendingEmail)` retained because `IUserEmailService.DeleteEmailAsync` blocks OAuth deletion and reassigns notification target — semantics differ from pending-email rejection.

Controllers with direct DbContext access (violation of §2a, tracked separately):
- `AdminController`, `ProfileController`, `GoogleController`, `DevLoginController` (dev-only, low priority).
