# G5 Section-Project Split — Mechanics Design (nobodies-collective/Humans#866)

**Pilot section: Store.** Design only — no code changed by this doc.

#866's *policy* is settled and is not revisited here: `internal` by default with the public
surface confined to `Contracts/`; cross-section deps are assembly references and cycles are
compile errors; `<Section>.Contracts` carved only when the build forces it; primitives-only
kernel with no DTOs; the keystone analyzer; shared-contract exceptions limited to
`User`/`UserInfo`, Auth, Audit; strangler rollout one section per PR with Users/Teams last.

This doc settles the *mechanics* — the things you only learn by planning a real cut — and
records the shape decisions Peter took on 2026-08-07 (§12).

Audited at `ba2323f15` (origin/main, 2026-08-07).

---

## 0. The shape

Three tiers, dependencies pointing one way:

```
Humans.Shell  →  Humans.<Section>  →  Base (Humans.UI, Humans.Interfaces, Humans.Domain,
   (today's         (Store first)            Humans.Application, Humans.Infrastructure,
   Humans.Web)                               Humans.Analyzers)
```

Shell owns the composition root, the middleware pipeline and the nav. A section owns its full
vertical. Base holds everything two or more of them share. **Nothing in Base ever references a
section, and no section ever references Shell** — that is the whole boundary, and the compiler
enforces it once the projects exist.

**Now:** sections are not optional and Shell references each one directly. Nav stays hardcoded in
Shell. `AddStoreSection()` is called by name from `Program.cs`, exactly as today.

**Later, after the migration completes:** sections become optional. Shell stops naming them and
instead discovers registered implementations of a Base contract — by reflection or configuration —
so different organisations enable the sections they want. That work is explicitly **out of scope
until every section has moved**; it is recorded here only so the pilot does not foreclose it. The
one thing it will need that today's design lacks is section-contributed nav (§7), because a
section Shell cannot name is a section Shell cannot link to.

**Longer term still:** these projects are expected to ship as NuGet packages, so organisations can
assemble the subset they need rather than take the whole application. That is not pilot work
either, but it settles two naming decisions now (§2, §14): a package's assembly name and its root
namespace must agree, and a package needs a name that makes sense standing alone.

---

## 1. Empirical basis

Question 1 below (views) could not be settled by reading this repo, so it was settled by
experiment: a throwaway ASP.NET Core 10 host + Razor Class Library, built and run outside the repo,
probing each behaviour the cut depends on. Results are measurements, not recollection.

| Probe | Result |
|---|---|
| RCL discovered as an application part with no `AddApplicationPart` | **Yes** — `/_parts` lists `SectionLib` twice (assembly part + compiled-razor part) |
| RCL controller routes resolve under the host's default route | **Yes** |
| Host `Views/_ViewStart.cshtml` applies to an RCL view | **Yes** — host layout rendered |
| Host `Views/Shared/_Layout.cshtml` resolvable from an RCL view | **Yes** |
| Host `Views/Shared/_HostPartial.cshtml` usable via `<partial>` from an RCL view | **Yes** |
| RCL-local `Views/<Ctrl>/_ViewStart.cshtml` overrides the host's root `_ViewStart` | **Yes** — picked the alternate layout |
| Host `Views/_ViewImports.cshtml` applies to RCL views | **NO** — `asp-controller`/`asp-action` emitted as literal attributes, **build succeeded with 0 warnings** |
| RCL-local `Views/_ViewImports.cshtml` fixes that | **Yes** — `<a href="/">` |
| `AddRazorRuntimeCompilation()` hot-edits an RCL view | **NO** |
| …with `MvcRazorRuntimeCompilationOptions.FileProviders.Add(new PhysicalFileProvider(sectionDir))` | **NO** — provider resolved the file (`exists=True`, correct physical path) and the precompiled view still won |
| …on a *fresh boot* with the source edited and the DLL stale | **NO** — served the stale precompiled view |
| `dotnet watch run` hot-reloads an RCL `.cshtml` edit | **Yes** — "C# and Razor changes applied in 580ms" |

Two findings drive the whole design:

1. **`_ViewImports` is compile-time and per-project.** A view moved into a section assembly
   without a section-local `_ViewImports.cshtml` produces *silently wrong HTML* — no build error,
   no warning, no runtime error. This repo's `Views/_ViewImports.cshtml` injects
   `IStringLocalizer<SharedResource>` and registers `@addTagHelper *, Humans.Web`, so every moved
   view depends on it. **This is the pilot's single most dangerous failure mode.**
2. **`Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation` cannot serve section views.** Precompiled
   RCL views win unconditionally. Its dev-loop value therefore drains away section by section.
   `dotnet watch` replaces it and is strictly better (it also covers C#).

---

## 2. Views — Razor Class Library per section

**Decision: one RCL per section — the section project itself, `Microsoft.NET.Sdk.Razor` with
`<AddRazorSupportForMvc>true</AddRazorSupportForMvc>`. Not application parts, not a separate
view project.**

Application parts were the alternative in #866's open-decisions list. They lose: the RCL *is* an
application part (auto-discovered, §1), so "application parts" is not a distinct option — it is
what you get for free. A separate `<Section>.Views` project only adds a project per section for no
boundary the section project doesn't already give.

### What this repo has today

`src/Humans.Web` holds 400 `.cshtml` files. `Views/_ViewImports.cshtml` is the load-bearing one:

```
@using Humans.Web                     ← SharedResource
@using Humans.Web.Extensions          ← DateTimeDisplayExtensions, EnumLocalizationExtensions…
@using Humans.Web.Models              ← view models
@using Humans.Web.ViewComponents
@using Humans.Domain.Entities / Enums
@inject IStringLocalizer<SharedResource> Localizer
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
@addTagHelper *, Humans.Web           ← authorize-policy, page-header, markdown-editor, nonce
```

Store's six views (`Views/Store/{Index,Order}.cshtml`,
`Views/StoreAdmin/{Catalog,CatalogEdit,Payments,Summary}.cshtml`) use, from outside the section:

- `Localizer[...]` and `Localizer.EnumDisplay(...)` — every view
- `authorize-policy` tag helper (`Humans.Web.TagHelpers.AuthorizeViewTagHelper`)
- `<partial name="_Table" />` with `Humans.Web.Models.Tables.TableModel` — five of six views
- `<partial name="_ValidationScriptsPartial" />`
- `<vc:audit-log>` view component (Audit horizontal)
- `_Layout` / `_AdminLayout` via `Views/_ViewStart.cshtml` and `Views/StoreAdmin/_ViewStart.cshtml`

Per §1, the layouts, the shared partials and the view components all keep working across the
assembly boundary unchanged. **Only the `_ViewImports` payload has to be reachable from the
section project** — and reaching it in `Humans.Web` would mean `Store → Shell`, the one direction
the model forbids.

### `Humans.UI` — the one new Base project

The shared view layer moves out of `Humans.Web` into `src/Humans.UI`, a Razor Class Library in
Base that both Shell and every section reference:

| Moves to `Humans.UI` | Why |
|---|---|
| `Resources/SharedResource.{cs,resx,es,ca,de,fr,it}` (2 617 keys) | `@inject IStringLocalizer<SharedResource>` |
| `TagHelpers/` (4 files) | `@addTagHelper *, Humans.UI` |
| `Models/Tables/` (5 files) | `_Table` partial model |
| `Views/Shared/` (52 partials + `_Layout`, `_AdminLayout`, `_GateLayout`, `_GuideLayout`) | cross-section partials |
| Cross-section view components | `<vc:*>` used by more than one section |
| Display extensions (`DateTimeDisplayExtensions`, `EnumLocalizationExtensions`, `StatusBadgeExtensions`, `PageSizeExtensions`, `HtmlHelperExtensions`) | referenced from views |
| `Authorization/PolicyNames.cs` | section controllers name policies |

