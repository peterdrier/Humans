# G5 Section Move — Checklist

The recipe for moving one section into its own project under `src/Sections/`
(nobodies-collective/Humans#866, G5). Extracted 2026-08-09 from §15 of
[`2026-08-07-g5-section-project-split-design.md`](../superpowers/specs/2026-08-07-g5-section-project-split-design.md)
(cited below as "spec §N") after five sections executed it: Store (peterdrier/Humans#1223),
SystemSettings + EventGuide (peterdrier/Humans#1235, A1), Containers + Finance
(peterdrier/Humans#1239, A2), Expenses (peterdrier/Humans#1240, A3), Surveys
(peterdrier/Humans#1251, A4), Agent (A4b — the first section with static assets, migrations
and a contracts leaf all at once), Gate (the first with no resource set, the first to take a
layout with it, and the first to hit a Shell-resident view component), CityPlanning (the first
with a SignalR hub, the first to reference another section's project, and the first whose
resource carve had to *return* keys to an already-moved section), Scanner (the first section
owning no tables at all — no G4 gate, no `Humans.Infrastructure` reference, an empty
`Section.Register`), Budget (the first to keep an *internal* `I<Section>Service`, the first
whose enums had to move onto the contracts leaf, and the first to hand a Shell dev seeder a
one-method contract), Calendar (the first to keep an *internal* `I<Section>ServiceRead` while
shipping an empty `Contracts/`, the first whose name collided with a Base concern, and the first
whose render test had to seed through a §15 caching decorator), Campaigns (the first moved
section carrying an `Architecture/Baselines` row, and the first whose contracts leaf had to
reference `Humans.Domain`), Feedback (the first whose `Contracts/` is a folder despite a
consumer in another section, and the first to drop `I<Section>Service` entirely — its whole
DTO surface stayed internal behind two primitive-returning reads), Issues (the first to take a
block of markup *out* of a Shell view to bring its resource keys home, and the first whose
`Contracts/` leaf is two one-method interfaces), Notifications (the first section to own a
*view component*, which needed a Shell feature provider of its own, and the first whose leaf
is consumed by eleven Base services), and Governance (the first whose resx carve had to
leave four whole prefixes behind because Shell renders the same form, the first to bind two
localizers by design, and the first whose contracts leaf carries write members under an
unchanged name). Step numbers match the former §15, so an old
"§15 step 3b" citation reads as "step 3b" here.

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
      prod/QA/previews. **A section that owns no tables has no G4 gate** and skips every
      Data/DbContext/migration step below — steps 9 and 10 vanish, step 11's
      `AddSectionDbContext` and `PeeledConfigurationNamespaces` bullets have nothing to move,
      and the project takes **no `Humans.Infrastructure` reference at all** (proven: Scanner,
      the first such section — one controller, one view model, four views, two JS modules).
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
   Models/ Views/ Resources/ Authorization/ Filters/ Docs/ Properties/ wwwroot/` + `Section.cs`. Migrations
   land at `Data/Migrations/` — their `namespace` line changes to the section's, which is the one
   sanctioned edit to a migration file (spec §7); say so in the PR. **Everything the section needs
   comes with it — no exceptions.**
3. [ ] **Write `Views/_ViewImports.cshtml` in the same commit as the views.** Start from the
   shipped Store example (spec §2) but derive the `@using` list from the section's own folders.
   Omitting a line — or one `@addTagHelper` — ships broken HTML with a green build.
3b. [ ] **First ask whether the section has any keys at all.** A section whose views carry no
   `Localizer[…]` call and no `<Section>_*` key in `SharedResource` ships **no `Resources/`
   folder and no `<Section>Resource`** — `SectionResourceTypes()` simply returns one fewer
   marker and the boot diagnostic is happy (proven both ways: Finance and Gate ship none; the
   `GateLogin_*` keys that look like Gate's belong to Shell's `/Account/GateLogin` page and stay).
   Assert it structurally instead: *no* type in the section may take `IStringLocalizer<T>` for
   any `T` (`GateArchitectureTests.SectionTypesTakeNoStringLocalizer`), so the day someone adds
   copy the build tells them to carve a resource set first. Skip the rest of this step.
   Otherwise: carve the section's `.resx` — the `<Section>_*` and `Enum_<Section>*` keys move out of
   `Humans.UI`'s set into `Resources/<Section>Resource.{resx,es,ca,de,fr,it}` beside a
   `<Section>Resource.cs` in the section's namespace. The `.cs` and `.resx` must sit in the same
   folder, and the `.cs` namespace determines the manifest prefix (spec §3) — get it wrong and
   every string in the set degrades to its key at runtime. The boot diagnostic needs no
   per-section edit, **but only if `<Section>Resource` is `public`** — discovery reads
   `GetExportedTypes()` and skips an `internal` marker in silence.
   - **Carve the `.resx` block-aware, not line-by-line.** `SharedResource.resx` writes each entry
     on one line; the five translations do **not** — theirs are three lines
     (`<data …>` / `<value>…</value>` / `</data>`). A line-based filter that matches the opening
     tag takes the opening line and leaves the orphaned `<value>`/`</data>` behind, producing five
     invalid `.resx` files. That one fails loudly (`MSB3103: Invalid Resx file`) rather than
     silently, but it costs a full build cycle; consume to the closing `</data>` (proven: Feedback).
   - **A key whose *renderer* lives in Base stays in Base — carve by renderer, not by prefix.**
     The third direction, after "carve by owner" and "the key goes home". Feedback's
     `Email_FeedbackResponse_{Subject,Body}` look like the section's, and are read by
     `Humans.Infrastructure/Services/EmailRenderer.cs`, which composes every transactional email
     and cannot see a section's resource set. Taking them would degrade that email to raw keys in
     all six languages, and no render test covers an email body. Grep each candidate key's call
     sites before moving it, exactly as with `Enum_*` (proven: Feedback).
   - If step 5 renames an enum, rename its `Enum_{TypeName}_*` keys in all six languages in the
     same commit — the key is the **live CLR type name** (spec §3; proven the hard way: Store).
   - **…and the fourth direction: move the *markup* instead of the key.** "Carve by renderer"
     (above) leaves a key in Base because the renderer cannot see a section set. The other way
     out is to move the renderer. The floating help widget in `Humans.Web/Views/Shared/
     Components/HelpWidget/Default.cshtml` is Shell chrome, but its issue-submission modal is
     eleven `Issue_*` keys, an `IssueCategory` loop and a badge helper — leaving them behind
     would have split one resource set across two and forced the presentation helper public.
     The modal moved to the section's `Views/Shared/_IssueWidgetModal.cshtml` and Shell calls
     `@await Html.PartialAsync("_IssueWidgetModal", model)`; partial lookup resolves by name
     across application parts, the same mechanism as `_GateLayout` and
     `Component.InvokeAsync`. Available when the block is self-contained *markup* — the
     widget's JS addresses DOM ids, not Razor, so it stayed in Shell untouched. Prefer this
     over binding a second localizer in a Base view when the Base view owns none of the copy
     (proven: Issues).
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
     is the shape). **A section that deliberately keeps reading shared keys allows two
     markers, not one** — Governance's applications controller renders *only* keys that stayed
     in `SharedResource`, so its guard is "`<Section>Resource` or `SharedResource`, nothing
     else", which still catches a controller bound to some third set (proven: Governance).
     **A render test is not enough here** — controller-resolved copy tends to sit on
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
     - **…and the unit that stays is the whole *prefix*, not the individual key.** Governance's
       views read 139 keys across 22 prefixes; nine were its alone and moved (112 keys), and
       four stayed *entirely* because one key in each is co-owned — `Application_*` and
       `ApplicationStatus_*` (Shell's `ProfileController` renders the same tier-application form
       and maps the same `ErrorKey`s to the same messages during profile setup), `AdminApp_*`
       (one label read by `TeamAdmin/Members.cshtml`), and the `Profile*`/`OnboardingReview_*`
       singles. Splitting a nine-key message set to claim five of them is worse than leaving all
       nine. **Then verify mechanically**, because the section now binds two localizers and a
       half-switched call site renders the raw key: extract every `Localizer["…"]` key from the
       section and diff it against the section `.resx` key list — anything the diff reports is a
       call site on the wrong localizer (proven: Governance, six caught this way and six more by
       the step 12 render test).
   - **The mirror image: a key *this* section owns that an already-moved section reads goes into
     this section's set, and that section takes a reference to the section *project*.** Not the
     leaf — a resource marker is a section type (`public` only so `GetExportedTypes()` finds it),
     so the consumer needs `Humans.<Section>`, binds `@inject IStringLocalizer<<Section>Resource>`
     in its own `_ViewImports`, and switches the call site off `SharedLocalizer`. Cheap, because
     the assembly boundary means the only public types over there are `Section`,
     `<Section>Resource` and whatever sits under `Contracts/`. Check the reference direction
     first (proven: Budget, whose `Budget_ColCategory` is rendered by Expenses' report grid;
     `Humans.Expenses` → `Humans.Budget` is acyclic because Budget's own graph names only
     `Humans.Finance.Contracts`). The alternative — leaving one key of a set behind in
     `SharedResource` — splits a three-column header across two resource sets and weakens the
     step 12 `NotContain("<Section>_")` assertion to nothing.
   - **When the other owner has already moved, the key goes home rather than staying shared, and
     the shared binding on *both* sides has to move with it.** The reverse of the rule above:
     once both consumers are sections, `SharedResource` owns nothing and the key belongs to
     whichever section's vocabulary it is. City Planning's move took the 9 `ContainerMap_*` keys
     into `ContainersResource` and bound `@inject IStringLocalizer<ContainersResource>` in its
     own `_ViewImports` — and had to switch **Containers'** two call sites from `SharedLocalizer`
     to `Localizer` in the same commit, because the key left the set they were reading. Grep
     `SharedLocalizer["<Prefix>_` across both sections before moving a key out of
     `SharedResource`; missing the other side is a silent raw-key render (proven: CityPlanning).
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
   - **`Register` may legitimately be empty, and the class still ships.** A section that is one
     controller over other sections' read interfaces registers nothing — its dependencies are
     registered by their owners. Keep the type anyway: `ISection` is what puts the assembly in
     `SectionDiscoveryExtensions`'s discovered-sections log, which is the first thing you read
     when a section's page 404s. It is *not* what makes the controllers route — that is step
     4b's assembly attribute, which is independent (proven: Scanner).
   - **…and it is not a parking lot either: read the old extension's registrations against the
     section's scope doc, not its filename.** `GovernanceSectionExtensions` also registered
     three Base cache invalidators that four sections evict through, and the Base human-lifecycle
     service — none of which the section owns
     (`memory/architecture/governance-scope.md` says Governance is tier applications and board
     voting, full stop). Those moved to Shell's `InfrastructureServiceCollectionExtensions`
     beside the moved-out sections' jobs. The section that owns the *file* is not always the
     section that owns the *line* (proven: Governance).
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
     - **"`I<Section>Service` goes" has a real exception, and NSubstitute is what surfaces it.**
       Internalising makes the service `internal sealed` (MA0053), and Castle DynamicProxy
       cannot substitute a sealed class — so any *other* type in the section whose unit tests
       stub the service forces the interface to stay. Keep it, `internal`, as
       `I<Section>Service : I<Section>ServiceRead, IApplicationService`: the section's own code
       carries on injecting it unchanged, and the split that matters (four public read methods
       on the leaf versus forty internal ones) is unaffected. Discovering this after replacing
       every `I<Section>Service` injection with the concrete type costs a second sweep — decide
       it from the *test* files before touching the source (proven: Budget, whose ticketing
       bridge substitutes it).
   - **Decide the leaf-vs-`Domain/` question per enum, not per section.** Budget moved both of
     its enums because both appeared in contract signatures. Issues' two split: `IssueCategory`
     is named by `Humans.Agent`'s `route_to_issue` proposal, so it went to the leaf;
     `IssueStatus` has no consumer outside the section and turned `internal` in `Domain/`
     beside the entities. Grep each enum's out-of-section references separately (proven:
     Issues).
   - **A `Humans.Domain.Enums` enum named in a `Contracts/` signature moves onto the leaf, and
     its `EnumStringStabilityTests` row moves with it.** The entities go to `Domain/` and turn
     internal; an enum in a public DTO cannot. `Humans.Domain.Tests` then cannot name it either
     — take the row into `tests/Humans.<Section>.Tests`, do not delete it: these enums are
     persisted with `HasConversion<string>()` and the row is the only thing standing between a
     rename and silent data mismatch (proven: Budget, `BudgetYearStatus` and `ExpenditureType`).
   - Two renames are **not** inert: a type name written to the database and a type name that
     forms a resource key. `nameof(<AnotherSection'sEntity>)` stops compiling at the move —
     replace it with a literal beside the section's own audit discriminators (Store's
     `Services/AuditEntityTypes.cs` is the shape), never with a project reference. Declare the
     section's own discriminators as literals while you are there; that is what makes the rename
     schema-inert. See `memory/code/type-name-as-persisted-string.md`.
     - **"Stops compiling" only holds when the other section has already moved; otherwise the
       `nameof` survives the move and becomes that section's problem.** `nameof(Team)` in
       Calendar's audit calls compiles fine today — `Team` is still a public
       `Humans.Domain.Entities` type — so nothing forces the fix, and the day Teams goes to G5 it
       breaks in a section nobody is editing. Grep the moved code for `nameof(` over *every* type
       the section does not own, not just the ones the build complains about, and take them all
       into `AuditEntityTypes` (proven: Calendar).
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
     - **…and "moves" can mean "stays inside the section, `internal`".** A populated
       `I<Section>ServiceRead` whose only consumers are the section's own controller is not a
       cross-section contract; it is the seam between the §15 caching decorator and the write
       path, and it belongs in `Services/` beside `I<Section>Service`. Promoting it to
       `Contracts/` because it is named `…Read` publishes a boundary nobody crosses — the same
       mistake as carrying the empty one, one step later. Decide from the *consumer list*, never
       from the name (proven: Calendar, whose `Contracts/` is empty while both interfaces live on
       internal).
   - **A Base type that shares the section's name prefix may belong to an entirely different
     concern — read the signatures before adopting it.** `Humans.Application.Interfaces.ICalFeed`
     holds `ICalendarFeedContributor`, `CalendarFeedItem` and `IICalFeedService`: a Base-owned
     fan-out that assembles a user's personal iCal feed from Shifts and Events. It names nothing
     the Calendar section owns and Calendar does not implement it, so it stayed in Base along with
     Shell's `UserCalendarViewComponent` and `ICalFeedApiController`. A recon pass keyed on the
     string "Calendar" pulls all of it in; a pass keyed on *whose types are in the signatures*
     does not — the same test as step 5b's connector case, applied to a name collision instead of
     a vendor (proven: Calendar).
   - **Carve the leaf from the *call sites*, not the interface.** Notifications' inbox service
     has twelve members and three consumers outside the section, which between them call three
     of them — two auto-resolve calls and, before the bell moved in, the badge count. Moving
     `INotificationInboxService` whole would have published four inbox read-model records and
     nine members nobody outside calls. Grepping each member's out-of-section call sites first
     produced `INotificationAutoResolve` (two methods, no DTO) on the leaf, with the internal
     `INotificationInboxService : IApplicationService, INotificationAutoResolve,
     INotificationRetention` inheriting it so the section's own controller is unchanged. Same
     mechanic on the other three: `INotificationMeterProvider` had **zero** consumers once the
     controller moved in and was deleted outright (step 5's "keep an interface only where
     something needs the seam"), and `INotificationRecipientResolver` survived internal only
     because a section test substitutes it (proven: Notifications).
   - **A leaf may be two one-method interfaces, and splitting them beats one misnamed one.**
     Issues' whole outside surface is a nav-badge count (Shell's `NavBadgesViewComponent`) and
     the retention sweep (Base's `CleanupIssuesJob`). Both return `int`. Putting the purge on
     `I<Section>ServiceRead` would name a write "Read"; putting the count on the retention
     contract is worse. Ship `IIssuesServiceRead` and `IIssuesRetention` and let the section's
     internal `I<Section>Service` inherit both — the interface count is not the thing to
     minimise, the *surface* is (proven: Issues; the retention half is Agent's
     `IAgentConversationRetention` shape, step 6b).
   - **A leaf may keep the full service's *name* while carrying only its writes, when the writes
     are what leaves.** `IApplicationDecisionService` had 16 members and six outside callers:
     three on Shell's `ProfileController` (which submits a tier application from the profile
     setup form) and three on Base's `TermRenewalReminderJob`. Approve, reject, withdraw and the
     whole board-voting surface have no consumer outside the section, so the leaf is the
     decision service minus the decisions — and renaming it `…ServiceRead` would have been a lie
     about six write members. Campaigns' shape, reached from the write side (proven:
     Governance).
   - **Folder vs project is decided by *where the consumer lives*, not how much surface there
     is.** A consumer in Base forces `Humans.<Section>.Contracts` as its own project referencing
     only the bottom of the graph — a folder would cycle
     (`memory/architecture/section-project-cycle-fix.md`; proven: SystemSettings, Events, both on
     first build). Consumers all in Shell → folder is fine at any size (proven: Containers,
     17 types).
   - **A consumer in *another section* whose `Contracts/` is a folder: reference that section's
     project directly.** Not a cycle, and not a reason to re-carve the other section. City
     Planning's barrio container pages inject Containers' `IContainerService` and render its card
     partials; Containers kept `Contracts/` as a folder because its own consumers were all in
     Shell. `Humans.CityPlanning` references `Humans.Containers`, and the assembly boundary
     already limits what that buys — the only public types over there *are* the `Contracts/`
     ones plus `Section` and `ContainersResource`. Acyclic because the pair goes through the leaf
     in the other direction: `Humans.Containers` → `Humans.CityPlanning.Contracts`, never the
     section project. Check that direction before adding the reference (proven: CityPlanning;
     contrast Expenses → `Humans.Finance.Contracts`, where the other side had a leaf already).
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
   - **A Base *enum* in a leaf signature is not Budget's case — reference `Humans.Domain` from
     the leaf.** Budget's rule (step 5) is about enums the *section* owns: they cannot follow the
     entities into internal `Domain/`, so they move onto the leaf. `EmailOutboxStatus` is the
     opposite — the Email section's enum, Base vocabulary that Campaigns re-exports in
     `CampaignGrantSummary` and `UpdateGrantEmailStatusAsync`. Moving it onto the leaf would
     steal another section's type; retyping the member to `string` is behavioural. The leaf takes
     `Humans.Domain` alongside `Humans.Interfaces`, which is acyclic — `Humans.Domain` references
     only `Humans.Interfaces`, and both sit below `Humans.Application`. Every earlier leaf
     referenced `Humans.Interfaces` alone, so this reads as a break with the pattern and is not
     one (proven: Campaigns).
   - **A DTO the section re-exports from Base forces the project split even when nothing else
     does** — and promoting the connector's DTO downward is the forbidden fix; the section owns a
     boundary type and maps at the edge (proven: Finance, `HoldedLedgerLineDto`). Check every
     `Contracts/` signature for a Base-owned type before assuming the carve is mechanical.
   - **A Shell *dev seeder* is the job shape wearing different clothes.** `DevSeedController`
     does `GetRequiredService<Development<Section>Seeder>()` on a concrete type, and the seeder
     drives the section's whole write surface — fifteen methods that would otherwise have to go
     public on the leaf to serve one dev button. Same answer as step 6b's job: one method on the
     leaf returning what the caller actually uses (`Task<string>`, the operator-facing success
     message), the seeder internal in the section, registered from `Section.Register`. The
     ten-field result record it built stays internal (proven: Budget, `IBudgetDemoSeeder`).
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
   - **The same rule catches a Shell-resident *view component*, and that one fails at runtime,
     not at build.** A section's `Views/_ViewImports.cshtml` can only `@addTagHelper *,
     Humans.UI` — it cannot name `Humans.Web` — so any `<vc:…>` whose component class lives in
     Shell renders as **inert literal markup**: green build, 200 response, element silently
     dropped by the browser. Grep the moved views for `<vc:` and check where each component
     actually lives before writing `_ViewImports`. `<vc:human>` was already in `Humans.UI`;
     `<vc:human-search>` was not, and moving it (component + `HumanSearchPickerViewModel` +
     `HumanSearchScope` + `Views/Shared/Components/HumanSearch/Default.cshtml`) is what made
     Gate's `Claim` and `Admin` pages render. `Humans.UI/Views/_ViewImports.cshtml` needs the
     matching `@using` for the moved model. Same test as the filter base: the picker names no
     section's vocabulary. Caught by the step 12 render test, never by the build (proven: Gate).
     **Shell keeps rendering when the component moves down, so this is cheaper than it looks**:
     `Humans.Web/Views/_ViewImports.cshtml` already carries `@addTagHelper *, Humans.UI`, so
     every existing `<vc:…>` in Shell resolves the moved component with no edit. The whole
     change is `git mv` of the component class and its
     `Views/Shared/Components/<Name>/Default.cshtml` plus the namespace line — check only that
     the `Default.cshtml` uses no `@using` that `Humans.UI/Views/_ViewImports.cshtml` lacks
     (proven: Scanner moved `TicketStubViewComponent`, which four Shell views also render, and
     touched none of them).
   - **Fourth case, a rider on City Planning's: invoking by name still needs the component's
     *argument types* to be nameable from the section.** `ProfileCardViewComponent` stitches
     contact fields, emails, teams and roles from seven Base services, so it stays in Shell and
     `<vc:profile-card view-mode="@ProfileCardViewMode.Admin" />` becomes
     `@await Component.InvokeAsync("ProfileCard", new { userId, viewMode })` — except the enum
     was declared beside the component in `Humans.Web.ViewComponents` and the section cannot
     name it, so the invocation does not compile. The enum moved to `Humans.UI/ViewComponents/`
     (Self/Public/Admin carries no section vocabulary, the same test as the filter base),
     Shell's `Views/_ViewImports.cshtml` gained `@using Humans.UI.ViewComponents`, and Shell's
     own `<vc:profile-card>` call sites were untouched. Check the component's parameter types
     before choosing invoke-by-name (proven: Governance).
   - **A SignalR hub is the health-check shape, and where it goes depends on whose types are in
     it.** `Program.cs`'s `app.MapHub<TheHub>("/hubs/…")` names the concrete type, so a hub cannot
     live in the section — HUM0034 fails the build for a public section type, and the section's
     `IHubContext<TheHub>` injection needs the type visible to it either way. Apply the same test
     as the filter base: `CityPlanningHub` relays a connection id, a display name and a lat/lng
     and names no City Planning type at all, so it went to `Humans.UI/Hubs` and both Shell's
     `MapHub` and the section's `IHubContext<…>` resolve it. A hub whose signatures *do* name
     section types would stay in Shell with a contract in between, the way a health check does
     (proven: CityPlanning; nothing has hit the second case yet).
   - **The third case: the component belongs to the section, and moving it in needs a feature
     provider Shell did not have.** Gate's rule moves a section-neutral component *down* to
     `Humans.UI`; City Planning's leaves a registry-reading one in Shell and invokes it by
     name. Notifications' bell is neither — it renders the section's own unread counts and two
     of its resource keys, so leaving it in Shell would split a 38-key set. Taking it in costs
     two things. First, MVC's `ViewComponentConventions.IsComponent` requires `IsPublic`, so an
     `internal` component is silently never discovered — exactly the hazard
     `SectionControllerFeatureProvider` exists for on the controller side. The counterpart is
     `Humans.Web/Infrastructure/SectionViewComponentFeatureProvider`: a second
     `IApplicationFeatureProvider<ViewComponentFeature>` pass (the base one is not virtual and
     `ViewComponentConventions` is internal to MVC) that adds non-public components from
     assemblies carrying `[assembly: Section("…")]`. Write it once; every later section with a
     view component inherits it. Second, **every `<vc:…>` call site in Shell must become
     `@await Component.InvokeAsync("Name")`** — the tag helper is generated at compile time
     from *public* types in referenced assemblies, so it cannot see the section's. Shell's
     `_Layout`/`_AdminLayout` already invoked the bell by name and needed no edit; the widget
     gallery's `<vc:notification-bell />` did. Failure mode is loud in one direction and silent
     in the other: an unresolvable `Component.InvokeAsync` **throws**, so one render test on any
     authenticated page catches the provider being missing, while a stray `<vc:>` renders as
     inert markup and needs the `NotContain("<vc:")` assertion (proven: Notifications).
   - **A `<vc:…>` whose component reads a Shell-owned *cross-section registry* does not move —
     invoke it by name.** The Gate fix (move the component down to `Humans.UI`) is right when the
     component is self-contained. `<vc:access-matrix>` is not: `AccessMatrixViewComponent` reads
     `AccessMatrixDefinitions` and `SectionHelpContent`, ~900 lines naming every section's roles,
     routes and FAQ, which `Humans.Web`'s agent preloader reads too. Dragging that into
     `Humans.UI` to satisfy one section is the registry-inversion problem (step 5b) wearing a
     different hat. `@await Component.InvokeAsync("AccessMatrix", new { section = "…" })` resolves
     the component **by name across application parts**, so the widget renders from a section
     view with the registry left in Shell. Assert it rendered — the component emits a modal id
     built from the section key, and a component that fails to resolve throws rather than
     degrading, so one assertion per call site is enough (proven: CityPlanning, the first moved
     section to use the widget).
   - **A Shell-only *helper* a moved controller calls moves the same way.** Gate's `/Gate/Search`
     used `Humans.Web.Models.HumanLookupSearchResult` and
     `SearchResultMappingExtensions.OrderByRelevance` — a JSON row shape and the canonical
     person-search ordering, both section-neutral and both used by three other Shell controllers.
     They went to `Humans.UI/Models` and `Humans.UI/Extensions`; inlining the ordering into the
     section would have forked a rule the codebase documents as having one owner (proven: Gate).
     **Second sighting, and the cheapest yet: a plain view model two sections bind.**
     `AssigneeOption` and `ReporterDropdownItem` (an id + label, and a reporter + label + count)
     sit in `Humans.Web.Models/FeedbackViewModels.cs` and are bound by Issues' triage pages too —
     `IssueViewModels.cs` says so in a comment. Taking them into the section breaks Issues;
     duplicating them forks the shape the comment exists to keep. Same test as the filter base
     and the helper: they name no section vocabulary, so `Humans.UI/Models`. Shell's
     `Views/_ViewImports.cshtml` already has `@using Humans.UI.Models`, so only the two `.cs`
     files needed a `using` (proven: Feedback).
     **Third sighting, and this one is done for everybody: `PagedListViewModel`.** Any section
     with an admin list page derives its list view model from Shell's pagination base;
     Governance was the first, and `TabbedMarkdownDocumentsViewModel` +
     `_TabbedMarkdownDocuments.cshtml` came with it (the statutes tabs, also rendered by the
     legal and consent pages). Both went to `Humans.UI/Models` and `Humans.UI/Views/Shared/`
     for three `using` lines (proven: Governance).
   - **Do this on paper *before* step 5b.** Moving the handler is often what takes the read
     surface's fan-in to zero. Expenses' fan-in read as "`IExpenseReportServiceRead`'s only
     outside consumer is Shell's `IbanAccessHandler`", which would have made six types public to
     serve one handler — a handler that this step moves into the section anyway (proven:
     Expenses; its `Contracts` project ended up one interface wide).
6a. [ ] **A section's in-memory state holders go under `Services/Stores/`, not `Services/`.**
   `ApplicationServicesTakeNoMemoryCacheRule` sweeps every namespace matching `Humans.*.Services`
   — widened at Expenses so a moved section cannot fall out of it — so a throttle bucket or a
   sent-ledger that was fine in `Humans.Web/Infrastructure` becomes a violation the moment it
   lands in `Humans.<Section>.Services`. Agent's `Services/Stores/` is the shape and the sweep's
   predicate does not match it, which is the honest classification rather than a dodge: these
   types *are* the cache, where the rule is about a service that has acquired one. Gate's
   `GatePinThrottle` and `GateVendorMirrorLedger` moved there (proven: Gate).

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
     - **Third sighting, and the tuple is what keeps it a pure carve.** `CleanupNotificationsJob`
       injected `INotificationRepository` and applied three cutoffs itself — a job reaching past
       the service layer into a repository, which the move is the moment to fix. The obstacle is
       that the job logs the three delete counts separately, so a single `Task<int>` would change
       the log line. `Task<(int Resolved, int StaleInformational, int RetiredSource)>` needs no
       DTO on the leaf and leaves the message byte-identical; the cutoffs and the retired-source
       list move into the section beside the repository. Its job test becomes a retention test in
       the section's own project — the job left is a try/catch and a metric (proven:
       Notifications, `INotificationRetention`).
7. [ ] `wwwroot/` assets move with the section; URLs become `/_content/Humans.<Section>/…` in the
   same PR. Only Shell's own chrome assets stay in Shell. **Proven: Agent (A4b), the first
   section to move any; and Gate, which also proved a section may take a *layout* with it —
   `_GateLayout` moved from `Humans.UI/Views/Shared` into the section's own `Views/Shared`, and
   the kiosk `_ViewStart`'s `Layout = "_GateLayout"` string still resolves across application
   parts. A layout miss throws at request time rather than degrading, so one render test per
   layout is enough.** What A4 predicted was right, and the fix is one line — in the *test
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
7b. [ ] The section's **invariants doc** moves into `Docs/` along with its own design specs;
   **its `docs/features/*.md` spec does not.** `AgentFeatureSpecReader` lists and fetches
   `docs/features/{stem}.md` from GitHub at runtime with **no whitelist** — the stem set is the
   folder listing — so moving a feature doc silently removes it from what the agent can serve,
   with no probe and no fallback (contrast `AgentSectionDocReader`, which probes
   `src/Sections/Humans.{key}/Docs/{key}.md` second and is why the *invariants* doc may move).
   Rewrite the feature doc's own `freshness:triggers` to `src/Sections/Humans.<Section>/**` and
   leave the file where it is (proven: Gate, whose `gate-admissions.md` stayed).
   Also:
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
   - **Delete the section's `SectionDb<…>` pair from `ServiceTestHarness` in the same commit.**
     `Humans.Application.Tests`' harness declares a lazy context+factory pair per peeled section
     (§858); the one for a moving section stops compiling the moment its `DbContext` leaves Base.
     Its former user is the section's own service test, which needs two members of the harness —
     the clock and that factory — and owns them in about ten lines rather than inheriting a base
     class built around an in-memory `HumansDbContext` (proven: Budget; same call as Expenses').
   - **When the section test needs the *whole* harness, the replacement is a registry, not
     three inlined fields.** Budget's and Expenses' service tests used a clock and a factory, so
     "own them in ten lines" worked. Campaigns' used `Db.Users`/`Db.Teams` through
     `SeedUser`/`SeedTeam`/`SeedTeamMember` and DB-backed `ITeamService`/`IUserService` stubs —
     none of which a section test project can see. Rewriting the *stubs* rather than the tests
     is what keeps it small: two `Dictionary<Guid, …>` of `UserInfo`/`TeamInfo`, seeders that
     keep their old signatures, and the ~500 lines of test bodies compile unchanged. Decide
     between the two shapes from what the tests read, not from how many harness members they
     name (proven: Campaigns).
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
   - **Split the helper before deciding — "share it" and "inline it" can both be right for one
     file.** `Humans.Application.Tests`' `ServiceLocatorBuilder` (46 lines, NSubstitute only) is
     genuinely shared: it moved to `tests/Humans.Testing/` and is `Compile`-included from
     `tests/Directory.Build.props`, and the ~25 existing users needed no edit because
     `<Using Include="Humans.Testing" />` is already global. `UserInfoStubHelpers` is not: its
     pure `ToUserInfo` projection is 12 lines, but the same file carries
     `StubGetUserInfosFromDb` overloads over an in-memory `HumansDbContext`, so sharing it would
     push `InternalsVisibleTo` on `HumansDbContext` into every section test project. The section
     copied the projection (proven: Governance).
   - **An enum that turns `internal` breaks every `[InlineData]` theory that names it, and the
     fix is a `[Fact]`.** `InternalsVisibleTo` makes the type *visible* to the test project but
     a `public` test method still cannot take an `internal` parameter — CS0051. Making the test
     class `internal` would satisfy the compiler and is a bet on xunit v3 discovering non-public
     classes; folding the cases into one `[Fact]` with an assertion per value provably still
     runs. Same for a `public static TheoryData<TInternalEnum, …>` member (proven: Issues,
     `IssueStatusTransitionTests`).
   - **A table-less section's test project must opt out of the shared EF fixture, not take an EF
     package to satisfy it.** `tests/Directory.Build.props` `Compile`-includes
     `TestDbContextFactory` (and `CapturingLogger`) into every test project but
     `Humans.Analyzers.Tests`, and `TestDbContextFactory` needs `Microsoft.EntityFrameworkCore`
     to compile. A section with no `DbContext` therefore fails to build until it either takes an
     EF `PackageReference` it never uses or is added to that exclusion — take the exclusion; the
     package would be a lie about what the section is (proven: Scanner, the second project on
     that list).
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
   - **The `Humans.Domain.Entities.<Entity>` strings left in the moved Designer and snapshot are
     schema-inert — do not touch them.** They look like the loudest thing in the diff and they are
     the one thing that is fine: EF's differ compares the *relational* model (tables, columns,
     keys), and the CLR type name only feeds model construction, so a namespace change produces
     an identical relational model and no migration. Regenerating to "fix" the strings would
     change the migration id and orphan the baseline already applied in prod/QA.
     `has-pending-model-changes` is the proof and it is already step 12 (proven: Gate left them,
     CityPlanning verified clean).
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
    `src/Sections/Humans.<Section>/**` — **if the section has no bucket of its own, retarget the
    stale path where it sits rather than inventing one**; Scanner's controller was one line in
    the `Platform` catch-all, and adding a `Scanner` bucket would have been a scoring change on
    top of a file move. Delete the section's `*ArchitectureTests.cs` assertions
    the assembly boundary now subsumes; delete its `[Grandfathered]` attributes
    (⚠️ UNPROVEN — no moved section has had any).
    - **An `Architecture/Baselines` row is a *retarget*, not a deletion — and the rule's scan
      is probably keyed on the Base path.** Campaigns was the first moved section to carry one
      (`DisplaySortInControllers`, one `OrderByDescending` in the repository). Deleting the row
      would say the violation was fixed while the code is byte-identical; the row moves to
      `src/Sections/Humans.<Section>/Data/…`. But the rule only *sees* it if the scan does:
      `DisplaySortInControllersRule.Scan` walked `src/Humans.Infrastructure/Repositories` alone,
      so every section repository that moved before Campaigns had silently left the sweep and
      the rule was reporting success by finding nothing. Widened it to Base plus each section's
      `Data/` (skipping `Data/Migrations/`) — the same "widen the sweep, never shrink the
      expectation" call as the reflection sweeps below, applied to a path-keyed one. Widening
      surfaced no other violation, so the cost of being right here was one retargeted line
      (proven: Campaigns).
    - **One assertion shape does not survive the move and must be restated rather than deleted:
      `typeof(<Section>Service).Assembly.GetReferencedAssemblies()` must not contain
      `Microsoft.EntityFrameworkCore`.** It was a true statement about `Humans.Application`; the
      section assembly holds the repository and legitimately references EF, so the test either
      fails or — worse, when written as a "does not contain" that now runs over a different
      assembly — keeps passing while asserting nothing. Restate it on the constructor: no
      parameter is a `DbContext`, an `IDbContextFactory<>`, or a
      `Humans.Application.Interfaces.Stores` type. That is what the original was reaching for and
      it is stronger. Ten unmoved sections carry the shape (`CampaignsArchitectureTests`,
      `GovernanceArchitectureTests`, `TeamsArchitectureTests`, …), so expect it every time
      (proven: Calendar).
    - Re-run `grep -rn 'Assembly.Name' src/Humans.Analyzers/` and confirm any analyzer added
      since the pilot is section-aware (spec §10). **Do not treat this as a formality because
      earlier moves found nothing** — Expenses was the first to find something, and what it found
      had been silently off inside all five previously-moved sections:
      `ConcurrencyTokenAnalyzer` and `DateTimeFormatStringAnalyzer` each carried a private
      `ProductionAssemblies` set of the four literal Base names. Fixed with
      `AssemblyScope.IsProduction`; widening produced zero new findings, so the cost of being
      wrong here is nothing and the cost of skipping it is silent.
    - **Move the section's `AddSectionDbContext<…>` line out of
      `Humans.Infrastructure/Hosting/InfrastructureServiceCollectionExtensions.cs` into
      `Section.Register`, sentinel table included.** Leaving it behind is a compile error the
      moment the context leaves Base, and copying the *wrong* sentinel into `Section.cs` is a
      silent baseline-detection change — read the line you are deleting rather than guessing
      (Gate's is `gate_settings`, not the obvious `gate_scan_events`).
    - **Delete the section's row from `HumansDbContext.PeeledConfigurationNamespaces`.** The
      array names each peeled section's configuration namespace by `typeof(...)`, so the row
      stops compiling the moment the configurations leave `Humans.Infrastructure` — self-
      revealing, but it is the first error of the move and it reads like a mistake rather than a
      step. The comment above the array already says a G5 section drops its entry.
    - **A controller named with `typeof(...)` outside its own test project stops compiling.**
      Two shapes, both easy to miss because neither is a "reference to the section": a Shell XML
      doc comment's `<see cref="<Section>Controller"/>` (becomes `<c>…</c>` prose), and
      `EndpointAuthorizationTests.CriticalEndpointPolicies`, which names critical controllers by
      `typeof` — that one takes the same `SectionType("Humans.<Section>.Controllers.…")`
      reflection helper `GdprExportDependencyInjectionTests` and
      `ApplicationServicesTakeNoMemoryCacheRule` already use, throwing on a miss so the row
      cannot silently drop out of the table (proven: Scanner; Gate's controllers were not in it).
    - **`ServiceBoundaryArchitectureTests` is a fourth place a section type is named by
      `typeof`** — its repository-interface → section map. It already carries a
      `SectionRepository(fullName)` reflection helper for the sections that moved before yours;
      swap the row rather than dropping it (proven: Budget, `IBudgetRepository`).
    - **…and `AuthorizationPolicyTests` is a fifth, which reads the policy *off* the controller
      rather than naming it in a table.** `FeedbackControllerPolicy` is
      `typeof(FeedbackController).GetCustomAttribute<AuthorizeAttribute>()?.Policy` — four tests
      run the real authorization pipeline against whatever the controller actually carries, which
      is the point of it. That file had no `SectionType(...)` helper of its own, so the first
      section whose controllers it names has to add one (copy `EndpointAuthorizationTests`'; it
      throws on a miss). Grep `typeof(<Section>` across `tests/` rather than waiting for the
      compiler — three of the five sites are in files whose names suggest nothing about your
      section (proven: Feedback).
    - **A Base badge helper has two shapes and only one of them is `EnumBadgeMap`.** The registry
      inversion (step 5b) covers `EnumBadgeMap`'s literal rows, read by `CellFormat.EnumBadge`
      table columns. `Humans.UI/Extensions/StatusBadgeExtensions` is the other: a
      `GetBadgeClass(this <Section>Enum)` overload the section's own views call directly. When the
      section's views are its only callers, that is not a registry problem — delete the overload
      from Base and ship an `internal static` extension in the section's `Models/`, which is what
      Expenses already did for `ExpenseReportStatus` (proven: Budget).
      **When a *Shell* caller also reads it, neither half works and the answer is the registry.**
      `GetBadgeClass(this ApplicationStatus)` was called from the section's views and from
      `ProfileController`'s tier block; reshipping it in the section leaves Shell unable to spell
      it, and keeping it in `Humans.UI` makes Base reference the section's contracts leaf for the
      enum. Delete both overloads, push the rows in from `Section.Register` via
      `EnumBadgeMap.Register`, and let both sides call `EnumBadgeMap.For(value)` — the colours are
      identical because `For` returns `bg-secondary` for an unmapped value, which is the switch's
      `_` arm (proven: Governance).
    - **A Shell-resident presentation helper is the badge-helper rule's third shape, and it is
      the easy one.** `EnumBadgeMap` inverts (step 5b); `Humans.UI/Extensions/
      StatusBadgeExtensions` gets deleted and reshipped in the section (Budget finding 35).
      `Humans.Web/Helpers/IssuePresentation` was neither: it sits in *Shell*, not `Humans.UI`,
      so no Base type ever named it, and once its only non-section caller moved into the
      section (step 3b's fourth direction) it went in whole and turned `internal`. Check which
      of the three you have from *where the helper lives* before reaching for the inversion.
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
    - **A §15-decorated section's render test must seed through the service, not the
      `DbContext`.** The decorator is a Singleton that warms at startup, i.e. before the test
      body runs, so a row written straight into `<Section>DbContext` is invisible to every
      snapshot-scan read — the section's list/index/window pages render empty and the resx-carve
      assertion covers whatever copy is left on an empty page. Resolve the section's own
      `I<Section>Service` (the test project already sees internals) and create the fixture
      through it; the decorator refreshes its entry on the write path. By-id pages happen to work
      either way because `TrackedCache` lazy-loads a missing key, which makes the broken half
      look like a data problem rather than a caching one (proven: Calendar; contrast Budget,
      which has no decorator and seeds through its context).
    - **The non-English case is not optional and is not decoration.** An English-only check
      passes whether or not the section's satellite assemblies shipped, because the neutral set
      is embedded in the main assembly and the fallback is silent. One request with
      `Accept-Language: es` asserting a Spanish string is the only thing that proves an RCL's
      satellites reach the host's probing path.
    - **`Accept-Language` does not reach a signed-in page.** `Program.cs` registers an *initial*
      `CustomRequestCultureProvider` that returns the authenticated user's `PreferredLanguage`
      and short-circuits the rest of the chain, so the header provider never runs — the page
      renders `lang="en"` and the Spanish assertion fails while the satellites are perfectly
      fine. Surveys got away with the header because its Spanish case is the anonymous public
      page. For a section whose pages are all `[Authorize]`, switch the language the way the UI
      does: GET any section page for the antiforgery token, POST it to `/Language/SetLanguage`
      with `culture=es`, then GET the page under test (proven: CityPlanning).
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
