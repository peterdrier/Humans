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

**Corrected against the shipped pilot, 2026-08-08.** Store landed as `src/Sections/Humans.Store`
in peterdrier/Humans#1223 (PR 0 = #1220 `Humans.UI`, PR A = #1222 the `StoreDbContext` peel). §13 is
resolved rather than open, §15 is transcribed from the shipped project rather than predicted, and
every §15 step the pilot never exercised is labelled **unproven** with the section likely to be its
first real test. Where this doc and `src/Sections/Humans.Store/` disagree, the code is right.

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

**Correction to finding 2, measured in PR B.** `dotnet watch` *sees* section views — it logs
`File updated: .\src\Sections\Humans.Store\Views\Store\Index.cshtml` — but applying the delta fails
in this repo, before and after the split:

```
error CS7038: Failed to emit module 'Humans.Store': Changing the version of an assembly
reference is not allowed during debugging: 'Humans.Domain, Version=0.0.0.0' changed
version to '1.0.0.0'.
```

MinVer stamps `AssemblyVersion` at build time and the hot-reload recompilation does not, so it falls
back to `1.0.0.0`. Editing a `Humans.Web` view fails identically against `Humans.Infrastructure` —
neither assembly is touched by the split, so this is pre-existing and repo-wide, not a section
problem. It matters here because §2 makes `dotnet watch` the dev loop for section views. Tracked as
nobodies-collective/Humans#1008; until it is fixed, the dev loop for a section view is a rebuild.

**A third mechanism the probe never tested: MVC does not discover `internal` controllers.**
`ControllerFeatureProvider.IsController` requires `IsPublic`. A section whose controllers are
`internal` — which "public means `Section` or `Contracts/`" requires — builds green with zero
warnings and 404s at runtime. Measured, not assumed: all 20 Store controller integration tests
failed on the first internalisation attempt. Fixed by `SectionControllerFeatureProvider` in Shell,
which relaxes that one check for assemblies carrying `[assembly: Section]`. See
[`section-controllers-need-feature-provider`](../../../memory/architecture/section-controllers-need-feature-provider.md).

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