`RoleNames` already lives in `Humans.Domain` and needs no move.

**Scope: minimal plus the unambiguously shared.** Move what Store's six views need, plus anything
adjacent that is obviously cross-section, and let `Humans.UI` grow over the first few sections.
Sizing it comprehensively up front would be guessing at a convergence point nobody can predict
(§11.1).

`ViewComponents/` is the one messy part: of the 31, several inject section services
(`MyCampsViewComponent`, `TicketHoldingsViewComponent`, `ShiftSignupsViewComponent`, …) and cannot
sit in a leaf Base project without dragging those sections upward. **Only the ones a *different*
section's views use must move**; a view component used by exactly one section moves *into* that
section at its own G5. Store uses one: `<vc:audit-log>`, which injects `IAuditLogService` — a
horizontal, so `AuditLogViewComponent` moves cleanly.

### Section-local `_ViewImports.cshtml` — mandatory, and the checklist item that must not be skipped

`src/Sections/Humans.Store/Views/_ViewImports.cshtml`:

```
@using Humans.Store
@using Humans.Store.Models
@using Humans.UI
@using Humans.UI.Models.Tables
@using Microsoft.Extensions.Localization
@inject IStringLocalizer<StoreResource> Localizer
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
@addTagHelper *, Humans.UI
```

`Localizer` is bound to the *section's* resource set (§3), so the view bodies are untouched by
the move.

Omitting it, or omitting one `@addTagHelper` line, ships broken markup with a green build. The
only mechanical guard is a rendered-output assertion — see §8 step B8 and §10.

### Dev loop

`AddRazorRuntimeCompilation()` stops covering any view that has moved (§1), so
`dotnet watch run --project src/Humans.Web` becomes the dev command. Measured at 580 ms for a Razor
edit in a referenced RCL — comparable to a page refresh today, and it covers C# too. Delete the
`Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation` package and its
`if (builder.Environment.IsDevelopment())` block at **G6**, not at the pilot: until the last view
leaves `Humans.Web` it still serves the views that remain.

---

## 3. Localization

**Every section owns its own `.resx` set. Nothing a section needs stays outside it.**

`Humans.UI` keeps only the vocabulary the *shared* surface renders — layout, nav, footer, the
`Views/Shared` partials, common form/button words. Everything else moves into the section that
uses it, and `Humans.UI`'s set shrinks with each section that carves.

The carve is mechanical: **all 2 617 keys are already section-prefixed, with zero unprefixed
keys.** Prefix counts run `Profile_` 118, `Shifts_` 108, `CityPlanning_` 103, `Camp_` 98,
`Enum_` 96 … down to `Store_` 23. Store's full share is **25 keys** — its 23 `Store_*` plus
`Enum_StoreOrderCounterpartyType_{Camp,Team}` — across six languages, so 150 resx entries. Small
enough that the pilot establishes the pattern without the pattern being the risk.

A string two sections both want gets **duplicated**, per #866's own rule: duplication inside two
sections is healthier than a coupling no one owns. Promoting it to `Humans.UI` is only correct
when the *shared* surface renders it.

### Per-section shape

`src/Sections/Humans.Store/Resources/StoreResource.cs` (`namespace Humans.Store`) with
`StoreResource.resx` + `.es/.ca/.de/.fr/.it` beside it; the section's `_ViewImports` injects
`IStringLocalizer<StoreResource>` as `Localizer`, so no view body changes — the keys and the
`Localizer[...]` call sites are untouched by the move.

Three pieces of shared machinery are hardcoded to the single resource type and must widen first:

- **`EnumLocalizationExtensions`** — `EnumDisplay<TEnum>` and `EnumSelectItems<TEnum>` extend
  `IStringLocalizer<SharedResource>` specifically. Widen both to `this IStringLocalizer`;
  `IStringLocalizer<T>` implements `IStringLocalizer`, so every existing call site compiles
  unchanged and any section's localizer works. The `Enum_{TypeName}_{Value}` key convention is
  unaffected — a section's enum keys simply live in the section's resx.
- **`DataAnnotationLocalizerProvider`** (`Program.cs:473`) routes *every* `[Display]`/`[Required]`
  lookup through `Humans.Web.SharedResource`. It becomes convention-routed on the model type's
  assembly: a view model in `Humans.Store` resolves `factory.Create("StoreResource",
  "Humans.Store")`, falling back to the `Humans.UI` set. One lambda.
- **resx parity (#848)** becomes per-resource-set rather than one comparison. The check itself is
  unchanged; it just runs N times.

### The non-obvious mechanic that makes every one of these moves risky

`SharedResource.cs` sits at `Resources/SharedResource.cs` with `namespace Humans.Web;` — note the
namespace does **not** include `.Resources`. The `.resx` files sit beside it. This works because
the SDK's `EmbeddedResource` `DependentUpon` convention derives the manifest name from the
same-named adjacent `.cs` file's namespace, yielding `Humans.Web.SharedResource.resources` rather
than the path-derived `Humans.Web.Resources.SharedResource.resources`.
`builder.Services.AddLocalization()` is called with **no** `ResourcesPath`, so nothing else
corrects for it.

**Consequence:** in every project — `Humans.UI` and each section — the `.cs` and the `.resx` must
sit in the same folder, and the `.cs` namespace determines the resource prefix. Get it wrong and
every localized string in that set falls back to its key at runtime. The startup diagnostic block
at `Program.cs:531-570` exists because this has bitten before; **extend it to assert one known key
per registered resource set**, so a botched section carve fails at boot instead of shipping a page
of raw keys.

### Decision: `Humans.UI`'s own resource renames to `namespace Humans.UI`

The alternative — keeping `namespace Humans.Web` inside assembly `Humans.UI`, as
`Humans.Interfaces` deliberately does with `Humans.Application.*` — avoids touching ~30
`IStringLocalizer<SharedResource>` constructor sites. It was rejected on the packaging ground
(§0): a NuGet package named `Humans.UI` whose public type sits in `Humans.Web` is a defect the
moment anyone consumes it standalone. The `Humans.Interfaces` trick was cost-avoidance for an
internal-only assembly and that justification expires at packaging.

So the move is: files to `src/Humans.UI/Resources/`, namespace to `Humans.UI`, ReSharper
move-to-namespace across the call sites, plus one string literal —
`EmailRenderer.cs:21` calls `localizerFactory.Create("SharedResource", "Humans.Web")` where the
second argument is the **assembly** name, and becomes `"Humans.UI"`. (Email body strings are
`Email_*`-prefixed and belong to whichever section sends the mail; they follow their sections
later, not in PR 0.)

(Follow-up, not pilot: `Humans.Interfaces` has the same mismatch — `Humans.Application.*`
namespaces in a differently-named assembly — and wants a real name and matching namespace before
it ships as a package.)

### The localization sweep is unaffected

`LocalizationCoverageSweep` boots the app through `PseudoLocalizationWebApplicationFactory`, which
does `services.RemoveAll<IStringLocalizerFactory>()` and substitutes a pseudo-localizer, then
crawls routes discovered from `IActionDescriptorCollectionProvider` (`SweepRouteCatalog.cs:43`).
Both mechanisms are assembly-agnostic: section controllers appear in the action-descriptor
collection automatically (§1), and the substituted factory intercepts every lookup regardless of
which assembly the resx lives in. **No sweep changes are needed at any point in the rollout.**

---

## 4. Static assets

Same rule as §3: **a section's assets live in the section.** An RCL's `wwwroot/` is published as a
static web asset automatically and served at `_content/<AssemblyName>/…` — so
`Humans.Gate/wwwroot/gate/x.js` becomes `/_content/Humans.Gate/gate/x.js`. The URL change is made
in the same PR as the move. Only Shell's own chrome assets (`site.css`, `site.js`,
`client-metrics.js`, favicons, `img/`) stay in Shell's `wwwroot/` — they belong to the layout, not
to any section.

