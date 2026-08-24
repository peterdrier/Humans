<!-- freshness:triggers
  src/Humans.Analyzers/**
  Directory.Build.props
  tests/*/Architecture/**
  docs/architecture/code-analysis.md
-->
<!-- freshness:flag-on-change
  Forward-looking inventory of Roslyn analyzer candidates beyond the shipped set (see the code-analysis.md catalogue).
  Flag if a new analyzer ships (move that entry from Tier 1 → catalogue in code-analysis.md),
  if a new atom lands with call-site shape, or if a recent clamp-fix commit would have been
  prevented by a not-yet-shipped analyzer.
-->

# Roslyn Analyzer Candidates

Forward-looking inventory of *additional* in-repo analyzer rules beyond the currently-shipped set — 25 live rules: `HUM0001`–`HUM0003`, `HUM0005`–`HUM0011`, `HUM0014`–`HUM0016`, `HUM0019`–`HUM0020`, `HUM0025`–`HUM0028`, `HUM0030`–`HUM0035`, catalogued in [`code-analysis.md`](code-analysis.md). **Retired ids are never reassigned:** `HUM0004` (Profile.IsSuspended write guard, dropped with the column in nobodies-collective/Humans#1217), `HUM0017`/`HUM0018` (cross-section repository injection and "cannot determine the section", retired in nobodies-collective/Humans#1064 — the assembly boundary makes the first a compile error and `[Section]` no longer exists to be missing), `HUM0022`/`HUM0023` (the per-section Notification and Event DbSet-write analyzers, subsumed by the universal `HUM0025`), `HUM0021`/`HUM0024` (retired in nobodies-collective/Humans#1278), and `HUM0012`/`HUM0013`/`HUM0029` (all keyed on a `Humans.Application.*` namespace layout the section assemblies replaced). **IDs are assigned at ship time from the next free slot in `AnalyzerReleases.Unshipped.md` — currently `HUM0036`.** The candidate headings below are deliberately *un-numbered*: several once carried provisional `HUM00xx` numbers that later shipped for unrelated rules, so do not pre-claim an id here. This file is the queue we draw from when adding the next analyzer; do not start writing one without checking here first.

## Framing

A Roslyn analyzer earns its keep over a test when the rule is **call-site
shaped** ("X may not call Y" / "must not write property P" / "must not
reference symbol S in scope A") and would fire on **every compile**, in-editor
under the squiggle. Cost is ~50 lines of analyzer + ~30 lines of tests once
the `Humans.Analyzers` project exists (see the worked example in
`code-analysis.md` §"Writing a new analyzer"). A test is the right tool when
the rule is **baseline-ratcheted** (accumulated existing violations, new ones
forbidden), **marker / existence** (a symbol must exist, an interface must be
implemented), or **filesystem-aware** (rule depends on which directory a file
lives in).

The ratchet rules under `tests/Humans.Web.Tests/Architecture/Rules/` (they
moved there when `Humans.Application.Tests` was dissolved into per-section test
projects) and the 4 boundary scans in `ServiceBoundaryArchitectureTests.cs` all
fall outside the analyzer envelope and stay as tests. Tier 3 below lists them so
they aren't re-proposed.

**Don't scope a new analyzer by assembly name.** `src/Directory.Build.props`
already attaches the analyzer to every `src/` project and nothing else, so a rule
runs where it should by construction; a literal-name gate only makes it go silent
in whichever assembly the code moves to next. Where a rule genuinely applies to
sections only, `Internal/AssemblyScope.IsSection` answers it — an assembly is a
section when it declares `<AssemblyName>.Section : ISection`. For the section
*name*, `Internal/Sections.cs` strips the `Humans.` prefix and any `.Contracts`
suffix off the assembly name.

**Prefer adding a rule to `SectionRulesAnalyzer` over writing a new analyzer
class.** One file per rule under `Internal/Rules/`, listed in the analyzer's class
comment, so a section's rules are readable in one place.

---

## Tier 1 — High value, ready to ship

Rules where the call-site shape is crisp, no baseline is required, and the
in-editor feedback prevents a class of regression that has already cost the
project at least one fix commit.

### HUM0020 — Caching decorators must not reference repositories (SHIPPED)

- Rule: a class whose top-level name starts with `Caching` and ends with
  `Service`, or one of its nested helper types, must not structurally reference
  any `IRepository` implementation/interface. Cache misses and warm paths go
  through the keyed inner application service, never sideways into persistence.
  The match is on the name alone — no namespace or assembly gate — which is why
  the rule followed all 11 decorators into their section projects without an edit.
- Source: generalized from the deleted `CachingTeamServiceBypassArchitectureTests`
  one-off and `TicketQueryArchitectureTests.CachingTicketQueryService_HasCurrentEventTicketAsync_DoesNotCallRepositoryOrFilter`.
- Why analyzer, not one-off test: the important invariant is system-wide:
  decorators are transparent wrappers around the application service surface.
  If a decorator injects a repository, it can bypass the inner service's
  authorization, write orchestration, section boundaries, and cache invalidation
  behavior. The call-site/constructor shape is crisp and should fail at build
  time for every new decorator.
- Status: shipped as `HUM0020` and enforced as an error. The original
  repository-backed cache loaders now route through keyed inner services.

### 2026-05-24 architecture-test audit follow-ups

The current architecture-test suite still contains several repeated one-off
assertion families that are plausible analyzer candidates:

- Application services should not inject forbidden infrastructure-ish
  dependencies: `IMemoryCache`, store abstractions, `IServiceProvider`,
  `UserManager`, or direct SDK clients. `IMemoryCache` is already covered by a
  generalized arch test; promote it to an analyzer once the remaining
  grandfather story is clear.
- Repository sealing and placement — **retired as predicted, not re-proposed.**
  `IRepositoryImplementationsAreSealedRule` and
  `RepositoryImplementationsLiveInInfrastructureRule` were both scoped to the
  `Humans.Infrastructure` assembly; both files were deleted when G5 lane 5b-6
  deleted that project. Do not repoint them at the section assemblies: there,
  HUM0034 forces every non-`Contracts/` type `internal` and MA0053 then errors on
  an unsealed `internal` class, so sealing is a structural fact, and a repository
  lives in its own section's `Data/` folder by construction (HUM0035 additionally
  errors on a repository declared under `Contracts/`). What is still unenforced is
  the third clause — a repository must take `IDbContextFactory<TContext>` rather
  than construct or inject a context directly. HUM0009 covers the "not a
  repository" half; nothing covers the factory shape.
- Interface marker obligations should be compile-time enforced:
  `I*Service`/`I*Query`/`I*Calculator` extend `IApplicationService`, and
  `I*Repository` extends `IRepository`.
- External SDK types must not leak across Application/Web boundaries. The
  repeated Ticket Tailor, Stripe, and Google bridge tests are one rule family:
  application interfaces expose DTOs/abstractions, infrastructure owns vendor
  SDK types.
- DbSet ownership as one analyzer family — only the owning repository touches
  its owned DbSets — **(SHIPPED)** as `HUM0025`
  (`SingleRepositoryPerTableAnalyzer`), the day after this audit: a DbSet
  referenced, read or written, by more than one `IRepository` implementation is
  an error, with `[Grandfathered("HUM0025", …, scope: "<DbSet>")]` per
  (repository, table) pair. The bespoke AuditLog / Events / Notifications
  ratchets it replaced are gone. Since the per-section split it matches DbSets
  on **every** application context via `Internal/SectionDbContexts.cs`, not just
  one hard-coded context. Catalogued in `code-analysis.md`.
- Application service read methods should not expose domain/EF entities.
  `ApplicationServiceEntityReadReturns.baseline.txt` has existing debt, so this
  needs either a grandfather mechanism or a warning-first migration.
- Cross-section EF nav configuration was error-enforced by `HUM0024`, **retired in
  nobodies-collective/Humans#1278**. Structure took over: each section has its own
  `DbContext` in its own assembly, so there is no shared EF model for a navigation to
  join across. The debt was already drained before retirement —
  nobodies-collective/Humans#992 cut all 54 cross-section relationships, leaving no
  `[Grandfathered("HUM0024", ...)]` markers. The residual the retirement note reserved —
  an EF configuration inside `Humans.Infrastructure` mapping another section's entity —
  is moot: that project no longer exists, and all 29 contexts are section-owned. What is
  left is the review-time check that a *section* context does not map a foreign entity;
  `DbContextEntityOwnershipTests` catches the zero-context and two-context cases of that.
  Do not re-propose this analyzer.

### HUM0007 — `IsConcurrencyToken` / `[ConcurrencyCheck]` / `[Timestamp]` forbidden

- Rule: an EF configuration `Property(...)` chain may not call
  `.IsConcurrencyToken()` or `.IsRowVersion()`, and entity properties may not
  carry `[ConcurrencyCheck]` or `[Timestamp]`.
- Source: [`memory/architecture/no-concurrency-tokens.md`](../../memory/architecture/no-concurrency-tokens.md)
  ("HARD RULE … never add without explicit user permission").
- Call-site shape: invocation of a single, well-known method on
  `PropertyBuilder<T>`, or an attribute on a property. Identical to HUM0002 in
  shape — operation kind + symbol metadata match.
- Why analyzer, not ratchet: at the symbol-not-baseline level there are zero
  legitimate uses anywhere in the live source today. An analyzer gives Peter a
  build-break the moment someone adds one, in-editor and in CI.
- Current coverage: `HUM0007` analyzer. **Its migration carve-out is inert:**
  `ConcurrencyTokenAnalyzer.IsInMigration` still matches only the
  `Humans.Infrastructure.Migrations` namespace and the
  `/Humans.Infrastructure/Migrations/` path, neither of which survives — migrations
  now live in `src/Sections/Humans.<Section>/Data/Migrations/` and
  `src/Humans.Web/Migrations/System/`. Nothing has tripped it because EF does not
  scaffold concurrency metadata unasked, but the carve-out no longer covers what it
  was written to cover.

### View components may not inject `IMemoryCache`

- Rule: a class deriving from `ViewComponent` may not have a constructor
  parameter typed `Microsoft.Extensions.Caching.Memory.IMemoryCache`.
- Source: [`memory/code/viewcomponent-no-cache.md`](../../memory/code/viewcomponent-no-cache.md)
  ("View components MUST NOT inject or use `IMemoryCache` directly").
- Call-site shape: base type check + constructor parameter type. Same shape as
  HUM0008.
- Why analyzer, not ratchet: small, sharp, fully-current (no historical
  violations to baseline). The fix-up commit history (`UserAvatarViewComponent`
  in PR #222) is exactly the regression this catches at the keystroke level.
- Current coverage: none.

### `Bootstrap Icons` (`bi bi-*`) class strings forbidden

- Rule: a string literal in any `.cshtml` / `.cs` file that matches the
  pattern `\bbi bi-[a-z0-9-]+` is a violation.
- Source: [`memory/code/icons-fa6-only.md`](../../memory/code/icons-fa6-only.md)
  (Bootstrap Icons CSS is not loaded; renders as invisible).
- Call-site shape: literal string operation, regex on text. Analyzers can
  walk `ILiteralOperation` and inspect `ConstantValue.Value`. For `.cshtml`
  this needs the Razor source generator output — landed in .NET 8+, so the
  generated C# is in the compilation and the analyzer sees the literal.
- Why analyzer, not ratchet: silent-failure rule (no exception, just invisible
  icons in prod). In-editor feedback at the moment someone pastes a
  Bootstrap snippet is the entire value proposition.
- Current coverage: none. Today this is caught by review or by the user
  noticing missing icons.

### `TempData["SuccessMessage"]` / `["ErrorMessage"]` / `["InfoMessage"]` forbidden in controllers

- Rule: in classes under `Humans.Web.Controllers`, an element-access
  expression on `TempData` with one of the three magic-string keys is a
  violation. The reviewer should use `SetSuccess` / `SetError` / `SetInfo` on
  the controller base instead.
- Source: [`memory/code/controller-base-conventions.md`](../../memory/code/controller-base-conventions.md)
  ("Do not write direct `TempData[\"SuccessMessage\"]` … assignments").
- Call-site shape: `IPropertyReferenceOperation` on `Controller.TempData`
  combined with a constant-string indexer argument. The three keys are a
  fixed allowlist of forbidden literals.
- Why analyzer, not ratchet: tiny rule, finishes in one operation kind, and
  the diagnostic can include a fixer suggestion in the message body
  ("use SetSuccess(...) from HumansControllerBase"). No baseline — the
  codebase is already clean per the atom.
- Current coverage: none — convention-only today.

### `System/` namespace shadows forbidden

- Rule: no type may live in a namespace whose components include a segment
  literally named `System` (other than the BCL `System` root itself).
- Source: [`memory/code/no-system-subfolder.md`](../../memory/code/no-system-subfolder.md)
  (relative-then-absolute resolution shadows BCL `System.X` types, breaks
  sibling files).
- Call-site shape: every `INamedTypeSymbol` declared in compilation; walk
  `ContainingNamespace` ancestors and check for a segment named `System`
  that isn't the global root. Trivial.
- Why analyzer, not ratchet: silent-failure that took out compile across
  every sibling folder in the 2026-04-23 reorg. An analyzer would have fired
  on the first new file added under `Configurations/System/`, before the
  cascade.
- Current coverage: none — caught by the resulting compile error far from
  the cause.

### HUM0015 / HUM0016 — `[SurfaceBudget(N)]` analyzer (SHIPPED)

- Rule: a type (interface, class, or struct) decorated with
  `Humans.Base.Attributes.SurfaceBudgetAttribute(N)` must declare
  exactly `N` directly-declared **public-instance** ordinary methods.
  Over-budget fires HUM0015; under-budget (slack) fires HUM0016.
- Source: replaces the retired `InterfaceMethodBudgetTests`
  (issue [nobodies-collective/Humans#700](https://github.com/nobodies-collective/Humans/issues/700)).
  Budgets live as a per-type attribute with the rationale in XML `<remarks>`
  on the type. Owner-applied only; agents never add it or suggest adding it — see
  `memory/code/surface-budget-owner-applied.md`. **There are zero live
  applications right now** — the attribute was lifted off every type for the
  remainder of the 866 migration while surfaces are in flight, so both diagnostics
  currently match nothing. `SurfaceBudgetAttribute` itself still ships from
  `src/Humans.Base/Attributes/`.
- Call-site shape: `SymbolKind.NamedType`, filter to interface/class/struct
  carrying the attribute, count public-instance `MethodKind == Ordinary`
  members directly on the symbol. Accessibility filter is a no-op on
  interfaces (all members are implicitly public-instance) but discriminates
  on classes/structs.
- Status: shipped. Catalogued in `code-analysis.md`.

### `Cached*` type names forbidden for public surface

- Rule: a `public` or `internal` `INamedTypeSymbol` whose name starts with
  `Cached` may not be declared anywhere except a section's `Services/` or `Data/`
  folder (where caching decorator implementation classes legitimately live) —
  `Internal/AssemblyScope.IsInSectionDataLayer` already answers half of that.
- Source: [`memory/architecture/caching-transparent.md`](../../memory/architecture/caching-transparent.md)
  ("Never introduce a type named `Cached*` for domain data").
- Call-site shape: type declaration symbol. Pure name + scope check; same
  shape as HUM0009.
- Why analyzer, not ratchet: directional rule that's easy to backslide on
  during a future section migration. The in-editor squiggle on the class
  name as you're typing the file is exactly where Peter wants the feedback.
- Current coverage: none — convention plus PR-review-time pushback.

---

## Tier 2 — Plausible but needs framework

Rules that want analyzer enforcement but the analyzer project doesn't yet
have the supporting machinery. Each notes the missing piece.

- **`Razor boolean attribute foot-gun`** (`disabled="@bool"`, `checked="@bool"`,
  etc., from [`docs/architecture/code-review-rules.md`](code-review-rules.md)
  "8+ historical fixes"). Call-site is in Razor markup, not C#. Roslyn sees
  the generated `WriteAttribute(...)` calls in the source-generated file,
  but pattern-matching the boolean-attribute case in the generated output is
  fragile across Razor compiler versions. **Needs:** a Razor-aware analyzer
  shape, or an MSBuild task that walks `.cshtml` AST directly. Defer.

- **`Cross-domain .Include()` calls in Application services** (design-rules §6
  + the §15i landmark commentary). Call-site shape is clean
  (`IInvocationOperation` on `EntityFrameworkQueryableExtensions.Include`),
  but the rule is "no `.Include()` whose target navigation crosses a section
  boundary" — requires a section-ownership map for entity types. Half the
  prerequisite has since landed: `Internal/Sections.cs` resolves a *type's*
  section from its assembly name or, failing that, its namespace segment, and
  `Internal/SectionDbContexts.cs`
  enumerates the DbSets on every application context. What is still missing is
  the entity→section direction — `Sections.cs` keys on assembly name, then on
  service/interface namespace, and an entity declared in a `.Contracts` leaf
  (`Humans.Users.Contracts.User`) resolves to its own section, not to the section
  doing the querying. HUM0024 used to derive entity
  ownership from EF-config namespace layout, but it is retired
  (nobodies-collective/Humans#1278), so that derivation is no longer available to
  copy — the mapping has to be written fresh. Land it as a shared helper before
  writing the Include analyzer.

- **`Display sort in repositories/services`** (HARD-ish rule, see
  [`memory/architecture/display-sort-in-controllers.md`](../../memory/architecture/display-sort-in-controllers.md)).
  Call-site is `OrderBy` / `OrderByDescending` inside a section's `Services/` or
  `Data/` folder, but the rule has an inline
  `// arch:db-sort-ok` opt-out comment. Comment-driven suppression is doable
  in an analyzer (read trailing trivia on the invocation syntax) but it's a
  new pattern for this project. **Needs:** a small `TriviaSuppression`
  helper in `Internal/`. Worth adding once a second rule wants the same
  comment-suppression shape.

- **`IsConcurrencyToken in migrations is OK`** (the suppression carve-out for
  HUM0007) — **now a live gap, not a nicety.** The shipped carve-out matches only
  the deleted `Humans.Infrastructure` namespace and path, so it covers no migration
  today. The fix is the same ~5 lines it always was: recognize `**/Migrations/**`
  from `Compilation.SyntaxTrees`, which reaches both
  `src/Sections/Humans.<Section>/Data/Migrations/` and
  `src/Humans.Web/Migrations/System/`.

- **`No `.Include()` for navigations across sections by name`** (a softer
  Phase-1 of the §6 rule). Could fire on hardcoded nav-property names that
  match cross-section conventions (`Profile`, `Team`, `User` accessed from
  outside the owning section's namespace). **Needs:** the same
  section-ownership map as the strict version above. Same defer.

- **`No new `[Authorize]`-less POST/PUT/DELETE actions in controllers`**
  (`code-review-rules.md` "Authorization Gaps, 8+ historical fixes"). Each
  method is a `MethodDeclarationSyntax` decorated with
  `[HttpPost]`/`[HttpPut]`/`[HttpDelete]`; the rule is "must also carry
  `[Authorize]` or have it inherited from the class". **Needs:** baseline
  framework — there are existing controller actions that legitimately use
  alternate auth attributes (`[AllowAnonymous]`, custom policies, the
  attribute on a base class). Without a baseline-aware analyzer the
  false-positive rate is too high.

---

## Tier 3 — Captured for completeness (covered by ratchet tests)

Listed so the next maintainer doesn't propose them as analyzers. Each one is
shaped for ratchet / marker / filesystem-aware enforcement, not for an
analyzer.

- `NoConcurrencyTokensRule` — replaced by semantic analyzer `HUM0007`.
- `NoCrossSectionEfJoinsRule` — was replaced by analyzer `HUM0024`; both are gone, the analyzer retired in nobodies-collective/Humans#1278 (per-section `DbContext`s make the join structurally impossible). Do not resurrect either.
- `NoLinqAtDbLayerRule` — retired in nobodies-collective/Humans#866's GoogleIntegration move. It scanned a hardcoded `src/Humans.Application/Services`, which G5 had drained to 28 files while 360 service files moved to `src/Sections/*/Services`; its baseline reached zero through lost coverage, not through fixes. The `no-linq-at-db-layer` rule itself still stands (`memory/architecture/no-linq-at-db-layer.md`) — only the regex ratchet is gone. Do not resurrect it in this form.
- `NoBusinessLogicInControllersRule` — retired (the regex heuristic was noisy and only saw public action signatures; nobodies-collective/Humans#793). Replaced by semantic analyzer `HUM0031` (`ControllerBusinessLogicAnalyzer`): statements > 40 or cyclomatic complexity > 15 on any controller method, method-level `[Grandfathered]`, **thresholds frozen at 40/15 until 2027**. The freeze is Peter's call and is not up for revisiting — do not propose tightening, unfreezing, or "re-evaluating now that #866 has progressed". He will lower them when he decides to.
- `NoObsoleteNavReadsRule` — was replaced by semantic analyzer `HUM0021`; both are gone, the analyzer retired in nobodies-collective/Humans#1278 as a dead rule (zero live `[Obsolete("Cross-domain nav…")]` markers since nobodies-collective/Humans#996). Do not resurrect either.
- `NoDestructiveMigrationOpsRule` (`tests/.../Rules/NoDestructiveMigrationOpsRule.cs`) — operates on EF-generated migration files which legitimately contain destructive ops in other contexts. Filesystem-aware. Stay as ratchet.
- `NoStartupGuardsRule` — retired alongside `NoLinqAtDbLayerRule` (Peter's call). Its scan root `src/Humans.Web` was still valid and its baseline genuinely zero, but it was a regex over one project out of 36+ and saw no section's `Section.Register`. The `no-startup-guards` rule itself still stands (`memory/architecture/no-startup-guards.md`).
- `DisplaySortInControllersRule` (`tests/.../Rules/DisplaySortInControllersRule.cs`) — accumulated debt + inline `// arch:db-sort-ok` opt-out; baseline-ratcheted today, see Tier 2 for the analyzer prerequisite.
- `ServiceBoundaryArchitectureTests` (`tests/Humans.Web.Tests/Architecture/ServiceBoundaryArchitectureTests.cs`) — four boundary scans (marker-attribute presence for services and for repositories, repository-ownership-map completeness, and the entity-read-return ratchet; the Users/Profiles single-section pin went with the merge, and the former repository-injection scans across Web and Application shipped as analyzers). All shaped as reflection/marker tests or baselined ratchets. Stay as tests.
- `ApplicationServicesTakeNoDbContextRule` / `ApplicationServicesTakeNoMemoryCacheRule` (`tests/Humans.Web.Tests/Architecture/Rules/`) — generalized ctor sweeps over the section assemblies. Their scope comes from `ApplicationSweepScope.Assemblies()`, which resolves through `SectionDiscoveryExtensions.SectionAssemblies()` rather than a hardcoded assembly name — the fix for the four silent anchor drifts G5 produced. Stay as tests.
- The per-section `*ArchitectureTests.cs` files (in the section's own test project, under `Architecture/` or at its root; 39 of them today, across 33 test projects) — each pins namespace location, ctor shape, no-DbContext-injection, and "owned entities have no cross-domain navs" using reflection on the loaded assemblies. Marker/existence + reflection shape. Stay as tests.

Cited reference for the policy: `docs/architecture/code-analysis.md`
§"When to write an analyzer vs. a test" (the decision table).

---

## Out of scope — judgment / terminology / vocabulary

These atoms describe rules that can't be enforced mechanically by either an
analyzer or a test. Listed once so the next sweep doesn't churn on them.

- [`memory/product/humans-terminology.md`](../../memory/product/humans-terminology.md) — "UI says 'humans', never 'members'/'volunteers'/'users'". Localized strings + view text + comments; an analyzer would have a catastrophic false-positive rate on the C# side (variable names, technical comments).
- [`memory/product/no-event-name-nowhere.md`](../../memory/product/no-event-name-nowhere.md) — "never 'Nowhere' in user-facing text". Distinguishing user-facing from technical strings is the unsolvable part.
- [`memory/code/no-hallucinated-content.md`](../../memory/code/no-hallucinated-content.md) — judgment call about whether copy is invented vs. admin-editable.
- [`memory/code/no-magic-strings.md`](../../memory/code/no-magic-strings.md) — `nameof`-vs-literal preference, but distinguishing "code identifier" strings from legitimate string literals is judgment. Roslynator's `RCS1163`-family covers a fraction.
- [`memory/architecture/burnername-is-the-display-name.md`](../../memory/architecture/burnername-is-the-display-name.md) — "use `<vc:human>` or `FullProfile.DisplayName`, not `User.DisplayName`". The call-site shape is clean (`PropertyReference` on `User.DisplayName`), but the legitimate fallback paths are everywhere in legacy code; would need a baseline framework + per-section sweep before turning into an analyzer. Tracked as a future Tier-2 candidate, not in scope today.
- All `memory/process/*` atoms — git workflow, PR rules, issue triage, release notes — none of these touch source code mechanically.
- [`memory/code/no-extensions-for-owned-classes.md`](../../memory/code/no-extensions-for-owned-classes.md) — "no extensions on types we own". The "we own this type" predicate is doable (assembly + namespace check), but the rule is directional and legitimate carve-outs (BCL helpers re-exposed as project-local extensions) are common enough to need judgment.
- The date/time-format-string half of [`memory/code/datetime-display-formatting.md`](../../memory/code/datetime-display-formatting.md) **shipped** as `HUM0030` (custom `.ToString` format, interpolation format clause, NodaTime `*Pattern.Create` literal — all forbidden outside the single home `Humans.Base.Extensions.DateFormattingExtensions`; catalogued in [`code-analysis.md`](code-analysis.md), backed by [`memory/architecture/datetime-format-single-home.md`](../../memory/architecture/datetime-format-single-home.md)). Still review-only (not analyzer-enforced): `time-parsing-standardization` / `culture-and-language` / `csv-and-pagination-helpers` "use shared helpers", and the HUM0030 v1 gaps (`ParseExact` / composite `string.Format` strings and `.cshtml` Razor, which Roslyn analyzers don't see). Reconsider per-rule if any drift back into the codebase.

---

## When this list grows

Every time a clamp-fix commit lands (a "ratchet", "hotfix", "guard", "pin",
"tech debt", or "fix at X but should not have happened" pattern — recent
examples: `b5944b09` wiring TicketTransfer into the boundary ratchet,
`c5cce53d` pinning `UpdateEmailAsync`'s sole caller, `60c4d5b1` pinning the
public-camp-detail-never-renders-EE invariant), ask whether the regression
was a call-site shape an analyzer would have caught. If yes, add it to
Tier 1 or Tier 2 here.

The doc cap is ~400 lines on purpose — when it grows past that, the next
sweep should retire any Tier-1 entries that have shipped (move them to the
catalogue in `code-analysis.md`) and re-prune Tier 2 / Tier 3 for staleness.
