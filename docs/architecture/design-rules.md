<!-- freshness:triggers
  src/Humans.Base/**
  src/Humans.Web/Extensions/**
  src/Humans.Web/Hosting/**
  src/Humans.Web/Services/**
  src/Humans.Web/Data/**
  src/Humans.Web/Repositories/**
  src/Humans.Analyzers/**
  src/Sections/**
  docs/architecture/freshness-catalog.yml
-->
<!-- freshness:flag-on-change
  Layer responsibilities, service/repository/store ownership, caching decorator pattern, and authorization handler doctrine. Flag if any architectural pattern shift in src/** alters the layering or ownership rules.
-->

# Design Rules

> **Subordinate to [`peters-hard-rules.md`](peters-hard-rules.md).** Those are the constitution — the final word; on any conflict, the hard rules win. This doc is the *regulations*: the implementing detail. Open a section on demand. Architectural term definitions (Section / Crosscut / Orchestrator / Lane / Width) live in [`CONTEXT.md`](../../CONTEXT.md).

Architectural rules governing how the Shell, the section projects, and the shared base interact. **These are target-state rules.** New code must follow them; existing code is migrated incrementally per [Migration Strategy](#15-migration-strategy).

## 1. Layer Responsibilities

**The layers are roles now, not projects.** `src/Humans.Domain`, `src/Humans.Application`, `src/Humans.Infrastructure` and `src/Humans.UI` were all deleted over the course of G5 (nobodies-collective/Humans#866); what survives is three kinds of project — `src/Humans.Base` (the base), `src/Sections/Humans.<Section>[.Contracts]` (42 section projects, 22 with a paired `.Contracts` leaf), and `src/Humans.Web` (the Shell). Dependency direction is unchanged in spirit: a section may reference the base and other sections' `.Contracts` leaves; nothing may reference the Shell.

```
Humans.Base  ←  Humans.<Section>.Contracts  ←  Humans.<Section>
                                           ←  Humans.Web (Shell)
```

The four role names below still describe what code *does*, and a section assembly holds all four:

| Role | Contains | Forbidden |
|---|---|---|
| **Domain** | Entities, enums, value objects — a section's `Domain/` folder, or its `.Contracts` leaf when other sections need the shape. | Services, framework references, EF types, DTOs |
| **Application** | Service **interfaces** and **implementations** (business logic), repository **interfaces**, DTOs, use cases, authorization handlers — a section's `Services/`, `Authorization/` and `Contracts/` folders | `DbContext`, `Microsoft.EntityFrameworkCore.*`, HTTP types, external SDKs, direct I/O |
| **Infrastructure** | Repository implementations, caching decorators, the section's `<Section>DbContext` and its migrations, external API clients — a section's `Data/` folder | Controller logic, Razor, HTTP request/response, business rules |
| **Web** | Controllers, views, view models, API endpoints, DI wiring — a section's `Controllers/`, `Views/`, `Models/` and its `Section.cs` | `DbContext`, direct EF queries, direct cache access for domain data, raw SQL |

Because those roles now share one assembly, the old project graph no longer enforces "no EF in business logic" structurally. The enforcement moved to the analyzers: HUM0008 (controller injects a context), HUM0009 (non-repository uses a context), HUM0025 (a table read or written by two repositories), and `Internal/AssemblyScope.IsInSectionDataLayer` is how a rule tells a section's `Data/` folder from the rest of it.

`Humans.Base` is the base — the bottom of the graph, and the only project every section may reference. **Its one invariant is the reference rule, not a package count:** it may take any NuGet package or shared framework it needs (Peter's ruling, 2026-08-15), and it currently carries NodaTime, CsvHelper, Serilog, Octokit, HtmlSanitizer, Markdig, EF Core + Npgsql, and `FrameworkReference Microsoft.AspNetCore.App`. It has exactly one `ProjectReference` — `Humans.Users.Contracts` — allowed only because that chain terminates (`Humans.Base → Humans.Users.Contracts → Humans.Onboarding.Contracts → nothing`); a reference to anything not on a terminating chain would close an assembly cycle. It holds the role markers (`IApplicationService`, `IRepository`, `IOrchestrator`, `IFanout`, `IInvalidator`, `ISection`, `IRecurringJob`), the architecture attributes (`GrandfatheredAttribute`, `DontFixAttribute`, `SurfaceBudgetAttribute`, `ExternalWriteAttribute`, `CrossSectionWriteAttribute`, `ExpiresOnAttribute`), `TrackedCache` and the cross-cutting cache invalidators, the `AddSectionDbContext` registration seam and migration runner, and the shared view layer (below). Namespaces stayed `Humans.Application.*` / `Humans.Domain.*` / `Humans.Infrastructure.*` / `Humans.UI.*` through the G5 project moves (namespace ≠ assembly), so call sites and the `Humans.Analyzers` full-name constants were unaffected by those moves; the Interfaces→Base rename that followed then renamed every one of those namespaces to `Humans.Base.<folder>`, folder-mirrored (nobodies-collective/Humans#866).

**The shared view layer lives in the base.** `Humans.UI` was extracted from `Humans.Web` for the section-project split and then folded into `Humans.Base` at G5 lane 4b-iii, which is why that project builds with `Microsoft.NET.Sdk.Razor` + `AddRazorSupportForMvc`. It holds `SharedResource` and its satellite resx files, the tag helpers (`AuthorizeView`, `MarkdownEditor`, `Nonce`, `PageHeader`), the shared `Views/Shared` partials (`_Table`, `_Pager`, `_ValidationScriptsPartial`, …; the chrome — `_Layout`, `_AdminLayout`, `_LoginPartial`, `_LanguageChooser`, `_AuthorizationPill`, `_VersionInfo` — is the Shell's, per G5 lane 4b-ii), the generic table + pager view models, `PolicyNames`, `TempDataKeys`, `HumansControllerBase`, the display extensions, and the section-agnostic view components (`AccessMatrix`, `Human`, `HumanSearch`, `TempDataAlerts`). Its types now live under `Humans.Base.*` — notably `Humans.Base.Authorization.PolicyNames` and `Humans.Base.Extensions.DateTimeDisplayExtensions` — and the assembly Razor binds by name is `Humans.Base`, which is what `@addTagHelper *, Humans.Base` names.

**A section project is internal by default.** Each lives under `src/Sections/Humans.<Section>/` and declares `Section : ISection` at its project root — that type, not any list, is what makes the assembly a section, and the section's *name* is the assembly name minus `Humans.` (so `Humans.Store.Contracts` is still section Store). There are **42** of them: Agent, AuditLog, Auth, Budget, Calendar, Campaigns, Camps, Cantina, CityPlanning, Consent, Containers, Debug, Development, EarlyEntry, Email, Events, Expenses, Feedback, Finance, Gate, Gdpr, GoogleIntegration, Governance, Guide, Holded, Issues, Mailer, Monitor, Notifications, Onboarding, Scanner, Search, Shifts, Store, Stripe, Surveys, SystemSettings, Teams, TicketTailor, Tickets, Tour, Users. `Humans.TicketTailor` is the vendor adapter behind Tickets' `ITicketVendorService` port and `Humans.Stripe` the payment connector — neither is a UI section, but both are sections by the same test and carry the same internal-by-default rule. **22** of them publish a cross-section surface from a paired `Humans.<Section>.Contracts` project; **28** own tables and therefore a `<Section>DbContext`. A section's only public surface is its `ISection` entry point, its `<Section>Resource` localization marker, EF Core migrations, types the framework requires to be public in order to function, types declared under a `Contracts/` folder, and Hangfire jobs declared under a `Jobs/` folder — everything else must be `internal`. This was convention-only across the first sections that moved; analyzer `HUM0034` (`SectionRulesAnalyzer`, nobodies-collective/Humans#1013) now fails the build on any other public type in a section assembly. Within a section, `Contracts/` is the public folder and `Interfaces/` the internal one.

**Those are two different kinds of exception.** `Contracts/`, `Jobs/`, the `ISection` entry point and the `<Section>Resource` marker are a *deliberate surface* — someone chose to let other assemblies depend on them. `Jobs/` is narrower than `Contracts/`: it admits only `IRecurringJob` implementors and `*Job`-named Hangfire jobs, whose audience is the Shell scheduler naming the concrete type — not other sections. The framework carve-out is not a choice: the type is public because it stops working otherwise. **The membership test is whether making it `internal` fails loudly or silently renders nothing** — silent means it belongs in the exception. Razor/MVC discovery passes that run at *compile time* filter on public accessibility and skip what they cannot see, emitting no error, warning or diagnostic; runtime resolution throws instead, so it does not qualify. **Current membership is view components and tag helpers, and that is the whole set** — controllers look like a candidate and are not, since `SectionControllerFeatureProvider` routes internal ones and a missing controller 404s loudly. The cost of getting this wrong is not theoretical: an `internal` `ProfileCardViewComponent` made `<vc:profile-card>` ship as inert literal markup and silently emptied the Profile page, with a green build and 5,475 passing tests (nobodies-collective/Humans#866 lane 2).

**Key change from prior rules:** a service lives in its own section's `Services/` folder and reaches persistence only through that section's `Data/` folder. The old rule ("services own their data access") meant "services inject `DbContext` directly," which conflated business logic with persistence and made "no cross-domain joins" impossible to enforce structurally. The new rule is "services go through their owning repository."

## 2. Service Ownership — The Core Rule

Each service is the exclusive gateway to its data. No component — controller, other service, job, or view component — may bypass the owning service to reach its tables, its cache, or its store.

### 2a. Controllers Cannot Talk to the Database

Controllers call services. Controllers never inject `DbContext`, never write EF queries, never instantiate repositories or stores directly, never access `IMemoryCache` for domain data. Their job is: receive HTTP request → authorize → call service(s) → return response.

**Exception:** `UserManager<User>` / `SignInManager<User>` for ASP.NET Identity operations (login, password, claims) are allowed in controllers since Identity is a framework concern, not a domain service.

### 2b. Services Live in `Services/`, Not `Data/`

Business services (`ProfileService`, `TeamService`, `BudgetService`, etc.) live in their section's `Services/` folder. They contain business rules, workflow logic, validation, and orchestration. They **never** import EF types. When they need to load or persist entities, they call their owning repository interface; when they need cached data, they go through their owning caching decorator.

Repository **implementations** (the classes that talk to `DbContext`) live in the owning section project's `Data/`. `Humans.Infrastructure` was the shared home until G5 lane 5b-6 deleted it (nobodies-collective/Humans#866); only the platform context is left, in `Humans.Web/Data/`.

Every application context is `internal sealed` (issue #750). External access is via the section's repository interface, which lives in that section's `Data/` folder alongside the implementation and may **not** be declared under `Contracts/` (HUM0035). Persistence wiring is via the extension methods in `Humans.Web.Extensions.PersistenceServiceCollectionExtensions` (`AddHumansPersistence`, `PersistKeysToSystemDbContext`) — renamed from `InfrastructureServiceCollectionExtensions` when Web's `Infrastructure/` folder dissolved, to stop colliding with the roll-call class of that name. The migration runner is a hosted service (`DatabaseMigrationHostedService`) registered by `AddHumansPersistence`. Test projects access the contexts directly via `InternalsVisibleTo`.

**There is no single context any more.** Since the per-section split (nobodies-collective/Humans#858) each section has its own `internal sealed <Section>DbContext` mapping only that section's tables, with its own `__EFMigrationsHistory_<Section>` table and its own migrations folder — `src/Sections/Humans.<Section>/Data/Migrations/`, and `src/Humans.Web/Migrations/System/` for the platform context. There are **28 section contexts plus `SystemDbContext`**. `HumansDbContext` and its root migration chain were deleted at peel 15 (nobodies-collective/Humans#858); the merged Users+Profiles section (`UsersDbContext`) carries the Identity base. Consequences:

- **One design-time factory per context**, each next to its context: every section's in its own project under `Humans.<Section>.Data` (`AgentDbContextFactory`, `HoldedDbContextFactory`, …), and `SystemDbContextFactory` in `Humans.Web/Data/`. Every `dotnet ef` command therefore needs `--context` — see [`ef-multi-context-commands`](../../memory/process/ef-multi-context-commands.md).
- **History-table names are derived, never typed.** `SectionMigrationsHistory.TableFor<TContext>()` (`Humans.Base/Data/`) is the single source for both the runtime registration (`AddSectionDbContext`) and the design-time factories.
- **Section contexts apply their configurations explicitly** (no assembly scanning, which would drag in other sections). `DbContextEntityOwnershipTests` (`tests/Humans.Web.Tests/Architecture/`) fails the build if an `IEntityTypeConfiguration` ends up applied by zero contexts (invisible to `has-pending-model-changes`) or by two.
- **Unit tests for a section context** build their in-memory options with a `NewSectionDbOptions<TContext>()` helper rather than hand-rolling a `DbContextOptionsBuilder`. The helper is per-test-project now that the section test projects are separate assemblies — `tests/Humans.Users.Tests/Infrastructure/ServiceTestHarness.cs` and `tests/Humans.Camps.Tests/Infrastructure/CampsTestHarness.cs` are the two harness-shaped copies; the rest declare it locally.

### 2c. Table Ownership Is Strict and Sectional

Each domain's tables are owned by exactly one service (and that service's repository). **No other service may query, insert, update, or delete rows in tables it does not own.** If `CampService` needs person/profile display data, it calls `IUserServiceRead` - it does not query the `profiles` table, instantiate `IUserRepository`, or access the Users/Profile read-model cache directly.

### 2d. Cache Ownership Follows Data Ownership

Caching is an internal concern of the owning service. Callers don't know whether data came from memory, the store, or the database — they call the service method and get the result. The mechanism for caching is the **store pattern** (§4) and the **caching decorator** (§5), not raw `IMemoryCache` calls inlined in service methods.

## 3. Repository Layer

Every domain has a narrow, entity-shaped **repository interface** and an EF-backed **implementation**, both in the owning section's `Data/` folder (`src/Sections/Humans.<Section>/Data/`). The repository is the single point of EF access for its tables. `IRepository`, the marker they extend, is the only piece left in `Humans.Base.Interfaces.Repositories` (`src/Humans.Base/Interfaces/Repositories/IRepository.cs`).

### 3a. Repository Rules

1. **Entities in, entities out.** Return types are `Profile`, `IReadOnlyList<Profile>`, `IReadOnlyDictionary<Guid, Profile>`, or scalar / id values. Never `IQueryable<T>`, never EF types, never DTOs.
2. **No cross-domain method signatures.** A repository for the Profile domain never takes a `Team`, returns a `User`, or accepts a filter that requires joining another domain's table. If a caller needs a compound shape, a composer at the service layer stitches it from multiple repositories.
3. **Bulk-by-ids is first class.** Every repository exposes a `GetByIdsAsync(IReadOnlyCollection<Guid>)` returning a dictionary. This is what makes in-memory joins (§6) cheap.
4. **`GetAllAsync` exists for store warmup.** At ~500 users it is trivial. Larger datasets would replace it with a streaming shape; at our scale it is strictly cheaper than lazy loading.
5. **No cross-domain navigation properties in return shapes.** `Profile.User` is a cross-domain nav — callers get the FK (`Profile.UserId`) and resolve via `IUserRepository` if they need the User. Aggregate-local navs (`Profile.Languages`) are fine.
6. **No logging of domain events, no audit, no `IClock`, no caching.** Just persistence. Side effects belong to the service.

### 3b. Canonical Repository Shape

```csharp
// src/Sections/Humans.Users/Data/Repositories/IUserRepository.Profiles.cs
internal partial interface IUserRepository
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

Services are cached by **wrapping them in a decorator**, not by inlining `IMemoryCache` calls. The decorator is registered via a keyed-inner + factory-forward pattern: the inner is registered against `IUserService` under a key (`AddKeyedScoped<IUserService, UserService>(CachingUserService.InnerServiceKey)`); the decorator is registered as itself and `IUserService` is forwarded to it. See `src/Sections/Humans.Users/Section.cs` for the canonical wiring — every section registers its own DI from its `Section.cs` now; only `AdminSectionExtensions` and `AuthSectionExtensions` are left in `Humans.Web/Extensions/Sections/`. Callers inject `IUserService` and get the cached version transparently.

### 5a. Decorator Rules

1. **One decorator per service.** `CachingUserService : IUserService` wraps the real `UserService`.
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

Ownership is now physical as well as conventional: the map below is **per DbContext**, not per single model, and every table belongs to exactly one section context. Users/Identity and Profiles merged into `UsersDbContext` at peel 15, which also deleted the root `HumansDbContext` (framework-owned tables live in `SystemDbContext`). See the methodology block of [`service-data-access-map.md`](service-data-access-map.md) for the context-to-table listing.

The 28 table-owning sections are listed below. The other 14 section projects own no tables and so have no row: Cantina, Debug, Development, EarlyEntry, Gdpr, Guide, Mailer, Monitor, Onboarding, Scanner, Search, Stripe, TicketTailor, Tour — several appear anyway, with an explicit *(no owned tables)* note, because a reader looking for them expects to find them here.

| Section | Service(s) | Owned Tables |
|---------|-----------|--------------|
| **Profiles** | `ProfileService`, `ContactFieldService`, `UserEmailService`, `CommunicationPreferenceService`, `ProfileEditorService` — all in the merged `Humans.Users` project, sharing `UsersDbContext` | `profiles`, `profile_languages`, `contact_fields`, `user_emails`, `communication_preferences`, `volunteer_history_entries` |
| **Users/Identity** | `UserService`, `AccountProvisioningService`, `UnsubscribeService`, `AccountMergeService`, `DuplicateAccountService`, `ExternalLoginService`, `AccountDeletionService`, `HumanLifecycleService` (G5 project `Humans.Users`, published via `Humans.Users.Contracts`) | `users`, `user_claims`, `user_logins`, `user_tokens`, `roles` (legacy), `user_roles` (legacy), `role_claims` (legacy), `event_participations`, `account_merge_requests` — the ASP.NET Identity tables are renamed to the PostgreSQL snake_case convention in `UsersDbContext.OnModelCreating`, which also carries the Identity base since peel 15 |
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
| **Google Integration** | `GoogleWorkspaceSyncService` (the production `IGoogleSyncService`; `StubGoogleSyncService` stands in when no credentials are configured), `GoogleAdminService`, `GoogleWorkspaceUserService`, `SyncSettingsService`, `EmailProvisioningService`, `TeamResourceService` (the Teams↔Drive linking surface — lives in `Humans.GoogleIntegration.Services`, not Teams) — G5 project `Humans.GoogleIntegration`, published via `Humans.GoogleIntegration.Contracts` | `sync_service_settings`, `google_sync_outbox`, `google_resources` |
| **Email** | `EmailOutboxService`, `OutboxEmailService` (the `IEmailService` implementation, published via `Humans.Email.Contracts`) | `email_outbox_messages` (reads the `IsEmailSendingPaused` flag via `ISystemSettingsService`) |
| **System Settings** | `ISystemSettingsService` (G5 project `Humans.SystemSettings`; implemented by the section-internal `Service`) | `system_settings` (cross-cutting key/value store; consuming sections read/write via `ISystemSettingsService`) |
| **Mailer** | `MailerImportService`, `MailerLiteClient` | _(no owned tables — MailerLite is read-only; classifier writes through other sections' services)_ |
| **Feedback** | `FeedbackService` | `feedback_reports`, `feedback_messages` |
| **Issues** | `IssuesService` | `issues`, `issue_comments` |
| **Notifications** | `NotificationService`, `NotificationInboxService`, `NotificationMeterProvider` | `notifications`, `notification_recipients` |
| **Audit Log** | `AuditLogService` (G5 project `Humans.AuditLog`, published via `Humans.AuditLog.Contracts`) | `audit_log` |
| **Agent** | `AgentService`, `AgentSettingsService`, `AgentPromptAssembler`, `AgentToolDispatcher`, `AgentUserSnapshotProvider`, `AgentAbuseDetector`, `AnthropicClient`, `AgentConversationRetentionJob` (G5 project `Humans.Agent`) | `agent_conversations`, `agent_messages`, `agent_settings` |
| **Event Guide** | `EventService` (G5 project `Humans.Events`; implements the section-internal `IEventService`, with `IEventServiceRead` published cross-section via `Humans.Events.Contracts`) | `events`, `event_guide_settings`, `event_categories`, `event_venues`, `event_moderation_actions`, `event_favourites`, `event_preferences` |
| **Survey** | `SurveyService` (G5 project `Humans.Surveys`) | `surveys`, `survey_questions`, `survey_question_options`, `survey_invitations`, `survey_responses`, `survey_answers` |

**`system_settings` is owned by the System Settings section** (G5 project `Humans.SystemSettings`; its internal `Service` / `Repository`) and exposed cross-section via `ISystemSettingsService`; consuming sections read/write their keys through it rather than touching the table directly. Currently-tracked keys: `IsEmailSendingPaused` (Email's send-pause flag), `DriveActivityMonitor:LastRunAt` (Google Integration's drive-monitor last-run).

**Admin is not a section.** The `/Admin/*` controllers are a nav holder for admin-only actions that live in other sections (outbox pause in Email, suspend/purge in Profiles, account merge in Users, sync settings in Google Integration, role assignments in Auth, legal-doc management in Consent). Services referenced from `AdminController` belong to their owning section, not to Admin.

See [`docs/architecture/dependency-graph.md`](dependency-graph.md) for the full directed dependency graph with current vs target edges and circular dependency analysis.

### 8a. User-Scoped Sections Must Contribute to the GDPR Export

Every section whose owned tables hold per-user rows MUST implement `IUserDataContributor` (`Humans.Gdpr.Contracts`) so the GDPR Article 15 data export (`IGdprExportService`) can assemble a complete document without any cross-section database reads. The orchestrator injects `IEnumerable<IUserDataContributor>`, fans out one call per contributor, and merges the returned slices into the JSON document the user downloads from `/Profile/Me/DownloadData`.

Adding a new user-scoped section to §8 above requires four coupled steps — all four, in any order, before the PR can land:

1. Add the new section-name constants to `GdprExportSections` (`Humans.Gdpr.Contracts`).
2. Make the owning service implement `IUserDataContributor` and return its own slice. A contributor reads only its own section's tables — cross-section data flows through other contributors, not through `Include` chains. Collection slices must always return the shaped list (empty when the user has no records); `null` data is reserved for single-object sections whose entity doesn't exist for this user.
3. Register the service in the section's own `Section.cs`, using the forwarding pattern so the same scoped instance serves both the primary interface and `IUserDataContributor`:

   ```csharp
   services.AddScoped<MyNewService>();
   services.AddScoped<IMyNewService>(sp => sp.GetRequiredService<MyNewService>());
   services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<MyNewService>());
   ```

4. Add the concrete service type to `GdprExportDependencyInjectionTests.ExpectedContributorTypes` — the enforced view of the §8 rows that hold user-scoped data.

The architecture test suite in `tests/Humans.Web.Tests/Services/Gdpr/GdprExportDependencyInjectionTests.cs` enforces every step automatically:

- `EverySectionServiceMustImplementIUserDataContributor` — each listed type really implements the interface. A section service is `internal`, so the list names it by reflection (`SectionType("Humans.Gate.Services.GateService")`) rather than `typeof`.
- `EveryIUserDataContributorInInfrastructureIsExpected` — every `IUserDataContributor` found via reflection is in the expected list (catches new contributors that forget the list). It sweeps the Shell assembly, `Humans.Users`, **and every section assembly**, discovered through `SectionDiscoveryExtensions.SectionAssemblies()` — the same discovery the runtime uses, so a section that moves or renames cannot silently drop out of the scan.
- `EveryExpectedContributorIsRegisteredInInfrastructure` — every listed type has a DI registration.
- `EveryIUserDataContributorFactoryForwardsToAnExpectedConcreteType` — each forwarding factory resolves to a distinct expected concrete type, so a duplicated or mis-wired factory can't silently drop a section.
- `GdprExportServiceIsRegistered` — the orchestrator itself is registered.

**Uncaught case (convention, not test):** if a new user-scoped section is added to §8 but its owning service never implements `IUserDataContributor` at all, reflection finds nothing to enumerate and the suite passes vacuously. The four-step list above is the prose-level guardrail — reviewers should reject any §8 edit that adds a user-scoped row without touching `ExpectedContributorTypes` in the same PR.

**Provenance FKs are not user-scoped data.** A section's tables can carry user FK columns that record *who performed an action* (`AddedByUserId`, `RecordedByUserId`, `IssuedByUserId`, etc.) without the section's data being user-scoped. The rule of thumb: if you delete the user, do their rows go with them, or do they belong to a different aggregate (a camp, a team, an event) and merely lose their actor reference? If the latter, the section is not user-scoped — the FKs are provenance and belong to audit-style "what happened" data, not to the user's "what's mine" export. The **Store** section is the canonical example: store orders, lines, payments, and invoices belong to a camp season; the user FKs only record which lead clicked which button. Store data flows out of GDPR export through the audit log, not through a Store-section contributor.

See [`docs/features/global/gdpr-export.md`](../features/global/gdpr-export.md) for the JSON output shape, the contributor table, and a worked example of adding a new section.

### 8b. Cross-Section Fanout — Contributor Pattern

§8a's GDPR export is one instance of a recurring shape: an **orchestrator that owns no tables, injects `IEnumerable<IContributor>`, calls only the contributor interface, and merges the returned slices** — never reaching into another section's repository or running cross-section `Include` chains. Sections opt in by implementing the contributor interface; each contributor reads only its own owned tables, and cross-section names flow through the existing `I{Section}ServiceRead` surfaces. The orchestrator iterates sequentially and never appears in §8's table-ownership map. **The original reason for iterating sequentially is obsolete** — it was "the contributors share the scoped `HumansDbContext`, which is not thread-safe", but since the per-section split (nobodies-collective/Humans#858) contributors such as `ExpenseReportService`, `SurveyService`, `AgentService`, `EventService` and Finance's `Service` read through their own `IDbContextFactory<TContext>` against separate contexts, and independent factory-created contexts are safe to use concurrently (EF's restriction is on concurrent operations against the *same* instance). Sequential iteration is now a consistency and simplicity choice, not a correctness requirement.

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

**Which instance a section forwards** to a contributor interface follows where that contributor's read is actually served from, not a fixed lifetime. Forward the caching decorator only when the fanout read comes off the section's cached projection — `Humans.Camps`' `Section.cs` registers `AddSingleton<IEarlyEntryProvider>(sp => sp.GetRequiredService<CachingCampService>())` because `CachingCampService.GetEarlyEntriesAsync` projects entirely from the cached `CampSettingsInfo` + `CampInfo` snapshot. Otherwise forward the inner scoped service: `Humans.Teams` and `Humans.Shifts` both register scoped providers, because `team_early_entry_grants` and the volunteer-tracking rows are read from the repository per call and are not in `TeamInfo`. The orchestrator is keyed-scoped so it resolves either lifetime; registering a decorator that does not itself serve the read buys no caching and only adds a hop.

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
| `CampAuthorizationHandler` (`Humans.Camps.Authorization`) | `CampOperationRequirement` | `CampInfo` / `Camp` / `Guid` (untyped — the handler accepts all three) | Lead/CampAdmin checks |
| `RoleAssignmentAuthorizationHandler` (`Humans.Auth.Authorization`) | `RoleAssignmentOperationRequirement` | `string` (role name) | Who can assign which roles |
| `HumanAdminOnlyHandler` (`Humans.Web.Authorization.Requirements` — one of three still in the Shell, alongside `CampComplianceAccessHandler` and `IsAnyTeamManagerOrCoordinatorHandler`) | `HumanAdminOnlyRequirement` | — | Admin profile operations |
| `UserEmailAuthorizationHandler` (`Humans.Users.Authorization`) | `UserEmailOperationRequirement` | `Guid` (owning user id) | Who may manage another human's email addresses |
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

- **Domain entities** live in their section's `Domain/` folder, or in its `.Contracts` leaf when another section needs the shape (`Humans.Users.Contracts.User`, `.Profile`). They are mutable, have identity, and carry invariants. Entities never reference EF types.
- **DTOs** live in their section's `Services/Dtos/` or `Contracts/` folder. They are read-optimized shapes for specific use cases (admin tables, API responses, view data). Services return DTOs when the shape is call-specific and the entity does not match; they return entities when the caller needs the full aggregate.
- **ViewModels** live in their section's `Models/` folder (or are inlined in controllers). Controllers map DTOs or entities to view models for Razor. The section-agnostic ones — the generic table models and `PagerViewModel` — are now under `Humans.Base.Models`, in the base assembly (§1).
- **Domain entities should not leak into Razor views** when a DTO would provide better separation. Simple 1:1 cases are acceptable; anything that would have required `.Include` for navigation in the old model is not.
- **View components** are part of the Web layer — a section's `ViewComponents/`, or the base's for the section-agnostic ones. They call services, not repositories. They are one of the two framework carve-outs HUM0034 allows a section to declare `public` (§1).

## 15. Section Caching Pattern — Canonical Cache-Collapse Architecture

The `TrackedCache` decorator stack below is the target caching architecture for any section that warrants one. `CachingUserService` (`src/Sections/Humans.Users/Data/CachingUserService.cs`, owning the `UserInfo` read model) is the reference implementation; the pattern was first proven on the Profile migration (PR #235, 2026-04-20) and every G5 section that caches a canonical domain projection follows it. (Short-TTL request-acceleration caches — the `ApplicationServicesTakeNoMemoryCacheRule` allowlist — are a different, sanctioned shape; see §15i.)

> Not every section needs §15 — reach for it only when traffic or bulk-read patterns justify an in-memory projection. **Governance** dropped its caching layer entirely (issue #533): the section is low-traffic enough that DB reads per request are fine, so `ApplicationDecisionService` talks directly to `IApplicationRepository` and invalidates cross-cutting caches (`INavBadgeCacheInvalidator`, `INotificationMeterCacheInvalidator`, `IVotingBadgeCacheInvalidator`) inline after successful writes. (One short-TTL exception, added in #1010: the per-board-member unvoted-application count — read on every page load via the nav badge — is cached inline via `IMemoryCache` with a 2-min TTL, the same request-acceleration pattern as the other nav-badge counts; it is not a §15 projection.)

### 15a. Four-Layer Stack

```
Controller / View Component
  ↓ I<Section>Service                               [Contracts/ or Services/ — interface]
Caching<Section>Service   (optional decorator)      [Services/ or Data/ — Singleton]
  ↓ keyed resolve via IServiceScopeFactory
<Section>Service          (inner, keyed)            [Services/ — Scoped]
  ↓ repositories + cross-section service interfaces
<Section>Repository                                 [Data/ — Singleton via IDbContextFactory]
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

The application service (`UserService`, `ProfileService`, `ContactFieldService`, etc.) lives in its section's `Humans.<Section>.Services` namespace and is `internal sealed` per HUM0034. (A handful of Users files still carry the legacy `Humans.Application.Services.*` namespace; the G5 namespace normalization retires those, nobodies-collective/Humans#866 4b-iv.) It:

- Injects repository interfaces, never `DbContext`.
- Never imports `IMemoryCache` or any caching abstraction — it is completely cache-unaware.
- When the section has a caching decorator: registered as **Scoped** and **keyed** under that decorator's `InnerServiceKey` (e.g., `CachingUserService.InnerServiceKey` = `"user-inner"`) so the Singleton decorator can resolve fresh instances per-call without self-resolution.
- Implements **every** read method against the DB, with one carve-out: **cache-only reads** — projections served from the decorator's warmed snapshot with no repository equivalent (relevance-ranked search: `UserService.SearchUsersAsync`, `TeamService.SearchAsync`, `CampService.SearchAsync`; merge-tombstone scan: `UserService.GetMergedSourceIdsAsync`, §12) — have no DB implementation. For those, the inner method throws `NotSupportedException` so a DI mis-registration fails loudly instead of silently bypassing the cache. The base service must **never** return empty results for a method "so the decorator can override" — implement it against the DB, or throw.
- Sub-aggregates belong to the parent section (CV entries are written through `IProfileService.SaveCVEntriesAsync`; the parent repository owns the reconcile logic).

### 15d. Caching Decorator Rules

A section's caching decorator is a **Singleton** that inherits (or composes) `TrackedCache<TKey, TValue>` (`Humans.Base`) — a primitive owning a private `ConcurrentDictionary<TKey, TValue>` keyed by the aggregate's identity, with hit/miss/invalidation counters and built-in startup-warmup machinery. The dict persists across HTTP requests. There is no separate store interface, no store class, no `IMemoryCache` for canonical domain data.

**Live example:** `CachingUserService` — Singleton, inherits `TrackedCache<Guid, UserInfo>` (`src/Humans.Base/Caching/TrackedCache.cs`), owns the unified User+Profile read-model spanning 8 contributing tables. See `src/Sections/Humans.Users/Data/CachingUserService.cs` for the canonical pattern (constructor passing `warmOnStartup: true`, scope-factory-resolved inner, `RefreshEntryAsync`, `WarmAllAsync` override, `IUserInfoInvalidator` aliasing).

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

### 15g-bis. Decorator-Integrity Hazards

Architecture tests verify *structure* (namespace, no-DbContext, no-IMemoryCache, required-constructor-deps). They do **not** verify that a caching decorator actually calls its private `RefreshEntryAsync` / dict upsert after delegating a write. The bug class "decorator forgot to invalidate after a write" is silent — stale data persists until process restart, with no error and no timeout. Unit tests of the inner service won't catch it because the inner service is correct in isolation.

Three guards mitigate this:

1. **Singleton identity.** The decorator and its invalidator interface must resolve to the *same* instance (§15e CRITICAL). If two instances exist, in-process invalidation routes to the wrong dict.
2. **Repository-bypass analyzer.** HUM0020 fails the build when a `Caching*Service` structurally references an `I*Repository` type. Decorators are transparent wrappers around the scoped service surface; they do not side-step that service for warmup or refresh.
3. **Integration test through the decorator.** For every cached section, a test writes through the public service interface and reads back through the same interface, asserting the post-write read reflects the change. This is the only automated guard that catches a missing `RefreshEntryAsync` call.

GoogleIntegration's cached projection, if it ever gains a decorator, is reconciliation state (permission maps, group memberships) rather than a table — it would need a state-store shape, not the entity-collection shape used by Users.

### 15h. Adoption Rules

1. **New sections must comply.** New features use the §15 pattern from day one — where a cache is warranted, create the decorator (and, for a table-owning section, the repository) the same day you create the service; where it isn't, Option A (no decorator) is the compliant shape. A tableless section can still carry a decorator over a derived projection (`CachingEarlyEntryService`) — it never acquires a repository just to comply.
2. **EF migration review still applies.** Schema changes still go through the EF migration reviewer agent — the repository layer does not change what migrations look like.

### 15i. Current State (post-G5)

The section-by-section migration this section used to track is **complete** — every section lives in its own project (nobodies-collective/Humans#866), with services in `Humans.<Section>.Services` (`internal` per HUM0034) and, for table-owning sections, repositories in `Humans.<Section>.Data` behind the section's own `<Section>DbContext`. (Tableless sections — e.g. Onboarding, EarlyEntry, Gdpr, Guide — correctly ship neither.) The per-section migration history lives in git and the closed issues (#540–#556, #574–#576, #635, #866).

- **11 caching decorators** (§15 pattern): `CachingUserService`, `CachingTeamService`, `CachingCampService`, `CachingEventService`, `CachingCalendarService`, `CachingConsentService`, `CachingRoleAssignmentService`, `CachingShiftViewService`, `CachingTicketQueryService`, `CachingLegalDocumentSyncService`, `CachingEarlyEntryService` — each in its section project. Every other section is Option A (no decorator).
- **Cross-section reads stitch through service interfaces** (`IUserServiceRead.GetUserInfosAsync` for display names is the canonical example, §6b). Cross-domain `.Include()` is gone from every service; cross-section navigation properties and EF FK constraints are gone from every entity (nobodies-collective/Humans#992, #996) — cross-section linkage is a bare `Guid` column. The one surviving nav is `User.UserEmails`, which the `User.Email` override computes from (#635) — no longer cross-domain at all, since `User` and `UserEmail` are both `Humans.Users.Contracts` types on the same context. `User_HasNoCrossDomainNavigationProperties` enforces the User side; each peeled section's `internal` DbContext enforces the rest structurally.
- **Inline short-TTL `IMemoryCache`** remains appropriate for request-acceleration counters (nav badges, notification meters, claims, shift auth) — never for canonical domain data. `ApplicationServicesTakeNoMemoryCacheRule` allowlists the sanctioned users.
- **`HumansMetricsService`** (`Humans.Web`, operational host service) reads its gauges through section read interfaces resolved per-scrape (`IUserServiceRead`, `ITeamServiceRead`, `IGoogleSyncServiceRead`, …); its former direct `IGoogleSyncOutboxRepository` resolve is gone. It still resolves a couple of full service interfaces (`IRoleAssignmentService`, `ITeamResourceService`) where the Read split hasn't landed — tracked in the debt ledger.
- **External connectors** are their own sections (Stripe, TicketTailor, Holded) or section-internal SDK bridges (GoogleIntegration's `Services/Workspace/` clients, Email's MailKit processor). Only the owning section's csproj references the vendor SDK; contracts stay SDK-free. The one approved exception outside a section: `Humans.Base` references Octokit for the GitHub content sources (`GitHubGuideContentSource`, `GitHubCommunityKbContentSource`), a sanctioned G5 placement in Base.