**Nothing to do for the pilot.** `find src/Humans.Web/wwwroot -iname "*store*"` returns nothing and
Store's views reference no section-specific CSS or JS. `wwwroot/js/` already has per-section folders
(`agent/`, `city-planning/`, `gate/`, `scanner/`) that will move with *those* sections.

---

## 5. Tests

**Store's tests move into `tests/Humans.Store.Tests`, with `InternalsVisibleTo` from the section
project.** Today they are spread across four assemblies:

| Today | Disposition |
|---|---|
| `Humans.Application.Tests/Services/Store/*` (8 files) | → `Humans.Store.Tests` |
| `Humans.Application.Tests/Repositories/StoreRepositoryTests.cs` | → `Humans.Store.Tests` |
| `Humans.Domain.Tests/Entities/StoreEntityDefaultsTests.cs` | → `Humans.Store.Tests` |
| `Humans.Web.Tests/Authorization/StoreOrderAuthorizationHandlerTests.cs` | → `Humans.Store.Tests` (the handler moves too, §7) |
| `Humans.Application.Tests/Architecture/StoreArchitectureTests.cs` | **deleted** — see §10 |
| `Humans.Integration.Tests/Controllers/Store*Tests.cs` (5 files) | **stay put** — host-level end-to-end, the one assembly allowed to see many sections |

`tests/Directory.Build.props` supplies xunit, `HumansFact`/`HumansTheory`, the banned-`[Fact]`
analyzer and the shared runner config to any project under `tests/`, so a new section test project
inherits the whole harness with an empty `<ItemGroup>`. Add the project to `Humans.slnx`.

`src/Sections/Humans.Store/Humans.Store.csproj` gains
`<InternalsVisibleTo Include="Humans.Store.Tests" />` — mirroring what `Humans.Application.csproj`
and `Humans.Infrastructure.csproj` already do.

### EF-InMemory: carried, not fixed

The G0 audit records `StoreRepositoryTests.cs:18` on `.UseInMemoryDatabase(...)` as a G3
predicate-1 FAIL. **That finding is void.** Peter's standing rule: EF-InMemory is fine, never push
Postgres fixtures, stop scoring G3 predicate 1 — the DB does nothing complicated by design and a
real provider catches nothing here. The file moves verbatim; only its `DbContext` type changes from
`HumansDbContext` to `StoreDbContext`, and that change belongs to the G4 peel (§8 step A3), not the
project move. The G0 audit's G3 gap-list row and the plan's G3 predicate 1 should be struck in a
separate docs pass.

---

## 6. DI composition — `ISection`, discovered

**Every section exposes one registration type in the same place, and Shell finds them rather than
naming them.** `src/Humans.Web/Extensions/Sections/` holds 35
`internal static Add<Section>Section(this IServiceCollection)` files today, each called by name
from `Program.cs`. That file moves into the section and becomes an `ISection` implementation:

```csharp
// src/Sections/Humans.Store/StoreSection.cs
namespace Humans.Store;

public sealed class StoreSection : ISection
{
    public void Register(IServiceCollection services)
    {
        services.AddSectionDbContext<StoreDbContext>(sentinelTable: "store_orders");
        services.AddSingleton<Repository>();
        services.AddScoped<Service>();
        services.AddScoped<IAuthorizationHandler, OrderAuthorizationHandler>();
    }
}
```

`ISection` lives in `Humans.Interfaces` — a leaf Base project with no references, which is exactly
where a marker consumed by both Shell and every section belongs.

It is the **only** `public` non-`Contracts/` type in the section, so the keystone analyzer needs a
carve-out for one `public sealed class <Section>Section : ISection` at the project root, or the
first section fails its own rule.

### Why an instance class, not `static class Section` with `AddSection()`

- **Static classes cannot implement interfaces**, so a static entry point cannot be discovered.
  C# 11 static abstract members don't help: they are consumed through generic constraints, which
  requires the type at compile time — the opposite of discovery. An instance class costs nothing
  (parameterless, `Activator.CreateInstance`).
- **A same-named `AddSection()` extension in 35 assemblies is CS0121-ambiguous** the moment Shell
  has two `using`s in scope, forcing `Humans.Store.Section.AddSection(services)` at every call
  site. Discovery makes the question moot — nobody calls it by name.
- **A type named `Section` collides with `[Section("…")]`.** `SectionAttribute`
  (`Humans.Domain/Attributes/SectionAttribute.cs`) would be in scope alongside a `Section` type in
  the section's own namespace, and attribute usage in those files stops compiling. `<Section>Section`
  avoids it.

### Discovery works now, with hard references

MVC already finds section controllers and views by walking the entry assembly's `DependencyContext`
(§1 measured it: the RCL was discovered with no `AddApplicationPart`). The same walk finds
`ISection` implementations. So `Program.cs` drops its 35-line roll-call **at the pilot** — roughly
fifteen lines of discovery replace it — while ProjectReferences stay hard-coded exactly as decided
in §12.2. Later optionality is then only a change of where the assembly list comes from: a config
allowlist, or `AssemblyLoadContext` over a plugin folder. No section code changes.

Two consequences to handle in the same PR:

- **Registration order becomes assembly-enumeration order.** Sort by section name for determinism.
  Nothing currently depends on the hand-written order:
  `DatabaseMigrationHostedService` migrates section contexts in registration order, but #858 §6
  establishes that no section baseline contains a cross-section FK, so the contexts are independent.
- **A section that fails to load is now silently absent**, where #866 wanted "a startup error
  naming the section". Shell logs the discovered set at boot; that log is what you check when a
  page 404s.

### The dependency graph is derived, not declared

Once sections are optional, Shell must know that enabling Store requires Camps, Teams and Shifts.
Do **not** put a `DependsOn` list on `ISection`: it duplicates a fact the compiler already owns and
will drift. `assembly.GetReferencedAssemblies()` filtered to assemblies carrying
`[assembly: Section("…")]` (§10) *is* the graph — zero maintenance, cannot go stale, and yields
both the topological registration order and a boot-time "Store requires Camps, which is not
registered" instead of a DI resolution failure deep in the stack.

Only needed when sections become optional. The derivation is worth writing down now so nobody adds
a hand-maintained list in the meantime.

### Two things must change for this to compile

- **`AddSectionDbContext<TContext>` becomes `public`.** It is `internal` today
  (`InfrastructureServiceCollectionExtensions.cs:71`) and depends on `NpgsqlDataSource`,
  `QueryMonitoringInterceptor` and `UserInfoSaveChangesInterceptor` from `Humans.Infrastructure`.
  Since `Humans.Infrastructure` is Base and will never reference a section, `Store →
  Humans.Infrastructure` is a legal Base reference — so it stays where it is and simply goes
  public. It relocates later, when Infrastructure splits (§14), not now.
- **`StoreOrderAuthorizationHandler`'s registration moves** out of
  `AuthorizationPolicyExtensions.cs:27` into the section. The *policy*
  `PolicyNames.StoreCatalogAdmin` (`AuthorizationPolicyExtensions.cs:125`) stays in Shell — §8.

---

## 6a. Naming inside a section

Once the vertical is one assembly and everything in it is `internal`, the `Store` prefix on
internal types is stutter — they are already in Store. Drop it. Three categories cannot follow,
each for a concrete reason.

