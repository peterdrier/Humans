# G5 Section Move — Checklist

The recipe for moving one section into its own project under `src/Sections/`
(nobodies-collective/Humans#866, G5). Extracted 2026-08-09 from §15 of
[`2026-08-07-g5-section-project-split-design.md`](../superpowers/specs/2026-08-07-g5-section-project-split-design.md)
(cited below as "spec §N") after five sections executed it: Store (peterdrier/Humans#1223),
SystemSettings + EventGuide (peterdrier/Humans#1235, A1), Containers + Finance
(peterdrier/Humans#1239, A2), Expenses (peterdrier/Humans#1240, A3), Surveys
(peterdrier/Humans#1251, A4), Agent (A4b — the first section with static assets, migrations
and a contracts leaf all at once). Step numbers match the former §15, so an old "§15 step 3b"
citation reads as "step 3b" here.

**Where this template and `src/Sections/` disagree, the code is right.** Deviations are the
exception and get stated in the PR. Steps marked ⚠️ UNPROVEN have never been executed; whoever
runs one first corrects this template from what happened.

**One section per commit, one G5 lane at a time.** The turnstile is the file-move conflict
surface between *concurrent* lanes — batching two sections as separate clean commits in one
serialized PR satisfies the reason for the rule (A1 and A2 both did). Within a section's commits:
move → visibility + renames → anything behavioural, never combined — a 40-file move plus a
visibility flip in one diff is unreviewable.

## Preconditions

- [ ] Section is at G4: own `<Section>DbContext`, own history table, baseline fake-applied in
      prod/QA/previews.
- [ ] Fan-in known: run `reforge` for inbound references before starting. A section with many
      inbound section references is a knot, goes later, and may need `<Section>.Contracts`.

## Read the controller — do not assume it

The most expensive thing A2 found was not in any grep: `FinanceController` was 31 actions and 23
of them were Budget CRUD. A controller's name is the file most likely to lie about its contents,
because route prefixes accrete pages from whatever team needed a URL. Go through the action list
and ask, per action, *which section's tables does this write*. If the answer is mostly "not this
one", split the controller before the move — keeping the `[Route]` prefix on both halves so no URL
changes — and take only your own actions and their views. Moving the whole class drags the other
section's dependencies into your section and forces some of them down into Base, all to be undone
at that section's own G5. `/Admin/*` is documented as a nav holder rather than a section;
`/Finance` turned out to be the same shape without saying so, and it will not be the last.

## The seven pre-flight searches

Each one caught a silent failure in the pilot or in A1/A2. Substitute the section name for
`<Section>`; run them as separate lines, never chained with `&&` — a search that finds nothing is
the *good* outcome and would kill the rest of the chain.

```bash
# what actually exists to move (step 7b)
git ls-files 'docs/**' | grep -i <section>
# runtime readers of docs paths (step 7b)
grep -rn --include='*.cs' 'docs/sections\|docs/guide\|docs/features' src/
# reflection-anchored sweeps that would silently start covering nothing (step 11)
grep -rn 'typeof(<AnyTypeYouWillMove>).Assembly' tests/
# the same hazard keyed on a namespace prefix instead of an assembly — a different
# shape, and the one A2 nearly missed (step 11)
grep -rn 'Namespace?\.StartsWith\|Namespace\.StartsWith' tests/
# Base types whose *signatures* name the section's types — the ones recon misses because
# the filename carries a vendor, not the section (step 5b's connector case). Read the
# signatures of every hit, not just the filenames.
grep -rln '<Section>' src/Humans.Application/Interfaces src/Humans.Infrastructure/Services
# type names written to the database (step 5) — plain prefix, no trailing glob.
# Run it for the OTHER sections' entity names too: nameof(Camp) in an audit
# discriminator stops compiling at the move and must become a literal.
grep -rn 'nameof(<Section>' src/
# type names that form resource keys (step 3b)
grep -rn --include='*.resx' 'Enum_<Section>' src/
# whether those Enum_ keys are LIVE — grep the CALL SITES, never the helper class (step 3b).
# Added by Expenses (A3): the methods are EnumDisplay/EnumSelectItems and nothing is named
# "Localize", so looking for the helper by name reports a live key set as orphaned.
grep -rn 'EnumDisplay\|EnumSelectItems' src/
```

Two shell notes, because the obvious spellings both fail *silently*: `grep`'s default is a basic
regular expression, so `nameof(<Section>*)` parses `*` as "repeat the previous character" and
misses `nameof(StoreProduct)` — use the plain prefix. And `src/**/*.resx` is not recursive without
`shopt -s globstar`; use `--include`. (`rg` avoids both, but is not on `PATH` in this repo's
Git Bash.)

## Steps

1. [ ] `src/Sections/Humans.<Section>/Humans.<Section>.csproj` — `Microsoft.NET.Sdk.Razor`
   **when the section has controllers or views**, plain `Microsoft.NET.Sdk` when it has neither
   (SystemSettings): discovery keys off `[assembly: Section("…")]`, not off being an MVC
   application part. With Razor: `<AddRazorSupportForMvc>true</AddRazorSupportForMvc>`,
   `<InternalsVisibleTo>` for **both** `Humans.<Section>.Tests` **and `Humans.Integration.Tests`**
   (spec §5), `FrameworkReference Microsoft.AspNetCore.App`, the section's own NuGet packages,
   `<None Include="**\*.md" />`, and the three `<Using>` items Sdk.Razor does not inherit from
   Sdk.Web (spec §2): `Microsoft.AspNetCore.Http`, `Microsoft.AspNetCore.Routing`,
   `Microsoft.Extensions.Logging`. Project references: `Humans.Interfaces`, `Humans.Domain`,
   `Humans.Application`, `Humans.Infrastructure`, `Humans.UI`. Add to `Humans.slnx`. No
   `Directory.Build.props` — `src/Directory.Build.props` resolves from `src/Sections/`.
   - **"The section's own NuGet packages" excludes anything in the ASP.NET Core shared
     framework.** Central Package Management fails the build with `NU1010` for a
     `PackageReference` that has no `PackageVersion`, and the ASP.NET packages deliberately have
     none — `Microsoft.AspNetCore.DataProtection` and friends arrive through the framework
     reference Sdk.Razor already adds (proven: Surveys, whose token provider takes
     `IDataProtectionProvider`). Add EF Core, NodaTime and Npgsql; never an `AspNetCore` one.
2. [ ] Move the vertical, folders as layers: `Contracts/ Domain/ Data/ Services/ Controllers/
   Models/ Views/ Resources/ Authorization/ Docs/ Properties/ wwwroot/` + `Section.cs`. Migrations
   land at `Data/Migrations/` — their `namespace` line changes to the section's, which is the one
   sanctioned edit to a migration file (spec §7); say so in the PR. **Everything the section needs
   comes with it — no exceptions.**
3. [ ] **Write `Views/_ViewImports.cshtml` in the same commit as the views.** Start from the
   shipped Store example (spec §2) but derive the `@using` list from the section's own folders.
   Omitting a line — or one `@addTagHelper` — ships broken HTML with a green build.
3b. [ ] Carve the section's `.resx`: the `<Section>_*` and `Enum_<Section>*` keys move out of
   `Humans.UI`'s set into `Resources/<Section>Resource.{resx,es,ca,de,fr,it}` beside a
   `<Section>Resource.cs` in the section's namespace. The `.cs` and `.resx` must sit in the same
   folder, and the `.cs` namespace determines the manifest prefix (spec §3) — get it wrong and
   every string in the set degrades to its key at runtime. The boot diagnostic needs no
   per-section edit, **but only if `<Section>Resource` is `public`** — discovery reads
   `GetExportedTypes()` and skips an `internal` marker in silence.
   - If step 5 renames an enum, rename its `Enum_{TypeName}_*` keys in all six languages in the
     same commit — the key is the **live CLR type name** (spec §3; proven the hard way: Store).
   - **Re-grep the moved views for keys the carve did not take.** Genuinely shared strings
     (`Common_*`, `Camp_Plural`) stay in `SharedResource`; bind a second localizer —
     `@inject IStringLocalizer<SharedResource> SharedLocalizer` — and switch those call sites.
     Extract every `Localizer["…"]` key in the section's views and controllers and assert each
     exists in the section `.resx`; leftovers are a missed carve or a shared key. The fallback
     renders the raw key, in every language, and survives the step 12 HTML diff unless you
     captured before the carve (proven: Events had four).
   - **Grep outside the section too**: `grep -rn 'Localizer\["<Section>_' src/` and expect hits
     only inside the section. A carved key referenced from a `Humans.UI` partial or a Shell view
     resolves against `SharedResource` and cannot see the section's set. Fix by passing localized
     strings in on the partial's model; a Shell caller can inject
     `IStringLocalizer<<Section>Resource>` directly (proven: Events, `_FavouriteButton`).
   - **Check what each moved *controller* injects, not just what the views bind.** `_ViewImports`
     rebinds `Localizer` for every view in one line, so views are safe by construction and the
     grep above passes — a controller that still takes `IStringLocalizer<SharedResource>` keeps
     compiling and renders its carved keys as raw key names. Assert it structurally rather than by
     eye: no type in the section may take `IStringLocalizer<T>` for any `T` but
     `<Section>Resource` (`SurveysArchitectureTests.SectionTypesLocalizeThroughTheSectionsOwnResourceSet`
     is the shape). **A render test is not enough here** — controller-resolved copy tends to sit on
     the failure paths (validation errors, empty-value fallbacks) that fixtures do not reach, so a
     page-renders-clean suite passes over it (proven: Surveys shipped three such call sites past
     four green render tests; caught in review, peterdrier/Humans#1251).
   - **Prefer keeping a shared partial in the section over promoting it to `Humans.UI`.** A
     partial in the section's `Views/Shared/` is found by name across application parts and
     compiles against the *section's* `_ViewImports`, so it keeps `Localizer` with no model
     plumbing. `Humans.UI` is right only for a partial with no section vocabulary (proven both
     ways: Containers kept three card partials, Shell renders them unchanged; Events promoted
     `_FavouriteButton` and had to take three labels on its model).
   - **A key used by both this section and a not-yet-moved section stays in `SharedResource`** —
     carve by *owner*, not by prefix (proven: Containers left the 9 `ContainerMap_*` keys with
     City Planning's map page).
   - **Never conclude an `Enum_*` key set is dead from the helper class alone.** Those keys are
     read through `EnumLocalizationExtensions`, whose methods are `EnumDisplay` and
     `EnumSelectItems` — no method contains "Localize", so grepping for one returns nothing and
     makes a live set look orphaned. Grep the **call sites**, per the seventh pre-flight search
     (proven: Expenses' five `Enum_ExpenseReportStatus_*` keys arrived flagged as dead and are
     rendered by the status filter in `Views/Expenses/Index.cshtml`; carving them out would have
     degraded it to `Humanize(value.ToString())` — English-looking text in every locale, and a
     resource fallback survives the step 12 HTML diff).
4. [ ] `Section.cs` at the project root: `public sealed class Section : ISection` with
   `Register(IServiceCollection services, IConfiguration configuration)` — `AddSectionDbContext`,
   repositories, services, section-owned authorization handlers (keyed caching-decorator pairs
   move verbatim). Shell discovers it; nothing is added to `Program.cs`. Remove the section's
   line from the `Add<Section>Section` roll-call (spec §6).
4b. [ ] `[assembly: Section("<Section>")]` in `Properties/AssemblyInfo.cs` — the analyzer marker,
   the discovery marker and the internal-controller marker, all three (spec §10, §6, §1). Add
   `[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]` beside it if the section's tests
   substitute anything. Delete any per-type `[Section("…")]` the section carried.
5. [ ] Everything else `internal` — **except `<Section>Resource`** (step 3b) — and internal types
   drop the section prefix: `Repository`, `Service`, entities, EF configurations, view models.
   Controllers, `<Section>DbContext`, `I<Section>Repository` and `Contracts/` types keep it, each
   for a mechanical reason (spec §6a). Renames in a **separate commit** after the move compiles.
   - **Internalising is also a sealing pass.** `MA0053` ("make class or record sealed") is a
     warning, `TreatWarningsAsErrors` is on, and the rule only fires once a class stops being
     `public` — so a clean `public` → `internal` sweep turns every non-sealed class in the
     section into a build error at once. `internal sealed` is the shape; EF entities included
     (nothing in this codebase subclasses them or uses EF proxies). Budget for it rather than
     being surprised by 17 errors (proven: Surveys).
   - **Ask whether the prefix is the *section* name or the *aggregate* name before dropping
     it.** Only Containers and Finance actually collapsed to `Service`/`Repository`, because
     their types duplicated the section name. Store, Expenses and Surveys kept aggregate-derived
     names — `Humans.Expenses.Domain.ExpenseReport`, `Humans.Surveys.Services.SurveyService` —
     and stripping those would produce `Question`/`Answer`/`Response`, which is worse in a
     `Humans.Surveys.Domain` namespace, not better. A rename that buys nothing still costs a
     pass over the persisted-name hazard below, so the default for an aggregate-named section
     is: do not rename (proven: Surveys, which renamed nothing).
   - Keep an interface only where something needs the seam: a caching decorator, a `Contracts/`
     entry, or a substituting unit test — in practice **`I<Section>Repository` stays and
     `I<Section>Service` goes**. A decorator that is not the section's plain `Service` keeps its
     `Caching*` name (proven: Events, fully internal decorator).
   - Two renames are **not** inert: a type name written to the database and a type name that
     forms a resource key. `nameof(<AnotherSection'sEntity>)` stops compiling at the move —
     replace it with a literal beside the section's own audit discriminators (Store's
     `Services/AuditEntityTypes.cs` is the shape), never with a project reference. Declare the
     section's own discriminators as literals while you are there; that is what makes the rename
     schema-inert. See `memory/code/type-name-as-persisted-string.md`.
   - **The keystone analyzer (nobodies-collective/Humans#1013) has landed, so this is a build
     gate, not a convention — and it collapses the move commit and the visibility commit into
     one.** HUM0034 fails the build for any public type in a `[assembly: Section("…")]` assembly
     that is not `Section`, `<Section>Resource`, a generated migration, or under `Contracts/`.
     A move-only commit therefore does not compile, and "renames in a separate commit after the
     move compiles" no longer describes a reachable state for the visibility half. Split what is
     still splittable — the move+internalise commit, then renames, then anything behavioural —
     and say in the PR why the first two are one (proven: Agent, A4b, ~60 files internalised in
     the move commit). Nested `public` members of an already-internal type are flagged too, which
     `internal sealed` at the top level does not cover.
5b. [ ] `Contracts/` holds **everything consumed from outside the section** — read *or* write
   (Peter, 2026-08-09: splitting read from write happens once every section has moved, not
   per-section). May be empty for a leaf section; ship the folder with a `README.md` saying why
   (proven: Store).
   - **An empty `I<Section>ServiceRead` is deleted at the move, not carried into `Contracts/`.**
     Several sections shipped one pre-emptively "as the boundary other sections would inject",
     with no members and no consumers. Moving it produces an empty public interface that
     documents a contract nobody has; the assembly boundary now says the same thing and says it
     to the compiler. Delete it with its `IsAssignableFrom` architecture test (proven: Surveys).
     A read interface that has *members* is a different question — that one moves.
   - **Folder vs project is decided by *where the consumer lives*, not how much surface there
     is.** A consumer in Base forces `Humans.<Section>.Contracts` as its own project referencing
     only the bottom of the graph — a folder would cycle
     (`memory/architecture/section-project-cycle-fix.md`; proven: SystemSettings, Events, both on
     first build). Consumers all in Shell → folder is fine at any size (proven: Containers,
     17 types).
   - **A Base-resident *connector* can be section-owned in disguise.** Agent's recon put the
     Anthropic client in Base with Stripe and Holded, on the "connectors stay in Base" rule — and
     the first build said otherwise: `IAnthropicClient` streams the section's own
     `AgentTurnToken` and `IAgentAnthropicBalanceProvider` returns its `AgentBalanceStatus`.
     Leaving either in Base means promoting a section DTO downward, which is forbidden, so the
     whole connector (interface, DTOs, options, client, balance provider, NuGet reference) moves
     in. The test is not "is it an external API" but **whose types are in its signatures** — a
     connector serving one section and shaped by that section's model belongs to it (proven:
     Agent; contrast Stripe, which Store left behind, and `GitHubCommunityKbContentSource`, whose
     signatures name only `string`).
   - **A DTO the section re-exports from Base forces the project split even when nothing else
     does** — and promoting the connector's DTO downward is the forbidden fix; the section owns a
     boundary type and maps at the edge (proven: Finance, `HoldedLedgerLineDto`). Check every
     `Contracts/` signature for a Base-owned type before assuming the carve is mechanical.
   - **A Base *registry* keyed by the section's enum is not a `Contracts/` case — invert it.**
     `Humans.UI` holds lookup tables naming ten sections' status enums (`EnumBadgeMap`,
     `StatusBadgeExtensions`); each move breaks one. Referencing the section's contracts leaf
     from `Humans.UI` is locally cheapest and globally worst — ten moves later Base references
     every section. Peter, 2026-08-09: the registry gains a `Register(...)` the section calls
     from `Section.Register`, and the literal ends empty
     (`memory/architecture/base-ui-registries-are-section-populated.md`; proven: Expenses). If
     the helper's only callers were the section's own views, it is not a registry problem at all
     — move it in and delete it from Base.
6. [ ] Authorization *policies* stay in Shell's `AuthorizationPolicyExtensions`; resource-based
   *handlers* move into the section (spec §8's asymmetry: DI registration moves, policy
   registration does not).
   - **A Shell-resident base class that several sections derive from moves down to `Humans.UI`
     at the first section's G5.** A section cannot reference `Humans.Web`, so the section's
     `<Section>ApiKeyAuthFilter` cannot keep deriving from `Humans.Web/Filters`'
     `ApiKeyAuthFilterBase` — and neither duplicating it nor leaving the filter behind (the
     section's `[ServiceFilter(typeof(…))]` would name a Shell type) is available. This is
     **not** the forbidden "promote the shared type into Base": that rule is about DTOs and
     section vocabulary, and the test is whether the type names anything a section owns.
     `ApiKeyAuthFilterBase` is an `X-Api-Key` header check with no section vocabulary at all,
     the same shape as `ApiControllerBase` which already lives in `Humans.UI`. Move the base,
     leave the per-section subclass + settings type with each section, and expect four more
     sections (Feedback, Issues, Log, Agent) to take theirs on the way out (proven: Surveys).
   - **Do this on paper *before* step 5b.** Moving the handler is often what takes the read
     surface's fan-in to zero. Expenses' fan-in read as "`IExpenseReportServiceRead`'s only
     outside consumer is Shell's `IbanAccessHandler`", which would have made six types public to
     serve one handler — a handler that this step moves into the section anyway (proven:
     Expenses; its `Contracts` project ended up one interface wide).
6b. [ ] Recurring Hangfire jobs stay in `Humans.Infrastructure/Jobs` for now: there is no
   `ISection`-style discovery seam for jobs, and a section-owned job would have to be `public` —
   the one thing step 5 exists to prevent. **Health checks are the same shape** —
   `Program.cs`'s `AddHealthChecks()` chain names each `IHealthCheck` by concrete type — so a
   section's health checks stay in Shell and consume it through `Contracts/` (proven: Agent kept
   `AgentDocsHealthCheck` and `AnthropicHealthCheck` in Shell, which is what put a one-property
   `IAgentAvailability` on its leaf).
   The job consumes the section through `<Section>.Contracts` like any other Base consumer,
   which also counts toward step 5b's consumer-in-Base test (proven: Finance, `HoldedSyncJob`;
   Expenses and Tickets are next). The eventual seam — `ISectionRecurringJobs` called after
   `WebApplication` is built — is not a G5 blocker.
   - **A job that orchestrates across the section's repository + service + store is a layer skip,
     and the move is when to fix it.** Carving `IAgentConversationRetention` — one method,
     returning the deleted count — moved the retention rule inside the section and took three
     public interfaces off the leaf in exchange for one. Look at what the job actually *does*
     before writing the contract for what it currently *takes* (proven: Agent, A4b).
7. [ ] `wwwroot/` assets move with the section; URLs become `/_content/Humans.<Section>/…` in the
   same PR. Only Shell's own chrome assets stay in Shell. **Proven: Agent (A4b), the first
   section to move any.** What A4 predicted was right, and the fix is one line — in the *test
   host*, not in the section.
   - **The move itself is nothing.** `git mv` the files under the section's `wwwroot/`, rewrite
     every `~/css/x.css` to `~/_content/Humans.<Section>/css/x.css`, done. Rewrite the
     references in **Shell's** views too: an asset can be Agent's while the markup linking it is
     Shell chrome (Agent's two assets have three reference sites and two are Shell's
     `HelpWidget`). Nothing in the `Dockerfile` or the publish pipeline needs a line: `dotnet
     publish` copies an RCL's static web assets **physically** into
     `publish/wwwroot/_content/Humans.<Section>/…` (verified by publishing and listing the
     folder), so in production `UseStaticFiles` serves them off `WebRootFileProvider` and
     `asp-append-version` finds them there. `dotnet run` is fine too — `WebApplicationBuilder`
     calls `UseStaticWebAssets()` in Development, which composes the manifest into the same
     provider.
   - **The test host is the gap, and it fails silently in two ways at once.**
     `WebApplicationFactory` runs under a non-Development environment, so nothing composes the
     static-web-assets manifest: every `/_content/<Rcl>/…` URL **404s**, and — the half you
     will not notice — `asp-append-version="true"` emits the bare `href` with **no `?v=` hash**
     rather than throwing. The page still returns 200 with correct-looking markup. Fix it once,
     in the factory:

     ```csharp
     builder.UseEnvironment("Testing");
     builder.UseStaticWebAssets();   // Humans.Web.staticwebassets.runtime.json is already in
                                     // the test project's output; nothing else is needed.
     ```

     This is a standing fix for the whole suite, not a per-section one: before it, *any*
     integration test asserting an RCL asset would have seen a 404 and called it a section bug.
   - **Assert both halves.** `html.Should().Contain($"href=\"{AssetPath}?v=")` catches the
     cache-buster; a separate `GET` on the asset asserting 200 catches the file. Agent's
     `AgentPageRenderTests` has one of each, and the pre/post HTML capture confirmed the only
     difference across every page in English and Spanish was the URL prefix — the `?v=` hashes
     were byte-identical before and after the move.
7b. [ ] The section's docs move into `Docs/` — invariants doc, feature doc, its own design specs;
   disambiguate filenames that collide case-insensitively. Fix inbound links (`docs/README.md`,
   `data-model.md`, **both** `docs/sections/_Index.md` rows, any `memory/` atom citing them, the
   `freshness-catalog.yml` globs if the section has an entry) and **rewrite the moved doc's own
   `freshness:triggers` block to `src/Sections/Humans.<Section>/**`** — the old scattered paths
   stop existing at the move and the doc silently stops being swept. Point-in-time plans and
   audits stay in `docs/`. Anything the app *serves or fetches* from `docs/` at runtime stays —
   `docs/guide/` until Guide's own G5 — and re-check `AgentSectionDocReader`'s fallback covers the
   section. **A docs path is an API until you have proved otherwise** (spec §7a).
   - **The `AgentSectionDocReader` fallback is a convention, not a map, and it has one real
     constraint: the project folder must be `Humans.{canonical key}` and the file
     `Docs/{canonical key}.md`.** The reader probes `docs/sections/{key}.md`, then
     `src/Sections/Humans.{key}/Docs/{key}.md`, for whatever `AgentSectionKeys` resolved — so a
     section whose project name does not match its agent key becomes unreachable, silently, via a
     swallowed 404 (`Humans.Events` carries a comment saying exactly this). Of the moved
     sections only Store, Containers and Events are whitelisted keys; Expenses, Finance, Surveys
     and SystemSettings are deliberately operator-only and unaffected. Agent's own doc is at
     `src/Sections/Humans.Agent/Docs/Agent.md` and "Agent" is *not* a whitelisted key — the
     agent still cannot fetch a guide to itself, which predates the move.
     `AgentSectionDocReaderTests.Every_whitelisted_section_has_a_matching_doc_file` globs both
     folders and is the guard (proven: Agent, A4b).
8. [ ] `tests/Humans.<Section>.Tests` — service, repository, entity and handler tests move in;
   integration tests stay in `Humans.Integration.Tests`. EF-InMemory stays EF-InMemory. **Read
   what each test actually exercises**: a file under `Services/<Section>/` that tests a connector
   is a connector test and does not move (proven: Store, two Stripe files).
   - The test project is plain `Microsoft.NET.Sdk`, so it does **not** get the ASP.NET Core
     shared framework. A section test that touches an ASP.NET type — a controller, a filter,
     `IDataProtectionProvider` — needs an explicit
     `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, and cannot get there with a
     `PackageReference` (see step 1's `NU1010` note). Proven: Surveys.
   - **Scope any bulk `sed`/rewrite over the new test project to tracked files.** A glob over
     `tests/Humans.<Section>.Tests/**/*.cs` also hits `obj/`, and prepending a `using` above the
     `<auto-generated>` header of `XunitAutoGeneratedEntryPoint.cs` disables the generated-code
     suppression — the build then fails inside a file you did not write, with `MA0006`,
     `MA0053` and `RCS1102` on xunit's own entry point. Use `git ls-files` or `-not -path
     '*/obj/*'` (proven: Surveys, ~10 minutes lost to it).
   - **A Base test helper the moved tests inherit is not automatically shared-set material.**
     Check what the section's tests actually *use* before linking it into
     `tests/Directory.Build.props`. Expenses' one harness-derived test used three members of
     `ServiceTestHarness` — an audit substitute, a clock, a static options builder — out of a
     211-line base class built around an in-memory `HumansDbContext`; sharing it would have
     granted a **section** test project `InternalsVisibleTo` on `HumansDbContext` and pushed the
     harness's `Humans.Infrastructure` + `NodaTime.Testing` dependencies into every test project
     compiling the shared set. It owned the three fixtures instead, in five lines. Share when the
     section needs the *harness*; inline when it needs three of its members (contrast: A2's
     `CapturingLogger`, which was genuinely shared).
9. [ ] `dotnet ef` for this section: `--project src/Sections/Humans.<Section>
   --startup-project src/Humans.Web --context <Section>DbContext --output-dir Data/Migrations`.
   Update the `context:project` pair in `.github/workflows/build.yml`.
   - **Change `MigrationsAssembly` in the moved `<Section>DbContextFactory` to
     `"Humans.<Section>"`.** Every design-time factory hardcodes the string, so a factory that
     moves with the section keeps pointing at `Humans.Infrastructure`, EF looks for the section's
     migrations in an assembly they just left, finds none, and reports **"Changes have been made
     to the model since the last migration"** — model *drift*, for what is a wiring bug. The
     runtime is unaffected and hides it: `AddSectionDbContext` → `ConfigureNpgsql` derives the
     assembly from `contextType.Assembly.GetName().Name`, so the app boots and migrates fine and
     only `dotnet ef` disagrees. It fails `build` and `verify-migrations-apply` identically, which
     reads as two problems. Verify with `dotnet ef migrations has-pending-model-changes --context
     <Section>DbContext --project src/Sections/Humans.<Section> --startup-project src/Humans.Web`
     before pushing — step 12 already asks for this and it is the check that catches it
     (`Humans.Expenses` is the correct reference; proven: Surveys, peterdrier/Humans#1251).
   - Diagnostic tell: `dotnet ef migrations add` on the section answers **"Your target project
     'Humans.<Section>' doesn't match your migrations assembly 'Humans.Infrastructure'"**, which
     names the real problem where `has-pending-model-changes` does not.
10. [ ] **Table renames are out of scope. A G5 move changes files, never the schema** (decision
    2026-08-09, recorded on nobodies-collective/Humans#866; tracked at
    nobodies-collective/Humans#1012). Mismatched table names — or a mismatched
    `<Section>DbContext` name, which `SectionMigrationsHistory.TableFor` turns into a live
    history-table name — move on schedule unchanged; record the mismatch on #1012 and carry on
    (proven: Events kept `EventGuideDbContext` and `event_*` tables).
11. [ ] Enforcement: collapse the section's `reforge.surface-score.json` paths to
    `src/Sections/Humans.<Section>/**`; delete the section's `*ArchitectureTests.cs` assertions
    the assembly boundary now subsumes; delete its `Architecture/Baselines` rows and
    `[Grandfathered]` attributes (⚠️ UNPROVEN — no moved section has had any).
    - Re-run `grep -rn 'Assembly.Name' src/Humans.Analyzers/` and confirm any analyzer added
      since the pilot is section-aware (spec §10). **Do not treat this as a formality because
      earlier moves found nothing** — Expenses was the first to find something, and what it found
      had been silently off inside all five previously-moved sections:
      `ConcurrencyTokenAnalyzer` and `DateTimeFormatStringAnalyzer` each carried a private
      `ProductionAssemblies` set of the four literal Base names. Fixed with
      `AssemblyScope.IsProduction`; widening produced zero new findings, so the cost of being
      wrong here is nothing and the cost of skipping it is silent.
    - **Delete the section's row from `HumansDbContext.PeeledConfigurationNamespaces`.** The
      array names each peeled section's configuration namespace by `typeof(...)`, so the row
      stops compiling the moment the configurations leave `Humans.Infrastructure` — self-
      revealing, but it is the first error of the move and it reads like a mistake rather than a
      step. The comment above the array already says a G5 section drops its entry.
    - **Check `AdminNavTree` after any controller rehoming.** Entries name a controller by
      *name*; one that no longer resolves makes the anchor tag helper omit `href` entirely, so
      the page returns 200 with a dead link and neither the suite nor the step 12 HTML diff
      notices. `AdminNavTreeRoutingTests` walks the table against the running app's
      `IActionDescriptorCollectionProvider` (proven: A2's `FinanceController` split shipped the
      Finance entry broken; caught and fixed in A3).
    - **Watch for reflection sweeps keyed on a hard-coded assembly list or a namespace prefix** —
      both fail by finding nothing and reporting success (proven: `EndpointAuthorizationTests`,
      `GdprExportDependencyInjectionTests`, `ApplicationServicesTakeNoMemoryCacheRule`). **Widen
      the sweep, never shrink the expectation**: a baseline row that reads as "fixed" the moment
      your section leaves the scan is the failure, not the fix. Resolve moved types by reflection
      through the existing `SectionType(...)` helper; anchor sweeps on
      `SectionDiscoveryExtensions.SectionAssemblies()` and assert a floor on the set size.
12. [ ] Verify: build; full suite; **render every page in the section and diff HTML against a
    pre-move capture** — capture twice pre-move to prove determinism, re-diff after *each* risky
    step (move, internalisation, renames). `has-pending-model-changes` clean for every context;
    preview deploy boots. **The HTML diff does not catch everything**: an emptied audit panel and
    a non-English resource fallback both survive it — capture in a non-English locale too, or
    check those two by hand. `dotnet watch` hot-reload is not a gate until
    nobodies-collective/Humans#1008 is fixed.
    - **Prefer writing the check as an integration test over taking a capture.** The capture is
      a one-off that only helps the person who took it, and it has to be taken *before* the
      first commit or it cannot be taken at all. A `<Section>PageRenderTests` in
      `Humans.Integration.Tests` that GETs every page of the section and asserts (a) resolved
      copy is present and (b) no raw `<Section>_` key appears in the body catches both G5
      failure modes — incomplete `_ViewImports`, and a key the resx carve missed — and it runs
      on every build afterwards, including for the *next* section that touches these views
      (proven: Surveys, 4 tests over 6 pages; the file is the model to copy).
    - **The non-English case is not optional and is not decoration.** An English-only check
      passes whether or not the section's satellite assemblies shipped, because the neutral set
      is embedded in the main assembly and the fallback is silent. One request with
      `Accept-Language: es` asserting a Spanish string is the only thing that proves an RCL's
      satellites reach the host's probing path.
    - **Assert on ASCII-only substrings of the non-English copy.** Razor's default `HtmlEncoder`
      escapes non-ASCII to numeric entities, so `está` reaches the response body as `est&#xE1;`
      and a literal assertion on the resx value fails while the page is perfectly correct. Take
      the longest accent-free run of the string (proven: Surveys, one wasted red run).

## Things outside the steps that bit a wave

- **The `Dockerfile`'s `RUN dotnet restore` layer does not work and has not for some time.** It
  copies five csprojs, but `Humans.Domain.csproj` references `Humans.Interfaces.csproj`, which is
  never copied — and neither is `Humans.Analyzers` (pulled in by `src/Directory.Build.props`) nor
  any of the section projects. The image still builds because `COPY src/ src/` and the `dotnet
  publish` that follows restore everything again; the layer is a dead cache optimisation. Adding
  your section's two `COPY` lines (as nobodies-collective/Humans#1006 will eventually automate)
  costs nothing and fixes nothing — do not spend time debugging it, and do not conclude your move
  broke the image. Found by A4.

## The `<vc:*>` rename hazard

ReSharper's move-to-namespace and rename refactorings read a `<vc:name>` element as a reference to
the view-component *type* and rewrite it to `<name-view-component>`. Nothing objects: green build,
green suite, 200 response, and the element renders as inert literal markup (proven: PR 0, 127
tags, caught only by the HTML diff). After any refactoring pass over `.cshtml`:

```bash
grep -rn --include='*.cshtml' -- '-view-component' src/
```

Expect zero hits. The step 12 diff is the backstop, not the first line of defence.
