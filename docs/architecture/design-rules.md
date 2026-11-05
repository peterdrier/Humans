<!-- freshness:triggers
  src/Humans.Analyzers/**
  tests/Humans.Web.Tests/Architecture/**
  src/Humans.Base/Interfaces/**
  src/Humans.Base/Attributes/**
  src/Humans.Base/Caching/**
  docs/architecture/peters-hard-rules.md
-->
<!-- freshness:flag-on-change
  The regulations under the hard rules. Flag when a rule's enforcement (an analyzer, an architecture test, a Base marker or attribute) changes or moves. A section changing is never a reason to edit this doc: section facts live in that section's Docs/<Section>.md.
-->

# Design Rules

> **Subordinate to [`peters-hard-rules.md`](peters-hard-rules.md).** Those are the constitution — the final word; on any conflict, the hard rules win. This doc is the *regulations*: the constraints every section obeys, why each exists, and what enforces it. Open one rule on demand.

**What belongs here.** A sentence belongs in this doc only if it stays true after any section is added, renamed or deleted. Which section owns which table, implements which handler or decorator, or plugs into which seam is that section's business and lives in its `Docs/<Section>.md`. A section's exception appears here only when other sections must know it to obey the rule. What the base and the Shell hold lives in [`Base.md`](../../src/Humans.Base/Docs/Base.md) and [`Shell.md`](../../src/Humans.Web/Docs/Shell.md). Architectural terms (Section / Crosscut / Orchestrator / Lane / Width) are defined in [`CONTEXT.md`](../../CONTEXT.md); task-triggered atoms live under [`memory/`](../../memory/INDEX.md).

Each rule states the constraint, why it exists, and what enforces it. Where enforcement is review only, it says so. Cite a rule by its heading, not by position.

## Layers and projects

Three kinds of project exist — `src/Humans.Base` (the base), `src/Sections/Humans.<Section>[.Contracts]` (the sections, some with a paired `.Contracts` leaf), and `src/Humans.Web` (the Shell). A section may reference the base and other sections; nothing may reference the Shell.

```
Humans.Base  ←  Humans.<Section>.Contracts  ←  Humans.<Section>
                                           ←  Humans.Web (Shell)
```

**The layers are roles, not projects.** A section assembly holds all four:

| Role | Contains | Forbidden |
|---|---|---|
| **Domain** | Entities, enums, value objects — a section's `Domain/` folder, or its contracts when other sections need the shape | Services, framework references, EF types, DTOs |
| **Application** | Service interfaces and implementations (business logic), repository interfaces, DTOs, authorization handlers — `Services/`, `Authorization/`, `Contracts/` | `DbContext`, `Microsoft.EntityFrameworkCore.*`, HTTP types, external SDKs, direct I/O |
| **Infrastructure** | Repository implementations, caching decorators, the section's `<Section>DbContext` and its migrations, external API clients — `Data/` | Controller logic, Razor, HTTP request/response, business rules |
| **Web** | Controllers, views, view models, API endpoints, DI wiring — `Controllers/`, `Views/`, `Models/`, `Section.cs` | `DbContext`, direct EF queries, direct cache access for domain data, raw SQL |

*Why:* the roles share one assembly, so the project graph no longer keeps EF out of business logic. *Enforced by:* HUM0008 (controller injects a context), HUM0009 (non-repository uses a context), HUM0025 (a table touched by two repositories); `AssemblyScope.IsInSectionDataLayer` is how an analyzer tells a section's `Data/` folder from the rest of it.

**The base has one invariant: no `Humans.*` project reference.** It may take any NuGet package or shared framework it needs (Peter's ruling, 2026-08-15). Anything every section needs lives in it; a type that needs another project's types does not belong in it. What it holds is inventoried in [`Base.md`](../../src/Humans.Base/Docs/Base.md).

**A section is internal by default.** An assembly is a section because it declares `Section : ISection` at its root; its name is the assembly name minus `Humans.` and any `.Contracts` ([`section-identity-is-the-assembly`](../../memory/architecture/section-identity-is-the-assembly.md)). Its only public surface is the `ISection` entry point, its `<Section>Resource` localization marker, EF Core migrations, types the framework requires to be public, types under `Contracts/`, and Hangfire jobs under `Jobs/` — everything else is `internal`. Within a section, `Contracts/` is the public folder and `Interfaces/` the internal one. A section publishes from its `Contracts/` folder; it uses a paired `Humans.<Section>.Contracts` leaf only where the folder would close a project cycle ([`section-project-cycle-fix`](../../memory/architecture/section-project-cycle-fix.md)). Contracts are the whole public face: `I<Section>Service` is the DTO-only read contract, `I<Section>Write` exists only where another section genuinely must write, and everything else is the internal `I<Section>Management` ([`section-public-contract`](../../memory/architecture/section-public-contract.md)). *Enforced by:* HUM0034 fails the build on any other public type in a section assembly.

**Those are two different kinds of exception.** `Contracts/`, `Jobs/`, the entry point and the resource marker are a *deliberate surface*: someone chose to let other assemblies depend on them. `Jobs/` is narrower than `Contracts/`: it admits only `IRecurringJob` implementors and `*Job`-named Hangfire jobs, whose audience is the Shell scheduler, not other sections. The framework carve-out is not a choice: the type is public because it stops working otherwise. **The membership test is whether making it `internal` fails loudly or silently renders nothing** — silent means it belongs in the exception. Compile-time Razor/MVC discovery filters on public accessibility and skips what it cannot see without any diagnostic; runtime resolution throws, so it does not qualify. **Current membership is view components and tag helpers, and that is the whole set.** Controllers look like a candidate and are not: `SectionControllerFeatureProvider` routes internal ones and a missing controller 404s loudly. The cost of getting this wrong is real: an `internal` view component once shipped its tag as inert literal markup and emptied a page, with a green build and every test passing.

**Services reach persistence only through their own section's `Data/` folder.** "Services own their data access" used to mean services injecting `DbContext` directly, which conflated business logic with persistence and made the join ban unenforceable. The rule is "services go through their owning repository".

## Service ownership

Each service is the exclusive gateway to its data. No component — controller, other service, job, or view component — may bypass the owning service to reach its tables or its cache.

**Controllers cannot talk to the database.** Controllers call services. They never inject `DbContext`, never write EF queries, never instantiate repositories directly, never read `IMemoryCache` for domain data. Their job is: receive request → authorize → call service(s) → return response. ASP.NET Identity's `UserManager<User>` / `SignInManager<User>` are Users-internal; everything else signs in and manages passwords by user id through `IUserSignInService`. *Enforced by:* HUM0008, HUM0014 (controller injects a repository), HUM0039 (the `User` entity named outside the base and Users).

**Services live in `Services/`, repositories in `Data/`.** Business services hold rules, workflow, validation and orchestration and never import EF types; they call their owning repository interface, and when they need cached data they go through their owning caching decorator. Repository implementations live in the section's `Data/`. Every application context is `internal sealed`. The repository interface lives in `Data/` beside its implementation, never under `Contracts/` (HUM0035), and no EF type or `IQueryable` appears under `Contracts/` or in a `.Contracts` leaf (HUM0037). Test projects reach contexts through `InternalsVisibleTo`.

**There is one context per table-owning section, plus the platform context.** Each section's `<Section>DbContext` maps only that section's tables, with its own `__EFMigrationsHistory_<Section>` table and its own `Data/Migrations/` folder; `SystemDbContext` (`src/Humans.Web/Data/`) holds the framework-owned tables no section could plausibly own. Consequences:

- **One design-time factory per context, beside it.** Every `dotnet ef` command therefore needs `--context` ([`ef-multi-context-commands`](../../memory/process/ef-multi-context-commands.md)).
- **History-table names are derived, never typed.** `SectionMigrationsHistory.TableFor<TContext>()` (`Humans.Base/Data/`) is the single source for the runtime registration (`AddSectionDbContext`) and the design-time factories.
- **Section contexts apply their configurations explicitly** — no assembly scanning, which would drag in other sections. `DbContextEntityOwnershipTests` (`tests/Humans.Web.Tests/Architecture/`) fails if an `IEntityTypeConfiguration` is applied by zero contexts or by two.
- **Only a baseline is ever fake-applied.** `SectionMigrationRunner` (`Humans.Base/Hosting/`) records a baseline as applied without executing it only when the context's history is empty and its sentinel table exists; a fresh database executes every baseline for real, and post-baseline migrations always run. There is no migrate-down below a baseline: rolling back a peel means reverting the PR.
- **Physical schema must match the model.** A baseline is generated from the model, so anything that exists only physically — raw-SQL triggers, the `DEFAULT` EF scaffolds for a non-nullable `AddColumn` — is silently missing on fresh databases unless carried forward explicitly. `PhysicalDefaultParityTests` and `SectionMigrationRunnerTests` (`tests/Humans.Integration.Tests/`) guard this.
- **`SystemDbContext` is under glass.** Adding a table to it is Peter's call.

**Table ownership is strict and sectional.** Every table belongs to exactly one section context and is read and written by exactly one repository. No other service may query, insert, update or delete rows in tables it does not own: if a service needs another section's data it calls that section's service interface. *Enforced by:* HUM0025 plus the internal contexts, which put the tables out of reach of other assemblies. Which tables a section owns is stated in that section's `Docs/<Section>.md` Architecture block; the context-to-table rollup is in [`service-data-access-map.md`](service-data-access-map.md).

**Cache ownership follows data ownership.** Caching is an internal concern of the owning service. Callers do not know whether data came from memory or the database — they call the service and get the result. The mechanism is the caching decorator (see *Section caching pattern*), never raw `IMemoryCache` calls inlined in service methods.

## Repository layer

Every table-owning section has a narrow, entity-shaped **repository interface** and an EF-backed **implementation**, both in its `Data/` folder. The repository is the single point of EF access for its tables. `IRepository` (`Humans.Base/Interfaces/Repositories/`) is the marker every one extends ([`repository-required-for-db-access`](../../memory/architecture/repository-required-for-db-access.md)).

1. **Entities in, entities out.** Return types are the entity, `IReadOnlyList<T>`, `IReadOnlyDictionary<Guid, T>`, or scalar / id values. Never `IQueryable<T>`, never EF types, never DTOs.
2. **No cross-domain method signatures.** A repository never takes or returns another section's entity, nor accepts a filter that requires joining another section's table. A compound shape is stitched at the service layer from several services.
3. **Bulk-by-ids is first class.** Every repository exposes a `GetByIdsAsync(IReadOnlyCollection<Guid>)` returning a dictionary. This is what makes in-memory joins cheap.
4. **`GetAllAsync` exists for warmup.** At our scale it is trivial and strictly cheaper than lazy loading.
5. **No logging of domain events, no audit, no `IClock`, no caching.** Just persistence. Side effects belong to the service.

Canonical shape:

```csharp
internal partial interface IUserRepository
{
    Task<Profile?> GetByIdAsync(Guid profileId, CancellationToken ct = default);
    Task<Profile?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, Profile>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken ct = default);

    Task AddAsync(Profile profile, CancellationToken ct = default);
    Task UpdateAsync(Profile profile, CancellationToken ct = default);
    Task DeleteAsync(Guid profileId, CancellationToken ct = default);
}
```

## No cross-domain joins

**No EF query may `.Include()` or `.Join()` across a section boundary**, and no section's EF model carries a navigation property or FK constraint into another section's table ([`no-cross-section-ef-joins`](../../memory/architecture/no-cross-section-ef-joins.md)). Cross-section linkage is a bare `Guid` column.

*Why:* cross-domain joins couple caching and invalidation to the database because no single service owns the joined shape. Nothing upstream can safely cache a Team+Profile join or invalidate it when either side changes, and the joining service ends up reading columns it does not own. *Enforced by:* each section's internal `DbContext` and internal entities make the join structurally impossible; review catches the rest.

**In-memory joins are the replacement.** When a caller needs Team + Profile + User together, it asks each owning service for its slice and stitches in memory:

```csharp
var team = await _teamService.GetByIdAsync(teamId, ct);
var userIds = team.Members.Select(m => m.UserId).ToList();
var users = await _userService.GetUserInfosAsync(userIds, ct);

var rows = team.Members.Select(m => new TeamMemberRow(
    UserId:      m.UserId,
    DisplayName: users[m.UserId].BurnerName,
    Role:        m.Role));
```

What you give up: server-side filter or sort on joined columns, and some LINQ elegance. At our scale you filter and sort in memory. What you gain: every table has exactly one writer and one cache, each service is cachable independently, and the table-ownership rule has teeth at query time, not just at write time.

## Cross-service communication

When a service needs data from another section, it calls that section's public service interface via constructor injection. Repositories are never crossed — only the public `I<Section>Service`.

```csharp
// CORRECT — CampService needs people, asks IUserService
internal sealed class CampService(ICampRepository campRepository, IUserService userService) : ICampManagement
{
    public async Task<CampDetailDto?> GetCampDetailAsync(Guid campId, CancellationToken ct)
    {
        var camp = await campRepository.GetByIdAsync(campId, ct);
        if (camp is null) return null;

        var leads = await userService.GetUserInfosAsync(camp.LeadUserIds, ct);
        return BuildDto(camp, leads);
    }
}

// WRONG — reaches into another section's repository
internal sealed class CampService(ICampRepository repo, IUserRepository userRepo) : ICampManagement { }

// WRONG — queries another section's tables through its context factory
internal sealed class CampService(ICampRepository repo, IDbContextFactory<UsersDbContext> factory) : ICampManagement { }

// WRONG — cross-domain Include
var camp = await db.Camps.Include(c => c.Leads).ThenInclude(l => l.Profile).FirstAsync(...);
```

- Cross-service calls are **by id or small parameter set** — `GetByUserIdAsync(Guid)`, `GetByIdsAsync(IReadOnlyCollection<Guid>)`. Never a predicate that pushes another section's schema knowledge into the caller.
- Services return **DTOs or their own domain entities** — never `IQueryable`, never cross-domain entity graphs ([`service-read-no-ef`](../../memory/architecture/service-read-no-ef.md)).
- Circular dependencies are resolved by extracting a shared interface or by an orchestrator, never by a leaf calling back up to a director ([`no-leaf-to-director-callbacks`](../../memory/architecture/no-leaf-to-director-callbacks.md)).

## Decorators versus in-service crosscuts

Not every crosscut belongs in a decorator. The decorator pattern works only for concerns that are **mechanical and context-free** — where the wrapper does not need to know *who* is calling or *why*.

| Concern | Pattern | Why |
|---|---|---|
| Caching | Decorator | Mechanical, context-free |
| Metrics / timing | Decorator | Mechanical, context-free |
| Retry / circuit breaker (external calls) | Decorator | Mechanical, context-free |
| Access logging (GDPR "who viewed what") | Decorator | Mechanical, context-free |
| **Domain audit** (suspended, approved, tier changed) | **In-service** | Needs actor, before/after state, semantic intent |
| **Authorization** | **In-controller** (resource-based handlers) | Needs HTTP identity + resource context |
| **Transactions / unit of work** | **In-repository method** | One repository method = one `SaveChangesAsync` |

**Audit is in-service and called after the business save.** A decorator wrapping `SuspendAsync(userId)` knows neither the actor nor the old state nor whether this was a name edit or a suspension, so the service calls `IAuditLogService.LogAsync(...)` itself. Audit commits separately from the business change and is best-effort: an audit failure never fails the business operation. Therefore call audit *after* the save (a business rollback never leaves a ghost audit row), never expect audit to roll back with a later business step, and never call `SaveChangesAsync` to flush it. How the audit crosscut persists its rows is AuditLog's business ([`AuditLog.md`](../../src/Sections/Humans.AuditLog/Docs/AuditLog.md)); which actions a section writes is declared by that section ([`section-owned-audit-actions`](../../memory/architecture/section-owned-audit-actions.md)).

```csharp
public async Task SuspendAsync(Guid userId, Guid actorId, string reason, CancellationToken ct)
{
    var previousState = await _repo.SetSuspensionAsync(userId, suspended: true, ct);   // business save first

    await _auditLog.LogAsync(                                                           // then audit
        UsersAuditActions.MemberSuspended, nameof(User), userId,
        $"Suspended (was={previousState}): {reason}", actorId);
}
```

**Compound writes that must be atomic** belong in a single repository method that performs both mutations and one `SaveChangesAsync`. Do not orchestrate several repository calls in the service and hope partial failure strands nothing. If audit calls become noisy across one service, the next evolution is domain events raised from the entity — not a decorator.

## Crosscuts, orchestrators and fan-out

**A Crosscut owns its own tables, is injected everywhere, and must not call into any section.** When a crosscut operation needs cross-lane data, an Orchestrator gathers it and calls the crosscut *with* it ([`crosscut-purity`](../../memory/architecture/crosscut-purity.md)). Crosscuts follow the same repository + optional caching-decorator pattern as any other service. Which sections are crosscuts is stated in [`CONTEXT.md`](../../CONTEXT.md).

**An Orchestrator owns no tables and injects no repository.** It coordinates two or more sections through their public interfaces only and carries `IOrchestrator`, a sibling of `IApplicationService`, never both ([`orchestrator-marker`](../../memory/architecture/orchestrator-marker.md)). *Enforced by:* HUM0026 (an orchestrator injects a repository, a context or a context factory), HUM0027 (a type carries both markers).

**Assembling per-user or per-aggregate rows from several sections is a fan-out, and it MUST follow the contributor pattern.** The orchestrator owns no tables, injects `IEnumerable<IContributor>`, calls only the contributor interface, and merges the returned slices. Sections opt in by implementing the interface; each contributor reads only its own tables, and cross-section names flow through the existing `I<Section>Service` contracts. Direct per-section calls from the orchestrator are banned: they couple it to every contributing section and bypass the opt-in registration that keeps the fan-out honest. Iteration is sequential by choice — contributors use their own section's context instances, and EF forbids concurrent operations on one instance.

- **Spokes name themselves.** A contributor declares its own keys and names; there is no central registry of who contributes ([`fanout-spokes-name-themselves`](../../memory/architecture/fanout-spokes-name-themselves.md)).
- **A contributor interface lives in its consumer's contracts.** It lives in the base only when it has more than one consumer and those consumers must not reference each other — `IEntityNameContributor` (`Humans.Base.Interfaces`) is that case: it lets wide-fan-in sections print names without naming any section.
- **One scoped instance serves both interfaces.** Register the service once and forward the contributor interface to it:

  ```csharp
  services.AddScoped<MyService>();
  services.AddScoped<IMyService>(sp => sp.GetRequiredService<MyService>());
  services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<MyService>());
  ```

- **Forward the instance that actually serves the read.** Forward the caching decorator only when the fan-out read is projected from its cached snapshot; otherwise forward the inner scoped service. The orchestrator is Scoped so it resolves either lifetime; forwarding a decorator that does not serve the read buys no caching and adds a hop.

Which fan-outs exist is each hub's business: the hub's `Docs/<Section>.md` names its contributor interface and what it merges.

## Section contributions to the Shell

**A section plugs into the Shell through a seam, never through a by-name edit.** The seams are the `ISectionContribution`-derived interfaces in `Humans.Base/Interfaces` (`ISectionNav`, `ISectionJobs`, `ISectionPolicies`, …); a section implements the ones it needs on its `Section` class as explicit interface implementations, keeps the class parameterless and its contributions stateless, and keeps file-local helpers in `Section.cs` beside it ([`section-contribution-seams`](../../memory/architecture/section-contribution-seams.md)). Discovery finds every implementation by the same dependency walk that finds `ISection` and registers it as a singleton against every seam it implements, so implementing an existing seam needs no Shell edit. *Why:* a composition class that names sections is a dependency from the Shell into every section, and a merge conflict on every feature. The catalog of seams and their composers is in [`Shell.md`](../../src/Humans.Web/Docs/Shell.md).

**Where a seam lives.** A universal seam lives in the base. A seam owned by one section lives in that section's `Contracts/` folder, and on its `.Contracts` leaf only when declaring it in the main assembly would create an assembly loop; either way it derives from the base's `ISectionContribution` so discovery keeps finding it. **Where a composer lives.** Navigational composition (nav, dashboard tiles, jobs, policies, health checks) is the Shell's job. A section that owns a page composes contributions into it itself.

**Ordering.** A descriptor seam's entries carry a `Weight`; every composition sorts by weight alone with a stable sort, so equal weights keep discovery order and no composer adds a tie-break of its own. Set-shaped seams (jobs, health checks, endpoints, policies, roles) have no ordering concept. Several existing orderings encode editorial judgement accumulated over time; composing them must reproduce the existing order, never re-sort by the composer's judgement.

**A descriptor that carries a policy is filtered by the composer, not by the component.** The composer has to know which entries exist before it renders any of them, and the host page must never 404: a user who passes no policy still gets the page with an empty state.

**View component slots are typed.** A contribution carries its view component as a `Type` (`typeof(XViewComponent)`), never a string name, so a renamed or deleted component is a compile error at the contributor rather than a silently blank slot. The seam or property that carries the `Type` declares `[ViewComponentSlot(typeof(TArgs))]`; the host passes an instance of `TArgs` and MVC binds its public properties to the component's `Invoke` parameters by name. **The host page declares and names the slot; the owner contributes the `typeof`.** Within a section, invoke by tag helper (`<vc:…>`), which is why such a component is `public`. *Enforced by:* `ViewComponentSlotContractTests` and `ViewComponentTagHelperBindingTests` (`tests/Humans.Web.Tests/Architecture/`), and HUM0036 on any string-named invocation in C# or `.cshtml` ([`view-component-slots`](../../memory/architecture/view-component-slots.md)).

**The section list is published from DI.** `ISectionCatalog` (`Humans.Base.Interfaces`) derives the set of sections and what each one has from the assemblies. Before hand-maintaining a list of section names, inject the catalog ([`section-list-from-di`](../../memory/architecture/section-list-from-di.md)). A consumer with a *deliberate subset* still owns its list and uses `ISectionAnnotations` to check it is honest: anything that matches no discovered section is logged at startup and shown at `/Debug/Sections` — a warning, never a startup failure, because a stale entry breaks nothing at runtime and failing startup on drift would make a rename un-shippable.

## Personal data obligations

**Every section whose owned tables hold per-user rows MUST implement `IUserDataContributor`** (`Humans.Base/Gdpr/`, namespace `Humans.Gdpr.Contracts`) so the GDPR Article 15 export and Article 17 erasure can be assembled without any cross-section read. A contributor declares its section-name constants on itself, reads only its own tables, always returns the shaped collection (empty when the user has none; `null` only for a single-object slice whose entity does not exist), and keys its `ErasureDeclaration` by every section name it returns. It registers with the forwarding pattern above. *Enforced by:* `GdprExportDependencyInjectionTests` derives the contributor roster by reflection and fails on any unregistered or mis-forwarded contributor. The uncaught case is a section whose service never implements the interface at all: reflection finds nothing and the suite passes vacuously, so review must reject any new user-scoped table whose owning service does not implement the interface in the same PR. The worked recipe is in [`gdpr-export.md`](../features/global/gdpr-export.md).

**Provenance FKs are not user-scoped data.** A table can carry `AddedByUserId`-style columns recording *who acted* without being user-scoped. The test: if you delete the user, do their rows go with them, or do the rows belong to another aggregate and merely lose their actor reference? The latter is provenance, which flows out through the audit log, not a contributor ([`provenance-fks-not-user-scoped`](../../memory/architecture/provenance-fks-not-user-scoped.md)).

**Special-category (GDPR Art. 9) fields are guarded by convention, not by type.** Such a field rides on the cached read model as a plain value, so any code holding the read model has it in memory. Three parts keep it contained, and all three are load-bearing: outbound DTOs omit the field by construction and say why in their XML doc; each such surface pins the omission with a test that reflects over the DTO and serializes a loaded result; write paths document the caller's authorization obligation, and the owning section's doc carries the matching negative access rules. A wrapper type (`Sensitive<T>`, forcing a policy token to read) was considered and rejected as over-engineering at this scale — but it is the designated fallback: **if such data ever leaks through a serializer, the fix is the wrapper type, not another one-off test.** There is no analyzer for this today.

## Authorization

Authorization uses **ASP.NET Core resource-based authorization** — one pattern, everywhere. Controllers call `IAuthorizationService.AuthorizeAsync(User, resource, requirement)`; handlers hold the logic; services are auth-free and trust the caller, with one narrow exception below.

```csharp
var authResult = await _authorizationService.AuthorizeAsync(User, category, BudgetOperationRequirement.Edit);
if (!authResult.Succeeded) return Forbid();
await _budgetService.DeleteLineItemAsync(id);
```

- **A section with resource-scoped checks owns a `*OperationRequirement` + `*AuthorizationHandler` pair** in its `Authorization/` folder, listed in its `Docs/<Section>.md`. No handler lives in the Shell; the Shell holds only policy registration and claims transformation. Don't invent a new pattern.
- **No `isPrivileged` booleans.** Don't pass auth decisions as parameters to services. If the controller maps it wrong, the service silently does the wrong thing.
- **No inline `IsInRole` chains in controllers** for resource-scoped checks — use the handler. Route-level role gates use `[Authorize(Policy = PolicyNames.X)]`, never raw `[Authorize(Roles = "...")]` strings. Views use the `authorize-policy` tag helper; reusable views and components that need conditional logic inject `IAuthorizationService` ([`auth-in-views-self-resolving`](../../memory/code/auth-in-views-self-resolving.md)).
- **Services are auth-free by default.** They don't check roles, don't inject `IHttpContextAccessor`, don't receive privilege flags. Authorization happens before the service is called.
- **Exception: full-Admin destructive deletes.** A service may inject `IAdminAuthorizationService` and call `RequireCurrentUserIsAdminAsync` only for a method that permanently deletes data or performs a destructive reset, and whose rule is exactly "must hold the full `Admin` role". The action must still carry `[Authorize(Policy = PolicyNames.AdminOnly)]` or stricter. Never for resource-scoped auth, reads, ordinary edits or privilege flags.
- **Roles are section-owned.** A grantable role is declared by its section through `ISectionRoles`; the base holds only the cross-section names ([`roles-are-section-owned`](../../memory/architecture/roles-are-section-owned.md)).

**Do not push authorization into services.** Two attempts shipped service-layer authorization and both crashed DI validation on startup: `Service → RoleAssignmentService → IAuthorizationService → Handler → Service` is a cycle, and the hot-fix that hid it (lazy `IServiceProvider` resolution) hid the cycle from the validator rather than removing it. The unwind moved `AuthorizeAsync` back to controllers. Service-layer auth gives no real defence in depth here: one UI, no public API, background jobs are trusted server code, controllers are the only human-facing mutation path. Do not reopen this without changing this section first.

## Append-only entities

Some entities are append-only: new state = new row. **Never add UPDATE or DELETE logic for an append-only entity.** Its repository interface exposes `AddAsync` and reads, never `UpdateAsync` or `DeleteAsync`.

**Only two tables are enforced by database triggers, and that is doctrine:** `audit_log` and `consent_records`, because an unforgeable history is the point of each ([`db-enforcement-minimal`](../../memory/architecture/db-enforcement-minimal.md), [`consent-record-immutable`](../../memory/architecture/consent-record-immutable.md)). Every other append-only table is append-only by convention, and which tables those are is each section's business, stated in its `Docs/<Section>.md`. A section peel must carry its triggers forward in its baseline — dropping them silently converts a DB-enforced invariant into a convention.

**Per-user reads over append-only history must follow the merge chain.** Account merge folds a source user into a target by re-FKing live rows, but append-only history stays at the source id by design. A per-user read of such a table therefore queries over `UserInfo.AllUserIds` of the record `IUserService` returned (the survivor first, then its merged ids), never over the id it was given. Users resolves and stamps that list on every `UserInfo` it hands out, so no other section decides what a merged id means.

## DTO and view model boundary

- **Domain entities** live in their section's `Domain/` folder, or in its contracts when another section needs the shape. They are mutable, have identity, carry invariants, and never reference EF types. The one globally relevant exception: `User` and `UserEmail` live in the base (`Users/`) because every section reads them; their tables and EF mapping stay in Users, and naming them anywhere else is HUM0039.
- **DTOs** live in `Services/Dtos/` or `Contracts/`. They are read-optimized shapes for specific use cases. Services return DTOs when the shape is call-specific; they return entities when the caller needs the full aggregate and is inside the section.
- **View models** live in a section's `Models/` folder or inline in controllers. Controllers map DTOs or entities to view models for Razor. The section-agnostic ones (the generic table models, `PagerViewModel`) live in the base.
- **Domain entities should not leak into Razor views** when a DTO gives better separation. Simple 1:1 cases are acceptable; anything that would have required `.Include` for navigation is not.
- **View components** are Web-role code: a section's `ViewComponents/`, or the base's for the section-agnostic ones. They call services, not repositories, and are one of the two framework carve-outs HUM0034 allows a section to declare `public`.

## Section caching pattern

The `TrackedCache` decorator stack is the caching architecture for any section that warrants one. Not every section needs it — reach for it only when traffic or bulk-read patterns justify an in-memory projection; a low-traffic section correctly talks to its repository per request. `CachingUserService` (`src/Sections/Humans.Users/Data/`) is the reference implementation. Short-TTL request-acceleration caches (nav badge counts, meters) are a different, sanctioned shape covered at the end.

### The stack

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

The decorator is optional in the sense that removing it leaves the system fully functional: the inner service implements every method against the DB, except declared cache-only reads. The decorator is a pure performance layer. It is registered keyed-inner + factory-forward: the inner is registered against the interface under a key (`AddKeyedScoped<IUserService, UserService>(CachingUserService.InnerServiceKey)`), the decorator as itself, and the interface forwarded to the decorator, so callers inject the interface and get caching transparently. Every section registers its own DI from its `Section.cs`.

### Repository rules

Repositories are **Singleton** because they inject `IDbContextFactory<TContext>` rather than a context; every method creates and disposes its own short-lived context. `DbContext` is not thread-safe, holds a connection and accumulates tracked entities, so it must be short-lived, and the factory creates isolated contexts without scope-factory indirection.

```csharp
public async Task<Profile?> GetByUserIdReadOnlyAsync(Guid userId, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    return await ctx.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct);
}
```

Read method naming: `*ReadOnlyAsync` is `AsNoTracking()` and returns detached entities; `*ForMutationAsync` tracks, for a caller that will mutate and call `UpdateAsync` on the same repository in the same method.

### Inner service rules

The application service is `internal sealed` in its section's `Services` namespace. It:

- Injects repository interfaces, never `DbContext`.
- Never imports `IMemoryCache` or any caching abstraction; it is cache-unaware. One exception (below): it may call its own section's concrete decorator to evict where ordering matters.
- When the section has a decorator: is registered **Scoped** and **keyed** under the decorator's `InnerServiceKey`, so the Singleton decorator resolves fresh instances per call without self-resolution.
- Implements **every** read method against the DB, with one carve-out: **cache-only reads** — projections served from the warmed snapshot with no repository equivalent, such as relevance-ranked search — throw `NotSupportedException` in the inner so a DI mis-registration fails loudly instead of silently bypassing the cache. Never return empty results "so the decorator can override".
- Owns its sub-aggregates: a child collection is written through the parent's service and the parent repository owns the reconcile logic.

### Decorator rules

A section's caching decorator is a **Singleton** that inherits or composes `TrackedCache<TKey, TValue>` (`Humans.Base/Caching/`): a primitive owning a private `ConcurrentDictionary` keyed by the aggregate's identity, with hit/miss/invalidation counters and startup-warmup machinery. There is no separate store interface, and no `IMemoryCache` for canonical domain data.

- **A decorator talks only to its inner.** It must not reference repository types (HUM0020); misses, warmup and per-key refreshes resolve the keyed inner through `IServiceScopeFactory`, never captured in a field ([`decorators-talk-only-to-inner`](../../memory/architecture/decorators-talk-only-to-inner.md)).
- **Reads:** a dict hit returns synchronously via `TryGet`; a miss falls through `GetAsync` → `LoadRowAsync` and populates the dict only if no cache mutation intervened. Load-all reads `await EnsureWarmedAsync(ct)`, which drives `WarmAllAsync` on demand when cold and is a no-op once warmed.
- **Writes:** delegate to the inner, then replace the affected entry via `Replace(key, value)` or `ReplaceAsync(key, ct)`. If reloading fails, `ReplaceAsync` discards the old entry and marks a warmed cache cold; the next read retries from source. Bare `Invalidate(key)` is for lazy-per-key caches or for tombstoning a confirmed deletion — on a warmed cache, removing without replacing breaks the all-rows invariant.
- **Warming:** `TrackedCache` implements `IHostedService`. Register the decorator as a hosted service (`services.AddHostedService(sp => sp.GetRequiredService<CachingFoo>())`); no separate warmup class. A startup-warmup failure is recovered by the next load-all read.
- **Caching a legitimate negative result needs manual `TryGet`/`Set`.** `GetAsync` only calls `Set` when the loader returns non-null, so it never caches not-found; a decorator whose value type is nullable and where "no result" is a real answer stores the null explicitly.
- **Decorators contain zero business logic.** A decision beyond "is it in the dict?" belongs in the service.

### The cache owner evicts

Only the section that owns a cache evicts it. There is no cross-section invalidator interface: HUM0028 flags any interface extending `IInvalidator`, and the family is at zero.

- **Own writes:** the decorator replaces after delegating, or the inner calls its own section's concrete decorator when eviction must happen at a specific point. Neither crosses a section boundary.
- **Another section's change makes the view stale:** use a signal the owner already receives, or read that data live from the other section's cached service instead of copying it. A cached copy of another section's data that nothing tells you about is a design smell; a short TTL is the fallback, not an invalidator.
- If none of those fits, stop and ask Peter. Don't add an `I*Invalidator` or a cache-clearing method on an `I*Write` interface.

### Read-model naming

| Type | Name |
|------|------|
| Canonical section read model | `<Section>Info` (`UserInfo`, `TeamInfo`) |
| Read method | Matches the read model when returning one item (`GetUserInfoAsync`, `GetTeamAsync`); plural collections use the natural plural. Load-all methods used by decorators are normal read methods (`GetAllEventInfosAsync`), never cache-shaped `ForCache` helpers |
| Sub-aggregate collections | Natural plural on the DTO (`VolunteerHistory`, `ContactFields`) |

The `Info` projection is the boundary: no EF entity type leaves a section's read API, and no DTO is named `Cached*` ([`caching-transparent`](../../memory/architecture/caching-transparent.md)).

### Decorator-integrity hazards

Architecture tests verify structure (no `DbContext`, no `IMemoryCache`, required constructor dependencies). They do **not** verify that a decorator actually refreshes its entry after delegating a write; that bug is silent — stale data until restart, no error, no timeout — and unit tests of the inner won't catch it because the inner is correct in isolation. Three guards:

1. **Singleton identity.** Every interface the decorator serves, and any concrete injection of it for same-section eviction, resolves to the *same* instance; a second instance makes evictions land in the wrong dict.
2. **Repository-bypass analyzer.** HUM0020 fails the build when a `Caching*Service` references an `I*Repository`.
3. **Integration test through the decorator.** For every cached section, a test writes through the public interface and reads back through it, asserting the post-write read reflects the change. This is the only automated guard for a missing refresh.

### Adoption and the sanctioned short-TTL shape

New features use this pattern from day one: where a cache is warranted, create the decorator (and, for a table-owning section, the repository) the day you create the service; where it isn't, a plain repository + Scoped service is the compliant shape. A tableless section may carry a decorator over a derived projection; it never acquires a repository just to comply. Schema changes still go through the EF migration reviewer — the repository layer changes nothing about migrations.

Inline short-TTL `IMemoryCache` remains appropriate for request-acceleration counters (nav badges, notification meters) — never for canonical domain data. `ApplicationServicesTakeNoMemoryCacheRule` (`tests/Humans.Web.Tests/Architecture/Rules/`) allowlists the sanctioned users; a new one is a reviewed addition to that list, not a new pattern.