| Names | Rule |
|---|---|
| `StoreRepository` → `Repository`; `StoreService` → `Service`; entities and enums (`StoreOrder` → `Order`, `StoreOrderState` → `OrderState`); EF configurations (`StoreOrderConfiguration` → `OrderConfiguration`); view models (`StoreIndexViewModel` → `IndexViewModel`) | **Drop the prefix.** All internal; nothing outside the assembly resolves them by name. Table names are declared explicitly in the EF configurations, so entity renames are schema-inert. `Enum_{TypeName}_{Value}` resx keys do change — but those keys are moving into the section's own resource set in the same PR (§3) |
| `StoreController`, `StoreAdminController`, `StoreStripeWebhookController` | **Keep the prefix.** View lookup is `/Views/{ControllerName}/{Action}.cshtml`, and that path is **global across application parts** — two sections with `Views/Controller/Index.cshtml` collide at one path. *Routes* are safe either way: all three Store controllers carry an explicit `[Route("Store")]` / `[Route("Store/Admin")]` / `[Route("Store/StripeWebhook")]`, and 78 of the repo's 90 controllers do. The 12 without must add one before any rename elsewhere |
| `StoreDbContext` | **Keep the prefix.** `SectionMigrationsHistory.TableFor` derives `__EFMigrationsHistory_Store` by stripping `"DbContext"` from the type name; renaming empties the suffix. Derivable from the section marker instead, but it names live schema — leave it |
| Public `Contracts/` types (`ICampsRead`, `CampSeasonInfo`, `Camp*` events) | **Keep the prefix.** They are read at cross-section call sites, where `ICampsRead campService` beats `using Humans.Camps.Contracts; … IRead campService` |

### The interface goes away too, unless something needs the seam

`IStoreRepository` exists to let a caller in another assembly depend on Store's persistence without
seeing it. Inside one assembly there is no other assembly, so the interface is ceremony:
`internal sealed class Repository` is the whole thing. Same for `IStoreService`.

**Rule: no interface unless something needs the seam.** Exactly two things do:

- **A caching decorator.** `memory/architecture/decorators-talk-only-to-inner.md` is a hard rule —
  a decorator over interface `I` depends only on `I`, via its keyed inner registration. A section
  with a caching decorator keeps the interface *for the decorator to wrap*, which is a reason
  rather than a habit.
- **A cross-section contract.** Anything in `Contracts/` is an interface by definition.

Store has neither, so it gets a bare `Repository` and `Service` with no interfaces at all.

**The rule changed to allow this** (Peter, 2026-08-07): `IRepository` is not a root interface
unless it exists for a reason, and tests and analyzers move to accommodate. Two follow-ons:

1. **One line in the constitution, edited when PR B gets there** — not a prerequisite.
   `docs/architecture/peters-hard-rules.md` reads "Repositories must derive from IRepository, and
   only the repository may read or write to its section's tables." Striking the first clause leaves
   exactly the rule that survives: *only the repository — the type holding the section's DbContext —
   may read or write its section's tables.* That file is never edited by an LLM; Peter makes the
   change when the pilot reaches it. `memory/architecture/repository-required-for-db-access.md` is
   already updated (the repository is mandatory; its interface is not).
2. **Store's service tests mock the interface.** `StoreServiceTests`,
   `StoreServiceTeamOrdersTests` and `StoreServiceStripeReconciliationTests` each open with
   `Substitute.For<IStoreRepository>()`; NSubstitute needs an interface or virtual members. Two
   ways out, and the standing EF-InMemory rule (§5) makes the second viable: mark the members
   `virtual` and substitute the class, or let those tests construct a real `Repository` over an
   InMemory context. The second is more churn in PR B and ends with tests that exercise the real
   query code. Implementer's call; flag whichever was taken in the PR.

Do the renames — and this de-interfacing — **in their own commit** inside PR B, after the move
compiles green. A move and a rename in one diff is unreviewable.

Two things must change for this to compile:

- **`AddSectionDbContext<TContext>` becomes `public`.** It is `internal` today
  (`InfrastructureServiceCollectionExtensions.cs:71`) and depends on `NpgsqlDataSource`,
  `QueryMonitoringInterceptor` and `UserInfoSaveChangesInterceptor` from `Humans.Infrastructure`.
  Since `Humans.Infrastructure` is Base and will never reference a section, `Store →
  Humans.Infrastructure` is a legal Base reference — so it stays where it is and simply goes
  public. It relocates later, when Infrastructure splits (§14), not now.
- **`StoreOrderAuthorizationHandler`'s registration moves** out of
  `AuthorizationPolicyExtensions.cs:27` into `AddStoreSection`. The *policy*
  `PolicyNames.StoreCatalogAdmin` (`AuthorizationPolicyExtensions.cs:125`) stays in Shell — §7.

---

## 7. Migrations

**`Migrations/Store/` moves into the section project, at
`src/Sections/Humans.Store/Data/Migrations/`.** Leaving it in `Humans.Infrastructure` is not an
option: the migration and its `Designer` reference `Store*` entity types, so Infrastructure would
have to reference `Humans.Store` while `Humans.Store` references Infrastructure — the first cycle.

### What changes

**`MigrationsAssembly` is hardcoded.** `InfrastructureServiceCollectionExtensions.cs:106` calls
`npgsqlOptions.MigrationsAssembly("Humans.Infrastructure")` for every context, and each design-time
factory repeats the literal (`ExpensesDbContextFactory.cs`). Fix once, generically:

```csharp
npgsqlOptions.MigrationsAssembly(typeof(TContext).Assembly.GetName().Name!);
```

Correct for every context before and after any move, and it removes the literal from the
design-time factories too.

**The startup migrator and the #845 snapshot need no change at all.** `DatabaseMigrationHostedService`
takes `IEnumerable<SectionDbContextRegistration>` from DI (`DatabaseMigrationHostedService.cs:25`)
and `CollectPendingFrontierAsync` enumerates that same injected set — it never reflects over an
assembly. A section that registers itself via `AddStoreSection` → `AddSectionDbContext` appears in
the frontier, the pre-migration `pg_dump` covers it, and `SectionMigrationRunner`'s
sentinel/baseline logic is untouched. This is the one piece of the machine already built right for
G5 — and the piece that makes later optionality cheap, since a section that isn't registered simply
doesn't migrate.

**`dotnet ef` gains a per-context `--project`.** Today every invocation is
`--project src/Humans.Infrastructure --startup-project src/Humans.Web`, and CI carries a flat
`SECTION_DB_CONTEXTS` name list consumed by three loops in `.github/workflows/build.yml`
(Layer 1 `has-pending-model-changes`, Layer 2 per-section `database update`, post-apply re-check).
`--startup-project` stays; `--project` becomes per-context. Change the env var to
`context:project` pairs:

```yaml
SECTION_DB_CONTEXTS: >-
  SystemSettingsDbContext:src/Humans.Infrastructure
  …
  StoreDbContext:src/Sections/Humans.Store
```

and split on `:` in each loop. `memory/process/ef-multi-context-commands.md` needs the same update
in the same PR.

**Table rename rides this slot.** G5 mandates renaming the section's tables to the section prefix.
Store's six tables are already `store_*` — **no rename migration is needed for the pilot**, a
further reason Store is the right first cut: it exercises the file move without also exercising a
prod schema change.

---

## 8. Reference direction — what Shell keeps

Confirmed against the wiring. Store's outbound dependencies, from `StoreService.cs:17-25`:
`IStoreRepository`, `IAuditLogService` (horizontal), `ICampServiceRead`, `ITeamServiceRead`,
`IShiftManagementService`, `IStripeService`, `IClock`. Inbound: only `Humans.Web` (three
controllers, one auth handler, four view-model files, six views) — plus `IStripeService`, which
names Store only in `Guid`/`decimal` parameters and doc comments, so no type dependency.

