<!-- freshness:triggers
  src/Humans.Application/Interfaces/**
  src/Humans.Application/Services/**
  src/Humans.Infrastructure/Repositories/**
  src/Humans.Infrastructure/Data/**
  src/Humans.Analyzers/**
  src/Sections/**
  docs/architecture/freshness-catalog.yml
-->
<!-- freshness:flag-on-change
  Layer responsibilities, service/repository/store ownership, caching decorator pattern, and authorization handler doctrine. Flag if any architectural pattern shift in src/** alters the layering or ownership rules.
-->

# Design Rules

> **Subordinate to [`peters-hard-rules.md`](peters-hard-rules.md).** Those are the constitution — the final word; on any conflict, the hard rules win. This doc is the *regulations*: the implementing detail. Open a section on demand. Architectural term definitions (Section / Crosscut / Orchestrator / Lane / Width) live in [`CONTEXT.md`](../../CONTEXT.md).

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
| **Infrastructure** | Repository implementations, store implementations, caching decorators, the five per-section `<Section>DbContext`s whose sections have not yet gone G5 (§2b), their migrations, external API clients (Google, Stripe, SMTP), background jobs | Controller logic, Razor, HTTP request/response, business rules |
| **Web** | Controllers, views, view models, API endpoints, DI wiring | `DbContext`, direct EF queries, direct cache access for domain data, raw SQL |

The project reference graph (`Humans.Application.csproj` references only `Humans.Domain.csproj` and `Humans.Interfaces.csproj`) **structurally enforces** that services in Application cannot import `Microsoft.EntityFrameworkCore`. EF pollution in business logic is a compile error, not a code-review finding.

`Humans.Interfaces` is the lowest-level project — no project references, and its only package is NodaTime. It holds the role markers (`IApplicationService`, `IRepository`, `IOrchestrator`, `IFanout`, `IInvalidator`, `ISection`), the architecture attributes (`GrandfatheredAttribute`, `DontFixAttribute`, `SurfaceBudgetAttribute`, `ExternalWriteAttribute`), and a small number of **shape interfaces** that several sections must agree on without referencing each other — `Interfaces/Shifts/IBurnSettingsInfo.cs` is the live example, and it carries default clock-rule implementations, which is why NodaTime is referenced here at all. Their namespaces intentionally stay `Humans.Application.*` (namespace ≠ assembly), so call sites and the `Humans.Analyzers` full-name constants are unaffected by the move.

**The Web layer is two projects.** `Humans.UI` is the shared view layer, extracted from `Humans.Web` for the section-project split (nobodies-collective/Humans#866): a Razor class library (`Microsoft.NET.Sdk.Razor`, `AddRazorSupportForMvc`) referencing only `Humans.Application`. It holds `SharedResource` and its satellite resx files, the tag helpers, the shared `Views/Shared` partials and layouts (`_AdminLayout`, `_Table`, `_Pager`, …), the generic table + pager view models, `PolicyNames`, `TempDataKeys`, the display extensions, and the section-agnostic view components (`AuditLog`, `Human`, `TempDataAlerts`). Its types live under `Humans.UI.*` — notably `Humans.UI.Authorization.PolicyNames` and `Humans.UI.Extensions.DateTimeDisplayExtensions`. `Humans.Web` (the Shell) references it, and so will every future section project, since a section project cannot reference the Shell.

**A section moved into its own project (nobodies-collective/Humans#866, G5) is internal by default.** Each lives under `src/Sections/Humans.<Section>/` and is marked with `[assembly: Section("<Name>")]` — that marker, not this list, is what HUM0034 keys on. Moved so far (35 projects): Agent, AuditLog, Auth, Budget, Calendar, Campaigns, Cantina, CityPlanning, Consent, Containers, Debug, Development, Email, Events, Expenses, Feedback, Finance, Gate, Gdpr, Governance, Guide, Holded, Issues, Mailer, Notifications, Onboarding, Scanner, Search, Store, Surveys, SystemSettings, Teams, TicketTailor, Tickets, Tour. `Humans.TicketTailor` is the vendor adapter behind Tickets' `ITicketVendorService` port, not a UI section, but it carries the same `Section` marker and the same internal-by-default rule. 23 of them publish a cross-section read surface from a paired `Humans.<Section>.Contracts` project. A section's only public surface is its `ISection` entry point, its `<Section>Resource` localization marker, EF Core migrations, types the framework requires to be public in order to function, and types declared under a `Contracts/` folder — everything else must be `internal`. This was convention-only across the first sections that moved; analyzer `HUM0034` (`SectionPublicSurfaceAnalyzer`, nobodies-collective/Humans#1013) now fails the build on any other public type in an assembly carrying the `Section` marker.

**Those are two different kinds of exception.** `Contracts/`, the `ISection` entry point and the `<Section>Resource` marker are a *deliberate surface* — someone chose to let other assemblies depend on them. The framework carve-out is not a choice: the type is public because it stops working otherwise. **The membership test is whether making it `internal` fails loudly or silently renders nothing** — silent means it belongs in the exception. Razor/MVC discovery passes that run at *compile time* filter on public accessibility and skip what they cannot see, emitting no error, warning or diagnostic; runtime resolution throws instead, so it does not qualify. **Current membership is view components and tag helpers, and that is the whole set** — controllers look like a candidate and are not, since `SectionControllerFeatureProvider` routes internal ones and a missing controller 404s loudly. The cost of getting this wrong is not theoretical: an `internal` `ProfileCardViewComponent` made `<vc:profile-card>` ship as inert literal markup and silently emptied the Profile page, with a green build and 5,475 passing tests (nobodies-collective/Humans#866 lane 2).

**Key change from prior rules:** Services now live in `Humans.Application`, not `Humans.Infrastructure`. The old rule ("services own their data access") meant "services inject `DbContext` directly," which conflated business logic with persistence and made "no cross-domain joins" impossible to enforce structurally. The new rule is "services go through their owning repository."

## 2. Service Ownership — The Core Rule

Each service is the exclusive gateway to its data. No component — controller, other service, job, or view component — may bypass the owning service to reach its tables, its cache, or its store.

### 2a. Controllers Cannot Talk to the Database

Controllers call services. Controllers never inject `DbContext`, never write EF queries, never instantiate repositories or stores directly, never access `IMemoryCache` for domain data. Their job is: receive HTTP request → authorize → call service(s) → return response.

**Exception:** `UserManager<User>` / `SignInManager<User>` for ASP.NET Identity operations (login, password, claims) are allowed in controllers since Identity is a framework concern, not a domain service.

### 2b. Services Live in Application, Not Infrastructure

Business services (`ProfileService`, `TeamService`, `BudgetService`, etc.) live in `Humans.Application`. They contain business rules, workflow logic, validation, and orchestration. They **never** import EF types. When they need to load or persist entities, they call their owning repository interface; when they need cached data, they go through their owning store.

Repository **implementations** (the classes that talk to `DbContext`) live in `Humans.Infrastructure`. That is the only project that may touch EF Core.

Every application context is `internal sealed` (issue #750). External access is via repository interfaces in `Humans.Application.Interfaces.Repositories`; wiring is via the extension methods in `Humans.Infrastructure.Hosting.InfrastructureServiceCollectionExtensions` (`AddHumansPersistence`, `AddHumansEntityFrameworkStores`, `PersistKeysToSystemDbContext`). The migration runner is a hosted service (`DatabaseMigrationHostedService`) registered by `AddHumansPersistence`. Test projects access the contexts directly via `InternalsVisibleTo`.

**There is no single context any more.** Since the per-section split (nobodies-collective/Humans#858) each section has its own `internal sealed <Section>DbContext` mapping only that section's tables, with its own `__EFMigrationsHistory_<Section>` table and its own migrations folder — `src/Humans.Infrastructure/Migrations/<Section>/` for a section still in Infrastructure, `src/Sections/Humans.<Section>/Data/Migrations/` once it has gone G5. `HumansDbContext` and its root migration chain were deleted at peel 15 (nobodies-collective/Humans#858); the merged Users+Profiles section (`UsersDbContext`) carries the Identity base. Consequences:

- **One design-time factory per context**, each next to its context: the five contexts still in `Humans.Infrastructure.Data` (`UsersDbContextFactory`, `CampsDbContextFactory`, `GoogleIntegrationDbContextFactory`, `ShiftsDbContextFactory`, `SystemDbContextFactory`), and each G5 section's in its own project under `Humans.<Section>.Data` (`AgentDbContextFactory`, `HoldedDbContextFactory`, …). Every `dotnet ef` command therefore needs `--context` — see [`ef-multi-context-commands`](../../memory/process/ef-multi-context-commands.md).
- **History-table names are derived, never typed.** `SectionMigrationsHistory.TableFor<TContext>()` is the single source for both the runtime registration (`AddSectionDbContext`) and the design-time factories.
- **Section contexts apply their configurations explicitly** (no assembly scanning, which would drag in other sections). `DbContextEntityOwnershipTests` fails the build if an `IEntityTypeConfiguration` ends up applied by zero contexts (invisible to `has-pending-model-changes`) or by two.
- **Unit tests for a section context** build their in-memory options with the shared `NewSectionDbOptions<TContext>()` helper in `tests/Humans.Application.Tests/Infrastructure/ServiceTestHarness.cs` rather than hand-rolling a `DbContextOptionsBuilder`.

### 2c. Table Ownership Is Strict and Sectional

Each domain's tables are owned by exactly one service (and that service's repository). **No other service may query, insert, update, or delete rows in tables it does not own.** If `CampService` needs person/profile display data, it calls `IUserServiceRead` - it does not query the `profiles` table, instantiate `IUserRepository`, or access the Users/Profile read-model cache directly.

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
// Humans.Application/Interfaces/Repositories/IUserRepository.Profiles.cs
public partial interface IUserRepository
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

Services are cached by **wrapping them in a decorator**, not by inlining `IMemoryCache` calls. The decorator is registered via a keyed-inner + factory-forward pattern: the inner is registered against `IUserService` under a key (`AddKeyedScoped<IUserService, UsersUserService>(CachingUserService.InnerServiceKey)`); the decorator is registered as itself and `IUserService` is forwarded to it. See `Humans.Web/Extensions/Sections/UsersSectionExtensions.cs` for the canonical wiring. Callers inject `IUserService` and get the cached version transparently.

### 5a. Decorator Rules

1. **One decorator per service.** `CachingUserService : IUserService` wraps the real `UsersUserService`.
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
GovernanceDbContext                             [Infrastructure]
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
var users = await _userService.GetUserInfosAsync(userIds, ct);

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
- ❌ `CampMember.User`, `BoardVote.BoardMember`, etc.
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

    var previousState = profile.State;
    profile.State = ProfileState.Suspended;
    await _repo.UpdateAsync(profile, ct);   // business save first
    _store.Upsert(profile);

    await _auditLog.LogAsync(               // then audit (self-persisting)
        AuditAction.ProfileSuspended, nameof(User), userId,
        $"Suspended (was={previousState}): {reason}",
        actorId);
}
```

**Compound writes that must be atomic** (e.g., season rename + historical-name insert) belong in a single repository method that performs both mutations and one `SaveChangesAsync`. Do not orchestrate multiple repo calls in the service and hope partial failure doesn't strand rows.

If audit calls become noisy across many methods inside one service, the next evolution is **domain events** raised from the entity and handled in Infrastructure — not a decorator.

## 8. Table Ownership Map

Each section's service owns these tables. Cross-service access goes through the service interface, never through direct DB queries, never through another domain's repository or store.

Ownership is now physical as well as conventional: the map below is **per DbContext**, not per single model, and every table belongs to exactly one section context. Users/Identity and Profiles merged into `UsersDbContext` at peel 15, which also deleted the root `HumansDbContext` (framework-owned tables live in `SystemDbContext`). See [`data-model.md`](data-model.md#dbcontext-ownership) for the context-to-table listing.

| Section | Service(s) | Owned Tables |
|---------|-----------|--------------|
| **Profiles** | `ProfileService`, `ContactFieldService`, `ContactService`, `UserEmailService`, `CommunicationPreferenceService` | `profiles`, `profile_languages`, `contact_fields`, `user_emails`, `communication_preferences`, `volunteer_history_entries` |
| **Users/Identity** | `UserService`, `AccountProvisioningService`, `UnsubscribeService`, `AccountMergeService`, `DuplicateAccountService`, `ExternalLoginService` | `users`, `user_claims`, `user_logins`, `user_tokens`, `roles` (legacy), `user_roles` (legacy), `role_claims` (legacy), `event_participations`, `account_merge_requests` — the ASP.NET Identity tables are renamed to the PostgreSQL snake_case convention in `UsersDbContext.OnModelCreating`, which also carries the Identity base since peel 15 |
| **Teams** | `TeamService`, `TeamPageService` (composer — owns no tables) — G5 project `Humans.Teams`, published via `Humans.Teams.Contracts` | `teams`, `team_members`, `team_join_requests`, `team_join_request_state_history`, `team_role_definitions`, `team_role_assignments`, `team_early_entry_grants` |
| **Auth** | `RoleAssignmentService` (G5 project `Humans.Auth`, published via `Humans.Auth.Contracts`), `MagicLinkService` (owns no tables; `Humans.Auth.Services`, published via `Humans.Auth.Contracts.IMagicLinkService`) | `role_assignments` |
| **Governance** | `ApplicationDecisionService` | `applications`, `application_state_history`, `board_votes` |
| **Consent** | `LegalDocumentService`, `LegalDocumentSyncService`, `ConsentService` (`src/Sections/Humans.Consent`) | `legal_documents`, `document_versions`, `consent_records` |
| **Onboarding** | `OnboardingService` (intake funnel). `HumanLifecycleService` (suspend/unsuspend state-machine) moved to **Users** at G5 lane 4b-2d — Peter, 2026-08-14: membership machinery is Users, never Governance — and is published via `Humans.Users.Contracts` | *(no owned tables — orchestrator over Profiles, Consent, Teams, Governance)* |
| **Camps** | `CampService`, `CampRoleService`, `CampContactService` | `camps`, `camp_seasons`, `camp_members`, `camp_images`, `camp_historical_names`, `camp_settings`, `camp_role_definitions`, `camp_role_assignments` |
| **Containers** | `IContainerService` (G5 project `Humans.Containers`; implemented by the section-internal `Service`) | `containers`, `container_placements` |
| **City Planning** | `CityPlanningService` | `city_planning_settings`, `camp_polygons`, `camp_polygon_histories` |
| **Calendar** | `CalendarService` | `calendar_events`, `calendar_event_exceptions` |
| **Shifts** | `ShiftManagementService`, `ShiftSignupService`, `VolunteerTrackingService` | `rotas`, `shifts`, `shift_signups`, `event_settings`, `general_availability`, `volunteer_event_profiles`, `volunteer_build_statuses`, `shift_tags`, `volunteer_tag_preferences`, `rota_shift_tags` |
| **Cantina** | `CantinaRosterService` | *(no owned tables — reads the on-site cohort via `IShiftManagementService`, the active event via `IBurnSettingsService`, and dietary fields via `IUserServiceRead`)* |
| **Budget** | `BudgetService` | `budget_years`, `budget_groups`, `budget_categories`, `budget_line_items`, `budget_audit_logs`, `ticketing_projections` |
| **Expenses** | `ExpenseReportService` | `expense_reports`, `expense_lines`, `expense_attachments`, `holded_expense_outbox_events` |
| **Finance** | `IHoldedFinanceService` (G5 project `Humans.Finance`; implemented by the section-internal `Service`) | `holded_expense_docs`, `holded_category_map`, `holded_creditor_contacts`, `holded_doc_sync_state` |
| **Holded** | `IHoldedService` (G5 project `Humans.Holded`, published via `Humans.Holded.Contracts`; implemented by the section-internal `Service`) — the Holded API v2 ledger mirror, its reconciliation sweep, and the `/Holded` admin screen | `holded_ledger_lines`, `holded_accounts`, `holded_sync_states`, `holded_api_calls` |
| **Tickets** | `TicketQueryService`, `TicketSyncService`, `TicketTransferService` (G5 project `Humans.Tickets`, published via `Humans.Tickets.Contracts`; the vendor adapter is the separate `Humans.TicketTailor` project behind `ITicketVendorService`; the Tickets→Budget bridge `TicketingBudgetService` is **Budget**-owned — `src/Sections/Humans.Budget/Services/`) | `ticket_orders`, `ticket_attendees`, `ticket_sync_state`, `ticket_transfer_requests` |
| **Store** | `Service` (G5 project `Humans.Store`, internal — no public service interface; Store is self-contained and takes no cross-section calls into it) | `store_products`, `store_orders`, `store_order_lines`, `store_payments`, `store_invoices`, `store_treasury_sync_state` |
| **Scanner** | none (G5 project `Humans.Scanner`; no business logic and no `Services/` folder — its controller reads via `ITicketServiceRead`) | none |
| **Gate** | `GateService` (G5 project `Humans.Gate`) | `gate_scan_events`, `gate_settings`, `gate_staff_pins` |
| **Campaigns** | `CampaignService` | `campaigns`, `campaign_codes`, `campaign_grants` |
| **Google Integration** | `GoogleWorkspaceSyncService` (the production `IGoogleSyncService`; `StubGoogleSyncService` stands in when no credentials are configured), `GoogleAdminService`, `GoogleWorkspaceUserService`, `DriveActivityMonitorService`, `SyncSettingsService`, `EmailProvisioningService`, `TeamResourceService` (the Teams↔Drive linking surface — lives in `Humans.Application.Services.GoogleIntegration`, not Teams) | `sync_service_settings`, `google_sync_outbox`, `google_resources` |
| **Email** | `EmailOutboxService`, `OutboxEmailService`, `EmailService` | `email_outbox_messages` (reads the `IsEmailSendingPaused` flag via `ISystemSettingsService`) |
| **System Settings** | `ISystemSettingsService` (G5 project `Humans.SystemSettings`; implemented by the section-internal `Service`) | `system_settings` (cross-cutting key/value store; consuming sections read/write via `ISystemSettingsService`) |
| **Mailer** | `MailerImportService`, `MailerLiteClient` | _(no owned tables — MailerLite is read-only; classifier writes through other sections' services)_ |
| **Feedback** | `FeedbackService` | `feedback_reports`, `feedback_messages` |
| **Issues** | `IssuesService` | `issues`, `issue_comments` |
| **Notifications** | `NotificationService`, `NotificationInboxService`, `NotificationMeterProvider` | `notifications`, `notification_recipients` |
| **Audit Log** | `AuditLogService` (G5 project `Humans.AuditLog`, published via `Humans.AuditLog.Contracts`) | `audit_log` |
| **Agent** | `AgentService`, `AgentSettingsService`, `AgentPromptAssembler`, `AgentToolDispatcher`, `AgentUserSnapshotProvider`, `AgentAbuseDetector`, `AnthropicClient`, `AgentConversationRetentionJob` (G5 project `Humans.Agent`) | `agent_conversations`, `agent_messages`, `agent_settings` |
| **Event Guide** | `Service` (G5 project `Humans.Events`; implements the section-internal `IEventService`, with `IEventServiceRead` published cross-section via `Humans.Events.Contracts`) | `events`, `event_guide_settings`, `event_categories`, `event_venues`, `event_moderation_actions`, `event_favourites`, `event_preferences` |
| **Survey** | `SurveyService` (G5 project `Humans.Surveys`) | `surveys`, `survey_questions`, `survey_question_options`, `survey_invitations`, `survey_responses`, `survey_answers` |

**`system_settings` is owned by the System Settings section** (G5 project `Humans.SystemSettings`; its internal `Service` / `Repository`) and exposed cross-section via `ISystemSettingsService`; consuming sections read/write their keys through it rather than touching the table directly. Currently-tracked keys: `IsEmailSendingPaused` (Email's send-pause flag), `DriveActivityMonitor:LastRunAt` (Google Integration's drive-monitor last-run).

**Admin is not a section.** The `/Admin/*` controllers are a nav holder for admin-only actions that live in other sections (outbox pause in Email, suspend/purge in Profiles, account merge in Users, sync settings in Google Integration, role assignments in Auth, legal-doc management in Consent). Services referenced from `AdminController` belong to their owning section, not to Admin.

See [`docs/architecture/dependency-graph.md`](dependency-graph.md) for the full directed dependency graph with current vs target edges and circular dependency analysis.

### 8a. User-Scoped Sections Must Contribute to the GDPR Export

Every section whose owned tables hold per-user rows MUST implement `IUserDataContributor` (`Humans.Gdpr.Contracts`) so the GDPR Article 15 data export (`IGdprExportService`) can assemble a complete document without any cross-section database reads. The orchestrator injects `IEnumerable<IUserDataContributor>`, fans out one call per contributor, and merges the returned slices into the JSON document the user downloads from `/Profile/Me/DownloadData`.

Adding a new user-scoped section to §8 above requires four coupled steps — all four, in any order, before the PR can land:

1. Add the new section-name constants to `GdprExportSections` (`Humans.Gdpr.Contracts`).
2. Make the owning service implement `IUserDataContributor` and return its own slice. A contributor reads only its own section's tables — cross-section data flows through other contributors, not through `Include` chains. Collection slices must always return the shaped list (empty when the user has no records); `null` data is reserved for single-object sections whose entity doesn't exist for this user.
3. Register the service where its section's DI already lives — `src/Humans.Web/Extensions/Sections/<Section>SectionExtensions.cs` for a section still in the Shell, the section's own `Section.cs` for a G5 project — using the forwarding pattern so the same scoped instance serves both the primary interface and `IUserDataContributor`:

   ```csharp
   services.AddScoped<MyNewService>();
   services.AddScoped<IMyNewService>(sp => sp.GetRequiredService<MyNewService>());
   services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<MyNewService>());
   ```

4. Add the concrete service type to `GdprExportDependencyInjectionTests.ExpectedContributorTypes` — the enforced view of the §8 rows that hold user-scoped data.

The architecture test suite in `GdprExportDependencyInjectionTests.cs` enforces every step automatically:

- `EverySectionServiceMustImplementIUserDataContributor` — each listed type really implements the interface.
- `EveryIUserDataContributorInInfrastructureIsExpected` — every `IUserDataContributor` found via reflection is in the expected list (catches new contributors that forget the list). **It reflects over `Humans.Infrastructure` and `Humans.Application` only**, so a contributor declared inside a G5 section assembly is invisible to this scan and is caught only by the registration-walking tests below.
- `EveryExpectedContributorIsRegisteredInInfrastructure` — every listed type has a DI registration.
- `EveryIUserDataContributorFactoryForwardsToAnExpectedConcreteType` — each forwarding factory resolves to a distinct expected concrete type, so a duplicated or mis-wired factory can't silently drop a section.
- `GdprExportServiceIsRegistered` — the orchestrator itself is registered.

**Uncaught case (convention, not test):** if a new user-scoped section is added to §8 but its owning service never implements `IUserDataContributor` at all, reflection finds nothing to enumerate and the suite passes vacuously. The gap is wider for a G5 section, whose assembly the reflection scan does not visit at all. The four-step list above is the prose-level guardrail — reviewers should reject any §8 edit that adds a user-scoped row without touching `ExpectedContributorTypes` in the same PR.

**Provenance FKs are not user-scoped data.** A section's tables can carry user FK columns that record *who performed an action* (`AddedByUserId`, `RecordedByUserId`, `IssuedByUserId`, etc.) without the section's data being user-scoped. The rule of thumb: if you delete the user, do their rows go with them, or do they belong to a different aggregate (a camp, a team, an event) and merely lose their actor reference? If the latter, the section is not user-scoped — the FKs are provenance and belong to audit-style "what happened" data, not to the user's "what's mine" export. The **Store** section is the canonical example: store orders, lines, payments, and invoices belong to a camp season; the user FKs only record which lead clicked which button. Store data flows out of GDPR export through the audit log, not through a Store-section contributor.

See [`docs/features/global/gdpr-export.md`](../features/global/gdpr-export.md) for the JSON output shape, the contributor table, and a worked example of adding a new section.

### 8b. Cross-Section Fanout — Contributor Pattern

§8a's GDPR export is one instance of a recurring shape: an **orchestrator that owns no tables, injects `IEnumerable<IContributor>`, calls only the contributor interface, and merges the returned slices** — never reaching into another section's repository or running cross-section `Include` chains. Sections opt in by implementing the contributor interface; each contributor reads only its own owned tables, and cross-section names flow through the existing `I{Section}ServiceRead` surfaces. The orchestrator iterates sequentially and never appears in §8's table-ownership map. **The original reason for iterating sequentially is obsolete** — it was "the contributors share the scoped `HumansDbContext`, which is not thread-safe", but since the per-section split (nobodies-collective/Humans#858) contributors such as `ExpenseReportService`, `SurveyService`, `AgentService`, `EventService` and `HoldedFinanceService` read through their own `IDbContextFactory<TContext>` against separate contexts, and independent factory-created contexts are safe to use concurrently (EF's restriction is on concurrent operations against the *same* instance). Sequential iteration is now a consistency and simplicity choice, not a correctness requirement.

Three fanouts exist today:

| Orchestrator | Contributor interface | Sections that opt in | Merged result |
|--------------|----------------------|----------------------|---------------|
| `IGdprExportService` | `IUserDataContributor` (`Humans.Gdpr.Contracts`) | every user-scoped §8 section (see §8a) | GDPR Article 15 export document |
| `IICalFeedService` (`ICalFeedService`) | `ICalendarFeedContributor` (`Humans.Calendar.Contracts`) | `EventService` (Event Guide), `ShiftSignupService` (Shifts) | a user's personal iCal `VCALENDAR` of `CalendarFeedItem` rows |
| `IEarlyEntryService` (`EarlyEntryService`) | `IEarlyEntryProvider` (`Humans.EarlyEntry.Contracts`) | Camps, Shifts, Teams | a user's assembled early-entry grants |

Each contributor wires up with the same forwarding registration as §8a, so one scoped instance serves both the section's primary interface and the contributor interface:

```csharp
services.AddScoped<ICalendarFeedContributor>(sp => sp.GetRequiredService<EventService>());
```

**Invariant:** a new cross-section need of this shape — assembling per-user (or per-aggregate) rows from several sections into one document — MUST follow the contributor pattern (orchestrator owning no tables, fanning out over a contributor interface that sections opt into) rather than the orchestrator making direct cross-section service calls section-by-section. Direct calls couple the orchestrator to every contributing section and bypass the opt-in registration that keeps the fanout list honest.

**Which instance a section forwards** to a contributor interface follows where that contributor's read is actually served from, not a fixed lifetime. Forward the caching decorator only when the fanout read comes off the section's cached projection — `CampsSectionExtensions` registers `AddSingleton<IEarlyEntryProvider>(sp => sp.GetRequiredService<CachingCampService>())` because `CachingCampService.GetEarlyEntriesAsync` projects entirely from the cached `CampSettingsInfo` + `CampInfo` snapshot. Otherwise forward the inner scoped service: `Humans.Teams`' own `Section.cs` and `ShiftsSectionExtensions` both register scoped providers, because `team_early_entry_grants` and the volunteer-tracking rows are read from the repository per call and are not in `TeamInfo`. The orchestrator is keyed-scoped so it resolves either lifetime; registering a decorator that does not itself serve the read buys no caching and only adds a hop.

### 8c. Special-Category (GDPR Art. 9) Fields Are Guarded by Convention, Not by Type

`Profile.MedicalConditions` is special-category data under GDPR Article 9, and it is deliberately a plain `string?` that rides on the cached `UserInfo` / `ProfileInfo` read model like any other profile field. That means **any code holding a `UserInfo` already has the medical text in memory** — nothing at the type level stops it being serialized out.

What keeps it contained is a convention with three parts, and all three are load-bearing:

1. Outbound DTOs omit the field by construction (`RosterPersonDto`, `DailyPersonRowDto` — both carry an XML-doc note saying why).
2. Each such surface pins the omission with a test — e.g. `CantinaRosterServiceTests.GetWeeklyRoster_MedicalConditionsNeverInDto` reflects over the DTO to assert the property does not exist, *then* serializes a result containing medical text to JSON and asserts it does not appear.
3. Write paths document the caller's obligation (`IUserService`, `UserProfileCommands`: "MedicalConditions is GDPR Art. 9 — callers must already have verified the caller is allowed"), and the section docs carry the matching negative access rules (department coordinators and VolunteerCoordinator cannot view medical data).

**A wrapper type was considered and rejected.** The alternative was a `Sensitive<T>`-style wrapper that forces the caller to present a policy token to read the value, which would turn an accidental leak into a compile error rather than a review miss. It was rejected as over-engineering at this scale — but it is the designated fallback: **if medical data ever does leak through a serializer, the fix is the wrapper type, not another one-off test.** Adding `MedicalConditions` (or any future Art. 9 field) to a DTO therefore requires the omission test on that surface, and a leak is the trigger to escalate to type-level enforcement. There is no analyzer covering this today.

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
public class CampService(ICampRepository repo, IUserRepository userRepo) : ICampService { ... }

// WRONG — uses IDbContextFactory to query another domain's tables directly
public class CampService(ICampRepository repo, IDbContextFactory<UsersDbContext> factory) : ICampService
{
    public async Task<CampDetailDto> GetCampDetailAsync(Guid campId, CancellationToken ct)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var leadProfiles = await ctx.Profiles.Where(...).ToListAsync(ct); // ← Profile section's table
        ...
    }
}

// WRONG — direct DbContext access (impossible by project graph once migrated)
public class CampService(CampsDbContext db) : ICampService { ... }

// WRONG — cross-domain .Include
var camp = await db.Camps.Include(c => c.Leads).ThenInclude(l => l.Profile).FirstAsync(...);
```

### Rules

- Cross-service calls are **by id or small parameter set** — `GetByUserIdAsync(Guid)`, `GetByIdsAsync(IReadOnlyCollection<Guid>)`. Never a raw predicate that pushes another domain's schema knowledge into the caller.
- Services return **DTOs or domain entities** — never `IQueryable`, never cross-domain entity graphs.
- Circular dependencies are resolved by extracting a shared interface or using an orchestrating service (e.g., `OnboardingService` orchestrates Profiles + Legal + Teams).

## 10. Cross-Cutting Services

Some services are used across all sections. They own their own tables but are injected everywhere. These are **Crosscuts** ([`CONTEXT.md`](../../CONTEXT.md)): they own their own data but carry no section-specific logic and **must not call into another section** — when a crosscut operation needs cross-lane data, an **Orchestrator** gathers it and calls the crosscut *with* it (see [`memory/architecture/crosscut-purity.md`](../../memory/architecture/crosscut-purity.md)).

| Service | Purpose | Owned Tables |
|---------|---------|--------------|
| `RoleAssignmentService` | Temporal role memberships (Auth section) — the gateway for all role queries | `role_assignments` |
| `AuditLogService` | Append-only audit trail for user actions and sync operations | `audit_log` |
| `EmailOutboxService` | Queue and track transactional emails | `email_outbox_messages` |
| `NotificationService` | In-app notifications (G5 project `Humans.Notifications`) | `notifications`, `notification_recipients` |

These are standalone services, not embedded in section services. Any service or controller can call `IAuditLogService.LogAsync(...)` to record an action, or `IRoleAssignmentService.HasActiveRoleAsync(...)` to check a role. They follow the same repository + optional §15 caching-decorator pattern as any other service — `RoleAssignmentService` has one (`CachingRoleAssignmentService`); `AuditLogService`, `EmailOutboxService` and `NotificationService` do not.

## 11. Authorization Pattern

Authorization uses **ASP.NET Core resource-based authorization** — one pattern, everywhere.

### How it works

Controllers call `IAuthorizationService.AuthorizeAsync(User, resource, requirement)`. Authorization handlers contain the logic. Services are auth-free — they trust the caller except for the narrow full-Admin destructive-delete exception below.

```csharp
// Controller — authorize, then call service
var authResult = await _authorizationService.AuthorizeAsync(User, category, BudgetOperationRequirement.Edit);
if (!authResult.Succeeded) return Forbid();
await _budgetService.DeleteLineItemAsync(id);
```

### Existing handlers

| Handler | Requirement | Resource | Purpose |
|---------|-------------|----------|---------|
| `TeamAuthorizationHandler` (`Humans.Teams.Authorization`) | `TeamOperationRequirement` | `Team` | Coordinator/manager/admin checks |
| `BudgetAuthorizationHandler` (`Humans.Budget.Authorization`) | `BudgetOperationRequirement` | `BudgetCategorySnapshot` | Finance role + coordinator checks |
| `CampAuthorizationHandler` | `CampOperationRequirement` | `Camp` | Lead/CampAdmin checks |
| `RoleAssignmentAuthorizationHandler` (`Humans.Auth.Authorization`) | `RoleAssignmentOperationRequirement` | `string` (role name) | Who can assign which roles |
| `HumanAdminOnlyHandler` | `HumanAdminOnlyRequirement` | — | Admin profile operations |
| `UserEmailAuthorizationHandler` | `UserEmailOperationRequirement` | `Guid` (owning user id) | Who may manage another human's email addresses |
| `ContainerAuthorizationHandler` (`Humans.Containers.Authorization`) | `ContainerOperationRequirement` | `ContainerAuthorizationTarget` | Container placement/edit rights |
| `ExpenseReportAuthorizationHandler` (`Humans.Expenses.Authorization`) | `ExpenseReportOperationRequirement` | `ExpenseReportDto` | Submitter / approver / finance checks |
| `IssuesAuthorizationHandler` (`Humans.Issues.Authorization`) | `IssuesOperationRequirement` | `IssueDetail` | Reporter / assignee / admin checks |
| `OrderAuthorizationHandler` (`Humans.Store.Authorization`) | `OrderOperationRequirement` | `OrderDto` / `OrderCreateContext` / `OrderLineContext` | Camp-lead, coordinator, and store-admin checks (implements `IAuthorizationHandler` directly to span the three resource shapes) |

### Rules

- **No `isPrivileged` booleans.** Don't pass auth decisions as parameters to services. If the controller maps it wrong, the service silently does the wrong thing.
- **No inline `IsInRole` chains in controllers** for resource-scoped checks. Use the handler. Simple route-level role gates use `[Authorize(Policy = PolicyNames.X)]` — never raw `[Authorize(Roles = "...")]` strings. Views use the `authorize-policy="PolicyName"` tag helper for element-level visibility; reusable views/components that need conditional logic inject `IAuthorizationService` (see `auth-in-views-self-resolving` memory atom).
- **Services are auth-free by default.** They don't check roles, don't inject `IHttpContextAccessor`, don't receive boolean privilege flags. Authorization happens before the service is called.
- **Exception: full-Admin destructive deletes.** Application services may inject `IAdminAuthorizationService` and call `RequireCurrentUserIsAdminAsync` only for methods whose operation permanently deletes data or performs a destructive reset/delete cleanup, and whose authorization rule is exactly "must hold the full `Admin` role." The controller/action must still carry the matching `[Authorize(Policy = PolicyNames.AdminOnly)]` or stricter route-level guard. Do not use this exception for resource-scoped auth, read paths, ordinary edits, privilege flags, or direct `IHttpContextAccessor` access.
- **New sections need a handler.** When adding a new section with resource-scoped auth, add a `*OperationRequirement` + `*AuthorizationHandler` pair. Don't invent a new pattern.

### Tombstone: do not push authorization into services

Two prior attempts shipped service-layer authorization and both produced startup crashes:

- **PR #210 (role assignment, commit `225ac14`)** — `TeamService → RoleAssignmentService → IAuthorizationService → TeamAuthorizationHandler → ITeamService` formed a DI cycle that crashed DI validation on startup. The hot-fix made `TeamAuthorizationHandler` lazily resolve `ITeamService` via `IServiceProvider` — a service-locator escape hatch that hides the cycle from the validator rather than removes it.
- **PR for Google sync (`1626098`)** — reverted in `bbbe508` for the same cycle.
- **Budget mutations (#420)** — drafted, closed as *won't do* on 2026-04-15 once the pattern was retired.

The unwind PR removed `ClaimsPrincipal` parameters from `IRoleAssignmentService`, moved the `AuthorizeAsync` call to `ProfileController`, deleted `SystemPrincipal`, and removed the `IServiceProvider` hack. `RoleAssignmentAuthorizationHandler` and `RoleAssignmentOperationRequirement` remain — they are invoked from controllers, which is the correct pattern.

**Do not reopen this without updating §11 first.** Service-layer auth gives no real defence-in-depth here: one UI, no public API, background jobs are trusted server code, controllers are the only human-facing mutation path. Resource-based handlers belong; in-service `AuthorizeAsync` does not.

## 12. Immutable Entity Rules

Some entities are append-only. They have database triggers or application-level enforcement preventing UPDATE and DELETE.

| Entity | Table | Constraint |
|--------|-------|------------|
| `ConsentRecord` | `consent_records` | DB triggers block UPDATE and DELETE |
| `AuditLogEntry` | `audit_log` | DB triggers block UPDATE and DELETE |
| `BudgetAuditLog` | `budget_audit_logs` | Append-only by convention |
| `CampPolygonHistory` | `camp_polygon_histories` | Append-only by convention |
| `ApplicationStateHistory` | `application_state_history` | Append-only by convention |
| `TeamJoinRequestStateHistory` | `team_join_request_state_history` | Append-only by convention |

**Rule:** Never add UPDATE or DELETE logic for append-only entities. New state = new row. Repository interfaces for these domains expose `AddAsync` and `GetX` methods but no `UpdateAsync` or `DeleteAsync`.

The two trigger-backed tables now live in peeled contexts, and their `prevent_*_update` / `prevent_*_delete` triggers were re-issued in the peel baselines: `consent_records` in `src/Sections/Humans.Consent/Data/Migrations/…_BaselineLegal.cs` (`LegalDbContext`), `audit_log` in `src/Sections/Humans.AuditLog/Data/Migrations/…_BaselineAuditLog.cs` (`AuditLogDbContext`). A section peel must carry its triggers forward — dropping them silently converts a DB-enforced invariant into a convention.

**Merge chain-follow on append-only reads.** Account merge folds a source User into a target by re-FKing live rows, but append-only history (`audit_log`, `consent_records`, `budget_audit_logs`, plus per-user Expenses reads) stays at the source by design. Per-user reads of these tables therefore union the source-tombstone ids via `IUserService.GetMergedSourceIdsAsync(targetId)` before querying. That lookup is **cache-backed only** — it scans the cached `UserInfo` snapshot for `MergedToUserId` tombstones in `CachingUserService`; the inner `UserService.GetMergedSourceIdsAsync` throws `NotSupportedException`, so callers must depend on the cached service. Redirect-follow paths that walk `MergedToUserId` (Google Workspace/Group sync, Mailer import, attendee contact import) must follow the chain transitively (A→B→C when B is later merged into C) and **cap the walk at 16 hops** to defuse a circular-merge anomaly rather than spin forever.

## 13. Google Resource Ownership

All Google Drive resources are on **Shared Drives** (never My Drive). Google integration is managed by dedicated services:

- `GoogleWorkspaceSyncService` — the production `IGoogleSyncService`; syncs team membership to Drive/Groups (`StubGoogleSyncService` replaces it when no service-account credentials are configured)
- `GoogleAdminService` — admin operations on Google Workspace
- `GoogleWorkspaceUserService` — user provisioning
- `SyncSettingsService` — per-service sync mode (None/AddOnly/AddAndRemove)

**No other service queries Google resources directly.** If a section needs to know about a team's Google resources, it asks `ITeamResourceService`. The section's `internal` `GoogleIntegrationDbContext` and `IGoogleResourceRepository` put the table out of reach of other assemblies; HUM0008/HUM0009/HUM0025 cover controllers, services and second repositories inside it.

## 14. DTO and ViewModel Boundary

- **Domain entities** live in `Humans.Domain`. They are mutable, have identity, and carry invariants. Entities never reference EF types.
- **DTOs** live in `Humans.Application`. They are read-optimized shapes for specific use cases (admin tables, API responses, view data). Services return DTOs when the shape is call-specific and the entity does not match; they return entities when the caller needs the full aggregate.
- **ViewModels** live in `Humans.Web` (or are inlined in controllers). Controllers map DTOs or entities to view models for Razor. The section-agnostic ones — the generic table models and `PagerViewModel` — live in `Humans.UI.Models` (§1).
- **Domain entities should not leak into Razor views** when a DTO would provide better separation. Simple 1:1 cases are acceptable; anything that would have required `.Include` for navigation in the old model is not.
- **View components** are part of the Web layer — `Humans.Web`, or `Humans.UI` for the section-agnostic ones. They call services, not repositories or stores.

## 15. Profile Section Pattern — Canonical Cache-Collapse Architecture

The Profile section is the **reference implementation** for the target caching architecture (completed in PR #235, 2026-04-20). All future section migrations that warrant a caching layer follow this pattern. The original §4/§5 store-and-decorator spec was superseded during Profile migration; §15 documents the final, code-verified shape.

> **Governance** previously used the §4/§5 pattern but dropped its §15 projection layer entirely (issue #533): the section is low-traffic enough that DB reads per request are fine, so `ApplicationDecisionService` talks directly to `IApplicationRepository` and invalidates cross-cutting caches (`INavBadgeCacheInvalidator`, `INotificationMeterCacheInvalidator`, `IVotingBadgeCacheInvalidator`) inline after successful writes. (One short-TTL exception, added in #1010: the per-board-member unvoted-application count — read on every page load via the nav badge — is cached inline via `IMemoryCache` with a 2-min TTL, the same request-acceleration pattern as the other nav-badge counts; it is not a §15 projection.) Not every section needs §15 — reach for it only when traffic or bulk-read patterns justify an in-memory projection.

### 15a. Four-Layer Stack

```
Controller / View Component
  ↓ I<Section>Service                               [Application interface]
Caching<Section>Service   (optional decorator)      [Infrastructure — Singleton]
  ↓ keyed resolve via IServiceScopeFactory
<Section>Service          (inner, keyed)            [Application — Scoped]
  ↓ repositories + cross-section service interfaces
<Section>Repository                                 [Infrastructure — Singleton via IDbContextFactory]
  ↓ IDbContextFactory<TContext>                     [Singleton — the section's own context]
```

The decorator is "optional" in the sense that removing it leaves the system fully functional — the inner service implements every method against the DB, except declared cache-only reads (§15c). The decorator is a pure performance optimization layered on top.

### 15b. Repository Rules

Repositories are registered as **Singleton** because they inject `IDbContextFactory<TContext>` — the section's own `<Section>DbContext` — rather than a context directly. Every method creates and disposes its own short-lived context:

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

The application service (`UserService`, `ProfileService`, `ContactFieldService`, etc.) lives in its section's `Humans.Application.Services.*` namespace. It:

- Injects repository interfaces, never `DbContext`.
- Never imports `IMemoryCache` or any caching abstraction — it is completely cache-unaware.
- When the section has a caching decorator: registered as **Scoped** and **keyed** under that decorator's `InnerServiceKey` (e.g., `CachingUserService.InnerServiceKey` = `"user-inner"`) so the Singleton decorator can resolve fresh instances per-call without self-resolution.
- Implements **every** read method against the DB, with one carve-out: **cache-only reads** — projections served from the decorator's warmed snapshot with no repository equivalent (relevance-ranked search: `UserService.SearchUsersAsync`, `TeamService.SearchAsync`, `CampService.SearchAsync`; merge-tombstone scan: `UserService.GetMergedSourceIdsAsync`, §12) — have no DB implementation. For those, the inner method throws `NotSupportedException` so a DI mis-registration fails loudly instead of silently bypassing the cache. The base service must **never** return empty results for a method "so the decorator can override" — implement it against the DB, or throw.
- Sub-aggregates belong to the parent section (CV entries are written through `IProfileService.SaveCVEntriesAsync`; the parent repository owns the reconcile logic).

### 15d. Caching Decorator Rules

A section's caching decorator is a **Singleton** that inherits (or composes) `TrackedCache<TKey, TValue>` — a primitive owning a private `ConcurrentDictionary<TKey, TValue>` keyed by the aggregate's identity, with hit/miss/invalidation counters and built-in startup-warmup machinery. The dict persists across HTTP requests. There is no separate store interface, no store class, no `IMemoryCache` for canonical domain data.

**Live example:** `CachingUserService` — Singleton, inherits `TrackedCache<Guid, UserInfo>`, owns the unified User+Profile read-model spanning 8 contributing tables. See `src/Humans.Infrastructure/Services/Users/CachingUserService.cs` for the canonical pattern (constructor passing `warmOnStartup: true`, scope-factory-resolved inner, `RefreshEntryAsync`, `WarmAllAsync` override, `IUserInfoInvalidator` aliasing).

Key rules the example demonstrates:

- Caching decorators must not reference repository types directly (HUM0020). Cache misses, startup warmup, and per-key refreshes resolve the keyed inner service via `IServiceScopeFactory`; repository access stays inside the scoped application service.
- Scoped dependencies (the inner service) are resolved per-call via `IServiceScopeFactory.CreateAsyncScope()` — never captured in a Singleton field.
- Reads: dict hit returns synchronously via `TrackedCache.TryGet`; miss falls through `GetAsync` → `LoadRowAsync` (override) and populates the dict. Load-all reads `await EnsureWarmedAsync(ct)`, which drives `WarmAllAsync` on demand if the cache is cold and is a no-op once warmed.
- Writes: delegate to the inner service, then replace the affected entry via `Replace(key, value)` (caller-computed value) or `ReplaceAsync(key, ct)` (reloads via `LoadRowAsync`). Bare `Invalidate(key)` is for lazy-per-key caches or for tombstoning a row whose source has been confirmed deleted — on a warmed cache, removing without replacing breaks the all-rows invariant.
- Warming: `TrackedCache` itself implements `IHostedService`. Register the decorator as a hosted service (`services.AddHostedService(sp => sp.GetRequiredService<CachingFoo>())`) — startup triggers `WarmAllAsync` via `EnsureWarmedAsync`, which flips the warmed flag on success. No separate `*WarmupHostedService` class. Startup-warmup failures are recovered transparently: the next load-all read drives warmup on demand.
- Composing decorators that hold multiple inner `TrackedCache` instances (e.g. `CachingShiftViewService`) implement `IHostedService` directly with the same shape.
<!-- wheat: docs/superpowers/plans/2026-05-25-early-entry-roster.md §Task 5 -->
- **Caching a legitimate negative result needs manual `TryGet`/`Set`, not `GetAsync`.** `TrackedCache<TKey,TValue>.GetAsync` only calls `Set` when `LoadRowAsync` returns non-null, so it never caches a `null`/not-found outcome — every miss re-triggers the loader. A decorator whose value type is itself nullable, and where "no result" is a real cacheable answer, must call `TryGet` on read and `Set` explicitly, storing the null. `CachingEarlyEntryService.GetForUserAsync` is the live example: `if (TryGet(userId, out var cached)) return cached;` then `Set(userId, result)` where `result` may be null.

### 15e. Invalidator — One-Way Cross-Section Signal

Each cached section exposes a one-method invalidator interface (e.g., `IUserInfoInvalidator`):

```csharp
Task InvalidateAsync(Guid userId, CancellationToken ct = default);
```

Implemented by the section's caching decorator. External sections inject the invalidator when their writes make the cached view stale. The decorator reloads-or-removes the entry. External code never mutates the dict directly.

**CRITICAL:** the invalidator must resolve to the **same Singleton instance** as the section's read interface. Both registrations point to the single decorator. If two instances were created, the dict would diverge and invalidations would be silently lost.

### 15f. Canonical Read-Model Naming

| Type | Name |
|------|------|
| Canonical section read model | `<Section>Info` (e.g., `UserInfo`, `TeamInfo`). |
| Read method | Match the read model name when returning one item (e.g., `GetUserInfoAsync`, `GetTeamAsync`). Plural collection methods may use the natural plural (e.g., `GetTeamsAsync`). Load-all methods used by decorators must still be normal service/read-interface methods (e.g., `GetAllEventInfosAsync`, `GetTicketOrderInfosAsync`), never cache-shaped `ForCache` helpers. |
| Invalidator interface | `I<SectionInfo>Invalidator` (e.g., `IUserInfoInvalidator`). |
| Sub-aggregate collections | Natural plural on the DTO (e.g., `VolunteerHistory`, `ContactFields`). |

Do not expose EF entity types from section service read APIs — the canonical
`Info` projection is the boundary.

Old names that no longer exist: `CachedProfile`, `IProfileStore`, `ProfileStore`, `ProfileStoreWarmupHostedService`, `IVolunteerHistoryService`, `VolunteerHistoryService`, `FullProfile`, `IFullProfileInvalidator`, `CachingProfileService`, `FullProfileWarmupHostedService`, `UserInfoWarmupHostedService`, `TeamsWarmupHostedService`.

### 15g. Known Deferrals

**§15 NEW-B — Cross-section ShiftAuthorization cache staleness.** **Resolved 2026-04-24** — `ShiftManagementService` implements `IShiftAuthorizationInvalidator`, exposed so Profile / User / Team writes call `Invalidate(userId)` directly. Cross-section dependency direction is Shifts → (nothing); callers push the signal, the cache owner stays decoupled.

**`OnboardingService.SetConsentCheckPendingIfEligibleAsync`** does not invalidate the `UserInfo` dict. Pre-existing behavior. To be addressed after the section merge settles.

### 15g-bis. Decorator-Integrity Hazards


Architecture tests verify *structure* (namespace, no-DbContext, no-IMemoryCache, required-constructor-deps). They do **not** verify that a caching decorator actually calls its private `RefreshEntryAsync` / dict upsert after delegating a write. The bug class "decorator forgot to invalidate after a write" is silent — stale data persists until process restart, with no error and no timeout. Unit tests of the inner service won't catch it because the inner service is correct in isolation.

Three guards mitigate this:

1. **Singleton identity.** The decorator and its invalidator interface must resolve to the *same* instance (§15e CRITICAL). If two instances exist, in-process invalidation routes to the wrong dict.
2. **Repository-bypass analyzer.** HUM0020 fails the build when a `Caching*Service` structurally references an `I*Repository` type. Decorators are transparent wrappers around the scoped service surface; they do not side-step that service for warmup or refresh.
3. **Integration test through the decorator.** For every cached section, a Testcontainers-backed test writes through the public service interface and reads back through the same interface, asserting the post-write read reflects the change. This is the only automated guard that catches a missing `RefreshEntryAsync` call.

For the Google Workspace section in particular, the cached projection isn't a table — it's reconciliation state (permission maps, group memberships). When that section gains a decorator, it needs an `IGoogleStateStore` shape rather than the entity-collection shape used by Profile/User.

### 15h. Migration Rules During the Transition

1. **New sections must comply.** New features use the §15 pattern from day one. Create the repository and decorator the same day you create the service — do not accrue "migrate later" debt.
2. **Touch-and-clean within scope.** When modifying an existing service for unrelated reasons, don't scope-creep into a full §15 migration. Fix the immediate issue; migrate the section in a dedicated session.
3. **Don't half-migrate a section.** If you start extracting a repository, finish the full stack in one session. A half-migrated section is worse than either extreme.
4. **EF migration review still applies.** Schema changes still go through the EF migration reviewer agent — the repository layer does not change what migrations look like.

### 15i. Known Current Violations (as of 2026-04-23)

- **1 business service** still lives in `Humans.Infrastructure/Services/`: `HumansMetricsService`. Its direct `DbContext` injection is gone — the 60-second gauge refresh resolves a scope via `IServiceScopeFactory` and reads through section read interfaces (`IUserServiceRead`, `ITeamServiceRead`, `IApplicationServiceRead`, `IMembershipCalculatorRead`, `ILegalDocumentSyncServiceRead`, …). The one residual violation is that it also resolves `IGoogleSyncOutboxRepository` directly instead of going through a service. All other files in that folder are connectors, stubs, or renderers that correctly stay in Infrastructure. Target: 0. Migration progress by section:
  - **Governance** — migrated 2026-04-15 in PR #503, own project since G5 (nobodies-collective/Humans#866) — `ApplicationDecisionService` and `GovernanceIndexService` live in `Humans.Governance.Services`, going through `IApplicationRepository` (`Humans.Governance.Data`).
  - **Profiles** — fully migrated 2026-04-20 in PR #235 — `ProfileService`, `ContactFieldService`, `ContactService`, `UserEmailService`, `CommunicationPreferenceService` now live in `Humans.Application.Services.Profiles`. (`VolunteerHistoryService` was folded into the profile methods on `IUserRepository`; it no longer exists as a separate service.)
  - **User** — migrated 2026-04-21 in PR #243 — `UserService` now lives in `Humans.Application.Services.Users`, goes through `IUserRepository`, and invalidates the UserInfo cache on writes. A §15 caching decorator now owns the `UserInfo` read model (`CachingUserService`, Singleton, keyed-inner `UserService`, `TrackedCache<Guid, UserInfo>`). Its warm path calls the normal `IUserService.GetAllUserInfosAsync` read surface; there is no cache-only repository helper.
  - **City Planning** — migrated 2026-04-22 in PR #543, own project since G5 (nobodies-collective/Humans#866) — `CityPlanningService` lives in `Humans.CityPlanning.Services`, goes through `ICityPlanningRepository` (`Humans.CityPlanning.Data`), and routes cross-section reads (camps, teams, profiles, users) through the owning service interfaces. Cross-domain `.Include(h => h.ModifiedByUser)` on `CampPolygonHistories` replaced with a batched `IUserServiceRead.GetUserInfosAsync` lookup. Option A (no decorator) — admin-facing, low-traffic.
  - **Audit Log** — migrated 2026-04-22 for issue #552, own project since G5 (nobodies-collective/Humans#866) — `AuditLogService` lives in `Humans.AuditLog.Services`, goes through `IAuditLogRepository` (`Humans.AuditLog.Data`), and persists each entry immediately (auto-saved per call) rather than relying on a shared-scope `SaveChanges` from the caller. The repository is append-only per §12 — no update or delete surface — enforced by `AuditLogArchitectureTests.IAuditLogRepository_HasNoUpdateOrDeleteMethods`. Option A (no decorator) — writes are scattered across every section (~96 call sites) and reads are admin-only.
  - **Budget** — migrated 2026-04-22 in issue #544; flipped to §15b Singleton + `IDbContextFactory` in issue #572 (2026-04-23); own project since G5 (nobodies-collective/Humans#866). `BudgetService` lives in `Humans.Budget.Services`, goes through `IBudgetRepository` (`Humans.Budget.Data`), and calls `ITeamService` (via two narrow new methods `GetBudgetableTeamsAsync` and `GetEffectiveBudgetCoordinatorTeamIdsAsync`) for cross-domain team reads. `IBudgetRepository` exposes atomic per-method operations — no public `SaveChangesAsync`, no `FindForMutationAsync`-returns-tracked surface. Composite operations (e.g., `CreateYearWithScaffoldAsync`, `SyncTicketingActualsAsync`, `RefreshTicketingProjectionsAsync`) perform all their work inside one repository method so the transaction boundary is preserved inside a single `DbContext` lifetime. No caching decorator — Budget is admin-only, low-traffic. `BudgetAuditLog.ActorUser` and `BudgetCategory.Team` cross-domain navs are `[Obsolete]`-marked; `BudgetLineItem.ResponsibleTeam` is still read by the Finance CategoryDetail view and deferred to a nav-strip follow-up.
  - **Campaigns** — migrated 2026-04-22 in issue #546, own project since G5 (nobodies-collective/Humans#866) — `CampaignService` lives in `Humans.Campaigns.Services`, goes through `ICampaignRepository` (`Humans.Campaigns.Data`), and routes cross-section reads via `ITeamService.GetTeamMembersAsync` and the new `IUserEmailService.GetNotificationTargetEmailsAsync(IReadOnlyCollection<Guid>)`. No caching decorator. Cross-domain navs `CampaignGrant.User` and `Campaign.CreatedByUser` are `[Obsolete]`-marked; the `TicketQueryService.GetCodeTrackingDataAsync` code-tracking page still reads `grant.User.DisplayName` inside `#pragma warning disable CS0618` blocks — migration follow-up lands when Tickets moves to Application.
  - **Camps** — migrated 2026-04-22 for issue #542 — `CampService` now lives in `Humans.Application.Services.Camps`, goes through `ICampRepository`, routes lead display-names through `IUserService`, and delegates filesystem I/O to the shared `IFileStorage` abstraction (key prefix `uploads/camps/...`). **T-06 (2026-05-16):** added the canonical §15 caching decorator (`CachingCampService`, Singleton, `Humans.Infrastructure.Services.Camps`) holding a `ConcurrentDictionary<Guid, CampInfo>` plus a single-slot `CampSettingsInfo`. The legacy short-TTL `IMemoryCache` 5-min keys (`camps_year_{N}`, `CampSettings`) are retired. Invalidation is decorator-only — every mutating `ICampService` method calls `InvalidateCampAsync` (or `InvalidateSettingsAsync`) inline after the inner write. Cross-table effects (notably `camp_members.HasEarlyEntry` driving `CampSeasonInfo.EeGrantedCount`) are handled the same way because the mutating method already knows the affected camp id. The no-bypass rule — only the inner `CampService` / `CampRoleService` may touch `ICampRepository` — is pinned by `CampsArchitectureTests.ICampRepository_HasNoUnexpectedConsumers`, retiring the earlier SaveChanges-interceptor backstop.
  - **Email** — migrated 2026-04-22 in PR for issue #548 — `EmailOutboxService` and `OutboxEmailService` now live in `Humans.Email.Services` (moved there from `Humans.Application.Services.Email` by the section's G5 project split, nobodies-collective/Humans#866), go through `IEmailOutboxRepository`, and route the verified-email-by-recipient lookup through `IUserEmailService` so `user_emails` stays behind its owning service. Two new connector abstractions (`IEmailBodyComposer`, `IImmediateOutboxProcessor`) keep `IHostEnvironment`/`EmailSettings` and Hangfire out of the Application layer. No caching decorator — outbox is a sequential queue drain, not a hot-path read shape.
  - **Feedback** — migrated 2026-04-22 in issue #549, own project since G5 (nobodies-collective/Humans#866) — `FeedbackService` lives in `Humans.Feedback.Services`, goes through `IFeedbackRepository` (`Humans.Feedback.Data`), and resolves reporter / assignee / resolver display names + effective email via `IUserService`, `IUserEmailService.GetNotificationTargetEmailsAsync`, and `ITeamService.GetTeamNamesByIdsAsync`. Cross-domain `.Include()` chains on `FeedbackReport.User`, `.ResolvedByUser`, `.AssignedToUser`, `.AssignedToTeam`, and `FeedbackMessage.SenderUser` are gone from the service (4→0). Those five navs are **deleted** (nobodies-collective/Humans#996) — Feedback-owned entities expose only their FK (`UserId`, `ResolvedByUserId`, `AssignedToUserId`, `AssignedToTeamId`, `SenderUserId`). EF configurations use the typed-FK form (`HasOne<User>().WithMany().HasForeignKey(f => f.UserId)`) — no nav reference, no `#pragma warning disable CS0618` left in the section. No caching decorator — Feedback is admin-review-only and low-traffic.
  - **Auth** — migrated 2026-04-22 in issue #551; `RoleAssignmentService` moved to the G5 project `Humans.Auth.Services` (nobodies-collective/Humans#866), and `MagicLinkService`, which owns no tables, followed it there at G5 lane 4b-2i. `RoleAssignmentService` goes through `IRoleAssignmentRepository` (`Humans.Auth.Data`), stitches assignee / creator display names via `IUserService`, and invalidates the per-user claims cache via `IRoleAssignmentClaimsCacheInvalidator` + the nav-badge cache via `INavBadgeCacheInvalidator`. `MagicLinkService` owns no tables; verified-email lookup routes through `IUserEmailService.FindVerifiedEmailWithUserAsync`, and Data-Protection / URL / replay / signup-cooldown state sits behind the section-internal `IMagicLinkUrlBuilder` + `IMagicLinkRateLimiter` (same shape as `CommunicationPreferenceService` + `IUnsubscribeTokenProvider`). Cross-domain navs on `RoleAssignment` (`User`, `CreatedByUser`) are `[Obsolete]`-marked and populated in-memory; controllers + the two daily-digest jobs that still read them do so under `#pragma warning disable CS0618`. No caching decorator — Auth writes are rare (handful of admin events per month).
  - **Teams** — fully migrated 2026-04-23 under umbrella #540 (sub-task #540a landed last). `TeamService` now lives in `Humans.Teams.Services` (G5, nobodies-collective/Humans#866), goes through `ITeamRepository` for all owned-table access, and routes every cross-section read through the public service interface (`IUserService` for display-name/profile-picture stitching, `IRoleAssignmentService` for role checks, `IShiftManagementService` for active-event + pending-signup-count lookups, `ITeamResourceService` for Drive resource summaries, `IEmailService`/`ISystemTeamSync` for out-of-band side effects). Cross-domain `.Include(...User)` chains in the service are gone (5→0). Cross-domain navs (`TeamMember.User`, `TeamJoinRequest.User`, `TeamJoinRequest.ReviewedByUser`, `TeamRoleAssignment.AssignedByUser`, `TeamJoinRequestStateHistory.ChangedByUser`) are **deleted** (nobodies-collective/Humans#996) — Teams-owned entities expose only their FK. EF configurations use the typed-FK form (`HasOne<User>().WithMany().HasForeignKey(tm => tm.UserId)`), and the `#pragma warning disable CS0618` blocks in `TeamController`, `TeamAdminController` and `TeamViewModels` are gone with them. The section now uses the §15 caching decorator (`CachingTeamService`, Singleton, keyed-inner `TeamService`, `TrackedCache<Guid, TeamInfo>`) and exposes both `ITeamService` and `ITeamServiceRead` from the same Singleton. Sub-tasks #540b (`TeamPageService`) and #540c (`TeamResourceService`) landed earlier in this umbrella. §15i transitional #2 (`TeamService → UserEmailService → AccountMergeService → TeamService`) is no longer active. `AccountMergeService` has since migrated to the **Users** section (`Humans.Application.Services.Users`, not `.Profile` as originally projected); the lazy `IServiceProvider` resolution for `IEmailService` inside `TeamService` remains in place to break the cycle with `UserService → TeamService → EmailService → UserEmailService → UserService`, so the cycle-break was not removed by that migration.
  - **Notifications** — migrated 2026-04-22 for issue #550, own project since G5 (nobodies-collective/Humans#866) — `NotificationService`, `NotificationInboxService`, and `NotificationMeterProvider` live in `Humans.Notifications.Services`, go through `INotificationRepository` (`Humans.Notifications.Data`) for their owned tables (`notifications`, `notification_recipients`), and reach every other section's data via its public service interface. `NotificationMeterProvider` was the biggest clean-up: it previously read `Profiles`, `Users`, `GoogleSyncOutboxEvents`, `TeamJoinRequests`, `TicketSyncStates`, and `Applications` directly; it now aggregates count methods added to `IProfileService`, `IUserService`, `IGoogleSyncService`, `ITeamService`, `ITicketSyncService`, and `IApplicationDecisionService`. `IRoleAssignmentService.GetActiveUserIdsForRoleAsync` was added so `NotificationService.SendToRoleAsync` doesn't query `role_assignments`. `CleanupNotificationsJob` also moves through the repository. Option A (no caching decorator) — dispatch is fire-and-forget and the unread-badge-count read is cached in-service via short-TTL `IMemoryCache` (`NotificationInboxService.GetUnreadBadgeCountsAsync`, 2-min TTL, invalidated on every mark-read/dismiss write); the `NotificationBellViewComponent` is now a thin pass-through (#954).
  - **Onboarding** — migrated in PR #285 (issue #553); moved to `src/Sections/Humans.Onboarding` at G5 (#866) — `OnboardingService` now lives in `Humans.Onboarding.Services`. It owns no tables and orchestrates Profiles, Legal, and Teams via their public service interfaces.
  - **Shifts** — fully migrated 2026-04-25 under umbrella #541. `ShiftManagementService` and `ShiftSignupService` live in `Humans.Application.Services.Shifts` (the former `GeneralAvailabilityService` was later deleted in #820), going through `IShiftManagementRepository` (which absorbed the former `IShiftSignupRepository`) and `IVolunteerTrackingRepository`. Cross-domain navs `Rota.Team`, `ShiftSignup.User` / `EnrolledByUser` / `ReviewedByUser`, `VolunteerEventProfile.User`, and `VolunteerTagPreference.User` are **deleted** — Shifts-owned entities expose only their FK (`TeamId`, `UserId`, etc.). EF configurations now use the typed-FK form (`HasOne<Team>().WithMany().HasForeignKey(r => r.TeamId)`) — no nav reference, no `#pragma warning disable CS0618` left in the section. Cross-domain `.Include(Rota.Team)` / `.Include(ShiftSignup.User)` / `.Include(ShiftSignup.ReviewedByUser)` inside `ShiftSignupRepository` are stripped — the repo now returns ID-only graphs and consumers resolve display fields via `ITeamService.GetTeamNamesByIdsAsync` (or `GetByIdsWithParentsAsync` when slug is needed) and `IUserServiceRead.GetUserInfosAsync`. The `ShiftAdminViewModel` carries an `IReadOnlyDictionary<Guid, User> Users` populated by the controller; `ShiftAdmin/Index.cshtml` reads from it via `Model.Users.GetValueOrDefault(...)`. The `ShiftManagementService.GetRotaByIdAsync` / `GetRotasByDepartmentAsync` / `GetShiftByIdAsync` in-memory nav-stitching is gone — those methods are now thin pass-throughs to the repo. §15 NEW-B (ShiftAuthorization cache invalidation on profile mutation) is resolved — `ShiftManagementService` implements `IShiftAuthorizationInvalidator`, and `UserService.AnonymizeExpiredAccountAsync` + `TeamService` role-assignment writes call `Invalidate(userId)` to clear the 60 s `shift-auth:{userId}` cache. **Option A** (no separate caching decorator): the section keeps the existing short-TTL `IMemoryCache` entries (`shift-auth:{userId}` at 60 s, coordinator-dashboard at 5 min) in-service per §15f. Per-service architecture tests (`ShiftManagementArchitectureTests`, `ShiftSignupArchitectureTests`, `ShiftViewArchitectureTests`) pin namespace + no-DbContext-ctor + repository-dep invariants, plus `ShiftsOwnedEntities_HaveNoCrossDomainNavigationProperties` assertion that `User`/`Team` navs are not reintroduced.
  - **Tickets (#545, moved to `src/Sections/Humans.Tickets` at G5)** — `TicketQueryService` and `TicketSyncService` live in `Humans.Tickets.Services`, `internal sealed` per HUM0034, with the vendor adapter carved into its own `src/Sections/Humans.TicketTailor` behind the section-owned port `Humans.Tickets.Contracts.ITicketVendorService` (`src/Sections/Humans.Tickets/Contracts/`, moved out of Base by nobodies-collective/Humans#866 G5 lane 4b-2g; the adapter references `Humans.Tickets` directly); `TicketingBudgetService` moved to the Budget section project (`src/Sections/Humans.Budget/Services/`, `internal` per HUM0034, contract `Humans.Budget.Contracts.ITicketingBudgetService`). `TicketQueryService` and `TicketSyncService` go through `ITicketRepository`; `TicketingBudgetService` is a repository-free bridge that reads paid orders via `ITicketServiceRead` and delegates Budget-owned writes to `IBudgetService` (its former `ITicketingBudgetRepository` was removed in #815). The Ticket Tailor API side of `TicketSyncService` is structurally separated via the `ITicketVendorService` connector (PR #277), and since the G5 move only `Humans.Tickets` and Shell's `TicketVendorHealthCheck` may inject that port — everything else asks Tickets through `Humans.Tickets.Contracts`.
  - **Google Workspace (fully migrated, #554 / #574 / #575 / #576)** — all Google Integration business services now live in `Humans.Application.Services.GoogleIntegration`: `GoogleAdminService`, `GoogleWorkspaceUserService`, `DriveActivityMonitorService`, `SyncSettingsService`, and `EmailProvisioningService` (PR #267, issue #289); and `GoogleWorkspaceSyncService` (§15 Part 2b, issue #575, 2026-04-23). **Part 1 of #554 (2026-04-23):** `IGoogleSyncOutboxRepository` extracted — `google_sync_outbox_events` behind a dedicated repository for the count queries used by `NotificationMeterProvider`, `HumansMetricsService`, `SendAdminDailyDigestJob`, and `GoogleWorkspaceSyncService.GetFailedSyncEventCountAsync`. **Part 2a (issue #574, PR #302):** SDK bridge interfaces extracted — `IGoogleDirectoryClient`, `IGoogleDrivePermissionsClient`, `IGoogleGroupMembershipClient`, `IGoogleGroupProvisioningClient` — with real Google-backed implementations and dev-mode stubs in `Humans.Infrastructure/Services/GoogleWorkspace/`. **Part 2b (issue #575):** `GoogleWorkspaceSyncService` moved to `Humans.Application.Services.GoogleIntegration`; it now reads Google via the four Part 2a bridges + `ITeamResourceGoogleClient`, reads cross-section DB state through sibling service interfaces (`ITeamService` for team/member graph via two new methods `GetActiveMembersForTeamsAsync` + `GetActiveChildMembersByParentIdsAsync`, `IUserService` for User rows incl. new `SetGoogleEmailStatusAsync`, `IUserEmailService.MatchByEmailsAsync` for extra-email identity, `IGoogleResourceRepository` for narrow `google_resources` writes), and lazy-resolves `ITeamResourceService` via `IServiceProvider` to break the construction cycle with `TeamResourceService`. Non-sensitive options (Domain / CustomerId / TeamFoldersParentId / GroupSettings) live on a new Application-layer `GoogleWorkspaceOptions`; credential-sensitive `GoogleWorkspaceSettings` stays in Infrastructure and both bind to the same `GoogleWorkspace` appsettings section. Parallel-sync DbContext-factory plumbing retired — the bridge clients carry their own concurrency-safe state and the old `DbSemaphore` is no longer needed. **Part 2c (issue #576, 2026-04-23):** the three remaining direct-DbContext consumers were flipped onto the repository surface: `ProcessGoogleSyncOutboxJob` now injects `IGoogleSyncOutboxRepository` + `IGoogleResourceRepository` + `IUserService` + `ITeamService` (the outbox repo grew a `GetProcessingBatchAsync` / `MarkProcessedAsync` / `MarkPermanentlyFailedAsync` / `IncrementRetryAsync` processor surface); `GoogleController.SyncOutbox` routes the admin dashboard read through `IGoogleSyncOutboxRepository.GetRecentAsync` + sibling services for user/team/resource display; and `TeamService` — the only remaining Application-layer writer of `GoogleSyncOutboxEvent` — already delegates every `outboxEvent` insert to `TeamRepository.AddMemberWithOutboxAsync` / `ApproveRequestWithMemberAsync` / `MarkMemberLeftWithOutboxAsync` (kept inside the Teams transaction boundary per §6d). The Google Workspace section now has zero non-repository direct `DbSet<GoogleSyncOutboxEvent>` / `DbSet<GoogleResource>` / `DbSet<SyncServiceSettings>` reads or writes across Application + Web layers.
  - **Calendar** — migrated 2026-04-23 for issue #569, own project since G5 (nobodies-collective/Humans#866) — `CalendarService` lives in `Humans.Calendar.Services`, goes through `ICalendarRepository` (`Humans.Calendar.Data`), and routes owning-team display names through `ITeamServiceRead.GetTeamsAsync` (§6b in-memory join). Cross-domain `.Include(e => e.OwningTeam)` is gone; `CalendarEvent.OwningTeam` nav is `[Obsolete]`-marked and the EF configuration references it under `#pragma warning disable CS0618` to keep the FK + cascade behavior wired. **T-08 (2026-05-16):** added the §15 caching decorator (`CachingCalendarService`, Singleton, keyed-inner `CalendarService`, `TrackedCache<Guid, CalendarEventInfo>`) and split the DTO-only read surface onto `ICalendarServiceRead`; controller reads use the read interface while writes use `ICalendarService`. The `calendar_events` / `calendar_event_exceptions` tables are now listed under a new **Calendar** row in §8.
  - **Agent** — own project since G5 (nobodies-collective/Humans#866): `AgentService` lives in `Humans.Agent.Services` (a **Section** — it owns `agent_*` tables, so it is not an Orchestrator per [`CONTEXT.md`](../../CONTEXT.md); it composes intra-section helpers). All `agent_*` table access goes through a single `IAgentRepository` (settings + conversations + messages, `Humans.Agent.Data`, backed by `AgentDbContext`) — `AgentSettingsService` and `AgentService` both depend on it; nothing in the section injects an application `DbContext` directly. `AnthropicClient` moved into the section as `Humans.Agent.Services.Anthropic` and is the SDK bridge there. **No cross-section FK or nav at the DB/EF level** — `agent_conversations.UserId`, `agent_messages.HandedOffToFeedbackId`, and `feedback_reports.AgentConversationId` are plain `Guid` columns with no `HasOne<…>()` wiring and no navigation properties. Cross-section reads route through the owning section's service interface (`IAgentUserSnapshotProvider` composes `IProfileService` / `IUserService` / `IRoleAssignmentService` / `ITeamService` / `IConsentService` / `IFeedbackService`). User deletion does NOT cascade into `agent_*` tables; orphaned rows expire via `AgentConversationRetentionJob` (default `RetentionDays = 90`). `AgentToolDispatcher` and `AgentUserSnapshotProvider` moved into the section with it (`src/Sections/Humans.Agent/Services/`). No caching decorator — admin-only endpoints, low traffic.
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
  - **§15i landmark — landed (issue #635, 2026-05-04)** — `UserInfo` is now the canonical "everything-about-a-person" read path: new derived properties `PrimaryEmail` / `AllVerifiedEmails` / `GoogleEmail` populated by `CachingUserService` from already-loaded `UserEmail` rows (no new repo lookups). `Profile.State` (`Stub`/`Active`/`Suspended`) is the lifecycle marker, lazily computed and written back by the caching decorator on first read. `Profile.IsSuspended` was `[Obsolete]` (custom diagnostic id `HUM_PROFILE_ISSUSPENDED`) and has since been **dropped outright** — column, analyzer and NoWarn entry all gone (nobodies-collective/Humans#997); `User.NormalizedEmail` is `[Obsolete]` (custom diagnostic id `HUM_USER_NORMALIZEDEMAIL`). Stub Profile invariant — every newly created User gets a Stub Profile inline (`AccountController.ExternalLoginCallback`/`CompleteSignup`, `AccountProvisioningService.FindOrCreateUserByEmailAsync`, `ProfileService.SaveProfileAsync`); the Stub→Active transition fires when `BurnerName`/`FirstName`/`LastName` populate. `/Profile/Admin/Backfill` admin tool materializes Stub Profiles for legacy profile-less users (idempotent). `UserEmail.IsPrimary` invariant is service-enforced via `UserEmailService.EnsurePrimaryInvariantAsync` — no DB index, per [`memory/architecture/db-enforcement-minimal.md`](../../memory/architecture/db-enforcement-minimal.md). **User-side nav strip — landed.** Six cross-domain navs deleted from `User`: `Profile`, `RoleAssignments`, `ConsentRecords`, `Applications`, `TeamMemberships`, `CommunicationPreferences`. The `GetEffectiveEmail()` method is also gone — was a literal alias for `Email`. The seventh nav, `UserEmails`, **stays** because the `User.Email` override depends on it per the issue's AC ("computed via override (UserEmails.FirstOrDefault...)"). Inverse-side EF configurations on each owning entity now own the schema-level FK definitions (verified non-destructive: a fresh `dotnet ef migrations add` produces an empty `Up()`/`Down()`). Cross-domain readers migrated: `GetEffectiveEmail()` callsites (12) replaced with `user.Email`; `user.UserEmails` reads in `GoogleWorkspaceSyncService` / `GoogleAdminService` / `GoogleController` / `ProfileController` were routed through `IUserEmailRepository.GetByUserIdReadOnlyAsync` / a batched user read / `UserInfo.GoogleEmail` (the batched read is `IUserServiceRead.GetUserInfosAsync` today). Arch test `User_HasNoCrossDomainNavigationProperties` enforces.
- The repository set today (the dedicated `TicketingBudgetRepository` was removed in #815 — the Tickets→Budget bridge is now repository-free; `UserEmailRepository` was folded into `UserRepository` as a `.UserEmails` partial; `DriveActivityMonitorRepository` no longer exists; `SurveyRepository` added in #884; `GateRepository` added with the Gate section; the Holded ledger-mirror repository added with the Holded section). In the G5 section projects the concrete class lives in `Humans.<Section>.Data` and usually keeps its domain name (`TeamRepository`, `TicketRepository`, …); six sections — Containers, Events, Finance, Holded, Store, SystemSettings — name it plainly `Repository`. The names below are the interface's domain name, not the class name. Target: one per domain (~20 total, some sections need two):
  - `AccountMergeRepository` (Users)
  - `AdminDatabaseDiagnosticsRepository` (Admin)
  - `AgentRepository` (Agent)
  - `ApplicationRepository` (Governance)
  - `AuditLogRepository`
  - `BudgetRepository`
  - `CalendarRepository`
  - `CampaignRepository`
  - `CampRepository`
  - `CityPlanningRepository`
  - `CommunicationPreferenceRepository` (Profiles)
  - `ConsentRepository`
  - `ContainerRepository` (Containers)
  - `EmailOutboxRepository`
  - `EventRepository` (Events)
  - `ExpenseRepository` (Expenses)
  - `FeedbackRepository`
  - `GateRepository` (Gate)
  - `GoogleResourceRepository`
  - `GoogleSyncOutboxRepository`
  - `HoldedRepository` (Finance — `IHoldedRepository`, the expense-doc / category-map side)
  - `HoldedMirrorRepository` (Holded — `IHoldedMirrorRepository`, the ledger mirror)
  - `IssuesRepository` (Issues)
  - `LegalDocumentRepository`
  - `NotificationRepository`
  - `RoleAssignmentRepository`
  - `ShiftRepository` (single concrete, partial across `.Management` + `.Signups` files; implements the partial `IShiftManagementRepository` — the former `IShiftSignupRepository` was folded into it)
  - `StoreRepository` (Store)
  - `SurveyRepository` (Survey)
  - `SyncSettingsRepository`
  - `SystemSettingsRepository` (SystemSettings)
  - `TeamRepository`
  - `TicketRepository`
  - `TicketTransferRepository` (Tickets)
  - `UserRepository` (Users; partials: core + `.Profiles` + `.ContactFields` + `.UserEmails`)
  - `VolunteerTrackingRepository` (Shifts)
- **0 stores** exist today. `IApplicationStore` was retired when Governance dropped its caching layer (issue #533). Target: replaced by §15 `ConcurrentDictionary`-in-decorator where a cache is warranted; no separate store type.
- **11 caching decorators** exist today (§15 pattern): `CachingUserService`, `CachingTeamService`, `CachingCampService`, `CachingEventService`, `CachingCalendarService`, `CachingConsentService`, `CachingRoleAssignmentService`, `CachingShiftViewService`, `CachingTicketQueryService`, `CachingLegalDocumentSyncService`, `CachingEarlyEntryService`. Governance operates without one (it dropped its caching layer in #533). Target: every migrated section that needs caching uses the §15 pattern. Not every section needs caching.
- **Inline `IMemoryCache.GetOrCreateAsync`** still scattered across services for non-profile caches (nav badge, notification meter, notification unread-badge counts, role-assignment claims, shift auth, camps-for-year, camp settings). These are short-TTL request-acceleration caches, not canonical domain data caches, and are appropriate for `IMemoryCache`. Canonical domain data caches (full entity projections) must use the §15 pattern.
- **Cross-domain navigation properties**. Target: stripped at the entity boundary, FK-only everywhere.
  - Still declared:
    - `User.UserEmails` stays — the `User.Email` override computes from it (per the #635 AC). All other `User` navs were stripped in #635 (see Stripped, below).
    - Cross-section *navigation properties* are all gone (nobodies-collective/Humans#996 stripped the last 11, in Feedback, Teams and Campaigns), and so are the nav-less cross-section EF **FK constraints** — nobodies-collective/Humans#992 dropped all 54 (`HasOne<User>().WithMany().HasForeignKey(...)` and friends) across 38 configurations in one migration. Cross-section linkage is now a bare `Guid` column everywhere. HUM0024 (`CrossSectionEfJoinAnalyzer`) enforced this and was **retired** in nobodies-collective/Humans#1278: a peeled section maps only its own tables in its own `<Section>DbContext`, so there is no shared EF model for a navigation to join across. The accepted residual until the last section peels is an EF configuration inside `Humans.Infrastructure` mapping another section's entity — no longer a build error, so it is a review-time check.
  - Stripped:
    - User-side (issue #635, 2026-05-04): `User.Profile`, `User.TeamMemberships`, `User.RoleAssignments`, `User.Applications`, `User.ConsentRecords`, `User.CommunicationPreferences`, and the `GetEffectiveEmail()` method. Enforced by `User_HasNoCrossDomainNavigationProperties`.
    - Profile-section: `Profile.User`, `UserEmail.User`, `CommunicationPreference.User`.
    - Email-section: `EmailOutboxMessage.User`.
    - `CampLead.User` (issue #542) — the whole `CampLead` entity and its `camp_leads` table have since been dropped (nobodies-collective/Humans#787); camp leads are camp role assignments now, and lead display data routes through `IUserServiceRead`.

**External connectors (API bridge pattern).** External SDKs (Google, Stripe, SMTP/IMAP, Octokit, etc.) sit behind Application-layer interfaces so `Humans.Application` never references the SDK assembly. The concrete implementation lives in `Humans.Infrastructure/Services/` (or a subfolder) and is the only code that imports the SDK namespaces. Connectors own no database tables — side-effects that need persistence write through the owning section's repository (e.g., Stripe fee values land on `TicketOrder`, written by `ITicketRepository`).
- **Stripe** (issue #556, 2026-04-22; moved to its own section by nobodies-collective/Humans#866, 2026-08-14): `IStripeService` in `Humans.Stripe.Contracts`, `StripeService` (internal) in `Humans.Stripe.Services`. The bridge is structurally enforced (only `Humans.Stripe.csproj` references `Stripe.net`) and additionally covered by `StripeConnectorArchitectureTests` (SDK types cannot leak onto the interface surface). Store and Tickets consume it by direct project reference — the connector names no section, so no `.Contracts` leaf is needed.
- **Google Workspace** (pre-§15): resources are on Shared Drives only; all SDK access goes through the dedicated services listed in §13. Extracted connectors so far:
  - `ITeamResourceGoogleClient` (PR #274) — Teams→Drive linking.
  - `IWorkspaceUserDirectoryClient` (issue #554) — @nobodies.team account lifecycle.
  - `IGoogleDriveActivityClient` (issue #554) — Drive Activity v2 permission-change monitoring.
  - `IGoogleGroupMembershipClient`, `IGoogleGroupProvisioningClient`, `IGoogleDrivePermissionsClient`, `IGoogleDirectoryClient` (§15 Part 2a, issue #574) — SDK bridge surface consumed by the Application-layer `GoogleWorkspaceSyncService` (§15 Part 2b, issue #575). Real implementations in `Humans.Infrastructure/Services/GoogleWorkspace/`; stubs (in-memory fakes) registered when no service-account credentials are configured. Application-layer service never imports `Google.Apis.*`.
- **Email** (PR #266): `IEmailBodyComposer` (Application) renders the message; `IImmediateOutboxProcessor` (Infrastructure) drives MailKit/SMTP. The body-composer is SDK-free so Application-layer services can build messages without pulling MailKit in.
- **Ticket vendor** (PR #277): `ITicketVendorService` (Application), concrete `TicketTailorService` / `StubTicketVendorService` (Infrastructure). `TicketVendorSettings` lives in `Humans.Application.Configuration` so the Application-layer `TicketSyncService` can read non-sensitive fields without reaching into Infrastructure.

Former controller direct `DbContext` access cleanup status:
- (`AdminController`, `ProfileController`, and `GoogleController` were cleaned in earlier §15 work — no direct DbContext usage remains. `AdminController` routes database diagnostics through `IAdminDatabaseDiagnosticsService`; audience segmentation composes the owning User/Profile/Tickets services, while infrastructure-only migration metadata and Hangfire lock cleanup stay behind the Infrastructure implementation.)
