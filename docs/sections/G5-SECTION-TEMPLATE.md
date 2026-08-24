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
reference `Humans.Domain` — a project deleted in G5 lane 3b; that leaf now takes
`Humans.Interfaces`, same namespaces), Feedback (the first whose `Contracts/` is a folder despite a
consumer in another section, and the first to drop `I<Section>Service` entirely — its whole
DTO surface stayed internal behind two primitive-returning reads), Issues (the first to take a
block of markup *out* of a Shell view to bring its resource keys home, and the first whose
`Contracts/` leaf is two one-method interfaces), Notifications (the first section to own a
*view component*, which needed a Shell feature provider of its own, and the first whose leaf
is consumed by eleven Base services), Governance (the first whose resx carve had to
leave four whole prefixes behind because Shell renders the same form, the first to bind two
localizers by design, and the first whose contracts leaf carries write members under an
unchanged name), Email (the first whose resource set is *other sections'* copy, carved by
moving the renderer; the first whose leaf carries an interface Base *implements*; and the
first whose recurring job was the section's entire write path), Consent (the first whose
resx carve had to rebind call sites in *three* assemblies — Shell, another section and a
section partial's `ResourceManager` — and the first whose contracts leaf carries a view
model), and Guide (the first to reach a green build and a green suite with the section
*unreferenced by Shell and therefore absent at runtime*, and the first whose
same-named connector stayed in Base), and Cantina (the second table-less section, the first
whose *whole* outward surface is a policy name and a controller name — both `string` constants
in Base — and the first whose move corrected an invariants doc that had been asserting the
wrong HTTP status for its own access gate), and Debug (the first section whose only localizer
binding is `SharedResource` *by design*, the first to send a Shell helper further down than
`Humans.UI`, and the first table-less section whose test project needed no
`tests/Directory.Build.props` exclusion), and Development (the first whose `Section.Register`
had to gate on the **host environment**, the first whose move took a block of markup out of a
Shell view to keep a *type* internal rather than a resource key, and the first named by
`typeof` from Shell's own production code), and MailerLite (the first whose *whole* outward
surface is one `int`, the first whose vendor connector left `Humans.Infrastructure` without
needing a reference back to it, and the first whose controller names its views by absolute
path), and Gdpr (the first whose leaf exists because Base *implements* its contract at
scale rather than calling it — 21 implementers across Base and thirteen sections — the
first with neither a controller, a view, a table nor a resource set, and the first to write
its invariants doc because the section never had one), and Onboarding (the first whose
leaf exists because the section it consumes also consumes *it*, the first to hand another
section's presentation layer back to Shell behind a new view component, and the first whose
resx carve had to leave keys behind for MVC's global data-annotation localizer), and Search
(the widest fan-out of any section and the *smallest* outward surface — the first whose
`Contracts/` is empty because every consumer moved in with it, and the first whose resx carve
had to split a single prefix by renderer three ways), and Teams (the widest fan-in of any
section — ~55 consumers of a read interface it split years earlier — the first to ship three
levels of service interface, the first whose enums were pinned in Base by a `Humans.UI`
renderer, and the first to hand another section's dev seeders a leaf interface implemented
explicitly), and Tickets (the first section to carve
a *sibling adapter section* — `Humans.TicketTailor` — out of a Base connector while leaving the
port in Base; the first to ship both a `.Contracts` leaf and a `Contracts/` folder; the first
to take a view component back *out* of `Humans.UI`; and the first whose lane closed a
cross-section cow path by moving two call sites off a Base port onto its own leaf), and Auth
(the second horizontal, the first section to ship *no controller, view or resource set* because its
own controller turned out to write only other sections' tables; the first plain
`Microsoft.NET.Sdk` project to need a `FrameworkReference`; and the first whose leaf had to stop
naming a *vertical* section's record). Step
numbers match the former §15,
so an old "§15 step 3b" citation reads as "step 3b" here.

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
      and the project may take **no `Humans.Infrastructure` reference at all** (proven: Scanner,
      the first such section — one controller, one view model, four views, two JS modules).
      **"Table-less" does not imply that, though** — Guide owns no tables and still references
      `Humans.Infrastructure`, for the `GuideSettings` its Base-resident content source binds.
      Decide the reference from what the section *names*, not from whether it has a `DbContext`.
- [ ] **The section is actually unmoved.** The §858 peel roster is a list of *DbContexts*,
      not a list of sections, and a context can ride into another section's project — Legal's
      lane was a no-op for exactly that reason. One line settles it:
      `ls src/Sections | grep -i <section>` settles it.
- [ ] **A horizontal section (Auth, Audit, GDPR, Notifications) may reference a vertical's
      `.Contracts` leaf. It may not reference the vertical's *section project*.** This
      supersedes the rule that stood here until 2026-08-14, which read
      `peters-hard-rules.md`'s "a horizontal may not reference a vertical" as reaching the
      leaf too, and therefore concluded that a horizontal service injecting another section's
      `I<Section>ServiceRead` "cannot move into the horizontal's project". Peter's Base-floor
      decision makes a leaf referenceable from anywhere, and the two services that reading had
      stranded in `Humans.Application` — `AuditViewerService` and `MagicLinkService` — moved
      into AuditLog and Auth on exactly that basis.

      So still grep the section's services for `I*ServiceRead` and `Humans.*.Contracts`, but
      grep for the *section-project* reference the move would force, not for leaf names. A
      leaf reference is a line in the csproj with a reason attached; a section reference is
      the thing that cycles.
      - **"Is an orchestrator" is not "can't move".** That a service injects no
        `I*Repository` is a statement about the hard rules' orchestrator/service split, not
        about where the class may live. An orchestrator may live in the section it
        orchestrates for (proven: Auth, AuditLog — lane 4b-2i moved `MagicLinkService` into
        `Humans.Auth`).
      - **A controller does not follow its section's service, and a service coming home does
        not pull its controller in either.** `AccountController` is Auth's by every doc; run
        the "read the controller" test below and every one of its actions writes Users' or
        Profiles' tables through their services and injects nothing the move internalises. It
        stayed in Shell with its seven views and 35 resource keys when `MagicLinkService` was
        in Base, and it stayed there when `MagicLinkService` moved into `Humans.Auth`, so step
        3b still stops at its first question and step 12 still falls back to Gdpr's
        DI-registration check. A section that ships no controller, no view and no resource set
        is not an incomplete move (proven: Auth, twice).
- [ ] Fan-in known: run `reforge` for inbound references before starting. A section with many
      inbound section references is a knot, goes later, and may need `<Section>.Contracts`.
      - **A section whose fan-in is measured in three digits gets split into a read-boundary
        lane, a file-move lane and a presentation lane before anyone starts.** The cost is
        the build/test loop over ~250 changed files, not the thinking. Do the read-boundary
        lane first regardless: a move commit that also splits a 50-member interface across
        73 files is unreviewable (proven: Shifts, ~130 consumer files; HUM0032 caught a real
        one on the first build). HUM0032 works either side of the move now — it resolves both
        sections from the assembly name, falling back to the namespace.
      - **…and the read-boundary lane's first pass is "who is bypassing the boundary DTO the
        section already ships?", not "what should the leaf carry?"** Shifts had shipped
        `IBurnSettingsService` → `BurnSettingsInfo` a year earlier precisely so nothing
        outside the section would see `EventSettings`, and eleven external files were still
        reading the entity off the full service. Draining that cow path removed more entity
        leak than the whole recon found. Resolve each member's callers **per file**, from
        the constructor parameter's own name — a bare grep for `.GetActiveAsync(` collides
        with every service in the repo and answers wrongly in both directions
        (proven: Shifts lane A).

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
# references to the section's namespace that are only PARTIALLY qualified. The sed that
# repoints `Humans.Application.Interfaces.<Section>` → `Humans.<Section>.Contracts` matches
# the full name and misses `Interfaces.<Section>.SomeType`, which C# resolves through the
# enclosing namespace. One such site exists in the repo and it is in a test file whose
# usings say nothing about the section (step 5).
grep -rn 'Interfaces\.<Section>\.\|Services\.<Section>\.' src/ tests/
# a DI-graph test that builds the real service chain names the CONCRETE service and never
# writes typeof, so the typeof sweep above misses it (step 8). Two exist and neither
# filename mentions a section.
grep -rn 'AddScoped<<Section>Service>' tests/
# HORIZONTAL SECTIONS ONLY: vertical leaf types named in what will become your leaf's
# signatures. Not the same search as the ProjectReference audit — this one fails at the
# leaf, and a `(bool, string?)` record is the shape that hides in it (step 5b).
grep -rn 'Humans\.[A-Za-z]*\.Contracts' src/Humans.Application/Interfaces/<Section>
# whether those Enum_ keys are LIVE — grep the CALL SITES, never the helper class (step 3b).
# Added by Expenses (A3): the methods are EnumDisplay/EnumSelectItems and nothing is named
# "Localize", so looking for the helper by name reports a live key set as orphaned.
grep -rn 'EnumDisplay\|EnumSelectItems' src/
```

One more, for the bulk `sed` that follows the searches: a namespace rewrite whose
last segment is also the prefix of a type name will merge the two.
`Humans.Web.Models.VolunteerTracking` → `Humans.Shifts.Models` turns
`Humans.Web.Models.VolunteerTrackingPageViewModel` into `Humans.Shifts.ModelsPageViewModel`,
which fails inside *generated* Razor naming a namespace that never existed. Anchor the
pattern on a following `;`, `(` or newline, or sweep afterwards for
`Humans\.<Section>\.(Models|Services|Data)[A-Za-z]` (proven: Shifts, one hit).

Two shell notes, because the obvious spellings both fail *silently*: `grep`'s default is a basic
regular expression, so `nameof(<Section>*)` parses `*` as "repeat the previous character" and
misses `nameof(StoreProduct)` — use the plain prefix. And `src/**/*.resx` is not recursive without
`shopt -s globstar`; use `--include`. (`rg` avoids both, but is not on `PATH` in this repo's
Git Bash.)

## Steps

1. [ ] `src/Sections/Humans.<Section>/Humans.<Section>.csproj` — `Microsoft.NET.Sdk.Razor`
   **when the section has controllers or views**, plain `Microsoft.NET.Sdk` when it has neither
   (SystemSettings): discovery keys off `Section.cs : ISection`, not off being an MVC
   application part. **There is a third shape: plain `Microsoft.NET.Sdk` *plus*
   `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, with no `AddRazorSupportForMvc`,
   no `<Using>` group and no `Humans.UI` reference** — for a section that renders nothing but
   names an ASP.NET type anyway. Auth's are `AuthorizationHandler<,>` (its resource handler,
   step 6) and `IHostedService` (its §15 decorator). Decide the framework reference from what the
   section *names*, never from whether it has views; Central Package Management has no
   `PackageVersion` for the AspNetCore packages, so the framework reference is the only way to
   get them (proven: Auth). With Razor: `<AddRazorSupportForMvc>true</AddRazorSupportForMvc>`,
   `<InternalsVisibleTo>` for **both** `Humans.<Section>.Tests` **and `Humans.Integration.Tests`**
   (spec §5), `FrameworkReference Microsoft.AspNetCore.App`, the section's own NuGet packages,
   `<None Include="**\*.md" />`, and the three `<Using>` items Sdk.Razor does not inherit from
   Sdk.Web (spec §2): `Microsoft.AspNetCore.Http`, `Microsoft.AspNetCore.Routing`,
   `Microsoft.Extensions.Logging`. Project references: `Humans.Interfaces`,
   `Humans.Application`, `Humans.Infrastructure`, `Humans.UI`. (**`Humans.Domain` was on this
   list until G5 lane 3b deleted the project — 2026-08-15.** Its types are in `Humans.Interfaces`
   with their namespaces preserved, so `using Humans.Domain.Enums;` and friends still resolve;
   only the `<ProjectReference>` is gone. Re-verified by lane 3c: zero
   `<ProjectReference … Humans.Domain.csproj>` and zero `<Using Include="Humans.Domain…" />`
   remain anywhere outside `docs/`.) Add to `Humans.slnx` **and add a
   `<ProjectReference>` to it from `src/Humans.Web/Humans.Web.csproj`**. No
   `Directory.Build.props` — `src/Directory.Build.props` resolves from `src/Sections/`.
   - **The `Humans.Web` reference is the whole of what makes a section exist at runtime, and
     forgetting it is silent in every direction.** `SectionDiscoveryExtensions.SectionAssemblies()`
     walks `DependencyContext`, so a section outside Shell's dependency graph is simply not
     there: `Section.Register` never runs, `SectionControllerFeatureProvider` never sees the
     controllers, every page 404s — and the solution builds, the full suite passes, and each
     reflection-anchored architecture sweep quietly covers one section fewer. Nothing in the
     section's own project or in `Humans.slnx` implies it. The cheap proof is the step 12 render
     test; the cheaper one is `grep '<Section>' src/Humans.Web/Humans.Web.csproj` before you
     build (proven: Guide, which reached a green build and a green 5,400-test suite with the
     section unreachable).
   - **The three `<Using>` items are the *minimum*, not the list.** Sdk.Web's implicit usings
     are nine; a section that names anything outside those three gets a compile error whose
     text does not say "missing using". Development's two controllers gate on the host
     environment and the dev-auth setting, so they needed `Microsoft.AspNetCore.Hosting`
     (`IWebHostEnvironment`), `Microsoft.Extensions.Configuration` (`IConfiguration`),
     `Microsoft.Extensions.DependencyInjection` (`GetRequiredService`) **and**
     `Microsoft.Extensions.Hosting` — the last of which is the one to know about, because
     without it the only `IsProduction()`/`IsDevelopment()` extension in scope is the obsolete
     `IHostingEnvironment` overload and the error is `CS1929: 'IWebHostEnvironment' does not
     contain a definition for 'IsProduction'`, which reads as a broken type. A global `<Using>`
     also reaches the generated Razor code, so an `@inject` in a moved view resolves from the
     `.csproj` rather than needing a `_ViewImports` line (proven: Development). **When the
     view is the *only* thing that needs it, put it in `_ViewImports` instead** — Search's one
     gap was `@inject IConfiguration Configuration` for the `Features:Events` flag, and the
     `<Using>` group's stated job is keeping *moved `.cs` files* byte-identical, which a
     view-only need is not. The error is the same either way and names the type, not the
     mechanism: `CS0246: The type or namespace name 'IConfiguration' could not be found`,
     reported at the `@inject` line of the `.cshtml` (proven: Search).
   - **"The section's own NuGet packages" excludes anything in the ASP.NET Core shared
     framework.** Central Package Management fails the build with `NU1010` for a
     `PackageReference` that has no `PackageVersion`, and the ASP.NET packages deliberately have
     none — `Microsoft.AspNetCore.DataProtection` and friends arrive through the framework
     reference Sdk.Razor already adds (proven: Surveys, whose token provider takes
     `IDataProtectionProvider`). Add EF Core, NodaTime and Npgsql; never an `AspNetCore` one.
2. [ ] Move the vertical, folders as layers: `Contracts/ Interfaces/ Domain/ Data/ Services/
   Controllers/ Models/ Views/ Authorization/ Filters/ Docs/ Properties/ wwwroot/`
   + `Section.cs` (and, per step 3b, `<Section>Resource.cs` + its `.resx` at the project root).
   Sibling `Section*.cs` registration files — `SectionAdminNav.cs`, `SectionChrome.cs`,
   `SectionJobs.cs`, `SectionPolicies.cs` — are accepted at the root today (29 sections carry
   at least one), **but they are not the target shape**: the intent is to fold them back into
   a single `Section.cs` implementing the several interfaces. Prefer that in new work; do not
   add a new sibling kind. **`Contracts/` is the public folder and `Interfaces/` is the internal one** —
   that pair is the whole accessibility convention, and HUM0034 enforces it. Ship only the
   folders the section has.
   **A controller that names its views by absolute path pins the folder layout** — an RCL's
   compiled view paths are project-relative, so `View("~/Views/MailerLite/Admin/Index.cshtml")`
   keeps resolving only if `Views/MailerLite/Admin/` moves verbatim rather than being tidied into
   the `Views/<Controller>/` shape. Renaming it compiles, then 500s on every page and reads as
   a routing bug (proven: MailerLite). Migrations
   land at `Data/Migrations/` — their `namespace` line changes to the section's, which is the one
   sanctioned edit to a migration file (spec §7); say so in the PR. **Everything the section needs
   comes with it — no exceptions.**
3. [ ] **Write `Views/_ViewImports.cshtml` in the same commit as the views.** Start from the
   shipped Store example (spec §2) but derive the `@using` list from the section's own folders.
   Omitting a line — or one `@addTagHelper` — ships broken HTML with a green build.
3b. [ ] **First ask whether the section has any keys at all.** A section whose views carry no
   `Localizer[…]` call and no `<Section>_*` key in `SharedResource` ships **no resource set
   and no `<Section>Resource`** — `SectionResourceTypes()` simply returns one fewer
   marker and the boot diagnostic is happy (proven both ways: Finance and Gate ship none; the
   `GateLogin_*` keys that look like Gate's belong to Shell's `/Account/GateLogin` page and stay).
   Assert it structurally instead: *no* type in the section may take `IStringLocalizer<T>` for
   any `T` (`GateArchitectureTests.SectionTypesTakeNoStringLocalizer`), so the day someone adds
   copy the build tells them to carve a resource set first. Skip the rest of this step.
   Otherwise: carve the section's `.resx` — the `<Section>_*` and `Enum_<Section>*` keys move out of
   `Humans.UI`'s set into `<Section>Resource.{resx,es,ca,de,fr,it}` at the project root beside a
   `<Section>Resource.cs` in the section's namespace (root, not a `Resources/` folder — folder and
   namespace must agree, PR peterdrier/Humans#1365). The `.cs` and `.resx` must sit in the same
   folder, and the `.cs` namespace determines the manifest prefix (spec §3) — get it wrong and
   every string in the set degrades to its key at runtime. The boot diagnostic needs no
   per-section edit, **but only if `<Section>Resource` is `public`** — discovery reads
   `GetExportedTypes()` and skips an `internal` marker in silence.
   - **A key you *write* is prefixed with the section name; a key you *move* keeps its name.**
     New keys are `<Section>_…` — `Users_`, `Tickets_`, `CityPlanning_`
     (`memory/code/resource-key-prefix-matches-section.md`). A carve is a move, not a rename:
     renaming in flight touches six language files and every call site, and a missed one renders
     raw with no error. Carry the old prefixes over and let the backlog show up in
     `/section-doctor`'s conformance thread (`resource-key-prefix`), which reports it per section
     and never backfills as a side effect.
   - **Carve the `.resx` block-aware, not line-by-line.** `SharedResource.resx` writes each entry
     on one line; the five translations do **not** — theirs are three lines
     (`<data …>` / `<value>…</value>` / `</data>`). A line-based filter that matches the opening
     tag takes the opening line and leaves the orphaned `<value>`/`</data>` behind, producing five
     invalid `.resx` files. That one fails loudly (`MSB3103: Invalid Resx file`) rather than
     silently, but it costs a full build cycle; consume to the closing `</data>` (proven: Feedback).
     **Leave the section-banner XML comments where they are and delete the orphans by hand**:
     `SharedResource.resx` groups its entries under `<!-- ==== / Name / ==== -->` banners, and
     attaching a banner run to the next `<data>` block moves it with a key whose neighbours may
     have stayed. Shifts' carve emptied three banners and split none; the tidy-up is one edit
     (proven: Shifts).
     **Derive the prefix list from the resx, not from the handoff.** Shifts' recon named six
     prefixes totalling 247 keys; `VolTrack_` — 94 more, every one rendered by a controller and
     four views that moved in with the section — was in none of them, so a third of the carve
     was invisible until `grep -o 'name="[^"]*"' … | sed -E 's/^(prefix1|…).*/\1/' | uniq -c`
     was run against the file itself (proven: Shifts).
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
     **The same move is the answer when a Base view names a section *type* rather than a
     resource key, and that case has nothing to do with the resx carve.**
     `Views/Account/Login.cshtml` enumerated `DevLoginController.AllPersonas` — a `public
     static` list on the controller, which step 5 turns `internal`. The alternatives are all
     worse: publishing the persona list on a contracts leaf makes dev-login vocabulary a
     cross-section contract, and duplicating the list in Shell forks the thing the route
     depends on. The block moved to the section's `Views/Shared/_DevLoginPanel.cshtml` and
     Shell kept only its `@if (devAuthEnabled)` guard around
     `@await Html.PartialAsync("_DevLoginPanel")`. **Grep Shell's views for the section's type
     names, not just for `Localizer["<Section>_`** — a `.cshtml` that names a moved type is a
     compile error at publish time and, if the view is one Razor runtime compilation reaches
     first, at request time (proven: Development).
   - **…and the fifth: move the *renderer*, not the key and not the markup.** "Carve by
     renderer" (above) leaves a key in Base when the renderer cannot see a section set;
     Issues' fourth direction moves a block of Razor. Email's 70 `Email_*` keys are neither:
     they are every section's transactional-email subject/body text, and
     `Humans.Infrastructure/Services/EmailRenderer.cs` — the file Feedback's rule pointed at
     as immovable Base — turned out to have exactly two consumers, both of which move in
     (`EmailMessageFactory` and the section's own preview page). So the renderer moved, the
     whole key set came with it, and `SharedResource` kept nothing. **Check the renderer's
     consumer list before concluding a key is stuck**; the earlier finding named the file
     correctly and the ownership wrongly. The renderer also has to stop using
     `IStringLocalizerFactory.Create("SharedResource", "Humans.UI")` and take
     `IStringLocalizer<<Section>Resource>` instead — the string-keyed overload silently keeps
     reading the old set (proven: Email).
     **A Razor view can do the same thing, and `_ViewImports` does not reach it.**
     `_ConsentReviewBody.cshtml` builds a per-language JSON blob for its checkbox script with
     `new System.Resources.ResourceManager(typeof(SharedResource))` and three `GetString(key,
     culture)` calls. Rebinding `Localizer` in `_ViewImports` leaves that constructor pointing
     at the old set, so the tabbed-language checkbox text degrades to raw keys in every
     language while the surrounding markup is perfectly localized. Grep the moved `.cshtml`
     for `ResourceManager` as well as the `.cs` (proven: Consent).
   - **…and the sixth: MVC's global `DataAnnotationLocalizerProvider` is a renderer too.**
     `Program.cs`'s `AddDataAnnotationsLocalization` sets
     `options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource))`
     — type-agnostic, so a `[Display(Name = "<Section>_…")]` on a *section's* view model still
     resolves against `SharedResource` and cannot be pointed at a section set. Feedback's
     carve-by-renderer applies: those keys stay behind, and a key moved out of `SharedResource`
     renders as its own name on that one form. **The half that bites is when the same key is
     also rendered from a view** — the section's `_ViewImports` rebinds `Localizer`, so one key
     ends up read by two renderers against two sets and the view call site has to switch to
     `SharedLocalizer` in the same commit. Both failure modes look identical in the HTML, so
     fixing one reads as fixing both. Grep `[Display(Name = "<Section>_` beside
     `Localizer["<Section>_` (proven: Onboarding, `NamesViewModel`'s three labels).
   - **A section can have a resource set and no localized page copy.** Email's two admin views
     carry zero `Localizer[…]` calls, so the step-12 resx assertion has nothing to hang on in
     the section's own HTML. The probe was the template-gallery page (`/Email/EmailPreview`),
     which renders 20 templates in all six cultures in one response — a single GET proving the
     neutral set *and* the satellite assemblies, with no `/Language/SetLanguage` round trip.
     Look for a page that renders the copy before assuming the language-switcher dance
     (proven: Email).
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
     **A section with no keys at all can still need the guard in its bound form rather than
     Gate's "takes no `IStringLocalizer<T>` at all".** Debug ships no resource set — its
     copy is English developer text — and yet `/Debug/Translations` injects
     `IStringLocalizer<SharedResource>` on the action, because the page renders the whole shared
     set *as data*: every key in every culture, as a coverage gallery. Gate's structural
     assertion would fail on it and Surveys' would have nothing to name, so the guard is the
     one-marker form with `SharedResource` as the marker, and it sweeps method parameters as
     well as constructor ones. Ask what the localizer is *for* before choosing between the two
     shapes (proven: Debug).
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
   - **…and the mirror: when carving the *whole* prefix is available, take it and rebind the
     outside call sites.** Governance left four prefixes behind because claiming five keys out
     of nine splits a message set. Consent's `Consent_*` / `ConsentReview_*` are read by Shell's
     onboarding widget (which renders the same consent form) and by Governance's statutes page,
     which reads exactly Governance's case — except nothing gets split: all 36 keys moved, and
     the three outside call sites bind `IStringLocalizer<<Section>Resource>` instead
     (`OnboardingWidgetController`, `Views/OnboardingWidget/Consents.cshtml`, and
     `Humans.Governance` taking a reference to `Humans.<Section>` per Budget's rule). **Ask
     "would carving split a set?" before applying Governance's rule** — if the answer is no,
     the keys go home and the outside callers rebind (proven: Consent).
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
     - **…and a *prefix* can be three owners deep, so run the renderer test per key before
       trusting either rule.** Governance's rule keeps a whole prefix when one key is co-owned;
       Consent's asks whether carving would split a *message set*. Neither answers `Search_*` on
       its own: 17 keys, one prefix, three renderers. Twelve (`Search_Filter*`,
       `Search_Global*`) are the section's page. `Search_Title`/`Search_Placeholder` belong to
       Shell's `/Profile/Search`, and `Search_NoResults`/`Search_MatchedIn` to the shared
       `_HumanSearchResults` partial in `Humans.UI` — Feedback's carve-by-renderer, twice over.
       And `Search_MinChars` is read by the section's page **and** by `/Profile/Search`, which
       is Consent's question with the answer "yes, it would split a set": it stayed, and the
       section binds `SharedLocalizer` for that one call site. The whole-prefix reflex would
       have moved five keys that render elsewhere, or left twelve behind; **group the prefix's
       keys by who renders them first, then apply the rules to each group** (proven: Search).
     - **…and the one renderer that ends the argument is a `Humans.UI` partial.** Search's
       group-by-renderer pass resolves most prefixes; Teams' 193 keys across thirteen prefixes
       came down to five stragglers, and the two that were *forced* are `Teams_Member`
       (`_RoleBadge.cshtml`) and `MyTeams_View` (`_HumanSearchResults.cshtml`). A Shell view can
       inject `IStringLocalizer<<Section>Resource>`; `Humans.UI` cannot reference a section, so a
       key it renders stays in `SharedResource` and the section binds `SharedLocalizer` for it.
       Run the renderer test per key and treat a `Humans.UI` hit as a stop (proven: Teams).
     - **…and the second stop is a renderer this section already references.** Budget's "the
       key goes home and the consumer rebinds" needs `Humans.<Consumer>` → `Humans.<Section>`
       to be acyclic, and by the time a late section moves, several of its *own* project
       references point at other sections — Shifts references `Humans.Teams` (its admin
       controller derives from `HumansTeamControllerBase`) and `Humans.Onboarding` (its browse
       page renders the name-gate copy through `OnboardingResource`). Both of those sections
       render `Shifts_*` keys, and neither can bind `ShiftsResource` without a cycle, so five
       keys were pinned to `SharedResource` exactly as a `Humans.UI` hit pins one. **Check the
       section's own outbound reference list before applying Budget's rule, not just the
       consumer's** — the direction that works for an early mover is a cycle for a late one,
       and the compiler only tells you after you have moved the keys (proven: Shifts).
       A sixth key followed those five for set integrity: `Shifts_AllPhases` names the fourth
       `RotaPeriod` member in the same `switch` as the three pinned ones, and one enum's
       display names must not span two resource sets.
     - **`Localizer.EnumDisplay(value)` is a call site the extract-and-diff pass cannot see.**
       Governance's mechanical check extracts `Localizer["…"]`; an `EnumDisplay` /
       `EnumSelectItems` call resolves `Enum_{Type}_{Value}` at runtime and shows up in neither
       the extraction nor the key diff. When the enum stayed in Base, that call has to be
       switched to `SharedLocalizer.EnumDisplay(...)` by hand or the whole badge column renders
       raw keys (proven: Teams).
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
   - **A configuration type named after the section can still be Base's, and
     `Section.Register` cannot see `IHostEnvironment`.** `EmailSettings` binds `Email:*`, lives
     in `Humans.Infrastructure/Configuration`, and is read by Auth's `MagicLinkUrlBuilder`,
     Profiles' `UnsubscribeTokenProvider`, `SendReConsentReminderJob` and Email's
     own `SmtpHealthCheck` — so its `services.Configure<…>` call stayed in Shell (Governance's
     rule: the section that owns the file is not always the section that owns the line). The
     startup guard beside it — "Production must have SMTP configured" — stayed for a second
     reason: `ISection.Register(IServiceCollection, IConfiguration)` has no `IHostEnvironment`,
     and pushing the check into a service factory would first throw on a real send instead of
     at boot. The section keeps the half that is genuinely its own: which of its two internal
     transports to bind, off `configuration["Email:SmtpHost"]` (proven: Email).
   - **…and when the thing being gated on the environment is one of the section's own
     internal types, there is no half that can stay in Shell — read the environment out of
     the configuration.** Email split the decision because `EmailSettings` is Base's and only
     the *guard* needed the environment. Development cannot: `Program.cs` registered its three
     dev fixture seeders inside `if (!builder.Environment.IsProduction())`, and after the move
     Shell cannot name an `internal` type to register it conditionally. `ISection.Register`
     still has no `IHostEnvironment`, but the configuration it *is* handed carries the
     environment — `WebApplicationBuilder.Configuration` includes host configuration, so
     `configuration[HostDefaults.EnvironmentKey]` is the environment name, including under
     `WebApplicationFactory.UseEnvironment`. **Write the check so it fails closed**: register
     nothing when the name is missing *or* Production, rather than "register unless
     Production". The two failure modes are not symmetric — a key that stops resolving then
     shows up as the dev seeder being unregistered (loud: every integration test signs in
     through `/dev/login/{slug}`) instead of a dev seeder reaching Production (silent). Pin
     both directions in the section's architecture tests; they are four lines over a
     `ConfigurationBuilder().AddInMemoryCollection(...)` (proven: Development).

4b. [ ] **Nothing to declare — step 4's `Section : ISection` is the whole marker.** Discovery,
   controller/view-component routing and the analyzers all key on it, and the section's *name*
   is the assembly name minus `Humans.` (`Humans.Store.Contracts` is still section Store). The
   `[assembly: Section("…")]` this step used to ask for was retired in
   nobodies-collective/Humans#1064. Add `[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]`
   in `Properties/AssemblyInfo.cs` if the section's tests substitute anything; otherwise the
   file need not exist.
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
     - **…and a second exception, which is not a seam at all: the interface is where
       `IApplicationService` lives.** The hard rules define the service layer by that marker
       ("Services must derive from `IApplicationService`"), and a section whose service has no
       cross-section consumer, no decorator and no substituting test still has to carry it
       somewhere. Both places compile — the marker can sit on the `internal sealed` class — but
       moving it there means relocating the interface's XML contract onto the implementation
       and repointing the DI registration, which is a rename that buys nothing (step 5's own
       test) done inside the move commit. Keep the interface, `internal`, in `Services/`.
       Guide's `IGuideContentService` and Cantina's `ICantinaRosterService` are both this
       shape: one member set, one consumer, the section's own controller (proven: Guide,
       Cantina). Contrast Feedback and CityPlanning, where the marker had somewhere else to be
       — the leaf's read interface — and the plain `I<Section>Service` really did go.
   - **A third answer to the enum question: it stays in `Humans.Domain.Enums`.** Budget's
     rule moves the section's own enums onto the leaf; Issues' splits them per enum. Neither
     fits an enum that *other sections persist on their own tables*: `EmailOutboxStatus` is
     Email's vocabulary, and it is also a `HasConversion<string>()` column on Campaigns'
     `campaign_grants` and on Surveys' `survey_invitations`. Moving it onto Email's leaf would
     make two other sections' **domain** depend on Email's contracts, and would drag its
     `Enum_EmailOutboxStatus_*` keys and its `EnumBadgeMap` rows along for nothing. Left in
     Base, with its resx keys and badge rows. The test is *who writes it to a table*, not who
     named it (proven: Email). **Amended by G5 lane 3c, 2026-08-15:** the rule stands, the
     project name in it does not. `src/Humans.Domain` was deleted in lane 3b and
     `EmailOutboxStatus` went to `Humans.Interfaces` with its `Humans.Domain.Enums` **namespace
     preserved**, so `Humans.Campaigns.Contracts` now carries the ordinary `Humans.Interfaces`
     reference every leaf has and its `using Humans.Domain.Enums;` is unchanged. Read "stays in
     Base" wherever this section says "stays in `Humans.Domain`".
   - **…and a fourth answer that was asserted, believed for three lanes, and is wrong:
     "the enum stays in `Humans.Domain.Enums` because a `Humans.UI` partial renders it".**
     The original claim ran: Email's rule keeps an enum in Base when another section
     *persists* it; Teams persists `TeamMemberRole`, `SystemTeamType` and
     `TeamJoinRequestStatus` alone, so that test says "move" — but
     `Humans.UI/Views/Shared/_RoleBadge.cshtml` binds `TeamMemberRole` and "`Humans.UI` cannot
     reference a section leaf at any price". **Re-measured by G5 lane 4b-2k and false.**
     `Humans.UI` references `Humans.Application`, and `Humans.Application` declares direct
     `ProjectReference`s on `Humans.Camps.Contracts`, `Humans.Governance.Contracts`,
     `Humans.Shifts.Contracts` and `Humans.Teams.Contracts`. Base *is* allowed to name a leaf —
     that is the whole reason leaves exist (§15.5b); what it may not name is a section
     **project**. `TeamMemberRole`, `TeamJoinRequestStatus` and `RolePeriod` moved onto
     `Humans.Teams.Contracts` with `_RoleBadge.cshtml` needing one `@using` line and nothing
     else. `SystemTeamType` stayed, for the unrelated reason that it has no single section
     owner (phase 3a's file). **Ask who renders it and then check the renderer's project
     graph — a rendering consumer in `Humans.UI` is a `using` line, not a blocker**
     (proven: Teams, reversed by 4b-2k).
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
       `nameof` survives the move and becomes that section's problem.** The worked example — that
       `nameof(Team)` in Calendar's audit calls compiles because `Team` is still a public
       `Humans.Domain.Entities` type — **has since expired**: Teams went to G5 at 4b-2k and
       `src/Humans.Domain` was deleted at lane 3b. The rule is what matters and is unchanged; read
       the example as history. Grep the moved code for `nameof(` over *every* type
       the section does not own, not just the ones the build complains about, and take them all
       into `AuditEntityTypes` (proven: Calendar).
   - **The keystone analyzer (nobodies-collective/Humans#1013) has landed, so this is a build
     gate, not a convention — and it collapses the move commit and the visibility commit into
     one.** HUM0034 fails the build for any public type in a section assembly
     that is not `Section`, `<Section>Resource`, a generated migration, or under `Contracts/`.
     A move-only commit therefore does not compile, and "renames in a separate commit after the
     move compiles" no longer describes a reachable state for the visibility half. Split what is
     still splittable — the move+internalise commit, then renames, then anything behavioural —
     and say in the PR why the first two are one (proven: Agent, A4b, ~60 files internalised in
     the move commit). Nested `public` members of an already-internal type are flagged too, which
     `internal sealed` at the top level does not cover.
5b. [ ] `Contracts/` holds **everything consumed from outside the section**. May be empty for a
   leaf section; ship the folder with a `README.md` saying why (proven: Store).
   - **A `.Contracts` *assembly* exists only to break a reference cycle.** A `Contracts/` folder
     in `Humans.<Section>` is the default — every extra assembly is build and deploy cost we pay
     dozens of times a day. Sections may reference each other directly when it is acyclic
     (Peter, nobodies-collective/Humans#1064).
   - **The cross-section contract is `I<Section>ServiceRead`; a write contract is the
     exception.** HUM0032 fails a cross-section injection of a write-capable `I*Service` that
     has a read base, so the consumer either takes the read interface or the class carries
     `[CrossSectionWrite("what it writes and why")]`. Prefer neither: when another section needs
     a write, pull in *this* section's view component and let it write its own data.
   - **A horizontal section's read+render layer belongs to the section, and the leaves it
     needs come with it — this bullet used to say the opposite, and cost a lane to reverse.**
     `AuditViewerService` wraps the section's own `IAuditLogService` with actor, subject and
     team display names, so it names `IUserServiceRead`, `ITeamServiceRead` and
     `ITeamResourceService`. That was read as "a horizontal referencing three verticals,
     forbidden at any size", and the type was parked in `Humans.Application` with its
     `AuditEvent` DTO and verb table, registered from Shell, pinned by an assembly-level
     `GetReferencedAssemblies()` test. **Peter reversed it in the Base-floor decision of
     2026-08-14**: a former Base resident that names another section's read interface moves
     to its section, and Base gets no `Humans.Teams.Contracts` reference to keep it. A
     section taking another section's *contracts leaf* is sanctioned at end state; what is
     forbidden is a cycle, and there is none — Teams references `Humans.AuditLog`, AuditLog
     references `Humans.Teams.Contracts`, and the leaf reaches nothing. G5 lane 4b-2h moved
     the interface, the DTO, the verb table and `AuditLogViewComponent` into
     `src/Sections/Humans.AuditLog`, and **retired the pinning test**, whose premise the
     decision had inverted.
     - The widget is the expensive half, not the cheap one: a `<vc:>` component that leaves
       `Humans.UI` needs `@addTagHelper *, Humans.<Section>` in **every** consuming
       assembly's `_ViewImports.cshtml` plus a `ProjectReference`, and a missing line is
       silent — inert literal markup, green build, no log line. Prove each call site with a
       render test that asserts *seeded content*, never merely `NotContain("<vc:")`
       (proven: AuditLog, five consumers).
   - **…and the horizontal rule bites a second time at the *type* level, which is not the same
     search as the reference one.** The bullet above asks what a horizontal's services
     *inject*. This asks what its leaf's signatures *name*:
     `IRoleAssignmentService.AssignRoleAsync`/`EndRoleAsync` returned
     `Humans.Onboarding.Contracts.OnboardingResult` — a `(bool Success, string? ErrorKey)` record
     a *vertical* section owns — so the leaf could not compile without the forbidden reference.
     Moving the record down to `Humans.Interfaces` edits another section's leaf to fix yours and
     leaves a section-named type in Base; taking the reference is what the hard rules forbid. The
     section owns its own boundary record and maps at the edge (Finance's `HoldedLedgerLineDto`
     rule, applied to a primitive). Cheap when the callers only read the members — Auth's two
     production call sites read `.Success`/`.ErrorKey` and needed a type name change and nothing
     else. **Grep a horizontal's public signatures for `Humans.<AnyVertical>.Contracts` types
     before starting**; it fails at the leaf, not in the section, and no `ProjectReference` audit
     finds it (proven: Auth).
   - **Fan-*out* is not fan-in, and an orchestrator section can be the widest consumer in the
     repo with an empty `Contracts/`.** Search injects five sections' service interfaces and its
     recon touches ten already-moved projects, which reads as the knot the preconditions warn
     about. It is the opposite: what a leaf publishes is what points *at* the section, and the
     only thing that did was `SearchController` — which moves in. `ISearchService`, its three
     DTOs and the view model all turned `internal`, and the assembly exports `Section` and
     `<Section>Resource` and nothing else. **Count inbound references before scheduling a
     section, never outbound ones**; the two are unrelated and the scary-looking number is the
     one that costs nothing (proven: Search, the last section of the batch).
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
       from the name (proven: Calendar, whose calendar-event half has no cross-section surface at
       all — both its interfaces are internal under `Services/`, and the only thing under
       `Contracts/` arrived later, from Base).
   - **A Base type that shares the section's name prefix may belong to an entirely different
     concern — read the signatures before adopting it, and say which way it went.**
     `Humans.Application.Interfaces.ICalFeed` held `ICalendarFeedContributor`, `CalendarFeedItem`
     and `IICalFeedService`: a Base-owned fan-out assembling a user's personal iCal feed from
     Shifts and Events. It names nothing the Calendar section owns and Calendar does not implement
     it, so Calendar's own move left it in Base — correctly, on the evidence available then. The
     4a end-state design (2026-08-14) then placed it *in* Calendar anyway, because "who is named in
     the signatures" answers **may this move**, not **where does the concern live**: a feed of a
     user's dated commitments is calendar work no matter whose rows fill it, and Base is not a home
     for an orchestrator once every consumer is a section. Lane 4b-2c moved the three types to
     `Humans.Calendar/Contracts/` and the service, `ICalFeedApiController` and
     `UserCalendarViewComponent` into the section. The lesson survives in a narrower form: a
     recon pass keyed on the string "Calendar" would have pulled it in for the wrong reason, and
     the signature test is what stops that — but the signature test alone does not decide
     ownership (proven: Calendar, twice).
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
   - **A leaf entry can exist because Base *implements* it, not because Base calls it.**
     Every other rule here reads "the consumer is in Base, so the contract goes on the leaf".
     `IImmediateOutboxProcessor` is the mirror: the section's `OutboxEmailService` is the
     consumer, and the implementation — `HangfireImmediateOutboxProcessor`, which enqueues the
     recurring job — has to stay in Base because it names the Base job type. An interface with
     an implementer in Base belongs on the leaf for exactly the same reason as one with a
     caller there. Sort the section's abstractions by *which side the implementation is on*
     before deciding: Email's four connector abstractions split three internal
     (`IEmailRenderer`, `IEmailBodyComposer`, `IEmailTransport`) to one on the leaf
     (proven: Email).
     **The rule does not soften as the implementer count grows — it is the *only* thing
     that decides a fan-out section's leaf.** Gdpr's `IUserDataContributor` has exactly one
     consumer, the section's own orchestrator, and **21 implementers**: eight services still
     in `Humans.Application` and thirteen already-moved sections. Read consumer-first it looks
     internal; read implementer-first it is obviously public, and the whole contract —
     the interface, its `UserDataSlice` return DTO and the `GdprExportSections` constants
     the implementers key their slices by — goes on the leaf together, because splitting
     them would leave the section's own vocabulary in Base. Cost: `Humans.Application` and
     thirteen section projects gain a `ProjectReference` and ~40 files gain a one-line
     `using` swap. Notifications' lesson holds at the limit — **a wide fan-in over a narrow
     interface is cheap; it is the *surface* that costs**, and here the surface is five
     types with no method bodies (proven: Gdpr).
     - **A section whose whole substance *is* the contract has no "leave it in Base"
       option.** The tempting alternative — move only the orchestrator and leave the
       contributor contract behind — is what makes the move zero-risk and is wrong: the
       section would ship a 70-line class whose own DTO and constants live in another
       assembly, which no moved section has done. Ask what is left in the section project
       if the contract stays; if the answer is "not the section", the contract moves.
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
   - **A *view model* belongs on the leaf when a Base view constructs it for the section's own
     partial.** Consent's `_ConsentReviewBody` is rendered from both `/Consent/Review` and
     Shell's onboarding-widget Consents step; the partial moves into the section's
     `Views/Shared/` and Shell keeps finding it by name, but `<partial model="new
     ConsentReviewFormViewModel { … }" />` needs the model type. Step 3b's "move the markup"
     is not available here — the widget's step view binds `ConsentsStepViewModel`, a Shell type
     a section cannot name — and duplicating the model forks the shape the shared partial
     exists to keep. It is genuinely consumed from outside the section, which is what
     `Contracts/` is for; it names no ASP.NET type, so the leaf stays plain
     `Microsoft.NET.Sdk` (proven: Consent).
   - **…and the test has a second half: does the *other section* name anything of yours?**
     "No consumer in Base" is not sufficient for a folder. Onboarding's entire fan-in is Shell
     plus `Humans.Consent`'s controller, which by the rule below reads as "folder, and Consent
     references the section project". It cannot: `Humans.Onboarding` names `ConsentResource`,
     because the widget's Consents step renders Consent's copy through Consent's own
     `_ConsentReviewBody` partial — so a folder would make the two *section projects* reference
     each other. `Humans.Onboarding.Contracts` breaks the cycle
     (`Humans.Consent` → the leaf, `Humans.Onboarding` → `Humans.Consent`). CityPlanning's
     rule is the one-way case of this shape; the two-way case needs a leaf regardless of where
     the consumers live (proven: Onboarding).
   - **When "who returns it" and "who named it" disagree about a leaf type, ask where the other
     returners came from.** `OnboardingResult` is `(bool Success, string? ErrorKey)` — primitives
     only, so the connector signature test says Base — and ten `Humans.Application` members
     return it against four in the section, which reads as Base vocabulary wearing the section's
     name (`EmailSettings`' case). It is not: three of the four Base owners are the section's own
     *siblings* from an earlier narrowing that left them in Base, so the fan-out is an artefact
     of that split. It went on the leaf, for one `ProjectReference` from `Humans.Application` and
     `Humans.Infrastructure` each plus ten one-line `using` swaps — Gdpr's trade at a tenth of
     the size (proven: Onboarding).
   - **A section can ship both a `.Contracts` leaf and a `Contracts/` folder, and the split
     is what each half may name.** Five sections ship the folder (Store, Containers, Feedback,
     Calendar, Scanner) and a dozen ship the leaf; Tickets is the first with both, because its
     `TicketStubViewComponent` is public surface that is ASP.NET plumbing while its six Base
     consumers want only the section's vocabulary. **The leaf carries what Base consumers need;
     the folder carries public surface that is ASP.NET plumbing.** Nothing forbids the pair — say
     so in both csproj comments so the next reader does not have to re-derive it (proven:
     Tickets).
     - **This rule used to justify itself with "the leaf is framework-free / must stay
       `Microsoft.NET.Sdk`". That is false and always was — re-measured by G5 lane 3c,
       2026-08-15.** `Humans.Interfaces` has carried
       `<FrameworkReference Include="Microsoft.AspNetCore.App" />` since `ISection` landed, and
       `FrameworkReference` flows transitively through `ProjectReference`. Measured with
       `dotnet msbuild <leaf>.csproj -t:ResolvePackageAssets -getItem:FrameworkReference`: all 26
       leaves that reference Base resolve `Microsoft.AspNetCore.App` with
       `IsTransitiveFrameworkReference=true`. A leaf **can** name `IActionResult` or
       `ViewComponent`. Only `Humans.Events.Contracts`, `Humans.Onboarding.Contracts` and
       `Humans.Users.Contracts` — the three with no path to Base — resolve `Microsoft.NETCore.App`
       alone. **Do not use framework-freeness as a placement oracle.** Decide the split on what
       cross-section consumers should have to see; if you want a leaf's ASP.NET-freeness
       enforced, that's a universal analyzer keyed off convention, not a per-section test — a
       test asserting one assembly lacks a reference is forbidden by
       [`no-tests-for-absences`](../../memory/architecture/no-tests-for-absences.md). Every
       placement this oracle previously justified was re-checked in 3c and stands on other
       grounds; none moved.
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
     - **…and check what the connector uses *from Base*, not only what it exposes.** The
       instinct after deciding the connector moves in is that the section must therefore
       reference `Humans.Infrastructure`, because that is where the file was.
       `MailerLiteClient` is a `Humans.Infrastructure/Services` file whose every dependency is
       either the ASP.NET shared framework (`IHttpClientFactory`) or `Humans.Application`
       (`Extensions`, `Threading`), so `Humans.MailerLite` took **no `Humans.Infrastructure`
       reference at all** — Scanner's table-less shape, reached by a section that had code in
       Base's service folder on the way in (proven: MailerLite).
     - **…and a third disposition, when the connector is *replaceable*: give it its own
       section, and give the port to the section that owns the concern.** Agent's rule takes
       the connector into the section; Guide's leaves it in Base. Neither fits a vendor that is
       expected to change: `ITicketVendorService` is shaped in the application's terms (no
       vendor type in its signatures), the TicketTailor client is one implementation of it, and
       a 2027 vendor swap should be one project deleted and one added. The port went to
       `src/Sections/Humans.Tickets/Contracts/` and the client and its dev stub to
       `src/Sections/Humans.TicketTailor` — plain `Microsoft.NET.Sdk`, no tables, an empty
       `Contracts/`, and a direct `ProjectReference` on `Humans.Tickets` to name the port —
       and the owning section (`Humans.Tickets`) became the application's only door to
       ticketing. **The port belongs to the owning section, not to Base and not to the
       adapter's leaf:** it sat in Base until G5 lane 4b-2g (nobodies-collective/Humans#866)
       purely because Tickets was not yet a project, and it stays off the
       `Humans.Tickets.Contracts` leaf because no Base consumer names it and the leaf must keep
       vendor vocabulary away from other sections. **An adapter section referencing the owning
       section's project is the sanctioned shape** (Peter, 2026-08-14) — the price is that the
       adapter picks up the owner's transitive references, which for TicketTailor meant losing
       its "no `Humans.Infrastructure` reference" property. Nothing in it names them.
       **The load-bearing half is the invariant, not the folder:** an architecture test
       asserts that only the owning section injects the port, its own health check included, because
       what actually breaks a vendor swap is a second section reaching past the door
       (Campaigns was doing exactly that for discount codes, and this lane closed it). Where
       a consumer needs a port operation, the section publishes it on its own leaf in its own
       vocabulary and maps at the edge — Finance's `CreditorLedgerLine` shape, applied to a
       write (proven: Tickets/TicketTailor).
     **The same test can keep a connector in Base when the connector carries the section's own
     name.** `IGuideContentSource` / `GitHubGuideContentSource` / `GuideSettings` read as the
     whole point of the Guide section — they are the thing that fetches `docs/guide/*.md`. They
     are not Guide's: the signatures name only `string`, and the consumers are
     elsewhere (the Agent section's `AgentSectionDocReader` / `AgentFeatureSpecReader` /
     `CommunityFaqReader` over `docs/sections`, the section `Docs/features/` spec corpus and `docs/community-kb`,
     its `AgentDocsHealthCheck`, and Base's `GitHubCommunityKbContentSource`, which
     *implements* the same interface against a different repo). Taking it in would have forced
     a contracts leaf, made Base and another section consume a section's contracts for a plain
     string fetch, and split `GuideSettings` from the type binding it. Left in
     `Humans.Application/Interfaces` + `Humans.Infrastructure/Services`, with the two
     registrations moving from the section extension into Shell's
     `InfrastructureServiceCollectionExtensions` (Governance's rule: the section that owns the
     *file* is not always the section that owns the *line*). Calendar's name-collision test,
     applied to the section's own vocabulary rather than a neighbour's (proven: Guide).
   - **A Base *enum* in a leaf signature is not Budget's case — leave it in Base.** (This rule
     was written as "reference `Humans.Domain` from the leaf". **G5 lane 3b deleted that project**
     and lane 3c re-audited this line: the enum now lives in `Humans.Interfaces` with its
     namespace preserved, so the leaf needs only the `Humans.Interfaces` reference it already
     has. The decision is unchanged; the second `<ProjectReference>` the old wording called for
     no longer exists and must not be re-created.) Budget's rule (step 5) is about enums the
     *section* owns: they cannot follow the
     entities into internal `Domain/`, so they move onto the leaf. `EmailOutboxStatus` is the
     opposite — the Email section's enum, Base vocabulary that Campaigns re-exports in
     `CampaignGrantSummary` and `UpdateGrantEmailStatusAsync`. Moving it onto the leaf would
     steal another section's type; retyping the member to `string` is behavioural. Post-3b the
     leaf takes `Humans.Interfaces` alone, exactly like every other leaf — the apparent break with
     the pattern this rule used to warn about has resolved itself (proven: Campaigns).
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
   - **…and the seeder can be in *another section*, in which case the leaf takes the verbs and
     the class implements them explicitly.** Budget's rule carves one method when a *Shell* dev
     seeder drives the section's write surface. Three seeders drive Teams' — two in
     `Humans.Development`, one in `Humans.Budget` — and they build multi-section fixtures, so
     taking the seeding in would steal three sections' fixtures. `ITeamSeeding` on the leaf
     carries `CreateTeamAsync` / `UpdateTeamAsync` / `AddSeededMemberAsync` returning DTOs;
     each collides with the section's entity-returning member of the same name and parameters,
     **differing only in return type, which C# permits only through explicit interface
     implementation**. That is what keeps the seeder call sites unchanged and the names honest —
     the alternative is `CreateTeamForSeedAsync` (proven: Teams).
   - **A Shell controller *base class* goes into the section under `Contracts/`, not down to
     `Humans.UI`.** `HumansTeamControllerBase` resolves a team by slug and authorizes, so it
     names the section's whole vocabulary — and Shell's `ShiftAdminController` derives from it
     because a rota's "department" is a team row. `Humans.UI` would make Base name a section
     leaf; staying in Shell would make the section's own controller name a `Humans.Web` type.
     `Contracts/` takes it, `public abstract`: HUM0034's carve-out is the folder, the section
     project is `Sdk.Razor`, and — the binding constraint — the base class derives from
     `HumansControllerBase`, which lives in `Humans.UI`, a project no leaf may reference.
     Everything the base *body* touches — here the section's `IAuthorizationRequirement` — stays
     internal (proven: Teams). (This rule used to end "its protected members return
     `IActionResult`, which the framework-free leaf could not have named". G5 lane 3c measured
     that false — the leaf resolves `Microsoft.AspNetCore.App` transitively. The placement is
     unchanged; only the reason was wrong.)
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
     `Humans.Web/Views/_ViewImports.cshtml` already carries `@addTagHelper *, Humans.Interfaces`
     (the directive names an ASSEMBLY; Base's namespaces are still `Humans.UI.*`), so
     every existing `<vc:…>` in Shell resolves the moved component with no edit. The whole
     change is `git mv` of the component class and its
     `Views/Shared/Components/<Name>/Default.cshtml` plus the namespace line — check only that
     the `Default.cshtml` uses no `@using` that `Humans.UI/Views/_ViewImports.cshtml` lacks
     (proven: Scanner moved `TicketStubViewComponent`, which four Shell views also render, and
     touched none of them).
     **…and that parking is temporary: a component whose *model* belongs to a not-yet-moved
     section comes back out at that section's own lane.** `TicketStubViewComponent` looks
     section-neutral — no constructor, no registry, one `Invoke(TicketStubInfo)` — but
     `TicketStubInfo`'s factories name Tickets DTOs and it carries a Tickets enum, so at
     Tickets' G5 the model could not stay in Base and the component followed it out of
     `Humans.UI`. Blast radius was again zero, in the other direction: the five `<vc:>` call
     sites (Scanner's ticket card, three Shell partials, the widget gallery) were untouched,
     because the component went **`public` under the section's `Contracts/` folder** and
     Scanner + Shell each gained one `@addTagHelper *, Humans.<Section>` line. Read Gate's
     rule as "park it in `Humans.UI` until the owning section moves", not as a final home
     (proven: Tickets, correcting Scanner finding 22).
   - **HUM0034 in one sentence: a section's types are `internal` by default, except types
     the framework requires to be `public` in order to function.** Settled by Peter
     2026-08-14 after an internal `ProfileCardViewComponent` silently emptied the Profile
     page. **The membership test is whether making the type `internal` fails loudly or
     silently renders nothing** — silent means it belongs in the exception. Razor/MVC
     discovery that runs at *compile time* filters on public accessibility and skips what
     it cannot see, with no error, warning or diagnostic; runtime resolution throws, so it
     does not qualify. **Current membership: view components and tag helpers — the whole
     set.** Controllers look like a candidate and are not (`SectionControllerFeatureProvider`
     routes internal ones, and a missing controller 404s loudly). Both are carved out
     structurally, so `public` on them needs no `Contracts/` folder and no argument.
     A view component is rendering surface — being invoked from views in
     other assemblies is its whole purpose — and Razor generates a `<vc:…>` tag helper only
     for a *public* component, so `internal` is not a smaller surface, it is a broken one.
     `SectionViewComponentFeatureProvider` stays for the one case that cannot comply: a
     public constructor cannot take an internal parameter type (CS0051), so
     `NotificationBellViewComponent(INotificationInboxService)` is internal until that
     service has a `Contracts/` interface. **That is a dependency defect to fix, not a
     choice** — and while it holds, the component must be invoked by name, never with
     `<vc:…>`. HUM0034's carve-out is not the contracts *leaf* — read
     `src/Humans.Analyzers/Internal/Rules/PublicSurfaceRule.cs`, `IsUnderContracts`: it matches a
     namespace segment **or file-path segment** named `Contracts`, so a `Contracts/` folder
     inside the section project qualifies, and the section project is `Sdk.Razor` with the
     ASP.NET framework reference, so it can host an MVC `ViewComponent`. When the component
     is public, the default MVC provider discovers it, the `<vc:>` tag helper is generated
     (it reads *public* view components at compile time), and every call site is unchanged —
     callers just add `@addTagHelper *, Humans.<Section>`. Notifications' bell genuinely
     cannot take that path: `NotificationBellViewComponent(INotificationInboxService)`
     injects an internal service, and a public constructor cannot take an internal parameter
     type (CS0051). **Check the constructor before reaching for the feature provider**; the
     bell's precedent is a consequence of *its* dependencies, not a rule about components.
     Failure modes are asymmetric either way: an unresolvable `Component.InvokeAsync`
     **throws**, so one render test catches it, while a `<vc:…>` whose component is not
     visible renders as **inert literal markup** — green build, 200 response, element
     silently dropped — which is what the `NotContain("<vc:")` assertion is for
     (proven: Tickets).
     **Get this right before the Users lane.** `HumanViewComponent` (public in `Humans.UI`,
     **116** `<vc:human>` call sites) and `HumanSearchViewComponent` (13) belong to
     Users/Profiles and are parked in `Humans.UI` under the rule above. Read "public lives
     only on the leaf" and that lane converts 129 call sites to runtime-resolved strings on
     the codebase's most-used component; read HUM0034 correctly and both go `public` under
     `Humans.Users/Contracts/` — `IUserServiceRead` belongs on a leaf anyway and
     `IUrlHelperFactory` is framework, so neither constructor blocks it — and nothing changes
     at the call sites.
   - **Fourth case, a rider on City Planning's: invoking by name still needs the component's
     *argument types* to be nameable from the section.** Superseded for `ProfileCardViewComponent`
     itself, which moved into `Humans.Users` and stayed `public`, keeping `<vc:profile-card />`
     intact — but the constraint is general and still bites wherever invoke-by-name is genuinely
     forced. When it was still expected to stay in Shell,
     `<vc:profile-card view-mode="@ProfileCardViewMode.Admin" />` would have become
     `@await Component.InvokeAsync("ProfileCard", new { userId, viewMode })` — except the enum
     was declared beside the component in `Humans.Web.ViewComponents` and the section cannot
     name it, so the invocation does not compile. The enum moved to `Humans.UI/ViewComponents/`
     (Self/Public/Admin carries no section vocabulary, the same test as the filter base),
     Shell's `Views/_ViewImports.cshtml` gained `@using Humans.UI.ViewComponents`, and Shell's
     own `<vc:profile-card>` call sites were untouched. Check the component's parameter types
     before choosing invoke-by-name (proven: Governance).
   - **A section that renders *another section's* presentation layer gets a new Shell view
     component, invoked by name.** The three earlier answers are about a component that already
     exists; this is the case where one has to be written. The onboarding widget's shift step
     renders Shifts' rota tables — `ShiftBrowseViewModel`, `RotaShiftGroup`, `ShiftBrowseMapper`
     (`internal` to `Humans.Web`) and two `Views/Shared/` partials, ~400 lines of a section that
     has not moved. Pushing them to `Humans.UI` is the registry inversion at scale and would be
     undone at that section's own G5; taking them in steals its presentation; leaving the whole
     view in Shell splits the moving section's page. So the mapping and the markup became a new
     `Humans.Web` view component and the section's view calls
     `@await Component.InvokeAsync("…", new { … })`. Governance's rider is the constraint that
     shapes it: **every parameter must be nameable from a section**, so the component takes Base
     types only (written as "`Humans.Domain` / `Humans.Application`" before lane 3b deleted the
     former and lane 3a filled `Humans.Interfaces`) and the controller passes what it already
     fetched — otherwise the component re-queries and the move quietly doubles a page's reads
     (proven: Onboarding).
   - **A SignalR hub is `internal`, and the section maps it itself.** The section's
     `SectionEndpoints : ISectionEndpoints` calls `endpoints.MapHub<TheHub>("/hubs/…")` from
     inside its own assembly, so Shell never names the concrete type and the hub needs no public
     surface — `CityPlanningHub` lives at `Humans.CityPlanning/Services/CityPlanningHub.cs`
     (nobodies-collective/Humans#1075). The section's own `IHubContext<TheHub>` injection is
     inside that assembly too, so it sees the internal type (proven: CityPlanning).
   - **The third case: the component belongs to the section, and moving it in needs a feature
     provider Shell did not have.** Gate's rule moves a section-neutral component *down* to
     `Humans.UI`; City Planning's leaves a registry-reading one in Shell and invokes it by
     name. Notifications' bell is neither — it renders the section's own unread counts and two
     of its resource keys, so leaving it in Shell would split a 38-key set. Taking it in costs
     two things. First, MVC's `ViewComponentConventions.IsComponent` requires `IsPublic`, so an
     `internal` component is silently never discovered — exactly the hazard
     `SectionControllerFeatureProvider` exists for on the controller side. The counterpart is
     `Humans.Web/Hosting/SectionViewComponentFeatureProvider`: a second
     `IApplicationFeatureProvider<ViewComponentFeature>` pass (the base one is not virtual and
     `ViewComponentConventions` is internal to MVC) that adds non-public components from
     discovered section assemblies. Write it once; every later section with a
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
     **…and the same move can be forced by a *partial* rather than a type, in which case take
     the model, the mapper and the `.cshtml` together.** A section view calling
     `Html.PartialAsync("_X")` resolves the partial by name across application parts, so a
     Shell-resident `Views/Shared/_X.cshtml` would in principle keep working — but its
     `@model` type would not be nameable from the section, and neither would whatever projects
     onto it. `_HumanSearchResults.cshtml` is the person-search result card four Shell pages
     and `/Search` all bind; it went to `Humans.UI/Views/Shared/` with
     `HumanSearchResultViewModel` (out of `Humans.Web/Models/TeamViewModels.cs`) and
     `SearchResultMappingExtensions.ToHumanSearchViewModel`, beside the `OrderByRelevance`
     Gate had already pushed down. Splitting the three would have left a `Humans.UI` partial
     binding a `Humans.Web` model. Blast radius: two `using Humans.UI.Models;` lines and the
     partial's own `@model` line, because Shell's `Views/_ViewImports.cshtml` already has the
     `@using` (proven: Search).
     **Fourth sighting, and it says `Humans.UI` is the rule's *example*, not its depth.**
     `Humans.Interfaces/Logging/InMemoryLogSink` is the Serilog ring buffer `/Debug/Logs`
     renders; `Program.cs`'s logger configuration writes to it and Shell's `LogApiController`
     reads it, so it cannot come into the section and the section cannot name it where it is.
     It carries no section vocabulary — which is the test — but it is also not presentation, so
     `Humans.UI` would be the wrong shelf: it went to `Humans.Infrastructure/Logging`, beside
     the two Serilog enrichers already there, and the section takes the `Serilog` package
     reference it now names directly. Pick the layer from what the type *is*, then apply the
     no-section-vocabulary test (proven: Debug).
     **…and the split that decides between moving a helper in and pushing it down is the
     second consumer's *subject*, not its location.** Debug's two reflection-built gallery
     builders sat in the same folder and went opposite ways.
     `FormatGalleryModelBuilder` reflects over `DateFormattingExtensions` for
     `/Debug/FormatGallery` and had one other caller, its own unit test — it moved in,
     `internal`, and the test moved to `tests/Humans.<Section>.Tests`.
     `TranslationsGalleryModelBuilder` enumerates `IStringLocalizer<SharedResource>` through
     `CultureCatalog`, and its second caller is `SharedResourceParityTests`, whose subject is
     **`SharedResource`** — a `Humans.UI` concern the section merely displays. Taking it in
     would have internalised it and stranded a Base resx gate in a section test project, so it
     went down to `Humans.UI/Models` with the view model it builds. "Move the test with the
     helper" is right only when the test is about the helper (proven: Debug).
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
   - **When the offender is the section's actual service, the answer is the allowlist, not
     `Stores/`.** `GuideContentService` is `IGuideContentService`, the type the controller
     injects; relocating it to dodge the sweep's namespace predicate would be the dodge step 6a
     says `Stores/` is not. Guide has no tables to decorate and guide HTML is not an entity
     read, so §15's other two options do not exist either — it went on the allowlist with that
     rationale, beside Finance's and Feedback's. Note what surfaced it: the code was unchanged
     and had sat in `Humans.Infrastructure`, which the sweep covers neither before nor after;
     it became visible only because the sweep scans **section assemblies**. Email's "ask whether
     the sweep is keyed on a Base path" has a mirror — *a move can put code into a sweep as
     easily as out of one* (proven: Guide).

6b. [ ] **A recurring Hangfire job's *registration* stays in Shell; the job *type* moves with its
   section.** This step said the opposite until G5 lane 5b-1 re-measured both halves of the old
   claim and found both false:
   - *"Hangfire pins a job to its declaring assembly."* It does not. `UseHumansRecurringJobs`
     registers every job with `RecurringJob.AddOrUpdate<T>(id, …)`; the **id** is the stable key
     and `AddOrUpdate` rewrites the stored type string at every startup, so a job that changes
     assembly is re-pointed at boot. The only exposure is an instance in flight during the swap:
     it fails visibly into Hangfire's Failed list and re-runs on the next tick. Peter's ruling:
     accepted, jobs are expected to be resilient that way. No shim, no queue drain, no
     maintenance window, and **no `retired`-array entry** (that list is "stop registering this
     id", and no id changes when a type moves).
   - *"A section-owned job would have to be `public` — the thing step 5 prevents."* Being public
     is right here and step 5 already sanctions it. Shell names the concrete type at both the DI
     registration and the `AddOrUpdate<T>` line, so the job **is** deliberate Shell-facing
     surface: put the file under `src/Sections/Humans.<Section>/Contracts/` with namespace
     `Humans.<Section>.Contracts`, which is HUM0034's `Contracts/` carve-out, exactly as
     `HumansTeamControllerBase` does. `internal` is not an option — Shell has no
     `InternalsVisibleTo` from any section — and it would fail the build loudly, not silently.

   What genuinely stays in Shell is only the two registration lines, because there is still no
   `ISection`-style discovery seam for jobs; Shell references every section, so naming a
   section's concrete type there costs nothing. The eventual seam — `ISectionRecurringJobs`
   called after `WebApplication` is built — is not a G5 blocker.

   **A job in Base is *not* a "consumer in Base" for step 5b once it has moved.** Nine csproj
   comments justified a `.Contracts` **leaf project** on the strength of a job that could have
   moved all along; lane 5b-1 corrected Email's, Notifications', Issues' and Campaigns'. Check
   whether the job is the *only* out-of-section consumer before you cite it: if it is, the
   section wants a `Contracts/` **folder**, not a leaf project.

   **Health checks moved the same way.** A section registers its own through
   `SectionHealthChecks : ISectionHealthChecks`, so Shell names no `IHealthCheck` by concrete type
   and a check stays `internal` to its section — `AgentDocsHealthCheck` and `AnthropicHealthCheck`
   live in `Humans.Agent/Health/` (nobodies-collective/Humans#1075). The one-property
   `IAgentAvailability` on Agent's leaf is what the previous Shell-owned arrangement cost.
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
   - **Fourth sighting, and the one where the carve is not optional.** Notifications' and
     Agent's jobs *should* have been carved; `ProcessEmailOutboxJob` had to be. It injected
     `IEmailOutboxRepository`, `IEmailTransport` and the `EmailOutboxMessage` entity and ran
     the whole drain — pause check, batch pick-up, per-message send, retry backoff,
     campaign-grant mirror — from Base. All three of those turn internal at step 5, so there
     is no version of the move where the job compiles unchanged. The carve is
     `IEmailOutboxProcessor.ProcessQueuedAsync(ct)` returning `Task`, with the per-message log
     lines and the pending-count meter moving into the section beside the repository; what is
     left in Base is one call plus `RecordJobRun`. `IHumansMetrics` and `IMeters` are
     `Humans.Application` interfaces, so the section keeps the metric calls verbatim. Its job
     test became `EmailOutboxProcessorTests` in the section's own project. **Read the job body
     before writing the contract: if it names the entity or the repository, the contract is
     "do the thing", never "give me the rows"** (proven: Email).

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
7b. [ ] The section's **invariants doc and its own design specs move into `Docs/`; its feature
   specs move into `Docs/features/`.** `AgentFeatureSpecReader` derives the servable spec set
   from the repository structure — every `src/Sections/*/Docs/features/*.md`, plus
   `docs/features/global/` — so a spec is served from wherever its section keeps it, with
   nothing to register per file. The folder is the whole rule: the invariants doc, the generated
   companions (`authorization.md`, `data-access.md`, `health.md`) and the dated `20*.md` records
   stay directly in `Docs/` and are excluded by sitting outside `features/`. Only genuinely
   cross-section specs belong in `docs/features/global/`.
   Also:
   disambiguate filenames that collide case-insensitively. Fix inbound links (`docs/README.md`,
   any `memory/` atom citing them, the
   `freshness-catalog.yml` globs if the section has an entry) and **rewrite the moved doc's own
   `freshness:triggers` block to `src/Sections/Humans.<Section>/**`** — the old scattered paths
   stop existing at the move and the doc silently stops being swept. Point-in-time plans and
   audits stay in `docs/`. Anything the app *serves or fetches* from `docs/` at runtime stays,
   and re-check `AgentSectionDocReader`'s fallback covers the section.

   **A section born directly in `src/Sections/` has no doc to move — author its `Docs/<Section>.md`
   with both freshness markers from the start**, `freshness:triggers` set to
   `src/Sections/Humans.<Section>/**` plus a one-line `freshness:flag-on-change`. This step only ever
   described *moving* an existing doc's triggers, so brand-new sections fell straight through it:
   `Humans.Holded` and `Humans.Tour` both shipped unmarked, one sweep apart, and each sat invisible
   until a manual scan found it. An unmarked doc reads as *clean*, never as *unchecked*.
   **A docs path is an API until you have proved otherwise** (spec §7a).
   - **…and the probe you have to find is not always in `Humans.Agent`.** The
     invariants doc may move because `AgentSectionDocReader` falls back to
     `src/Sections/Humans.{key}/Docs/{key}.md`. `Humans.Agent/Health/AgentDocsHealthCheck`
     does not: it fetches `docs/sections/{ProbeSection}.md` through `IGuideContentSource`
     directly — deliberately, so a cached reader cannot keep reporting Healthy through an
     outage — with the section key as a literal, and the section it happened to name was
     Shifts. Moving the doc turns the health check Degraded on every deployed instance,
     with a green build and a green suite. Run
     `git ls-files | xargs grep -ln 'docs/sections/<Section>.md'` and read every hit whose
     filename says nothing about docs; repoint the probe to a whitelisted section whose doc
     is still in `docs/sections/` (proven: Shifts, repointed to Camps).
   - **`docs/guide/**` stays put, at Guide's own G5 and after.** The template used to say
     "until Guide's own G5", which read as a scheduled move; it is not one.
     `GitHubGuideContentSource` fetches `{GuideSettings.FolderPath}/{stem}.md` from
     `nobodies-collective/Humans@main` **over the network at request time**, so the folder is a
     live API against *production's* branch with no fallback and no whitelist. Moving it into `Docs/`
     would 404 all 28 files on every deployed instance from the moment the fork's `main`
     deploys until the change reached production `main`, and would need `FolderPath`'s default
     changed in the same commit. The section's *invariants* doc still moves — that probe has a
     second path (proven: Guide).
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
     - **Add it because a test names one, not because the section has a controller.** Scanner
       needs the framework reference — its section *is* a controller and its one test class
       constructs it. Cantina has the same controller shape and needs none: its four test
       classes cover the roster service, the display-sort assembler and the two CSV writers,
       and the controller is covered by the step 12 render test in `Humans.Integration.Tests`
       instead. Taking the reference "because the section is Sdk.Razor" is cargo cult
       (proven: Cantina).
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
   - **…and when it needs the whole harness *and* two unmoved sections' tests need the
     section's entities, both directions get an `InternalsVisibleTo` and both are temporary.**
     Teams' moved tests make 208 `SeedTeam`, 193 `SeedUser` and 172 `SaveAllAsync` calls — the
     whole harness, so `TeamsTestHarness` is a trimmed *copy* rather than a share or a registry
     (Campaigns' rewrite does not scale, Governance's split has nothing to split). Two entries
     fall out of that: `Humans.Infrastructure` → `Humans.<Section>.Tests`, because the section's
     service tests build the real service over other sections' still-internal Base contexts; and
     `Humans.<Section>` → `Humans.Application.Tests`, because five test files belonging to
     sections that have *not* moved seed this section's rows through `ServiceTestHarness`.
     Rewriting those five inside a move commit changes five unrelated sections' fixtures and is
     redone at their own lanes. State both entries with the condition that removes them
     (proven: Teams).
   - **…and the share-vs-copy-vs-stub question has a tiebreaker that outranks the call count:
     does the harness reach a type another lane is deleting?** Auth's service test used
     `Db.Users`, `SeedUser`, `NewDbBackedUserService()` and the section pair — by call count,
     Teams' "copy the harness". But `NewDbBackedUserService` stubs over an in-memory
     `HumansDbContext`, which §858 peel 15 deletes, so copying would have taken a fresh
     dependency on it. The service reads exactly two members of `IUserServiceRead`, so the
     harness became a `Dictionary<Guid, UserInfo>` with `SeedUser` keeping its signature and the
     seeding call sites unchanged — Campaigns' "rewrite the stub" reached from the other
     direction, and `UserInfo.Create` (Governance's `UserInfoFixtures`) is what makes the
     projection eight lines (proven: Auth).
   - **A DI-graph test names a concrete section service without ever writing `typeof`.**
     `EmailDependencyCycleTests` and `TeamsDependencyCycleTests` each build a real service chain
     with `services.AddScoped<<Section>Service>()` plus a repository substitute and an
     `ILogger<<Section>Service>`; all three stop compiling at the move and none is found by the
     `typeof(<Section>` pre-flight search. Both files already carried the fix as a comment about
     a *different* section ("X is another section; its concrete service is internal and its own
     cycle is pinned by its own test") — substitute the leaf interface and delete the other two
     registrations. **Add `grep -rn 'AddScoped<<Section>Service>' tests/` to the pre-flight
     list** (proven: Auth).
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
     **The fold is not always mechanical: a theory that asserts `Received(n)` loses its fresh
     substitutes.** xunit builds one test-class instance per case, so per-case
     `Substitute.For<…>` fields reset for free; a `foreach` inside one `[Fact]` shares them and
     the received-call counts accumulate — the second iteration fails, or worse, a
     `Received(0)` assertion silently stops meaning anything. `ClearReceivedCalls()` on each
     substitute at the top of the per-case helper restores exactly what was lost, and keeps the
     configured returns (proven: Search, whose `onlyType` theory is five cases of "this section
     was called once, the other four zero times").
   - **…and the opt-out is per *helper*, not per item group.** `CapturingLogger` was
     `Compile`-included in the same `ItemGroup` as `TestDbContextFactory`, inside the condition
     that excludes the table-less test projects — so a section with no EF on its compile path
     that still wants an in-memory `ILogger` (MailerLite's client asserts on its own 429 warning)
     could have neither, or take the EF package to get one. Split the group: `CapturingLogger`
     is unconditional, `TestDbContextFactory` keeps the exclusion list. Governance's "split the
     helper before deciding", applied to an MSBuild item (proven: MailerLite).
   - **A table-less section's test project must opt out of the shared EF fixture, not take an EF
     package to satisfy it.** `tests/Directory.Build.props` `Compile`-includes
     `TestDbContextFactory` (and `CapturingLogger`) into every test project but
     `Humans.Analyzers.Tests`, and `TestDbContextFactory` needs `Microsoft.EntityFrameworkCore`
     to compile. A section with no `DbContext` therefore fails to build until it either takes an
     EF `PackageReference` it never uses or is added to that exclusion — take the exclusion; the
     package would be a lie about what the section is (proven: Scanner, the second project on
     that list; Cantina is the third, so the condition is now three `MSBuildProjectName`
     clauses and the comment beside it needs updating rather than a new one added).
     **The criterion is "no EF on the compile path", not "the section owns no tables" — and the
     comment beside the condition says the second.** Debug owns no tables and needs *no*
     exclusion, because it references `Humans.Infrastructure` for `QueryStatistics` /
     `TrackingMemoryCache` / `InMemoryLogSink` and `Microsoft.EntityFrameworkCore` arrives
     transitively, so `TestDbContextFactory` compiles. Add a clause only after the build
     actually fails; the two facts came apart at the first section that had one without the
     other (proven: Debug). Development is the section where the build *did* fail — table-less
     **and** no `Humans.Infrastructure` reference — so the condition is four
     `MSBuildProjectName` clauses now and the comment beside it states the real criterion.
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
11. [ ] Enforcement: **nothing to do in `reforge.surface-score.json`** — sections are
    assembly-derived, and the file carries only type classifications, no section blocks.
    Delete the section's `*ArchitectureTests.cs` assertions the assembly boundary now subsumes.
    - **A `[Grandfathered]` attribute *moves with its type*; deleting it is the same mistake as
      deleting a baseline row.** The template used to say to delete them (⚠️ UNPROVEN — no
      moved section had any until Consent, which has two: `IConsentCacheInvalidator` and
      `ILegalDocumentCacheInvalidator`, both `[Grandfathered("HUM0028")]`). The violation they
      record — a cache flushed from outside the owning service — is byte-identical after a file
      move (`AccountMergeService` still evicts the consent cache on merge accept), so dropping
      the attribute reports the ratchet as advanced while the code stands still, and turns
      HUM0028 from Warning back into an Error on unchanged code. Campaigns' "retarget, not
      delete" reasoning, applied to an analyzer suppression (proven: Consent).
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
      **Second sighting, and it was a different rule with the identical bug.**
      `NoDestructiveMigrationOpsRule.ScanMigrations` walked
      `src/Humans.Infrastructure/Migrations` alone, so every G5 section's `Data/Migrations`
      had silently left that sweep too — thirteen sections deep, nobody had noticed, because
      the rule reports success by finding nothing. Widened to Base plus each section's
      `Data/Migrations`; it immediately surfaced three already-shipped drops in
      `Humans.Finance`'s own migrations, which were baselined with a comment rather than
      reverted. **Treat "is this sweep keyed on a Base path?" as a question to ask of every
      ratchet rule at every move, not once** (proven: Email).
      **…and the assembly-anchored form has a second, sharper failure: the anchor type is
      *yours*.** Widening a sweep protects the sections that already left it. It does not
      help when the `typeof(X).Assembly` naming "the Base assembly" is a type your own lane
      is moving — then the sweep does not lose one section, it silently relocates
      wholesale onto the section and stops covering Base at all. Four rules were anchored on
      AuditLog's two types: `ApplicationServicesTakeNoDbContextRule` and
      `ApplicationServicesTakeNoMemoryCacheRule` on `typeof(AuditLogService).Assembly`,
      `IRepositoryImplementationsAreSealedRule` and
      `RepositoryImplementationsLiveInInfrastructureRule` on
      `typeof(AuditLogRepository).Assembly` — the last of which would have started asserting
      that repositories live in `Humans.Infrastructure.Repositories` *while scanning the
      section*, i.e. reporting every one of its own repositories as a violation, or (had the
      namespace matched) nothing at all. Re-anchored on types that cannot leave their layer
      (`DontFixAttribute`, `InfrastructureServiceCollectionExtensions`) and the two
      repository sweeps widened to `SectionAssemblies()` besides. **Grep
      `typeof(<AnyTypeYouAreMoving>).Assembly` across `tests/` as its own pre-flight search**
      — it is not the same grep as `typeof(<Section>` for the row-in-a-table case, and it
      fails silently in the opposite direction (proven: AuditLog).

      **Sixth sighting, and it is the fifth one's other half: the interface that moves onto a
      leaf *entirely*.** The `GetInterfaces()` fix below rescues the read-split shape, where
      `I<Section>Service` stays behind and inherits the leaf half — walk its bases and the
      members come back. It does nothing for an interface with no Base-side deriving type:
      `ApplicationInterfaceTypes()` enumerated `Humans.Application.Interfaces.*` plus
      `SectionAssemblies()`, and a contracts leaf is in neither — it declares no
      `Section : ISection`, by design, because it is not an application part. So
      `IAccountProvisioningService`, whose `FindOrCreateUserByEmailAsync` returns a record
      wrapping the `User` entity, simply stopped being scanned the moment Users' leaf was
      carved, and its baseline row read as *fixed*. **Add a third clause enumerating
      `Humans.*.Contracts` from `DependencyContext` — a leaf is not a discovered section,
      so it needs its own discovery.** Widening it surfaced exactly one row across all
      twenty-one existing leaves, which is the usual answer and is why nobody had noticed
      (proven: Users, lane 2 PR A).

      **Fifth sighting, and the keying is neither a path nor an assembly — it is
      `Type.GetMethods()` not following interface inheritance.** A read split that leaves
      `I<Section>Service : I<Section>ServiceRead` moves members onto a leaf whose read interface
      carries no `IApplicationService` marker (the parenthetical here used to read "a
      framework-free leaf cannot reference the marker's home in a way the scan's filter
      recognises" — **false, corrected by G5 lane 3c**: 26 leaves reference the marker's home,
      `Humans.Interfaces`, and could inherit it. The read interface simply does not, by
      convention), and `GetMethods()` on an interface
      returns only *declared* members — so a reflection ratchet that iterates
      `IApplicationService` implementors sees a shorter member list and reports the moved
      violations as **fixed**, on byte-identical code. `ScanApplicationServiceEntityReadReturns`
      lost seven rows that way; the fix is to walk `serviceType.GetInterfaces()` too and
      de-duplicate. **The recursion inside such a rule needs the same question asked of it
      separately**: the same file's `IsApplicationReturnShape` only recursed into
      `Humans.Application.*` types, so a result record that moved onto the leaf still
      wrapping an entity (`UrgentShift(Shift, …)`) stopped the walk one hop short and lost
      two more rows. Widen that to `*.Contracts` assemblies, **not** to whole section
      assemblies — a section-internal DTO wrapping its own section's internal entity
      crosses no boundary, and recursing into them adds ~96 rows across twenty sections
      that the rule was never asserting about (proven: Shifts lane A). This shape fires for
      every read split, not only a G5 move.

      **Fourth sighting, and the keying was an *assembly*, not a path.**
      `ApplicationServicesTakeNoDbContextRule` anchored on `typeof(AuditLogService).Assembly`
      and filtered to the `Humans.Application.Services.` namespace, so every G5 section's
      services had silently left it — while its sibling
      `ApplicationServicesTakeNoMemoryCacheRule`, three files away, had already been widened to
      `SectionAssemblies()` plus a `Humans.*.Services` namespace clause. Copy that clause
      verbatim; widening surfaced nothing, which is the usual answer and is why nobody had
      noticed. **Ask the question of assembly-anchored sweeps too, and check whether a sibling
      rule in the same folder has already been fixed** (proven: Cantina).
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
      **…and the question is not only about `tests/`: `docs/architecture/freshness-catalog.yml`
      holds path-keyed sweeps with exactly the same failure mode.** Its
      `authorization-inventory` entry triggers on `src/Humans.Web/Authorization/**` and
      `src/Humans.Application/Authorization/**` only, so every section that took its
      resource-based handler in-house under step 6 had silently stopped triggering a
      regeneration of the authorization inventory — eight of them before Auth noticed, and a
      doc that is never triggered reports success by never changing. Widened with
      `src/Sections/**/Authorization/**/*.cs`. **Read the catalog's `triggers:` blocks for Base
      paths your section is leaving, not just the doc links** (proven: Auth).
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
      **The same file's other Debug-shaped list survives untouched, and the difference is worth
      knowing before you go looking**: `AllowAnonymousOnAuthorizedControllers_IsExplicitlyAllowlisted`
      keys its allowlist by the `"<Controller>.<Action>"` *string* and sweeps
      `AllControllerTypes()`, so `[AllowAnonymous]` on an action of an `[Authorize]` section
      controller needs no edit at all. One file, two rows about the same controller, only one
      of which is a `typeof` (proven: Debug, `DebugController.DbVersion`).
      **And a third shape, which is not in `tests/` at all: Shell's own production code.**
      `Humans.Web/Hosting/DevLoginControllerExclusionProvider` removes
      `typeof(Controllers.DevLoginController)` from MVC's controller feature in Production —
      the thing that keeps the dev sign-in page out of prod. It cannot move into the section
      (`Program.cs` constructs it by name, which would make it a public section type) and it
      cannot keep the `typeof`. Give it the same `SectionType(fullName)` reflection the tests
      use, resolved once into a `static readonly Type` that **throws** when the name misses, so
      a rename fails at Production startup rather than shipping a routable `/dev/login/*`.
      Grep `typeof(<Section>` over `src/` as well as `tests/` (proven: Development).
      **And a fourth, in Shell's *own* test project rather than `Humans.Application.Tests`:**
      `Humans.Web.Tests`' `MembershipRequiredFilterTests` and `NameRequiredFilterTests` build a
      `ControllerActionDescriptor` around a **real** controller type so the filter's
      `AllowAnonymous` reflection check has something to read. Neither filename mentions the
      section, and neither project carries a `SectionType(...)` helper, so each needs its own
      `static readonly Type` resolved through `SectionDiscoveryExtensions.SectionAssemblies()`
      that throws on a miss. Grep `typeof(<Section>` across **every** test project, not just the
      four that usually carry rows (proven: Onboarding).
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
      - **…and `AdminNavTree` is only the *guarded* instance of that hazard — grep
        `Url.Action("<Action>", "<Controller>")` too.** The nav tree has a test walking it;
        a bare `Url.Action` string pair anywhere in Shell has nothing. It returns **null**
        for an unresolvable pair rather than throwing, so a caller doing
        `ActionUrl = cond ? Url.Action("X", "Y") : null` keeps compiling, keeps returning
        200, and silently renders the affordance without a link — indistinguishable in the
        HTML from the "not applicable" branch it was already able to take. Shifts' case is
        `ThingsToDoViewComponent`'s link to `("ShiftInfo", "Profile")`, which the lane's
        controller split rehomes to `ShiftProfileController`. The two searches are
        different (`AdminNavTree` keys on the controller name alone) and so are their
        failure surfaces, so run both: `grep -rn 'Url.Action(' src/` filtered to the
        section's action names, and assert the resulting `href` in the step 12 render test
        (proven: Shifts lane C, found while splitting a controller under another section's
        route prefix).
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
      - **A section with no keys and no assets still has a probe, and it is the element name of
        whatever `Humans.UI` tag helper its pages open with.** An unbound tag helper neither
        throws nor degrades — it survives into the response as its own start tag, so the page is
        a 200 with correct-looking source and a missing widget, exactly like a stray `<vc:>`.
        Debug's ten pages all open with `<page-header>`, so `NotContain("<page-header")` beside
        the existing `NotContain("<vc:")` covers the whole section in one line. Grep the moved
        views for `<[a-z]+-` and assert on what comes back (proven: Debug, whose only other
        halves would have been a resx carve and a `?v=` hash it does not have).
      - **A section with no pages at all has no render test — so find what else drives
        `AddDiscoveredSections`, because that is the half of step 12 you still need.**
        Guide's missing `Humans.Web` `ProjectReference` (step 1) is normally caught by the
        render test 404ing; a section with no controller and no view cannot have one. What
        substitutes is any existing test that calls the *real*
        `InfrastructureServiceCollectionExtensions.AddHumansInfrastructure` and asserts the
        section's own registration came out of it — Gdpr's
        `GdprExportDependencyInjectionTests.GdprExportServiceIsRegistered` does exactly that,
        and it can only pass if `Section.Register` ran, which needs the assembly in Shell's
        dependency graph. Check that such a test exists *before* concluding a page-less
        section is untestable at step 12; if none does, the `grep '<Section>'
        src/Humans.Web/Humans.Web.csproj` check from step 1 is the whole of your coverage
        (proven: Gdpr).
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
      **Second sighting, with the cheaper fix: call the invalidator.** Consent's document-side
      decorator warms at startup with `warmOnStartup: true`, so the same trap fires — but its
      `I<Section>CacheInvalidator` is already on the leaf for another consumer, and the test
      project sees section internals either way. A context write followed by
      `Factory.Services.GetRequiredService<I…CacheInvalidator>().InvalidateAll()` keeps the
      fixture a plain `db.Add` and needs no service-shaped seeding path. Prefer it when the
      section already ships an invalidator (proven: Consent).
    - **Add the section's negative access rule to the render test, and read the status code
      off the app rather than off the invariants doc.** The move rehomes the controller into an
      internal type in another assembly routed by `SectionControllerFeatureProvider`, while its
      policy stays in Shell's `AuthorizationPolicyExtensions` (step 6's asymmetry) — one GET as
      a non-privileged persona is what proves the two halves still meet, and it is three lines
      beside the pages loop. Expect **`302` to `Program.cs`'s `AccessDeniedPath`, not `403`**:
      cookie authentication redirects an authenticated-but-unauthorized request, app-wide.
      Cantina's doc asserted a bare `403 (Forbid())` the app has never returned — the shape of
      claim that survives forever because nothing tests it, and worth grepping your own doc for
      before writing the assertion. Correct the doc in the move commit and say so; `Forbid()`
      in a *controller* is the same story, since it runs through the cookie handler's
      `HandleForbiddenAsync` (proven: Cantina).
    - **When the section's backing store is a vendor API, replace the client in the test
      factory.** The fourth sibling to Calendar's "seed through the service", Consent's "call
      the invalidator" and Guide's "seed the cache" — and the one the first three do not cover,
      because the thing to seed *is* the thing under replacement. `HumansWebApplicationFactory`
      already does this for Stripe; removing the `IMailerLiteService` descriptor and adding a
      stub is the same three lines. Two details cost a cycle each: NSubstitute cannot serve an
      interface exposing `IAsyncEnumerable<T>` (a default substitute returns `null` and the
      first `await foreach` NREs, so the stub is hand-written), and the page whose action calls
      the vendor *outside* a `try` is a 500 rather than the error banner its sibling degrades
      to — so "the pages handle a dead vendor" is not a substitute for the stub (proven:
      MailerLite).
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

- ~~**The `Dockerfile`'s `RUN dotnet restore` layer does not work and has not for some time.**~~
  **Obsolete — re-audited by G5 lane 3c, 2026-08-15.** The finding described a restore layer that
  copied five named csprojs and missed the rest. The `Dockerfile` no longer does that: it does
  `COPY src/ src/` before `dotnet restore src/Humans.Web/Humans.Web.csproj`, deliberately trading
  layer-cache granularity for a file that needs no per-project `COPY` line as the project count
  climbs toward ~40. **A new section needs no `Dockerfile` edit at all.** (The project the old
  finding named, `Humans.Domain.csproj`, no longer exists either — deleted in lane 3b.)

## The `<vc:*>` rename hazard

ReSharper's move-to-namespace and rename refactorings read a `<vc:name>` element as a reference to
the view-component *type* and rewrite it to `<name-view-component>`. Nothing objects: green build,
green suite, 200 response, and the element renders as inert literal markup (proven: PR 0, 127
tags, caught only by the HTML diff). After any refactoring pass over `.cshtml`:

```bash
grep -rn --include='*.cshtml' -- '-view-component' src/
```

Expect zero hits. The step 12 diff is the backstop, not the first line of defence.