Every one of those outbound interfaces lives in `Humans.Application` today, which is Base. So at
the pilot Store references Base and nothing references Store: fan-in of one, and that one becomes
Shell. This is why Store is the pilot.

Note `IShiftManagementService` is a **full** service interface, not `IShiftManagementServiceRead`.
At the pilot it resolves inside Base and cannot cycle — but it is the shape that will cycle once
Shifts becomes its own section. Record it; do not fix it in the pilot.

### What moves into the section

`Controllers/Store{,Admin,StripeWebhook}Controller.cs`, `Authorization/Requirements/StoreOrder*.cs`,
`Models/Store*.cs` + `Models/Store/`, `Views/Store/`, `Views/StoreAdmin/`, plus everything already
under `Interfaces/Store`, `Interfaces/Repositories/IStoreRepository.cs`, `Services/Store/`,
`Domain/Entities/Store*.cs`, `Domain/Enums/Store*.cs`, `Data/Configurations/Store/`,
`Repositories/Store/`.

### What breaks, and what does not

| Shell concern | Verdict |
|---|---|
| Global filters (`NameRequiredFilter`, `MembershipRequiredFilter`, `AuthorizationPillFilter`) | **Fine** — registered on `MvcOptions` in `AddControllersWithViews` (`Program.cs:451-464`); global filters apply to controllers from every application part |
| Middleware pipeline, `UseRequestLocalization`, session, CORS | **Fine** — Shell-level, part-agnostic |
| Model binder providers (`LocalDateTimeModelBinderProvider`) | **Fine** — same `MvcOptions` |
| `_ViewImports` | **Breaks silently** — §2 |
| `[Authorize]` policy *registration* | **Stays in Shell.** `AuthorizationPolicyExtensions` names `PolicyNames.StoreCatalogAdmin` and `RoleNames.StoreAdmin`. Per-section policy registration would need `AddAuthorizationBuilder` fragments per section — more machinery than the problem deserves at 35 sections. Policy *definitions* central; resource-based *handlers* in the section. `PolicyNames` moves to `Humans.UI` (§2) so section controllers can name policies without referencing Shell |
| Nav (`_Layout`, `AdminNavTree`) | **Stays hardcoded in Shell for now** (§0). It is the one thing that must change before sections can be optional: Shell cannot link to a section it does not name. Not pilot work |
| Area routing | **Not used** — no `Areas/` folder; Store routes are conventional `/Store/*`, `/StoreAdmin/*` |
| `DevLoginControllerExclusionProvider` application-part feature provider | **Fine** — targets one controller in Shell |

Note the asymmetry in §6: **DI registration** moves into the section, **authorization policy
registration** does not. State it in the convention doc or every section author re-litigates it.

---

## 9. The Store cut plan

Three PRs, in order. Do not combine A and B: the G4 peel is a schema-history change needing a
preview-deploy proof, and mixing it with a 40-file move makes the diff unreviewable.

### PR 0 — `Humans.UI`

Not Store-specific and not optional. §2's table, moved out of `Humans.Web` into `src/Humans.UI`
(an RCL), scoped minimal-plus-obvious. Mechanical; ReSharper move-to-namespace territory,
including the `SharedResource` namespace rename (§3). Ends green with zero behaviour change, and
the localization diagnostic in `Program.cs` must still print `Localization OK` — that line is the
smoke test for the resx move.

### PR A — G4: peel `StoreDbContext`

Follows `docs/superpowers/specs/2026-07-15-per-section-dbcontext-design.md` §8 exactly. Store was
**deferred** from the #858 stack by §5.1 (`store_orders.Year` carried a physical `DEFAULT 0` absent
from the model). **That wall is down**: `20260802203816_RealignScaffoldedPhysicalDefaults` dropped
it (`AlterColumn Year / store_orders / oldDefaultValue: 0`), and `PhysicalDefaultParityTests` now
holds the line. Nothing else blocks Store.

- **A1.** `StoreDbContext` + `StoreDbContextFactory` in `Humans.Infrastructure/Data/`, copied from
  `ExpensesDbContext`/`ExpensesDbContextFactory` line for line: `internal sealed`, six `DbSet`s
  (`StoreProducts`, `StoreOrders`, `StoreOrderLines`, `StorePayments`, `StoreInvoices`,
  `StoreTreasurySyncStates`), configurations applied explicitly via six `ApplyConfiguration` calls
  — never assembly scanning.
- **A2.** `services.AddSectionDbContext<StoreDbContext>(sentinelTable: "store_orders")` in
  `AddHumansPersistence`.
- **A3.** Real-up baseline:
  `dotnet ef migrations add BaselineStore --context StoreDbContext --output-dir Migrations/Store
  --project src/Humans.Infrastructure --startup-project src/Humans.Web`.
  Run the EF migration reviewer agent on it before committing
  (`memory/process/ef-migration-review-gate.md`). `StoreRepository` switches to
  `IDbContextFactory<StoreDbContext>`; `StoreRepositoryTests` switches its in-memory context type.
- **A4.** `HumansDbContext` stops mapping Store: add
  `typeof(Configurations.Store.StoreProductConfiguration).Namespace!` to
  `PeeledConfigurationNamespaces` (`HumansDbContext.cs:109`), drop the six `DbSet`s, generate the
  removal migration and hand-empty `Up()`/`Down()` — **only** under the same Peter-authorized
  per-instance exception the seven prior peels used; it is not a general licence.
- **A5.** `SECTION_DB_CONTEXTS` in `.github/workflows/build.yml` gains `StoreDbContext`.
- **A6.** Definition of done, per the #858 doc step 6: clean build; full suite incl. Docker
  integration tests; `has-pending-model-changes` clean for **every** context; throwaway-migration
  proof; `NoDestructiveMigrationOps` ratchet green with zero baseline additions. Then the preview
  deploy at `{pr_id}.n.burn.camp` boots and `/api/version` responds — the real-database proof of
  the mark-applied path.

### PR B — G5: the project move

- **B1.** `src/Sections/Humans.Store/Humans.Store.csproj` — `Microsoft.NET.Sdk.Razor`,
  `<AddRazorSupportForMvc>true</AddRazorSupportForMvc>`,
  `<InternalsVisibleTo Include="Humans.Store.Tests" />`, `FrameworkReference
  Microsoft.AspNetCore.App`, `PackageReference` for `Npgsql.EntityFrameworkCore.PostgreSQL`
  (+ NodaTime), `NodaTime`, `Stripe.net`. Project references: `Humans.UI`, `Humans.Domain`,
  `Humans.Application`, `Humans.Infrastructure`, `Humans.Interfaces`. Add to `Humans.slnx` under a
  new `/src/Sections/` folder. **No `Directory.Build.props` needed** — `src/Directory.Build.props`
  resolves for anything under `src/`, so analyzer wiring and repo-wide settings are inherited for
  free. That is the deciding argument for the `src/Sections/` layout (§11).
- **B2.** Move files, layer-as-folder: `Contracts/`, `Domain/`, `Data/` (context, factory,
  configurations, `Migrations/Store/` → `Data/Migrations/`), `Services/`, `Controllers/`, `Models/`,
  `Views/`, `Authorization/`, `StoreSection.cs`. ~40 files, no behavioural edits in the same commit.
- **B3.** `src/Sections/Humans.Store/Views/_ViewImports.cshtml` — §2. Do this **before** the first
  build-and-eyeball, not after.