**What actually moved** (PR 0 = #1220, plus two files PR B had to add). The prediction is kept in
the right-hand column where it was wrong, because the *shape* of the error — always
under-prediction, never over — is what the next section should expect.

| In `Humans.UI` | Why | vs. prediction |
|---|---|---|
| `Resources/SharedResource.{cs,resx,es,ca,de,fr,it}` | `@inject IStringLocalizer<SharedResource>` | exact — 2 617 keys, now 2 592 after Store's carve |
| `TagHelpers/` (4 files) | `@addTagHelper *, Humans.UI` | exact |
| `Models/Tables/` (5 files) **+ `Models/PagerViewModel.cs`** | `_Table` partial model; `ITableModel.Pager` | Pager unlisted |
| `Views/Shared/` — **17 of 52** partials, incl. `_AdminLayout`, `_GateLayout`, `_Table`, `_Pager`, `_LoginPartial` | cross-section partials | over-predicted: `_Layout` and `_GuideLayout` **stayed in Shell** |
| `ViewComponents/` — `AuditLog`, `TempDataAlerts`, `Human` + their component views | `<vc:*>` used by more than one section | `Human` unlisted (used by `AuditLog/_Entry`) |
| Display extensions (`DateTimeDisplay`, `EnumLocalization`, `StatusBadge`, `PageSize`, `HtmlHelper`) **+ `CultureCodeExtensions`** | referenced from views | `CultureCode` unlisted (`_LanguageChooser`) |
| `Authorization/PolicyNames.cs` **+ `Authorization/RoleChecks.cs`** | section controllers name policies and check roles | `RoleChecks` unlisted — 26 callers |
| **`Constants/TempDataKeys.cs`** | `TempDataAlertsViewComponent`, needed by `_AdminLayout` | unlisted |
| **`Controllers/HumansControllerBase.cs`** | every section controller derives from it | unlisted — 69 callers, and the bulk of PR B's line count |

`RoleNames` already lives in `Humans.Domain` and needs no move.

`_Layout` stayed because it needs `RoleAssignmentClaimsTransformation.IsActive`, an Auth horizontal
awaiting `Humans.Auth` (§14); §8 puts nav in Shell for now anyway. The other 35 `Views/Shared`
partials are typed `@model Humans.Web.Models.<X>ViewModel` on section-owned view models, so moving
them would need `Humans.UI → Humans.Web`. They follow their sections. **Nothing is blocked by
leaving them** — partial and layout lookup is by name across application parts, so a section project
resolves them today without referencing Shell.

**`HumansControllerBase` and `RoleChecks` are the shape to expect.** Both live in `Humans.Web`;
Store's controllers need them and `Store → Shell` is the one direction the model forbids. Neither is
a view-layer type, which is why §2's original table missed them — the boundary is not "the shared
*view* layer", it is *everything a section controller or view names that today lives in
`Humans.Web`*.

**Scope: minimal plus the unambiguously shared.** Move what the section's views and controllers
need, plus anything adjacent that is obviously cross-section, and let `Humans.UI` grow over the
first few sections. Sizing it comprehensively up front would be guessing at a convergence point
nobody can predict (§11.1).

### `Microsoft.NET.Sdk.Razor` drops Sdk.Web's implicit usings

Not a Razor problem — a C# one, and it bites every file moved out of `Humans.Web`.
`Microsoft.NET.Sdk.Web` adds `Microsoft.AspNetCore.Http`, `Microsoft.AspNetCore.Routing` and
`Microsoft.Extensions.Logging` to `ImplicitUsings`; `Microsoft.NET.Sdk.Razor` does not. Both
`Humans.UI.csproj` and `Humans.Store.csproj` declare them explicitly:

```xml
<ItemGroup>
  <Using Include="Microsoft.AspNetCore.Http" />
  <Using Include="Microsoft.AspNetCore.Routing" />
  <Using Include="Microsoft.Extensions.Logging" />
</ItemGroup>
```

The alternative is adding three `using` lines to every moved file, which turns a pure relocation
into a diff. Do this in the csproj in step 1, before moving anything.

`ViewComponents/` is the one messy part: of the 31, several inject section services
(`MyCampsViewComponent`, `TicketHoldingsViewComponent`, `ShiftSignupsViewComponent`, …) and cannot
sit in a leaf Base project without dragging those sections upward. **Only the ones a *different*
section's views use must move**; a view component used by exactly one section moves *into* that
section at its own G5. Store uses one: `<vc:audit-log>`, which injects `IAuditLogService` — a
horizontal, so `AuditLogViewComponent` moves cleanly.

### Section-local `_ViewImports.cshtml` — mandatory, and the checklist item that must not be skipped

`src/Sections/Humans.Store/Views/_ViewImports.cshtml`:

As shipped:

```
@using Humans.Store
@using Humans.Store.Domain
@using Humans.Store.Models
@using Humans.Store.Services
@using Humans.Store.Services.Dtos
@using Humans.Application.Extensions
@using Humans.UI
@using Humans.UI.Extensions
@using Humans.UI.Models.Tables
@using Microsoft.Extensions.Localization
@inject IStringLocalizer<StoreResource> Localizer
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
@addTagHelper *, Humans.UI
```

The extra `@using` lines over the predicted set are the section's own layer namespaces — a
consequence of §6a's layer-as-folder move, since the host's file listed `Humans.Web.Models` but the
section splits its types across `Domain/`, `Services/` and `Services/Dtos/`. Derive the list from
the section's own folders, not from this example.

`Localizer` is bound to the *section's* resource set (§3), so the view bodies are untouched by
the move.

Omitting it, or omitting one `@addTagHelper` line, ships broken markup with a green build. The
only mechanical guard is a rendered-output assertion — see §8 step B8 and §10.

**This warning was exactly right and cost nothing, because the guard was built first.** PR B wrote
`Views/_ViewImports.cshtml` in the same commit as the views, before the first build, and captured
normalized HTML for all seven Store pages before the move — proving the capture deterministic by
running it twice pre-move and diffing — then re-diffed after each of the three risky steps (move,
internalisation, renames). Byte-identical every time. The capture test was deleted afterwards: it
was scaffolding, not coverage.

### Dev loop

`AddRazorRuntimeCompilation()` stops covering any view that has moved (§1), so
`dotnet watch run --project src/Humans.Web` becomes the dev command. Measured at 580 ms for a Razor
edit in a referenced RCL **outside this repo** — comparable to a page refresh today, and it covers
C# too.

**Inside this repo it does not work at all**, and did not before the split either: MinVer's
build-time `AssemblyVersion` stamp makes every hot-reload delta fail with `CS7038` (§1). So the dev
loop for a moved view is a rebuild until nobodies-collective/Humans#1008 lands. Measured cost of
that rebuild after the pilot: **4 s** for the whole solution after touching one Store `.cshtml`,
against **8 s** after touching one Shell `.cshtml` — moving views out of Shell makes the incremental
loop faster, not slower (§13.3). Delete the
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

The marker class is **`public`**, and today that is load-bearing rather than incidental: the boot
diagnostic discovers it via `GetExportedTypes()` and skips an `internal` one without complaint
(§6).

Three pieces of shared machinery are hardcoded to the single resource type and must widen first:

- **`EnumLocalizationExtensions`** — `EnumDisplay<TEnum>` and `EnumSelectItems<TEnum>` extend
  `IStringLocalizer<SharedResource>` specifically. Widen both to `this IStringLocalizer`;
  `IStringLocalizer<T>` implements `IStringLocalizer`, so every existing call site compiles
  unchanged and any section's localizer works. The `Enum_{TypeName}_{Value}` key convention is
  unaffected — a section's enum keys simply live in the section's resx.
- **`DataAnnotationLocalizerProvider`** routes *every* `[Display]`/`[Required]` lookup through the
  shared resource. The plan was to make it convention-routed on the model type's assembly.
  **Not done, deliberately.** Store has no `[Display]` and one literal `ErrorMessage`, so every
  annotation still resolves against `Humans.UI`'s set exactly as before — confirmed by the
  byte-identical HTML. Making it convention-routed *without a composite fallback* would remove a
  working fallback to buy nothing. **The first section that localizes a data annotation is the
  moment to do it, and it must fall back to `Humans.UI` rather than replace it.**
- **resx parity (#848)** becomes per-resource-set rather than one comparison. The check itself is
  unchanged; it just runs N times.

### `Enum_{TypeName}_{Value}` keys are a rename hazard, not just a move

`EnumDisplay` looks up `Enum_{typeof(TEnum).Name}_{value}` — the **live CLR type name**. §6a drops
the section prefix from enums in the same PR, so `StoreOrderCounterpartyType` became
`OrderCounterpartyType` while the six carved resx files still defined
`Enum_StoreOrderCounterpartyType_*`. Every non-English locale silently fell back to the humanized
English value; no build error, no test failure, no missing-key exception. Caught in review, fixed in
`fee629d7`.

§6a's original phrasing — "`Enum_{TypeName}_{Value}` resx keys do change, but those keys are moving
into the section's own resource set in the same PR" — reads as though the move handles it. It does
not: the move and the rename are two edits to the same key and **the rename is the one that gets
forgotten**. Rename the keys in the same commit as the enum, in all six languages. Same family as
the audit-discriminator trap in §6a; see
[`type-name-as-persisted-string`](../../../memory/code/type-name-as-persisted-string.md).

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
every localized string in that set falls back to its key at runtime.

**The startup diagnostic asserts the embedded manifest, not a named key.** §3 originally said
"assert one known key per registered resource set", which needs a `Program.cs` edit per section and
a key-naming convention to go with it. As shipped, it enumerates
`SectionDiscoveryExtensions.SectionResourceTypes()` and asserts that
`Humans.<Section>.<Section>Resource.resources` is embedded in `Humans.<Section>` — which catches the
same failure (the `.cs`-namespace mechanic) with **nothing added per section**. Boot log reads
`Localization OK: Humans.Store.StoreResource.resources embedded in Humans.Store`.

The first attempt used `GetAllStrings` and threw `MissingManifestResourceException` under `en-US`;
the manifest check has no culture semantics, which is why it is the right assertion.

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

> ⚠️ **UNPROVEN.** Store shipped no `wwwroot/`, so the `_content/<AssemblyName>/…` URL rewrite has
> never been executed here. First real test: **Agent**, **CityPlanning**, **Gate** or **Scanner** —
> whichever moves first. Whoever does it should confirm that the URL change, the Dockerfile's
> static-asset copy and any `asp-append-version` cache-busting all still line up, and correct this
> section from what happened.

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

**Two `InternalsVisibleTo` grants were needed, not one.** The table above sends the five controller
tests to `Humans.Integration.Tests`, which seeds and asserts against Store's now-`internal` entities
and DbContext — so the section grants it too. That is consistent with §5's own "the one assembly
allowed to see many sections", and it is not optional: without it the integration tests do not
compile.

A third grant lives in `Properties/AssemblyInfo.cs`:
`[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]`, so Castle (behind NSubstitute) can
proxy the section's internal types. It is required whether or not the section keeps a repository
interface — Castle cannot proxy an internal interface any more than an internal class.

**Two of the eight `Humans.Application.Tests/Services/Store/` files did not move.**
`StripeCheckoutAmountTests` tests `Humans.Infrastructure.Services` and `StripeSignatureSanityTest`
tests the Stripe SDK. They are connector tests, not Store tests, and were relocated to
`Services/Stripe/`. **Read what a test actually exercises, not what folder it sits in.**

Landed as 137 tests in `tests/Humans.Store.Tests` across `Authorization/ Data/ Domain/ Services/`.

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
// src/Sections/Humans.Store/Section.cs
namespace Humans.Store;

public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<StoreDbContext>(sentinelTable: "store_orders");
        services.AddSingleton<IStoreRepository, Repository>();
        services.AddScoped<Service>();
        services.AddScoped<IAuthorizationHandler, OrderAuthorizationHandler>();
    }
}
```

`ISection` lives in `Humans.Interfaces` — a leaf Base project with no references, which is exactly
where a marker consumed by both Shell and every section belongs.

**There are two `public` non-`Contracts/` types per section, not one.** §6 originally said one. The
second is the resource marker `<Section>Resource` (§3): the boot diagnostic finds it through
`SectionDiscoveryExtensions.SectionResourceTypes()`, which enumerates `GetExportedTypes()` — so an
`internal` marker is **skipped silently**, and the section ships raw localization keys with the boot
log still reading OK. Store works only because `StoreResource` is `public`, which was never stated
as a requirement.

So the keystone analyzer needs **two** carve-outs — `public sealed class Section : ISection` and
`public class <Section>Resource`, both at known paths — or the first section fails its own rule.
(That analyzer does not exist yet — §10.)

The alternative is to make the marker `internal` and switch `SectionResourceTypes()` from
`GetExportedTypes()` to `GetTypes()`: nothing outside the section names the marker — the section's
own `_ViewImports` injects `IStringLocalizer<<Section>Resource>` and that is the only consumer — so
one public type per section really is achievable. One word of code, and it makes the diagnostic
robust against a section that internalises correctly. **Decide it before the keystone analyzer is
written**, since whichever way it goes the analyzer encodes it.

### The type is called `Section`, not `<Section>Section`

The original §6 rejected the plain name on three grounds. Two survive; the third was wrong and its
removal changed the answer.

- **Static classes cannot implement interfaces**, so a static entry point cannot be discovered.
  C# 11 static abstract members don't help: they are consumed through generic constraints, which
  requires the type at compile time — the opposite of discovery. An instance class costs nothing
  (parameterless, `Activator.CreateInstance`). *Stands.*
- **A same-named `AddSection()` extension in 35 assemblies is CS0121-ambiguous** the moment Shell
  has two `using`s in scope. Discovery makes the question moot — nobody calls it by name. *Stands.*
- ~~**A type named `Section` collides with `[Section("…")]`.**~~ **Void.** The per-type
  `[Section("…")]` is deleted at G5 (§10), and the surviving `[assembly: Section("Store")]` sits in
  `Properties/AssemblyInfo.cs` with no namespace declaration — so `Humans.Store.Section` is never in
  scope there. Verified by building the solution. With the collision gone there is no argument left
  for the stutter, and the entry point is `Section.cs` at the project root **by definition**:
  `Humans.Store.Section`, `Humans.Agent.Section`, distinct by namespace.

Discovery therefore logs the assembly's `[Section("…")]` name rather than the type name — the type
name is now the same word in every section, and the attribute is the canonical identity the
analyzers already key on.

### `ISection.Register` takes `IConfiguration` too

`Register(IServiceCollection services, IConfiguration configuration)`. Store does not need it, but
**7 of the 35 existing `Add<Section>Section` registrars already take `IConfiguration`** — so the
one-argument shape is known-wrong before the second section moves, and widening an interface
implemented by 35 assemblies later is 35 edits. Take the second parameter from the pilot.

### Discovery works now, with hard references

MVC already finds section controllers and views by walking the entry assembly's `DependencyContext`
(§1 measured it: the RCL was discovered with no `AddApplicationPart`). The same walk finds
`ISection` implementations, while ProjectReferences stay hard-coded exactly as decided in §12.2.
Later optionality is then only a change of where the assembly list comes from: a config allowlist,
or `AssemblyLoadContext` over a plugin folder. No section code changes.

**Walk `DependencyContext`, not `GetReferencedAssemblies()`.** Shell names no type in a section by
design, so the C# compiler elides the assembly reference entirely and `GetReferencedAssemblies()`
returns *zero* sections — with no error, just an empty set that reads as "no sections installed".
PR B hit this twice: once in discovery and once in the authorization sweep that reuses it (§10).

**The roll-call drains, it does not disappear.** §6 originally said `Program.cs` drops its 35-line
roll-call at the pilot. It cannot: the other 34 registrars are `static` classes, several taking
`IConfiguration`, and converting them is a change to 34 files outside the section. What actually
ships is discovery running *alongside* the roll-call, so the list loses a line per section instead
of gaining one — which is what the requirement was for. It reaches zero at the last section, not the
first.

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

### Three things must change for this to compile, and one for it to route

- **`AddSectionDbContext<TContext>` becomes `public`.** It is `internal` today
  (`InfrastructureServiceCollectionExtensions.cs:71`) and depends on `NpgsqlDataSource`,
  `QueryMonitoringInterceptor` and `UserInfoSaveChangesInterceptor` from `Humans.Infrastructure`.
  Since `Humans.Infrastructure` is Base and will never reference a section, `Store →
  Humans.Infrastructure` is a legal Base reference — so it stays where it is and simply goes
  public. It relocates later, when Infrastructure splits (§14), not now.
- **`SectionMigrationsHistory` goes public with it.** Unlisted in the original §6. The section's
  design-time factory needs it to name its own history table, and the alternative is an
  `InternalsVisibleTo` per section forever.
- **`StoreOrderAuthorizationHandler`'s registration moves** out of
  `AuthorizationPolicyExtensions.cs:27` into the section. The *policy*
  `PolicyNames.StoreCatalogAdmin` (`AuthorizationPolicyExtensions.cs:125`) stays in Shell — §8.
- **Shell registers `SectionControllerFeatureProvider`.** Without it, a section whose controllers
  are `internal` builds green and 404s (§1). Ten lines in Shell, once, for all 35 sections; the
  alternative is a controllers-shaped hole in "public means `Section` or `Contracts/`" in every
  section, which would leave each section's controllers, its action-parameter view models and
  everything those touch nameable from any other section.

---

## 6a. Naming inside a section

Once the vertical is one assembly and everything in it is `internal`, the `Store` prefix on
internal types is stutter — they are already in Store. Drop it. Four categories cannot follow, each
for a concrete reason.

| Names | Rule |
|---|---|
| `StoreRepository` → `Repository`; `StoreService` → `Service`; entities and enums (`StoreOrder` → `Order`, `StoreOrderState` → `OrderState`); EF configurations (`StoreOrderConfiguration` → `OrderConfiguration`); view models (`StoreIndexViewModel` → `IndexViewModel`) | **Drop the prefix.** All internal; nothing outside the assembly resolves them by name. Table names are declared explicitly in the EF configurations, so entity renames are schema-inert. **But the CLR name is not always inert** — see the two traps below |
| `IStoreRepository` (the repository *interface*, where one exists) | **Keep the prefix.** It derives from `IRepository` and cannot itself be called `IRepository` — the same mechanical exception the controllers and `<Section>DbContext` have |
| `StoreController`, `StoreAdminController`, `StoreStripeWebhookController` | **Keep the prefix.** View lookup is `/Views/{ControllerName}/{Action}.cshtml`, and that path is **global across application parts** — two sections with `Views/Controller/Index.cshtml` collide at one path. *Routes* are safe either way: all three Store controllers carry an explicit `[Route("Store")]` / `[Route("Store/Admin")]` / `[Route("Store/StripeWebhook")]`, and 78 of the repo's 90 controllers do. The 12 without must add one before any rename elsewhere |
| `StoreDbContext` | **Keep the prefix.** `SectionMigrationsHistory.TableFor` derives `__EFMigrationsHistory_Store` by stripping `"DbContext"` from the type name; renaming empties the suffix. Derivable from the section marker instead, but it names live schema — leave it |
| Public `Contracts/` types (`ICampsRead`, `CampSeasonInfo`, `Camp*` events) | **Keep the prefix.** They are read at cross-section call sites, where `ICampsRead campService` beats `using Humans.Camps.Contracts; … IRead campService` |

### The two renames that are not inert

A CLR type name is inert only where nothing outside the compiler reads it. Two places in this
codebase do, and both are silent when broken — green build, green tests, no exception.

1. **Type names persisted as data.** Store writes an audit `EntityType` discriminator and later
   filters on it by exact string equality. The prefix drop turned `nameof(StoreProduct)` into
   `nameof(Product)`, changing what the code writes *and* what it queries in one move — every audit
   row written before the deploy becomes unreachable, taking the order page's price-change panel and
   the catalog price history with it. The tests asserted `nameof` too, so they renamed themselves in
   lockstep and stayed green. Fixed by making the discriminators literals in
   `Services/AuditEntityTypes.cs` that hold their **persisted** values, with the tests pinning those
   instead. No backfill — the old rows were never wrong; the code was.
2. **Type names used as resource keys.** `Enum_{typeof(TEnum).Name}_{value}` — see §3.

**This is the sharpest trap in the pilot**, because both failures survive the §15 step 12 HTML diff
(the audit panel is empty rather than wrong, and English is the capture locale). Before renaming any
type in a section, grep for `nameof(<Type>)` and for the type's bare name in `.resx`, and ask what
reads that string. Atom: [`type-name-as-persisted-string`](../../../memory/code/type-name-as-persisted-string.md).

### Section-internal interfaces: the service goes, the repository stays

**This reverses half of decision 12.11, on measurement.** PR B followed the original rule and
deleted `IStoreRepository`. Inside one assembly it hid nothing, so the argument was sound — but
replacing it cost **28 `public virtual` members and an unsealed class**, purely so NSubstitute had
something to proxy. That is more ceremony than it removed, and it moved the ceremony *from a
test-facing interface into production code*. Commit 3 of the PR put the interface back;
`Repository` is `sealed` with no virtuals.

`IStoreService` stays deleted: nothing mocks it, nothing outside the assembly can name it, no
decorator, no `Contracts/` entry.

**Rule as it actually holds: no interface unless something needs the seam — and a substituting unit
test is a seam.** Three things qualify:

- **A caching decorator.** `memory/architecture/decorators-talk-only-to-inner.md` is a hard rule —
  a decorator over interface `I` depends only on `I`, via its keyed inner registration.
- **A cross-section contract.** Anything in `Contracts/` is an interface by definition.
- **A substituted collaborator.** In practice this is the repository, because the section's service
  tests mock it. Prefer the interface over `virtual`-ing the class: it keeps `Repository` sealed and
  keeps the test's needs out of the production type.

So the shape to copy is `internal interface I<Section>Repository : IRepository` +
`internal sealed class Repository : I<Section>Repository`, and a bare `internal sealed class Service`
with no interface. The interface keeps its section prefix (table above).

`peters-hard-rules.md:12` — "Repositories must derive from IRepository" — therefore **stands
unedited**. The earlier follow-on saying Peter strikes its first clause when PR B reaches it is
withdrawn; PR B reached it and the line turned out to be right.

The `InternalsVisibleTo("DynamicProxyGenAssembly2")` grant is required either way. Castle cannot
proxy an internal interface any more than an internal class, so restoring the interface does not
remove it (§5).

Do the renames — and this de-interfacing — **in their own commit** inside the section's PR, after
the move compiles green. A move and a rename in one diff is unreviewable. As shipped that was three
commits, not two: move → visibility+renames → the interface reversal.

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
design-time factories too. Confirmed as shipped.

**The moved migration files need their `namespace` line changed, and nothing else.** Unlisted here
originally, and it looks like a violation of
[`no-hand-edited-migrations`](../../../memory/architecture/no-hand-edited-migrations.md) until you
see the diff: the three files under `Data/Migrations/` declared
`namespace Humans.Infrastructure.Migrations.Store` inside assembly `Humans.Store`. `Up`, `Down` and
the model snapshot are untouched, and `has-pending-model-changes` stays clean. Leaving the old
namespace would make every following section copy the wart. **State it explicitly in the PR** — a
reviewer seeing any edit to a migration file is right to stop.

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

> ⚠️ **UNPROVEN.** The table rename has never been executed under G5. It is the single riskiest
> unexercised step: a prod schema change riding a file-move PR, with raw-SQL, backup-tooling and the
> #845 runbook to sweep for the old names. First real test: whichever of **SystemSettings**,
> **Containers**, **Agent**, **Surveys** or **EventGuide** moves first — check each one's table
> prefix before scheduling it, and prefer an already-prefixed section as section two so the rename
> lands on its own once the rest of the recipe is settled.

---

## 7a. Documentation

Same rule as §3 and §4: **a section's documentation lives in the section.** The aim is that a
worktree scoped to one section project is self-describing — an agent searching it finds the
invariants and the code that implements them in one pass, rather than having to know that
`docs/sections/` exists and is the canonical reference.

### Moves into `src/Sections/Humans.Store/`

| Today | Lands as | Why it moves |
|---|---|---|
| `docs/sections/Store.md` | `Docs/Store.md` | The section's invariants — its canonical current truth |
| `docs/features/store/store.md` | `Docs/Store-feature.md` | Feature documentation for this section only |
| `docs/superpowers/specs/2026-06-04-store-stripe-payment-reconciliation-design.md` | `Docs/` unchanged | The section's own design |

**`Docs/`, not the project root** — settled at the pilot, so it is a convention now rather than an
implementer's call. It keeps the project root to `Section.cs` plus the layer folders.

**Watch for filename collisions on a case-insensitive filesystem.** `Store.md` and `store.md` are
one file on Windows, which is why the feature doc lands as `Store-feature.md`. Any section whose
invariants doc and feature doc share a stem needs the same disambiguation.

Add `<None Include="**/*.md" />` to the section csproj so they still appear in Solution Explorer;
they leave `docs/docs.csproj`, whose only job is that glob.

**Check the list against the filesystem before writing it into the PR.** §7a's fourth row
(`2026-04-30-store-section-design.md`) named a file that had already been deleted, and Store had no
`freshness-catalog.yml` entry at all — so two of the predicted moves were no-ops. `git ls-files
'docs/**' | grep -i <section>` first.

### Stays in `docs/`

**Point-in-time artifacts.** `docs/plans/2026-08-03-g0-first-audit/Store.md` and the execution
plans (`docs/superpowers/plans/2026-05-18-store-summary-aggregates.md`,
`2026-05-27-store-team-orders.md`) record what a program did on a date. They are not the section's
current truth, and freezing them beside living docs is how a section folder rots. Same reasoning as
the plans in `docs/plans/` generally.

**`docs/sections/_Index.md` stays and points into the projects.** It is the "which sections exist,
where does X live" map, and deleting it in the name of locality makes discovery worse, not better —
the opposite of the goal.

### The trap, generalized: find every *runtime* consumer of a docs path before moving it

`docs/` is not only developer prose in this repo. Before moving anything out of it, grep the source
for the path — two separate subsystems read `docs/` at runtime and **both swallow the miss**.

**`docs/guide/` is product content.** `GuideSettings.FolderPath` defaults to `"docs/guide"` and the
app serves those files at `/Guide/{stem}`, with the rendered set pinned in `GuideFiles.cs`.
`docs/guide/Store.md` is user-facing content the Guide section publishes, not developer prose about
Store. Moving it would 404 the page unless Guide learns to aggregate markdown from section projects
*and* the Dockerfile copies those paths. **Leave it where it is** and record the question for
Guide's own G5: should a section ship its user-facing guide page, with Guide aggregating? That is
Guide's design decision.

**`docs/sections/` has a runtime consumer too, and §7a missed it.** `AgentSectionDocReader` fetches
`docs/sections/{key}.md` **from GitHub at runtime** for the agent's `fetch_section_guide` tool,
swallows the 404 and returns null — so moving `Store.md` would have silently removed a whole
section's guide from the agent. Caught by
`AgentSectionDocReaderTests.Every_whitelisted_section_has_a_matching_doc_file`, which exists
precisely because that failure is silent. The reader now probes
`src/Sections/Humans.<Section>/Docs` as a fallback: one convention, no per-section path map, so
sections two through thirty-five need no change here.

The rule that generalizes from both: **a docs path is an API until you have proved otherwise.**

### Inbound links to fix in the same PR

`docs/README.md` (two tables), `docs/architecture/data-model.md`, `docs/sections/_Index.md`,
`memory/architecture/refunds-manual-via-dashboard.md`, `memory/code/stripe-restricted-keys.md`, and
the section's `freshness-catalog.yml` entry if it has one — whose trigger globs collapse to the one
section path, the same collapse as `reforge.surface-score.json` in §10. `docs/sections/_Index.md`
has **two** Store rows, not one: the invariants link at the top and the
controller/service/repository/table row further down, which also needs the §6a renames applied.

`CLAUDE.md`'s "Section Invariants — `docs/sections/`" pointer now says that a section at G5 carries
its invariants doc in its own project, with `_Index.md` as the map. **Done** — updated when the
pilot landed, as planned.

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

**All three landed 2026-08-08**: PR 0 = peterdrier/Humans#1220, PR A = #1222, PR B = #1223. The
step-by-step below is kept as written so the deviations recorded against it stay legible; §15 is the
corrected recipe and is what the next section follows.

Three PRs, in order. Do not combine A and B: the G4 peel is a schema-history change needing a
preview-deploy proof, and mixing it with a 40-file move makes the diff unreviewable. **The
separation held** — and PR B split further, into four commits (move → visibility+renames → interface
reversal → review fixes), because a 40-file move plus a visibility flip in one diff is what §9's own
"unreviewable" argument warns against.

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
- **B3d.** Move the section's docs in and fix the inbound links — §7a. `docs/guide/Store.md` stays.
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
  this PR**, not deferred: without it the section silently loses its analyzers. (The count was 22 in this doc; the real figure and the extra work it implies are in §10.)
- **B8.** Verify: build; full suite; **render every Store page and diff the HTML against a
  pre-move capture** — the only mechanical defence against the `_ViewImports` trap; preview deploy
  boots; `dotnet watch` hot-reloads a Store view.

### Deviations PR B took against this plan

Each is corrected in the section it belongs to; collected here so section two can tell a considered
deviation from an omission.

| Plan | What shipped |
|---|---|
| B1 `PackageReference` for `Stripe.net` | **Not referenced.** Nothing in the section names a Stripe SDK type — it all goes through `IStripeService` in Base. `Microsoft.EntityFrameworkCore` added instead |
| B4c "replace the roll-call with discovery" | The roll-call **drains** — §6 |
| B4 visibility in the move commit | Separate commit — a 40-file move plus a visibility flip is unreviewable |
| §3 `DataAnnotationLocalizerProvider` widens | Unchanged, deliberately — §3 |
| §3 boot diagnostic asserts a named key | Asserts the embedded manifest — §3 |
| §6a delete `IStoreRepository` | Restored — §6a |
| §6 `<Section>Section`, one-arg `Register` | `Section`, two-arg `Register` — §6 |
| §5 8 test files move | 6 — two were connector tests — §5 |
| §7a 4 docs move | 3; the fourth no longer existed — §7a |
| — | Migration `namespace` lines edited; `SectionMigrationsHistory` made public; `SectionControllerFeatureProvider` added — all §6/§7 |

---

## 10. The enforcement apparatus after G5

### `AssemblyScope` — the trap

The analyzers in `src/Humans.Analyzers/` gate on the assembly they are compiling, and every gate
compares `assembly.Name` against the three literals `Humans.Application`, `Humans.Web`,
`Humans.Infrastructure`. **A `Humans.Store` assembly matches none of them, so they stop firing
inside the section that just moved** — HUM0012, the HUM0031 controller thresholds, cross-section
repository injection, the caching-decorator rules, the read-interface DTO rule, the lot. The split
would *reduce* enforcement, exactly inverting its purpose.

**The count was wrong, and the shape of the error matters.** §10 said 22 analyzers gate via the two
`AssemblyScope` helpers, so fixing the helpers fixes everything. Only **6** use the helpers;
**thirteen more compare `assembly.Name` to a literal directly**, and generalizing `AssemblyScope`
alone would have left every one of them silent inside the section while the PR claimed the gap was
closed. Opening the helpers is necessary and nowhere near sufficient — **grep for the literals, do
not trust the helper as the choke point.**

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

### Opening the gate is not sufficient — three more things had to change

The gate decides whether the analyzer *runs*. Three rules still did the wrong thing once it did.

- **Section resolution from namespace returns null inside a section.** HUM0017 and the
  cross-section full-service rule resolved a type's section from `Humans.Application.Services.<Section>`,
  which does not match `Humans.Store.Services.Service` — so both exited before inspecting anything,
  gate or no gate. Resolution now falls back to the assembly's `[Section("…")]` via a shared
  `Sections.Of`. That fallback is also what supersedes the per-type `[Section("Store")]`.
- **HUM0014 needed the opposite treatment.** In `Humans.Web` every class is Web-layer, so the
  assembly *is* the whole test; in a section, which holds all three layers, opening the gate made it
  fire on the section's own service. Inside a section it is restricted to `ControllerBase`-derived
  types — the rule it actually encodes.
- **`ApplicationServiceLocationAnalyzer` and `RepositoryInterfaceLocationAnalyzer` deliberately stay
  Base-only.** They assert `Humans.Application.*` *namespace* locations that a section does not use,
  so extending them would flag every section type. This corrects §10's original instruction to make
  them accept `Humans.<Section>.*`: the right answer is that they do not apply.

Net: 6 helper-gated + 11 converted to `AssemblyScope.IsLayerOrSection`, 2 deliberately Base-only.

> ⚠️ **One gate left open, unresolved.** `RequestScopedCancellationOnExternalWriteAnalyzer`
> (HUM0033) still gates on `assembly.Name is AssemblyScope.Web`, on the comment "request-scoped
> tokens only exist in the Web assembly" — which stopped being true the moment a section owned
> controllers. Store is compliant today by hand (`StoreController` passes `CancellationToken.None`
> to its Stripe call) and no `[ExternalWrite]` method is reachable from it, so nothing is broken —
> but the rule is unenforced inside every section from here on. Decide it before section two:
> either `IsLayerOrSection(assembly, Web)` or an explicit note here saying why not.

### The `IRepository` marker, and the seven analyzers keyed on it

**Most of this section was contingent on deleting `I<Section>Repository`, and §6a no longer does.**
The section keeps `internal interface I<Section>Repository : IRepository`, so the marker is still
present inside a section assembly and the seven analyzers that resolve
`Humans.Application.Interfaces.Repositories.IRepository` by full name keep working. What actually
changed is smaller: they had to be let into the section at all (the gate above), and two needed
their rule re-expressed:

| Analyzer | Fate as shipped |
|---|---|
| `CrossSectionRepositoryInjectionAnalyzer` (HUM0017) | **Stays, extended.** The compiler subsumes the cross-*assembly* case, but a section's own services can still name a sibling section's repository if one is ever made visible, and the analyzer is what catches the pre-split callers that remain. Needed `Sections.Of` to resolve a section from the assembly attribute |
| `WebRepositoryInjectionAnalyzer` (HUM0014) | **Stays, narrowed** to `ControllerBase`-derived types inside a section |
| `OrchestratorRepositoryInjectionAnalyzer` (HUM0026) | **Stays** — orchestrators live in Base, repositories are section-internal, and the rule is still live in Base |
| `RepositoryInterfaceLocationAnalyzer` (HUM0013) | **Base-only.** Asserts a `Humans.Application.*` namespace a section does not use |
| `ApplicationServiceDbContextInjectionAnalyzer` | **Stays** — "only the repository touches the DbContext" is live *inside* a section |
| `SingleRepositoryPerTableAnalyzer` (HUM0025) | **Stays** — two repositories over one `DbSet` is still writable within one section |
| `CachingDecoratorRepositoryAnalyzer` | **Stays** wherever a decorator exists |

The structural re-keying — **a repository is the type that injects the section's DbContext** — is
still the better rule and `SectionDbContexts` already resolves section contexts structurally, so it
remains available. It is now an improvement rather than a rescue, and nothing forces it at G5.

Sequencing: the `IRepository` marker survives G5 in every section. Its deletion is a G6 question, if
at all.

### Reflection-anchored tests — and the class of failure they share

Two tests discover contexts via `typeof(HumansDbContext).Assembly` and would silently stop covering
Store:

- `PhysicalDefaultParityTests` — the guard that unblocked Store in the first place (§9 PR A).
- `DbContextEntityOwnershipTests` — "every configuration mapped by exactly one context".

Both switch to the DI-registered `SectionDbContextRegistration` set (read off the descriptors — no
provider built, no database), which is the authoritative list anyway and already what the startup
migrator uses.

**A third, unlisted here and far more dangerous: `EndpointAuthorizationTests`.** Its three
repo-wide `[Authorize]` / `[ValidateAntiForgeryToken]` sweeps anchored on
`typeof(HumansControllerBase).Assembly` — and this PR moved that base class into `Humans.UI`, an
assembly with **no concrete controllers**. All three sweeps then passed over an empty set: not
"stopped covering Store", but *stopped covering all 86 controllers in the repo*, reporting success.
Any later PR could have shipped a POST with no antiforgery token or an action with no `[Authorize]`
and the suite would have stayed green.

Two rules come out of this, and they apply to any reflection-anchored sweep, not just these three:

1. **A type-anchor is a bet that the type will not move.** Anchor on the *runtime's own* discovery —
   these now reuse `SectionDiscoveryExtensions.SectionAssemblies()`, so the sweep and
   `SectionControllerFeatureProvider` cannot disagree about what a section is. A first fix that used
   `GetReferencedAssemblies()` also found zero sections, for the reason in §6.
2. **Assert a floor.** Every sweep over a reflected set needs "and the set is not implausibly
   small", because its failure mode is finding nothing and reporting success. These now assert 77
   controllers, Store's included.

Before moving *any* type at G5, grep the test projects for `typeof(<Type>).Assembly`.

One pre-existing gap left alone: the sweeps' `Controller`-derived filter excludes `ControllerBase`-only
API controllers, so `StoreStripeWebhookController` sits outside all three — unchanged by the split.

### Per-item disposition

| Item | After G5 |
|---|---|
| `[Section("Store")]` on `IStoreRepository.cs:43` | **Delete at G5** — superseded by `[assembly: Section("Store")]` above, which tells HUM0017/HUM0018 the same thing once per project instead of once per type. Also removes the name collision that would otherwise block calling the registration type `Section` (§6) |
| `reforge.surface-score.json` `Store.paths` (6 globs across 4 projects) | Collapses to `src/Sections/Humans.Store/**`. `symbols`, `repositoryInterfaces`, `serviceInterfaces` unchanged |
| `StoreArchitectureTests.StoreService_DoesNotReferenceEntityFrameworkCore` | **Delete.** It asserts `typeof(StoreService).Assembly` does not reference EF — false *by design* once the section assembly contains `StoreDbContext`. The guarantee it encoded (services don't touch EF) is now a §15 convention within one assembly, policed by `ApplicationServiceDbContextInjectionAnalyzer` — one of the rules that only survives if the assembly gate is fixed (§10) |
| `StoreArchitectureTests.StoreRepository_ImplementsIStoreRepository` | **Delete** — a tautology once interface and impl share an assembly |
| `Architecture/Baselines/*` | **No Store rows exist** (confirmed: zero hits across all five baseline files). Nothing to delete — another reason Store is the pilot |
| `[Grandfathered]` on Store types | None exist |
| Keystone analyzer (new) | **Not built at the pilot.** Design unchanged: lives in `Humans.Analyzers`, applied via `src/Directory.Build.props`, must **not** use `AssemblyScope` (it applies to section assemblies specifically), and needs the `public sealed class Section : ISection` carve-out from §6. Until it exists, §15 step 5's "everything else `internal`" is convention only — nothing stops section two making a type `public`, and `Contracts/` stops being a boundary the moment one does. Worth building before, not after, the sections with real fan-in |

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
   `public sealed class Section : ISection` at the project root — *plain `Section`, amended by the
   pilot: the naming collision that ruled it out is gone with the per-type attribute (§6)* — with
   `Register(IServiceCollection, IConfiguration)`. Shell discovers implementations instead of listing
   them in `Program.cs`. Ships with the pilot — it is what stops `Program.cs` needing an edit for
   each of the remaining 34 sections, though the existing roll-call drains one line per section
   rather than disappearing at the pilot (§6). Section *optionality* is still deferred (§12.2); this
   only removes the hard-coded roll-call, not the hard-coded references.
9. **Enforcement keys on `[assembly: Section("…")]`**, which also supersedes the per-type
   `[Section("…")]`. Cross-section dependency order, when it is eventually needed, is **derived**
   from assembly references — never declared on `ISection`.
10. **Internal types drop the section prefix** (`StoreRepository` → `Repository`); controllers,
    `<Section>DbContext`, `I<Section>Repository` and public `Contracts/` types keep it, each for a
    mechanical reason (§6a). Two names are *not* free to change: a type name written to the database
    and a type name that forms a resource key (§6a, §3).
12. **The section's documentation moves in with it** — the same "everything local" rule as 7,
    extended to docs. A section project holds its own invariants doc, feature doc and design specs,
    so an agent working the section finds the prose and the code in one scoped search instead of
    needing to know `docs/sections/` exists. Point-in-time artifacts stay in `docs/`. See §7a.
11. **Section-internal interfaces go away** — not renamed, deleted — **unless something needs the
    seam.** *Amended by the pilot, 2026-08-08.* As taken on 2026-08-07 this read "entirely", with a
    caching decorator or a `Contracts/` entry as the only exceptions. PR B executed it, measured the
    cost of replacing `IStoreRepository` — 28 `public virtual` members and an unsealed class, so
    NSubstitute had something to proxy — and reversed the repository half. **A substituting unit
    test is a seam.** `I<Section>Service` still goes; `I<Section>Repository` stays and `Repository`
    stays `sealed` (§6a).

    Consequently `IRepository` *is* still a root interface, `peters-hard-rules.md:12` **stands
    unedited**, and the follow-on saying Peter strikes its first clause when PR B reaches it is
    withdrawn. The policy in this decision is unchanged — no ceremony without a reason; only the
    inventory of reasons was incomplete.

---

## 13. What the pilot discovered

Resolved against the shipped pilot on 2026-08-08. Each item is **answered**, **still open** (with
what would settle it), or **not a thing**.

**1. The true size of `Humans.UI` — still open, but the *kind* of growth is answered.** It stands at
24 `.cs` (~1 770 lines excluding `SharedResource.cs`) + 23 `.cshtml` + the six resx. PR B added
exactly two files, `HumansControllerBase` and `RoleChecks` — but they were the bulk of that PR's
line count, because they have 69 and 26 callers between them. The correction is not the number, it
is the shape: **`Humans.UI` is not the shared *view* layer, it is everything a section controller or
view names that today lives in `Humans.Web`** (§2). Expect the growth to stay small in file count
and large in diff. Convergence is still a section-five question.

**2. Whether the 31 view components partition cleanly — still open; the easy three moved.**
`AuditLog`, `TempDataAlerts` and `Human` are in `Humans.UI` and none of them forced a stub or a
temporary reference. 26 remain in `Humans.Web`, including every one that injects a section service.
Store used exactly one (`<vc:audit-log>`, a horizontal), so the hard case was never touched. Settled
by the first section whose views use a view component owned by a *different* unmoved section.

**3. Build-time cost — answered, and it goes the right way.** Measured on the shipped tree
(15 projects, warm):

| | |
|---|---|
| Cold full-solution build | 66 s |
| No-op incremental, full solution | 3–5 s |
| Touch one **Store** `.cshtml`, build solution | **4 s** |
| Touch one **Shell** `.cshtml`, build solution | **8 s** |
| `Humans.Store` rebuild in isolation | 12 s |

A section edit is *cheaper* than a Shell edit, because Shell is the big Razor compilation and
everything downstream of it. Every section that leaves makes Shell's compile smaller. The residual
risk is the no-op floor — the graph walk over ~48 projects rather than 15 — and 3–5 s today leaves
plenty of headroom. **No case for fewer, larger section projects.** Re-measure at section five.

**4. Where the first real cycle appears — still open.** Store has fan-in of one and cannot produce
one. `IShiftManagementService` (a *full* service interface, §8) is still the first concrete
candidate, and it is still resolved inside Base. Settled when Shifts moves.

**5. Whether `dotnet watch` holds up across ~45 projects — not a thing, for a different reason.**
Hot reload is broken repo-wide by MinVer's `AssemblyVersion` stamp (`CS7038`), before and after the
split and independent of project count — nobodies-collective/Humans#1008. The question cannot be
asked until that is fixed. What *was* verified is that `dotnet watch` correctly detects the file
change in a section RCL, so §1's mechanism holds; only applying the delta fails.

**6. Whether the empty `Contracts/` folder survives review — answered: yes.** It ships as a folder
containing one `README.md` stating why it is empty, so the absence reads as a decision rather than
an omission. No reviewer objected. `Contracts/` is earned, not mandatory.

**7. How much nav is section-shaped — answered, and it is the largest piece of remaining work.**
`_Layout.cshtml` holds **20 `asp-controller` links** and `AdminNavTree.cs` hardcodes the admin tree,
both in Shell. `Humans.Store` is already the first case where a section's code and its nav entry
live in different projects. The shape of the fix is known: `ISection` grows a list of
`(text, link, policy)`. It is the prerequisite for section optionality (§0) and wants its own issue
before the nav-heavy sections move.

**8. How much shared vocabulary is left in `Humans.UI` — still open, and §13.8 called it exactly.**
Store carved 25 keys (23 `Store_*` + 2 `Enum_OrderCounterpartyType_*`), leaving 2 592. It took 2 of
the 96 `Enum_*` keys, which is far too small a sample to say where that block lands. Unchanged
answer: this becomes clear around section five.

**9. Whether duplication-over-promotion holds under pressure — not exercised.** All 25 of Store's
keys were unique to Store; no second section wanted any of them. The argument §12.7 anticipates is
still waiting for a real case.

**10. Whether section-owned docs help agents — still open, but the move cost more than a `git mv`.**
The premise is untestable until the next agent works Store cold. What the pilot *did* learn is
concrete: `docs/sections/` is read at runtime (§7a), so relocating the invariants doc required a
code change in `AgentSectionDocReader` and would otherwise have silently removed Store from the
agent's guide tool.

**11. Where user-facing guide content belongs — still open, and now louder as predicted.**
`docs/guide/Store.md` stayed in Shell. Two follow-ons the pilot made concrete: Help/Guide should
discover section docs via `ISection` rather than the literal list in `GuideFiles.cs`, and the
section's `Docs/*.md` are `<None>` items today, so they do not reach the container. Still Guide's
G5 decision.

---

## 14. Project inventory

**Shell**

| Project | Status | Expectation |
|---|---|---|
| `Humans.Web` → `Humans.Shell` | drain → rename | Composition root only: `Program.cs`, the `Add*Section()` roll-call, middleware, auth policies, health/telemetry/Hangfire host, nav, global `wwwroot`. 405 `.cs` + 400 `.cshtml` today; nearly all of both leave. Rename at G6 |

**Sections** — each an RCL owning its full vertical; `internal` by default, `Contracts/` public

| Project | Status | Expectation |
|---|---|---|
| `Humans.Store` | **shipped** (#1223) | The pilot. Fan-in of one, no baseline rows, tables already `store_*` — nothing to rename |
| `Humans.<Section>` × ~34 | new, one per PR | Same shape, one turnstile slot each. Users and Teams last; the ones expected to need `<Section>.Contracts` |

### What's next is not "pick a section" — it is the ladder feeding G5

Written before any section existed, §14 read as though the queue were a choice. After Store it is a
supply problem, and that changes where the effort goes.

- **Seven sections are at G4 today** and could start G5 immediately: **SystemSettings, Containers,
  Agent, Expenses, Finance, Surveys, EventGuide** — the peeled contexts still pointing at
  `src/Humans.Infrastructure` in `SECTION_DB_CONTEXTS`.
- **Gate needs its G4 peel first.**
- **The remaining ~27 need G1–G4 before G5 is even a question.**

G5 is a 🚧 turnstile: one section at a time, serialized by the file-move conflict surface. The gate
ladder below it is **parallel** work — G1–G4 on different sections do not conflict with each other
or with an in-flight G5. So the bottleneck is not the turnstile, it is whether the queue in front of
it stays full, and the highest-value work is usually feeding G1–G4 on the ~27, not arguing about
which of the seven goes next.

Pick section two from the seven for what it *proves*, not for what it costs — §15's unproven steps
name their likely first tests. On current shape, a section with `wwwroot/` assets and one with a
caching decorator are the two most valuable next cuts.

**Base**

| Project | Status | Expectation |
|---|---|---|
| `Humans.UI` | **shipped** (#1220) | 17 of 52 `Views/Shared` partials, tag helpers, three cross-section view components, `Models/Tables` + `Pager`, `SharedResource` + 6 resx, `PolicyNames`, `RoleChecks`, `HumansControllerBase`, `TempDataKeys`, display extensions. Grows for the first few sections, then settles (§2, §13.1) |
| `Humans.Interfaces` | **shipped** `ISection` | Already the right shape — marker interfaces and architecture attributes, no references, no packages; the correct home for `ISection` (§6), consumed by Shell and every section. Wants a real name and matching namespace before it ships as a package. Human-gatekept; no DTOs, ever |
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

**Transcribed from `src/Sections/Humans.Store/` after the pilot landed**, not predicted. Steps
Store never exercised are marked ⚠️ **UNPROVEN** with the section likely to test them first — treat
those as a plan, the rest as a record.

**Preconditions.** Section is at G4 (own `<Section>DbContext`, own history table, baseline
fake-applied in prod/QA/previews). `Humans.UI` exists. Fan-in known: run `reforge` for inbound
references before starting; a section with many inbound section references is a knot, goes later,
and may need `<Section>.Contracts`.

**Before you start — the five searches that cost time if skipped.** Each one caught a silent
failure in the pilot. Substitute the section name for `<Section>`; run them as separate lines, never
chained with `&&`, since a search that finds nothing is the *good* outcome and would kill the rest
of the chain.

```bash
# what actually exists to move (§7a)
git ls-files 'docs/**' | grep -i <section>
# runtime readers of docs paths (§7a)
grep -rn --include='*.cs' 'docs/sections\|docs/guide\|docs/features' src/
# reflection-anchored sweeps that would silently start covering nothing (§10)
grep -rn 'typeof(<AnyTypeYouWillMove>).Assembly' tests/
# type names written to the database (§6a) — plain prefix, no trailing glob
grep -rn 'nameof(<Section>' src/
# type names that form resource keys (§3)
grep -rn --include='*.resx' 'Enum_<Section>' src/
```

Two shell notes, because the obvious spellings both fail *silently* and this block exists precisely
to stop silent failures. `grep`'s default is a basic regular expression, so `nameof(<Section>*)`
parses `*` as "repeat the previous character" and matches `nameof(Stor)`, `nameof(Store)`,
`nameof(Storee)` — **not** `nameof(StoreProduct)`, which is the whole point of the search. And
`src/**/*.resx` is not recursive without `shopt -s globstar`; use `--include` instead.
(`rg` avoids both, but is not on `PATH` in this repo's Git Bash.)

**Steps.**

1. `src/Sections/Humans.<Section>/Humans.<Section>.csproj` — `Microsoft.NET.Sdk.Razor`,
   `<AddRazorSupportForMvc>true</AddRazorSupportForMvc>`, `<InternalsVisibleTo>` for **both**
   `Humans.<Section>.Tests` **and `Humans.Integration.Tests`** (§5), `FrameworkReference
   Microsoft.AspNetCore.App`, the section's own NuGet packages, `<None Include="**\*.md" />`, and
   the three `<Using>` items Sdk.Razor does not inherit from Sdk.Web (§2). Project references:
   `Humans.Interfaces`, `Humans.Domain`, `Humans.Application`, `Humans.Infrastructure`,
   `Humans.UI`. Add to `Humans.slnx`. No `Directory.Build.props`.
2. Move the vertical, folders as layers: `Contracts/ Domain/ Data/ Services/ Controllers/ Models/
   Views/ Resources/ Authorization/ Docs/ Properties/ wwwroot/` + `Section.cs`. Migrations land at
   `Data/Migrations/` — and their `namespace` line changes to the section's, which is the one
   sanctioned edit to a migration file (§7); say so in the PR. **Everything the section needs comes
   with it — no exceptions.**
3. **Write `Views/_ViewImports.cshtml` in the same commit as the views.** Start from §2's shipped
   example but derive the `@using` list from the section's own folders. Omitting a line ships broken
   HTML with a green build.
3b. Carve the section's `.resx`: `grep` the `<Section>_*` and `Enum_<Section>*` keys out of
   `Humans.UI`'s set into `Resources/<Section>Resource.{resx,es,ca,de,fr,it}` beside a
   `<Section>Resource.cs` in the section's namespace (§3 — the `.cs`-namespace mechanic decides the
   resource prefix, and getting it wrong degrades every string to its key). **The boot diagnostic
   needs no per-section edit** — it enumerates section resource types and asserts each manifest is
   embedded — **but only if `<Section>Resource` is `public`**, because discovery reads
   `GetExportedTypes()`. Make it `public` and exempt it in step 5, or the diagnostic skips the
   section in silence (§6). If the section renames an enum in step 5, rename its
   `Enum_{TypeName}_*` keys in all six languages in the same commit (§3).
4. `Section.cs` at the project root: `public sealed class Section : ISection` with
   `Register(IServiceCollection services, IConfiguration configuration)` — `AddSectionDbContext<…>`,
   repositories, services, section-owned authorization handlers. One of the two `public` types
   outside `Contracts/` — `<Section>Resource` is the other (step 3b). Shell discovers it; nothing is
   added to `Program.cs`. Remove the section's line from the `Add<Section>Section` roll-call (§6).
4b. `[assembly: Section("<Section>")]` in `Properties/AssemblyInfo.cs` — the analyzer marker, the
   discovery marker and the internal-controller marker, all three (§10, §6, §1). Add
   `[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]` beside it if the section's tests
   substitute anything. Delete any per-type `[Section("…")]` the section carried.
5. Everything else `internal` **except `<Section>Resource`, which stays `public`** (step 3b), and
   internal types drop the section prefix per §6a — `Repository`,
   `Service`, entities, EF configurations, view models. Controllers, `<Section>DbContext`,
   `I<Section>Repository` and `Contracts/` types keep it. Keep an interface only where something
   needs the seam: a caching decorator, a `Contracts/` entry, or a substituting unit test — in
   practice that means **`I<Section>Repository` stays and `I<Section>Service` goes** (§6a). Do the
   renames in a **separate commit** after the move compiles, and check the two non-inert rename
   cases (§6a) before running them.
   > ⚠️ **UNPROVEN: the caching-decorator exception.** Store has no decorator, so "keep the
   > interface for the decorator to wrap" has never been executed under a section boundary. First
   > real test: any section with a `Cached*` decorator — check `memory/architecture/caching-transparent.md`
   > for which. The open question is whether the decorator, its keyed inner registration and the
   > interface all stay `internal` inside the section, and whether `CachingDecoratorRepositoryAnalyzer`
   > still fires there.
   >
   > ⚠️ **UNPROVEN: nothing enforces "everything else `internal`".** The keystone analyzer is not
   > built (§10). This step is convention until it is.
5b. `Contracts/` holds only `I<Section>ServiceRead`, canonical read DTOs and domain events — and may
   be empty for a leaf section, in which case it ships as a folder with a `README.md` saying why, so
   the absence reads as a decision.
   > ⚠️ **UNPROVEN: a non-empty `Contracts/`.** Store's is empty — fan-in of one. Nothing has yet
   > tested what a section's public read surface looks like from the far side of an assembly
   > boundary. First real test: the first section with inbound section references; **Users** and
   > **Teams** are the ones #866 predicts will need it most, and they go last, so expect a mid-rollout
   > section to be the real first case.
6. Authorization *policies* stay in Shell's `AuthorizationPolicyExtensions`; resource-based
   *handlers* move into the section. (§8's asymmetry: DI registration moves, policy registration
   does not.)
7. `wwwroot/` assets, if any, move with the section and their URLs become
   `/_content/Humans.<Section>/…`. Only Shell's own chrome assets stay in Shell.
   > ⚠️ **UNPROVEN.** Store has no static assets. First real test: **Agent**, **CityPlanning**,
   > **Gate** or **Scanner** (§4). Confirm the Dockerfile's copy and any `asp-append-version`
   > cache-busting survive the URL change.
7b. The section's docs move into `Docs/` — invariants doc, feature doc, its own design specs (§7a);
   disambiguate filenames that collide case-insensitively. Fix the inbound links (`docs/README.md`,
   `data-model.md`, **both** `_Index.md` rows, any `memory/` atom citing them, the
   `freshness-catalog.yml` globs *if the section has an entry*). Point-in-time plans and audits stay
   in `docs/`. Anything the app *serves or fetches* from `docs/` at runtime stays — `docs/guide/`
   until Guide's own G5, and re-check `AgentSectionDocReader`'s fallback still covers the section.
8. `tests/Humans.<Section>.Tests` — service, repository, entity and handler tests move in;
   integration tests stay in `Humans.Integration.Tests`. EF-InMemory stays EF-InMemory. **Read what
   each test actually exercises**: a file under `Services/<Section>/` that tests a connector is a
   connector test and does not move (§5).
9. `dotnet ef` for this section: `--project src/Sections/Humans.<Section>
   --startup-project src/Humans.Web --context <Section>DbContext --output-dir Data/Migrations`.
   Update the `context:project` pair in `.github/workflows/build.yml`.
10. Rename the section's tables to the section prefix if not already prefixed — an ordinary
    migration in the section's own history, but still a prod schema change: EF migration reviewer,
    then prod-verified. Sweep raw-SQL / backup-tooling / #845-runbook references to the old names.
    > ⚠️ **UNPROVEN.** Store's tables were already `store_*`. This is the riskiest unexercised step;
    > see §7 for which section is likely to hit it first.
11. Enforcement: collapse the section's `reforge.surface-score.json` paths to
    `src/Sections/Humans.<Section>/**`; delete the section's `*ArchitectureTests.cs` assertions the
    assembly boundary now subsumes (EF-reference checks, interface-implementation tautologies);
    delete the section's `Architecture/Baselines` rows and `[Grandfathered]` attributes.
    **`AssemblyScope` and the thirteen literal gates were generalized once, in the pilot** — but
    re-run `grep -rn 'Assembly.Name' src/Humans.Analyzers/` before each section and confirm any
    analyzer added since is section-aware (§10).
    > ⚠️ **UNPROVEN: the cycle-fix playbook.** `memory/architecture/section-project-cycle-fix.md`
    > has never been exercised — Store cannot produce a cycle. First real test: the first section
    > with a mutual reference; §8 names `IShiftManagementService` as the concrete candidate.
    > Store also had **zero** `Architecture/Baselines` rows and zero `[Grandfathered]` attributes,
    > so the deletion half of this step is likewise untested.
12. Verify: build; full suite; **render every page in the section and diff HTML against a pre-move
    capture**, re-diffing after *each* risky step rather than once at the end, and proving the
    capture deterministic first by capturing twice pre-move;
    `grep -rn --include='*.cshtml' -- '-view-component' src/` returns zero;
    `has-pending-model-changes` clean for every context; preview deploy boots.
    **The HTML diff does not catch everything**: an emptied audit panel and a non-English resource
    fallback both survive it (§6a, §3). Capture in a non-English locale too, or check those two by
    hand. `dotnet watch` hot-reload is not a gate until nobodies-collective/Humans#1008 is fixed.

**The rename hazard: `<vc:*>` tags.** ReSharper's move-to-namespace and rename refactorings read a
`<vc:name>` element as a reference to the view-component *type* and rewrite it to the default
tag-helper convention — `<vc:human>` becomes `<human-view-component>`. Nothing objects: the build
is green, the suite passes, the page returns 200, and the element renders as literal markup that
contributes nothing to the output. PR 0 (peterdrier/Humans#1220) hit this on 127 tags across the
three view components it moved, and only the step 12 HTML diff caught it. So: after any
refactoring pass over `.cshtml`, run

```
grep -rn --include='*.cshtml' -- '-view-component' src/
```

and expect zero hits. Step 12 is the backstop, not the first line of defence — the grep is cheaper
and names the failure.

**One section per PR.** G5 is a 🚧 turnstile — the file-move conflict surface serializes it.

**Recommendation, not yet built: make §15 a copyable template.** It now reads as prose with twelve
steps, four ⚠️ markers and eight cross-references, and section two's author will work it as a
checklist regardless. `docs/sections/SECTION-TEMPLATE.md` already serves `docs/sections/` that way.
A `G5-SECTION-TEMPLATE.md` — a tickable checklist with the four pre-flight greps at the top, the
csproj skeleton, the `_ViewImports` skeleton and the verification gates — would make "which steps
did this PR skip and why" a diffable question. Worth doing after section two, when the recipe has
been executed twice and the parts that are genuinely per-section are visible. Peter's call.

---

## 16. Related

- `docs/plans/2026-06-13-q3-transition-plan.md` — pillar #2, gate ladder G5 predicates
- `docs/superpowers/specs/2026-07-15-per-section-dbcontext-design.md` — the G4 recipe PR A follows
- `docs/plans/2026-08-03-g0-first-audit/Store.md` — Store's G1/G3 audit (its G3 predicate-1 row is
  void per §5)
- `memory/architecture/section-project-cycle-fix.md` — the cycle-fix playbook this doc adds; still
  unexercised (§15.11)
- `memory/architecture/section-controllers-need-feature-provider.md` — why a section's controllers
  are discovered at all (§1)
- `memory/code/type-name-as-persisted-string.md` — the rename trap in §6a and §3
- `memory/architecture/repository-required-for-db-access.md` — amended by the pilot's interface
  reversal (§6a)
- `memory/process/ef-multi-context-commands.md` — updated by PR B step B5
- `src/Sections/Humans.Store/` — the authority. Where this doc disagrees with it, the code is right