- **B3c.** Carve the section's resource set — §3, steps in §15.3b. 25 keys × 6 languages.
- **B4.** Visibility pass: everything `internal` except `StoreSection` and `Contracts/`. Store has
  no `IStoreServiceRead` today (nothing consumes it), so its `Contracts/` folder starts **empty** —
  the honest end state for a leaf section, and a useful demonstration that `Contracts/` is earned,
  not mandatory.
- **B4b.** `[assembly: Section("Store")]` in `Properties/AssemblyInfo.cs`; add
  `AttributeTargets.Assembly` to `SectionAttribute`; delete the per-type `[Section("Store")]` from
  the repository interface (§10).
- **B4c.** `ISection` in `Humans.Interfaces`; `StoreSection : ISection`; replace the `Program.cs`
  roll-call with `DependencyContext` discovery, sorted by section name, logging the discovered set
  at boot (§6). This lands with the pilot because it is what stops `Program.cs` needing an edit per
  section for the remaining 34.
- **B4d.** *Separate commit, after the move is green:* drop the `Store` prefix from internal types
  per §6a. Controllers, `StoreDbContext` and `Contracts/` types keep theirs.
- **B5.** `dotnet ef` re-point: `--project src/Sections/Humans.Store`; CI env var to
  `context:project` pairs (§7); update `memory/process/ef-multi-context-commands.md`.
- **B6.** `tests/Humans.Store.Tests` (§5); delete `StoreArchitectureTests.cs` (§10).
- **B7.** Enforcement follow-through — §10. The `AssemblyScope` generalization is **required in
  this PR**, not deferred: without it the section silently loses 22 analyzers.
- **B8.** Verify: build; full suite; **render every Store page and diff the HTML against a
  pre-move capture** — the only mechanical defence against the `_ViewImports` trap; preview deploy
  boots; `dotnet watch` hot-reloads a Store view.

---

## 10. The enforcement apparatus after G5

### `AssemblyScope` — the trap

22 of the 27 analyzers in `src/Humans.Analyzers/` gate on `AssemblyScope.IsApplicationOrWeb` /
`IsApplicationWebOrInfrastructure`, which compare `assembly.Name` against the three literals
`Humans.Application`, `Humans.Web`, `Humans.Infrastructure` (`Internal/AssemblyScope.cs`).
**A `Humans.Store` assembly matches none of them, so all 22 stop firing inside the section that
just moved** — HUM0012, the HUM0031 controller thresholds, cross-section repository injection, the
caching-decorator rules, the read-interface DTO rule, the lot. The split would *reduce*
enforcement, exactly inverting its purpose.

Fix in PR B: **scope on a marker the section project carries, not on a name.**

```csharp
[assembly: Section("Store")]     // src/Sections/Humans.Store/Properties/AssemblyInfo.cs
```

`AssemblyScope` then asks `compilation.Assembly.GetAttributes()` whether a `[Section]` is present —
one lookup, no type walk — and Shell/Base keep their existing name checks. A name-prefix rule
(`starts with "Humans."`) would work too but is guessable rather than declared; the marker says
what the project *is*, which is what
`memory/architecture/universal-enforcement-over-per-section.md` asks for ("universal, keyed off
convention, never per-section").

Scanning for `ISection` implementations was the alternative and loses: it costs a full declared-type
walk per compilation, where the attribute is a single metadata read. `ISection` stays the runtime/DI
seam (§6); the assembly attribute is the analyzer seam.

**This requires one word of change to `SectionAttribute`**, which is currently
`[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class)]` — add
`| AttributeTargets.Assembly`.

The payoff is larger than the fix: the assembly attribute carries the section *name*, so it
**replaces** the per-type `[Section("Store")]` that HUM0017/HUM0018 read today. One annotation per
project instead of one per repository interface — and the row in the table below changes from
"keep until G6" to "delete at G5, superseded".

`SectionDbContexts` (the other analyzer helper) is already structural — it matches any type whose
base chain reaches `DbContext`, and its own doc comment says it "deliberately pins neither
namespace nor assembly, so relocating the contexts cannot silently switch the analyzers off." That
one survives untouched, and is the model for the `AssemblyScope` fix.

Also check in PR B: `ApplicationServiceLocationAnalyzer` and `RepositoryInterfaceLocationAnalyzer`
assert *namespace* locations (`Humans.Application.Services.*`,
`Humans.Application.Interfaces.Repositories`). Section types live under `Humans.Store.*` and must
be accepted, not flagged.

### The `IRepository` marker, and the seven analyzers keyed on it

Seven analyzers resolve `Humans.Application.Interfaces.Repositories.IRepository` by full name to
decide "is this a repository". Deleting section-internal interfaces (§6a) takes that marker away
inside section assemblies. Four of the seven do not care, because the compiler has already taken
their job:

| Analyzer | Fate |
|---|---|
| `CrossSectionRepositoryInjectionAnalyzer` (HUM0017) | **Retires** — section Y's repository is `internal` to Y's assembly, so a service in X cannot name it. Precisely the subsumption #866 predicted |
| `WebRepositoryInjectionAnalyzer` (HUM0014) | **Retires** — Shell cannot see a section's internals |
| `OrchestratorRepositoryInjectionAnalyzer` (HUM0026) | **Retires** for sections — orchestrators live in Base, repositories are section-internal |
| `RepositoryInterfaceLocationAnalyzer` (HUM0013) | **Retires** — moot where there is no interface |
| `ApplicationServiceDbContextInjectionAnalyzer` | **Stays** — "only the repository touches the DbContext" is live *inside* a section |
| `SingleRepositoryPerTableAnalyzer` (HUM0025) | **Stays** — two repositories over one `DbSet` is still writable within one section |
| `CachingDecoratorRepositoryAnalyzer` | **Stays** wherever a decorator exists |

The three survivors re-key on structure instead of a marker: **a repository is the type that
injects the section's DbContext.** `SectionDbContexts` already resolves section contexts
structurally (it matches any type whose base chain reaches `DbContext`, pinning neither namespace
nor assembly), so the detection is a constructor-parameter check away. That is strictly better than
the marker — a marker can be forgotten on a new class; holding the DbContext *is* being the
repository. It also collapses the first two survivors into one statement: **types injecting a
section DbContext are its repositories, and each `DbSet` is touched by exactly one of them.**

Sequencing: sections stop deriving from `IRepository` at their own G5; the marker type itself is
deleted at G6, when the last pre-split service leaves `Humans.Application` and the four retiring
analyzers go with it.

### Reflection-anchored tests

Two tests discover contexts via `typeof(HumansDbContext).Assembly` and would silently stop covering
Store:

- `PhysicalDefaultParityTests` — the guard that unblocked Store in the first place (§9 PR A).
- `DbContextEntityOwnershipTests` — "every configuration mapped by exactly one context".

Both switch to the DI-registered `SectionDbContextRegistration` set, which is the authoritative list
anyway and already what the startup migrator uses.

### Per-item disposition

| Item | After G5 |
|---|---|
| `[Section("Store")]` on `IStoreRepository.cs:43` | **Delete at G5** — superseded by `[assembly: Section("Store")]` above, which tells HUM0017/HUM0018 the same thing once per project instead of once per type. Also removes the name collision that would otherwise block calling the registration type `Section` (§6) |
| `reforge.surface-score.json` `Store.paths` (6 globs across 4 projects) | Collapses to `src/Sections/Humans.Store/**`. `symbols`, `repositoryInterfaces`, `serviceInterfaces` unchanged |
| `StoreArchitectureTests.StoreService_DoesNotReferenceEntityFrameworkCore` | **Delete.** It asserts `typeof(StoreService).Assembly` does not reference EF — false *by design* once the section assembly contains `StoreDbContext`. The guarantee it encoded (services don't touch EF) is now a §15 convention within one assembly, policed by `ApplicationServiceDbContextInjectionAnalyzer` — one of the 22 that only survives if `AssemblyScope` is fixed |
| `StoreArchitectureTests.StoreRepository_ImplementsIStoreRepository` | **Delete** — a tautology once interface and impl share an assembly |
| `Architecture/Baselines/*` | **No Store rows exist** (confirmed: zero hits across all five baseline files). Nothing to delete — another reason Store is the pilot |
| `[Grandfathered]` on Store types | None exist |
| Keystone analyzer (new) | Lives in `Humans.Analyzers`, applied via `src/Directory.Build.props`. Must **not** use `AssemblyScope` — it applies to section assemblies specifically. Needs the `<Section>Section` carve-out from §6 |

---

## 11. Project layout on disk

(Type naming *inside* a section is §6a.)

**`src/Sections/Humans.Store/`.**

- `src/Directory.Build.props` already applies `Humans.Analyzers` to every project beneath `src/`
  via `$(MSBuildThisFileDirectory)Humans.Analyzers/...`, and MSBuild's nearest-ancestor lookup
  finds it from `src/Sections/` (which needs no props file of its own). A section project gets the
  analyzer, the TFM, the warning policy and the satellite-language list with an empty csproj. Flat
  `src/Humans.Store/` inherits identically, so this is a tie on mechanics.
- The tiebreaker is legibility at 35 sections: `src/` lists 6 entries today. Flat, it would list
  ~45, with Base and Shell lost among the verticals. `src/Sections/` keeps the top level readable
  and makes "section or Base?" a directory question.
- Base projects stay flat at `src/Humans.UI`, `src/Humans.Domain`, … — a small cluster, not a
  folder tier (§12).
- Test projects stay flat at `tests/Humans.Store.Tests` — `tests/Directory.Build.props` must remain
  the nearest ancestor, and flat keeps that true with no extra props file.

---

## 12. Decisions taken (Peter, 2026-08-07)

1. **Tier model: `Shell → Section → Base`.** Shell is nav and composition; a section owns its
   vertical; Base is what they share. Nothing in Base references a section; no section references
   Shell.
2. **Sections are not optional yet, and dependencies are hard-coded.** Shell references each
   section and calls `Add<Section>Section()` by name. Optionality — Shell registering interfaces
   and discovering sections by reflection or config, so different organisations run different
   sections — is deliberate future work, taken up **after** the migration completes, not designed
   into the pilot.
3. **Base is a small cluster of projects, not one consolidated project.** A single `Humans.Base`
   was rejected as exactly the junk drawer #866 warns about: "no DTOs, ever" is unenforceable once
   shared views, cross-section contracts and primitives share an assembly.
4. **One new Base project for the pilot: `Humans.UI`.** Everything else Store needs is already in
   Base. `AddSectionDbContext` goes `public` in `Humans.Infrastructure` and stays there.
5. **`SharedResource` renames to `namespace Humans.UI`**, settled by the packaging direction (§0);
   assembly name and root namespace must agree in a package.
6. **`Humans.Web` becomes `Humans.Shell` by subtraction, renamed at G6** — when it has actually
   become one, not before.
7. **Everything a section needs lives in the section's own project — no exceptions.** Views,
   entities, data, services, controllers, static assets **and its `.resx` set**. `Humans.UI` keeps
   only what the *shared* surface itself renders, and shrinks as each section carves. A string or
   asset two sections both want is duplicated, not promoted (#866's own rule: duplication inside
   two sections beats a coupling no one owns). This reverses an earlier draft of §3, which kept one
   central resource set.
8. **One registration type per section, same place every time, found rather than named.**
   `public sealed class <Section>Section : ISection` at the project root; Shell discovers
   implementations instead of listing them in `Program.cs`. Ships with the pilot — it is what stops
   `Program.cs` needing an edit for each of the remaining 34 sections. Section *optionality* is
   still deferred (§12.2); this only removes the hard-coded roll-call, not the hard-coded
   references.
9. **Enforcement keys on `[assembly: Section("…")]`**, which also supersedes the per-type
   `[Section("…")]`. Cross-section dependency order, when it is eventually needed, is **derived**
   from assembly references — never declared on `ISection`.
10. **Internal types drop the section prefix** (`StoreRepository` → `Repository`); controllers,
    `<Section>DbContext` and public `Contracts/` types keep it, each for a mechanical reason (§6a).
11. **Section-internal interfaces go away entirely** — not renamed, deleted. `Repository` and
    `Service` are concrete classes unless a caching decorator or a `Contracts/` entry needs the
    seam. `IRepository` is not a root interface unless it exists for a reason; tests and analyzers
    move to accommodate (§6a, §10). The one line in `peters-hard-rules.md` is Peter's to strike when
    PR B reaches it — not a prerequisite.

---

## 13. What the pilot discovers rather than decides

Open by design. Treat a surprise here as expected output, not oversight.

1. **The true size of `Humans.UI`.** §2 lists what Store needs. The first two or three sections
   will each drag more out of `Humans.Web`. Nobody knows where it converges; expect growth until
   roughly section five.
2. **Whether the 31 view components partition cleanly.** Several inject section services. Some may
   have no correct home short of the owning section's G5, forcing either a stub in Shell or a
   temporary reference the DAG dislikes.
3. **Build-time cost.** 35 RCLs each running the Razor compiler; whether incremental build and
   `dotnet watch` stay pleasant is unknown until several exist. If it degrades, the answer is
   fewer, larger section projects — a rollout adjustment, not a design failure.
4. **Where the first real cycle appears.** #866 predicts Users and Teams. Store cannot produce one
   (fan-in of one). The `IShiftManagementService` full-interface injection noted in §8 is the first
   concrete candidate.
5. **Whether `dotnet watch` holds up across ~45 projects.** Measured at 580 ms with two.
6. **Whether the empty `Contracts/` folder survives review.** Store legitimately has no
   cross-section read surface. If that reads as wrong, the argument to have is whether `Contracts/`
   is mandatory — not whether Store needs one.
7. **How much nav is section-shaped.** Nav stays in Shell for now, but the optionality work needs
   sections to contribute their own entries. The pilot is the first chance to see how much of
   `_Layout` and `AdminNavTree` is genuinely per-section versus global.
8. **How much genuinely shared vocabulary is left in `Humans.UI`'s resource set** once every
   section has carved. Prefix counts predict the split cleanly (all 2 617 keys are prefixed), but
   whether e.g. the 96 `Enum_*` keys land mostly in sections or mostly in UI only becomes clear
   around section five. Store's 25 keys are too small a sample to extrapolate from.
9. **Whether duplication-over-promotion holds under pressure.** §12.7 says a string two sections
   want gets duplicated. The first time that is a 40-key block rather than one word, someone will
   argue for promotion. That argument is worth having on a real case, not pre-empting here.

---

## 14. Project inventory

**Shell**

| Project | Status | Expectation |
|---|---|---|
| `Humans.Web` → `Humans.Shell` | drain → rename | Composition root only: `Program.cs`, the `Add*Section()` roll-call, middleware, auth policies, health/telemetry/Hangfire host, nav, global `wwwroot`. 405 `.cs` + 400 `.cshtml` today; nearly all of both leave. Rename at G6 |

**Sections** — each an RCL owning its full vertical; `internal` by default, `Contracts/` public

| Project | Status | Expectation |
|---|---|---|
| `Humans.Store` | new | The pilot. Fan-in of one, no baseline rows, tables already `store_*` — nothing to rename |
| `Humans.<Section>` × ~34 | new, one per PR | Same shape, one turnstile slot each. Users and Teams last; the ones expected to need `<Section>.Contracts` |

**Base**

| Project | Status | Expectation |
|---|---|---|
| `Humans.UI` | **new** | Shared layout, `Views/Shared` partials, tag helpers, cross-section view components, `Models/Tables`, `SharedResource` + 6 resx, `PolicyNames`. The one project the pilot creates. Grows for the first few sections, then settles |
| `Humans.Interfaces` | stay, +`ISection` | Already the right shape — marker interfaces and architecture attributes, no references, no packages; the correct home for `ISection` (§6), consumed by Shell and every section. Wants a real name and matching namespace before it ships as a package. Human-gatekept; no DTOs, ever |
| `Humans.Domain` | drain | Entities leave with their sections. Ends as `User`/Identity (the shared-contract exception), `RoleNames`, value objects, genuinely cross-cutting enums |
| `Humans.Application` | drain | Services and interfaces leave with their sections. Ends as orchestrators plus cross-section contracts not yet pushed into section `Contracts/`. Expected to be last and messiest |
| `Humans.Infrastructure` | drain → split three ways | DbContext/migration plumbing → its own Base project; vendor connectors (Google, Stripe, Holded, MailKit, Octokit, Anthropic) → one per connector per `memory/architecture/vendor-connectors-own-sections.md`; repositories → their sections. `HumansDbContext` deleted at G6 |
| `Humans.Analyzers` | stay | Gains the keystone analyzer. Needs `AssemblyScope` generalized in the pilot (§10) |
| `Humans.Auth`, `Humans.Audit`, `Humans.Files` | new, later | The horizontals, carved as Application/Infrastructure drain. Not pilot work |

**Tests**

| Project | Status | Expectation |
|---|---|---|
| `Humans.Store.Tests` | new | Service, repository, entity and auth-handler tests move in. One per section thereafter |
| `Humans.Integration.Tests` | stay, grows | The only assembly allowed to see many sections. Store's five controller tests stay here |
| `Humans.Analyzers.Tests` | stay | Unaffected |
| `Humans.Application.Tests` | drain | Loses section tests; its `Architecture/` folder and `Baselines/` deleted at G6 once the compiler enforces what they policed |
| `Humans.Domain.Tests` | drain → likely dissolves | Entity tests follow their entities into section test projects |
| `Humans.Web.Tests` | drain | Ends as Shell-level tests only |
| `Humans.Testing` | stay | Not a project — shared `HumansFact`/`HumansTheory` compile items via `tests/Directory.Build.props`. New section test projects inherit the harness for free |
| `docs/docs.csproj` | stay | Unrelated to the split |

Net at the pilot: **one new Base project, one new section, one new test project.** Everything else
is existing projects draining.

---

## 15. Convention doc — the recipe for the next ~34 sections

Mechanical. Deviations are the exception and get stated in the PR.

**Preconditions.** Section is at G4 (own `<Section>DbContext`, own history table, baseline
fake-applied in prod/QA/previews). `Humans.UI` exists. Fan-in known: run `reforge` for inbound
references before starting; a section with many inbound section references is a knot, goes later,
and may need `<Section>.Contracts`.

**Steps.**

1. `src/Sections/Humans.<Section>/Humans.<Section>.csproj` — `Microsoft.NET.Sdk.Razor`,
   `<AddRazorSupportForMvc>true</AddRazorSupportForMvc>`,
   `<InternalsVisibleTo Include="Humans.<Section>.Tests" />`, `FrameworkReference
   Microsoft.AspNetCore.App`, the section's own NuGet packages. Add to `Humans.slnx`. No
   `Directory.Build.props`.
2. Move the vertical, folders as layers: `Contracts/ Domain/ Data/ Services/ Controllers/ Models/
   Views/ Resources/ Authorization/ wwwroot/` + `<Section>Section.cs`. Migrations land at
   `Data/Migrations/`. **Everything the section needs comes with it — no exceptions.**
3. **Write `Views/_ViewImports.cshtml` in the same commit as the views.** Copy the section-local
   template from §2. Omitting a line ships broken HTML with a green build.
3b. Carve the section's `.resx`: `grep` the `<Section>_*` and `Enum_<Section>*` keys out of
   `Humans.UI`'s set into `Resources/<Section>Resource.{resx,es,ca,de,fr,it}` beside a
   `<Section>Resource.cs` in the section's namespace (§3 — the `.cs`-namespace mechanic decides the
   resource prefix, and getting it wrong degrades every string to its key). Extend the startup
   diagnostic to assert one key from the new set.
4. `<Section>Section.cs` at the project root: `public sealed class <Section>Section : ISection`
   with one `Register(IServiceCollection)` — `AddSectionDbContext<…>`, repositories, services,
   section-owned authorization handlers. The only `public` type outside `Contracts/`. Shell
   discovers it; nothing is added to `Program.cs`.
4b. `[assembly: Section("<Section>")]` in `Properties/AssemblyInfo.cs` — the analyzer marker (§10).
   Delete any per-type `[Section("…")]` the section carried.
5. Everything else `internal`, and internal types drop the section prefix per §6a — `Repository`,
   `Service`, entities, EF configurations, view models. Controllers, `<Section>DbContext` and
   `Contracts/` types keep it. Delete section-internal interfaces outright unless a caching
   decorator or a `Contracts/` entry needs the seam. Do the renames in a **separate commit** after
   the move compiles.
5b. `Contracts/` holds only `I<Section>ServiceRead`, canonical read DTOs and domain events — and
   may be empty for a leaf section.
6. Authorization *policies* stay in Shell's `AuthorizationPolicyExtensions`; resource-based
   *handlers* move into the section.
7. `wwwroot/` assets, if any, move with the section and their URLs become
   `/_content/Humans.<Section>/…`. Only Shell's own chrome assets stay in Shell.
8. `tests/Humans.<Section>.Tests` — service, repository, entity and handler tests move in;
   integration tests stay in `Humans.Integration.Tests`. EF-InMemory stays EF-InMemory.
9. `dotnet ef` for this section: `--project src/Sections/Humans.<Section>
   --startup-project src/Humans.Web --context <Section>DbContext --output-dir Data/Migrations`.
   Update the `context:project` pair in `.github/workflows/build.yml`.
10. Rename the section's tables to the section prefix if not already prefixed — an ordinary
    migration in the section's own history, but still a prod schema change: EF migration reviewer,
    then prod-verified. Sweep raw-SQL / backup-tooling / #845-runbook references to the old names.
11. Enforcement: collapse the section's `reforge.surface-score.json` paths to
    `src/Sections/Humans.<Section>/**`; delete the section's `*ArchitectureTests.cs` assertions the
    assembly boundary now subsumes (EF-reference checks, interface-implementation tautologies);
    delete the section's `Architecture/Baselines` rows and `[Grandfathered]` attributes; keep
    `[Section("…")]` until G6.
12. Verify: build; full suite; **render every page in the section and diff HTML against a pre-move
    capture**; `has-pending-model-changes` clean for every context; preview deploy boots;
    `dotnet watch` hot-reloads one of the section's views.

**One section per PR.** G5 is a 🚧 turnstile — the file-move conflict surface serializes it.

---

## 16. Related

- `docs/plans/2026-06-13-q3-transition-plan.md` — pillar #2, gate ladder G5 predicates
- `docs/superpowers/specs/2026-07-15-per-section-dbcontext-design.md` — the G4 recipe PR A follows
- `docs/plans/2026-08-03-g0-first-audit/Store.md` — Store's G1/G3 audit (its G3 predicate-1 row is
  void per §5)
- `memory/architecture/section-project-cycle-fix.md` — the cycle-fix playbook this doc adds
- `memory/process/ef-multi-context-commands.md` — updated by PR B step B5
