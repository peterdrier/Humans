---
name: section-align
description: "Three-axis orchestrator: (1) clean section boundaries — naming, routes, views, ViewModels, DB ownership, cross-section access; (2) internal cohesion — no EF in service layer, caching in service only, proper interfaces, reusable ViewComponents, architecture-test coverage; (3) focused tests — grouped under the section, covering invariants/negatives/triggers, pruned of redundancy. Push-and-bot-review loop. Use when a sizable PR landed, a section shows arch drift, or /pr-review returned a non-trivial violation list. Camps is the gold-standard reference."
argument-hint: "PR 374 | section EventGuide | section Camps --inventory-only"
---

# Section Align

## Purpose

Drive one section of the Humans app to architectural alignment along three axes — boundary integrity, internal cohesion, test focus — through a phased PR that's pushed for bot review at each phase boundary. Surface follow-up /section-align targets for cross-section gaps the run discovers.

## Vision

Every section in the app is a self-contained vertical slice: one canonical name, its own data, public API others consume cleanly, current internal standards, and a focused test suite grouped with the section. Drift gets corrected one section at a time; new violations stand out instead of disappearing into noise.

## Alignment definition

A section is "aligned" when **all three axes hold**:

**Axis 1 — boundary integrity.** The section has a single canonical name shared by folder, namespace, controller, views, ViewModels, route prefix, role suffix, invariant doc. Routes follow `/<Section>/*`, `/<Section>/Admin/*`, `/api/<section>/*`. No other section reads, writes, joins, or navigates EF into this section's tables. The section's controller, views folder, and ViewModels file exist under the section's name — *not* in `Views/Shared/`, `Models/AdminViewModels.cs`, or other sections' controllers.

**Axis 2 — internal cohesion.** Services live in `Humans.Application.Services.<Section>/` and never import `Microsoft.EntityFrameworkCore` or take `DbContext`. Repositories are sealed, factory-based, registered Singleton. Caching lives in the service layer only (decorator or service-internal `IMemoryCache`), never in repos or controllers. Interfaces obey the budget ratchet. ViewComponents exist where the section's data is rendered on other sections' pages. Architecture tests pin each invariant. Migrations are EF-auto-generated.

**Axis 3 — test focus.** Section-aligned tests live under one canonical location (`tests/Humans.Application.Tests/<Section>/` or `tests/Humans.Application.Tests/Services/<Section>/` — pick one, not both). Coverage maps to invariants, Negative Access Rules, and Triggers from the section doc — not to every method permutation. Redundant tests are pruned. Test maintenance cost matches test value. Tests assert observable behavior, not mock-graph internals.

**Camps is the reference.**

## Boundary-fix protocol

**Zero-tolerance rule:** the only allowed cross-section data link going forward is the **User ↔ Profile** relationship (identity row ↔ volunteer profile). Every other cross-section read or write is a violation, including patterns previously documented as "sanctioned exceptions" in section docs (narrow display lookups like `ctx.Users.Select(u => u.DisplayName)`, `ctx.Teams.Select(t => t.Name)`, etc.). "Sanctioned" is not a category in this skill.

When the inventory finds cross-section access (either direction), apply this protocol — **never just "fix the offending file" silently**, because that violates section ownership.

- **We are the producer** (another section reads or writes our tables / navs into our entities): our job in THIS PR is to ensure the public API on our service satisfies the caller's need. Add the API if missing. Then flag the calling section as the next /section-align target — they own the call-site migration.
- **We are the consumer** (we read or write into another section's tables / `.Include` across into another section's entity): if their public service API exists and delivers what we need, switch to it in THIS PR. If their API doesn't exist, flag their section as the next /section-align target, document the gap in our plan's follow-up list, and pause that specific fix until they're aligned. Then we come back and close out our section.

Result-oriented framing: a section is **provisionally aligned** when all in-section work is done; **fully aligned** when every follow-up supplier has caught up and the last consumer-side fixes land. The Phase 4 doc polish acknowledges any provisional state by naming the pending suppliers.

Output every Phase 0 plan with an explicit **Follow-up /section-align targets** list naming each section that surfaced as needing its own pass.

## Input

- `PR <n>` — inventory against diff; work on PR branch in a worktree.
- `section <Name>` — resolves to `docs/sections/<Name>.md` + matching code; fresh branch off `origin/main`.
- `<empty>` — ask which target.
- `--inventory-only` — Phase 0 only.

## Stop conditions (check after Phase 0 — ask user before continuing)

- Canonical name collides with an existing controller/service/folder in another section.
- Cross-section DB fix would bump another section's budget by more than 1–2 methods.
- Owning section of an entity is genuinely ambiguous (affects `design-rules.md §8`).
- Invariant doc is missing more than half the `SECTION-TEMPLATE.md` shape.
- PR branch has uncommitted work or open conflicts.
- **Outbound API gap on a consumer-side fix** — we'd need a new public service method on another section to clean up our cross-domain read/write, and that section hasn't exposed one. Document, don't invent.

**Do not treat section-doc-declared exceptions as closed questions.** When the doc says "served from two controllers — required because…", that's a description of current drift, not a sanction. Evaluate the exception against the convention; if the convention can hold, fix; if a true exception is needed, surface it for owner sign-off.

Surface as: "I hit X. Decision needed: [A] / [B] / [C]."

## Phases

```
Phase 0  Inventory (both axes) → docs/plans/<YYYY-MM-DD>-section-align-<target>.md; stop conditions; ask user
Phase 1  Surface alignment (axis 1) — create/move controllers, views, ViewModels, extensions, routes
Phase 2  Fix arch violations + prior review items (both axes; boundary-fix protocol)
Phase 3  /simplify pass — Opus; internal cohesion + interface trimming
Phase 4  Doc polish — Opus; final review loop until merge-ready
```

Sequential phases. Within a phase, parallel subagents up to **hard cap of 3** (`~/.claude-shared/shared/claude.md`). Escalate model: Sonnet for Phase 1 mechanical work → Opus for Phases 2–4.

## Context discipline

Phase 0 inventory reads a lot of the section's code and grows the context fast. By the time Phase 0 lands as a committed plan file, the main-session context is typically 100k–250k tokens.

**Split rule:**
1. Phase 0 runs in the initial session. End by committing + pushing the plan file under `docs/plans/<YYYY-MM-DD>-section-align-<target>.md`.
2. **Start a fresh session (`/cls` or new conversation) before Phase 1** so impl begins on cached context.
3. The impl session runs as an **Opus orchestrator over subagents** via the `superpowers:subagent-driven-development` pattern. The orchestrator:
   - Reads the plan file once at the start.
   - Stays under **100k tokens** by dispatching each plan item to a subagent.
   - Aims to finish impl by **200k tokens**; if approaching that ceiling without convergence, commit progress and `/cls` again, resuming via the plan file.
4. Each subagent gets one bounded job (one rename group, one boundary fix with API+caller, one test-folder reorg) and returns a 200-word summary. The orchestrator never reads subagent intermediate output — only the final summary.
5. The closing /section-align re-run (Phase 4 tail) runs in the **same impl session** if budget allows, otherwise a third session.

This keeps the orchestrator focused on plan-execution decisions; the heavy reading/editing happens in disposable subagent contexts.

## Mode detection

| Mode | Trigger | Branch | Phase 2 prior comments |
|------|---------|--------|------------------------|
| PR | `PR <n>` | PR head branch in worktree | yes |
| Existing-section | `section <Name>`, main clean | new `align/<section>` | none |
| Mid-build | `section <Name>`, on feature branch | continue | none |

Always use a worktree under `.worktrees/section-align-<target>/` (`feedback_always_use_worktree`).

---

## Phase 0 — Inventory (two-axis)

Output: `docs/plans/<YYYY-MM-DD>-section-align-<target>.md`. Both axes always audited.

### Axis 1 — boundary integrity

**A1.1 Section name consistency.** Variants across folders, namespaces, controllers, views, ViewModels, roles, routes, docs. Propose canonical name. Flag collisions.

**A1.2 Controller existence.** Does `src/Humans.Web/Controllers/<Section>Controller.cs` (or `<Section>AdminController.cs` / `<Section>ApiController.cs` if split) exist? If routes serving this section's data live on *other* sections' controllers (e.g. `BoardController` hosting `/Board/AuditLog`), that is drift. List each foreign route and propose the move.

**A1.3 URL surface.** Every route serving this section's data. Catch:
- Routes outside `/<Section>/*` or `/<Section>/Admin/*` or `/api/<section>/*`
- `/Admin/<Section>/*` paths (`architecture_no_admin_url_section`)
- Non-Barrios↔Camps aliases (`feedback_no_url_aliases`)
- Generic top-level controllers hosting this section's concerns

**A1.4 Views folder.** Does `src/Humans.Web/Views/<Section>/` exist? Section-owned page views in `Views/Shared/` (e.g. `Views/Shared/<Section>.cshtml`) are drift unless they are genuinely cross-section partials reused by widgets. ViewComponent partials at `Views/Shared/Components/<Section>/` are fine (and expected).

**A1.5 ViewModel placement.** Section ViewModels in `Models/<Section>ViewModels.cs` or `Models/<Section>/`. Types parked in `Models/AdminViewModels.cs` or other grab-bag files = drift.

**A1.6 Controller-base leak.** Search `HumansControllerBase` (or equivalent) for section-specific helper methods or ViewModels. Section names appearing in `protected` method names, parameter types, or return types = drift. Move into `<Section>Controller`.

**A1.7 Extensions placement.** `src/Humans.Web/Extensions/Sections/<Section>SectionExtensions.cs` for DI wiring. Any `<Section>*.cs` in `Extensions/` root (not `Extensions/Sections/`) = drift.

**A1.8 Role surface.** Domain-scoped roles need `*Admin` suffix (`feedback_admin_superset`). Exceptions only when semantics don't fit (e.g., `ConsentCoordinator`).

**A1.9 INBOUND cross-section DB access (reads + writes).** Grep for any non-test file outside this section's repo touching this section's DbSets:

```bash
git grep -nE '_(db|context|ctx)\.(<TableA>|<TableB>)\.(Where|FirstOrDefault|Find|FindAsync|ToListAsync|Single|First|Count|Any|Add|AddRange|Update|Remove|Attach)\b' src/
git grep -nE '\b(_db|_context|ctx)\.<TableA>\b' src/  # bare DbSet refs catch e.g. AddRange that the verb regex misses
```

For each hit: apply the **boundary-fix protocol (producer side)**. Do we expose the public API the caller actually needs? If yes, the caller's section becomes a /section-align follow-up target (they own the migration). If no, add the API in THIS PR's Phase 2 — *then* the caller becomes a follow-up target.

**A1.10 INBOUND EF navigations.** Search other sections' entities for navigation properties pointing AT this section's entities:

```bash
git grep -nE 'public\s+<SectionEntity>\??\s+\w+\s*\{' src/Humans.Domain/Entities/
```

A nav from another section's entity to ours = a cross-domain coupling that breaks §6 if anyone `Include`s it.

**A1.11 OUTBOUND cross-section access (this section reaches OUT).** Grep this section's repository for reads of other sections' DbSets or `.Include` across into other-section navs:

```bash
git grep -nE '_(db|context|ctx)\.(<OtherSectionTableA>|<OtherSectionTableB>)' src/Humans.Infrastructure/Repositories/<Section>/
git grep -nE '\.Include\([^)]+\)' src/Humans.Infrastructure/Repositories/<Section>/
```

For each hit: apply the **boundary-fix protocol (consumer side)**. Does the other section expose a public service method that gives us what we need? If yes, switch (Phase 2). If no, document the API gap on their side, leave the current code in place, and flag their section as a follow-up /section-align target. **Do not add a method to their repo or service in this PR.**

**A1.12 Controller → DbContext.** Any controller injecting `HumansDbContext` violates §2a. Hard violation; always Phase 2 fix.

**A1.13 Migrations.** Hand-edited body or snapshot violates `architecture_no_hand_edited_migrations`. Intentional `migrationBuilder.Sql(...)` for triggers/seeds is allowed when named in the section doc.

**A1.14 Section invariant doc shape.** Present? name matches? follows `SECTION-TEMPLATE.md`? structural gaps?

**A1.15 Open prior-review items (PR mode).**
```bash
gh pr view <n> --repo peterdrier/Humans --json comments,reviews
gh api repos/peterdrier/Humans/pulls/<n>/comments
```
Also check `nobodies-collective/Humans` (`feedback_pr_review_both_repos`). List each unresolved comment with inline-comment ID.

### Axis 2 — internal cohesion

**A2.1 EF leakage from service layer.** Grep this section's services for EF types and query operators:

```bash
git grep -nE '(Microsoft\.EntityFrameworkCore|IQueryable|DbContext|DbSet|\.Include\(|\.AsNoTracking|\.ToListAsync|\.FirstOrDefaultAsync|\.SaveChangesAsync)' src/Humans.Application/Services/<Section>/
```

Hits in `using` statements, ctor params, or method bodies = violation. Doc comments (`<see cref>`) are OK. Add `<Section>Service_HasNoDbContextConstructorParameter` arch test if missing.

**A2.2 Caching placement.** Grep section's services + repos + controllers + view components:

```bash
git grep -nE '(IMemoryCache|MemoryCache|IDistributedCache|_cache\b)' src/Humans.Application/Services/<Section>/ src/Humans.Infrastructure/Repositories/<Section>/ src/Humans.Web/Controllers/<Section>*.cs src/Humans.Web/ViewComponents/<Section>*.cs
```

Caching belongs in the service layer **only** per §15 — either via a `Caching<Section>Service` decorator (Singleton, dict-backed) or service-internal `IMemoryCache` for short-TTL request acceleration. Never in repositories. Never in controllers. ViewComponents per `feedback_viewcomponent_no_cache`.

**A2.3 DI lifetimes.** Read `Extensions/Sections/<Section>SectionExtensions.cs`. Verify:
- Repository: Singleton, depends on `IDbContextFactory<HumansDbContext>`
- Service: Scoped (or Singleton if stateless + factory-based dependencies)
- Decorator (if present): Singleton
- Cross-interface re-exports (e.g. `IUserDataContributor` → existing service): bound to the registered concrete

**A2.4 Repository pattern.** `<Section>Repository`:
- `sealed`
- uses `IDbContextFactory<HumansDbContext>`
- has no Update/Delete if entity is append-only (§12)
- lives at `Infrastructure/Repositories/<Section>/`

**A2.5 Shared visual components — inventory by type, ViewComponent preferred.** Cross-page UI for this section's data can live as one of three things; this skill is opinionated about which:

| Type | Preference | Use when |
|------|-----------|----------|
| **ViewComponent** | ✓ preferred | Section data rendered on other sections' pages, needs DI to call section services, has parameter-driven invocation. |
| **TagHelper** | call out | Wrap small reusable markup primitives (badges, buttons, formatters). Often candidates for promotion to ViewComponent if they take section data. |
| **Partial view** (`_Partial.cshtml`) | call out | Acceptable for fragmenting one section's own view; **not** for cross-section reuse. Often candidates for promotion to ViewComponent if invoked across sections. |

Inventory all three categories for this section:
```bash
git grep -ln 'ViewComponent' src/Humans.Web/ViewComponents/<Section>*.cs src/Humans.Web/ViewComponents/*<Section>*.cs
git grep -ln 'ITagHelper\|TagHelper\b' src/Humans.Web/TagHelpers/ 2>/dev/null | grep -i <section>
find src/Humans.Web/Views -name '_*.cshtml' | xargs grep -l '<section-related-pattern>'
```

For each shared component, classify and decide: keep, convert TagHelper→ViewComponent, convert Partial→ViewComponent, or leave (intra-section partials). Inverse check: section pages with inline rendering of "should-be-a-VC" content (10+ lines of Razor stitching this section's data into another section's view).

Grep for ViewComponent invocations to verify reuse:
```bash
git grep -nE '<vc:<section-kebab>|Component\.InvokeAsync\("<Section>"' src/Humans.Web/Views/
```

**A2.5a Redundancy check against system-level shared components.** The biggest source of UI maintenance debt in this codebase is sections rolling their own renderer for things that already have a canonical shared component — especially anything that shows a user (avatar + name, profile cards, baseball-card popovers, role badges, email badges, search inputs). When a section invents its own version, every cross-cutting change (avatar shape, name format, link target, missing-user fallback, accessibility) has to be hunted down and applied N times.

First, build/refresh the catalog of system-level shared components the section might collide with. These live in `src/Humans.Web/ViewComponents/*.cs` and `src/Humans.Web/Views/Shared/_*.cshtml` and are owned by the platform, not any one section:

```bash
ls src/Humans.Web/ViewComponents/
ls src/Humans.Web/Views/Shared/Components/
find src/Humans.Web/Views/Shared -maxdepth 2 -name '_*.cshtml'
```

The user-display family — always check first, since it is where redundancy bites hardest:

| Concern | Canonical shared component |
|---------|---------------------------|
| User name + avatar inline (anywhere a user appears in a list, table cell, audit row) | `<vc:human>` / `HumanViewComponent` (`_HumanPopover` partial for the hover card) |
| Full profile / baseball card | `<vc:profile-card>` / `ProfileCardViewComponent` / `_ProfileCard` |
| Role pill / authorization indicator | `_RoleBadge.cshtml` / `_AuthorizationPill.cshtml` |
| Nobodies email badge | `<vc:nobodies-email-badge>` |
| User search box + results | `_HumanSearchInput.cshtml` / `_HumanSearchResults.cshtml` |
| User dropdown / signed-in menu | `_LoginPartial.cshtml` / `_AdminTopbarUserMenu.cshtml` |

(Refresh this table from the actual `ViewComponents/` and `Views/Shared/` listing each run — components get added.)

Then scan **the whole section's view + component surface** (not just shared-folder candidates from A2.5 — inline rendering inside the section's own page views is the more common form of this drift):

```bash
# Inline user displays — avatar + name combos, name links to /Humans/<id>, etc.
git grep -nE '(avatar|profile-pic|@user\.DisplayName|@Model\.DisplayName.*<img|asp-action="Detail".*Humans|/Humans/Detail/)' src/Humans.Web/Views/<Section>/ src/Humans.Web/ViewComponents/<Section>*.cs

# Hand-rolled role/auth pills
git grep -nE '(badge|pill).*role|role.*badge|class="[^"]*role[^"]*"' src/Humans.Web/Views/<Section>/

# Hand-rolled user lookups / searches
git grep -nE 'autocomplete.*user|user.*autocomplete|search.*human|human.*search' src/Humans.Web/Views/<Section>/
```

For each hit, decide:

| Finding | Disposition |
|---------|------------|
| Section renders user (avatar/name/link) inline with its own markup | **Phase 3 fix**: replace with `<vc:human user-id="..." />` or `@await Html.PartialAsync("_HumanPopover", ...)`. |
| Section has its own role/auth badge markup | **Phase 3 fix**: replace with `_RoleBadge` / `_AuthorizationPill`. |
| Section has its own user-search input + results panel | **Phase 3 fix**: replace with `_HumanSearchInput` / `_HumanSearchResults`. |
| Shared component is *almost* right but missing one parameter the section needs (e.g. compact mode, hide-link) | **Phase 2 fix on the shared component** (add the parameter), then Phase 3 callsite swap. Note: this means the shared component owner — typically the platform/admin shell — is the producer; flag as a follow-up if the parameter add is non-trivial. |
| Section's renderer is genuinely different in domain meaning (not a near-duplicate, just happens to also show a user) | Keep; record the distinction in the plan so future runs don't re-flag it. |

**Inverse check (don't only look for duplicates of user-display).** Anything that *should* be reusable across sections and currently isn't — section-local components rendering generic concerns (date formatters, money formatters, status pills, action menus, attachment lists) — is a Phase 3 candidate to **promote** into `Views/Shared/` or `ViewComponents/`. The signal: another section's view contains near-identical markup. Surface as a candidate; the actual promotion is judgment-call (don't preemptively over-share).

Output for each section pass: a `Redundancy candidates` subsection in the plan listing each duplicate find with disposition (Phase 3 swap / Phase 2 shared-component extension / keep + justify).

**A2.6 Interface budget + segregation + consolidation.** Count methods on each public interface. For each interface ≥10 methods, ensure `InterfaceMethodBudgetTests.Budgets` entry exists with current count. Beyond the budget number, flag:

- **Status-split methods that should be a single `GetAll()` + caller-side filter.** The canonical anti-pattern: `GetActiveUsers()`, `GetSuspendedUsers()`, `GetDeletedUsers()` instead of `GetUsers()` with callers writing `.Where(u => u.IsActive)`. At ~500-user scale most data fits in RAM; in-memory filtering is cheaper and clearer than predicate-pushed splits. Consolidate to `GetAll`/`GetByX` and let callers project. Architecture: the service holds the data; callers filter.
- **Exception sections** where DB predicate-push genuinely matters because the dataset is large or unbounded (AuditLog, EmailOutbox, file storage indexes, anything append-only with long retention). For those, predicate-on-DB methods stay on the interface; the section doc must justify the exception explicitly under `## Architecture`.
- **UI plumbing leaking through the main service interface** (methods that exist only to be called by *another service within the same section*). Example: `IAuditLogService.GetUserDisplayNamesAsync` called only by `AuditViewerService`. Move private, or move to the consumer.
- **Multiple interfaces carrying overlapping read shapes** (Service returns raw entity, ViewerService returns resolved record). Phase 3 candidate.
- **"Replace 2 methods with 1 broader bag-of-flags method"** — anti-pattern; count drops but surface grows.

**A2.7 Architecture test coverage — prefer generic over per-section.** Tests that apply to *every* section's same-shape class should be **one reflection-based test** over the matching interface, not a per-section copy. Prefer:

| Generic test (preferred) | Replaces per-section copies |
|--------------------------|------------------------------|
| `IRepository_Implementations_AreAllSealed` (reflect over all `IRepository` impls) | `FooRepository_IsSealed`, `BarRepository_IsSealed`, … |
| `Application_Services_TakeNoDbContext` (reflect over `IApplicationService` impls) | per-section `FooService_HasNoDbContextConstructorParameter` |
| `Application_Services_TakeNoIMemoryCache_UnlessRegistered` | per-section copies |
| `Repository_Implementations_LiveInInfrastructure` | per-section namespace tests |

Section-specific tests are reserved for **section-specific invariants** that don't generalize:
- Append-only repo shape (only some sections — `IAuditLogRepository_HasNoUpdateOrDeleteMethods`).
- Single-writer DbSet rule (`Only<Section>Repository_References_<DbSet>` — Roslyn or reflection scan).
- Domain-specific authorization handler shape.

Phase 0 inventory:
1. List section-specific tests in `<Section>ArchitectureTests.cs`.
2. For each, ask: does this generalize to all sections? If yes, propose moving it to a generic test in a single shared file (e.g. `tests/Humans.Application.Tests/Architecture/Rules/RepositorySealing.cs`).
3. List genuinely-section-specific invariants that need keeping or adding.

Missing tests (in either category) are Phase 2 adds. Per-section duplicates of generic patterns are Phase 3 prunes.

### Axis 3 — test focus

**A3.1 Test folder placement.** Canonical layout for this codebase: **one folder per section, top-level**, holding service + repository + viewmodel tests for that section's classes.

```
tests/Humans.Application.Tests/<Section>/
  <Section>ServiceTests.cs
  <SecondaryService>Tests.cs
  <Section>RepositoryTests.cs       # if the section's repo has unit tests
  ...
```

If the section's tests are split across `tests/Humans.Application.Tests/<Section>/` AND `tests/Humans.Application.Tests/Services/<Section>/` AND `tests/Humans.Application.Tests/Services/<Section>FooTests.cs` AND `tests/Humans.Application.Tests/Repositories/<Section>RepositoryTests.cs`, that's drift. Inventory the scatter, pick the top-level folder as the canonical home, and propose moves.

Tests that belong elsewhere by category (not section drift):
- `tests/Humans.Application.Tests/Architecture/<Section>ArchitectureTests.cs` — arch tests for this section (covered in Axis 2.7)
- `tests/Humans.Application.Tests/Authorization/<Section>AuthorizationHandlerTests.cs` — auth handler tests
- `tests/Humans.Integration.Tests/...` — cross-section by definition
- `tests/Humans.Web.Tests/Controllers/<Section>*Tests.cs` — controller tests live with the Web project
- `tests/Humans.Domain.Tests/Entities/<Entity>Tests.cs` — pure entity behavior

**A3.1a One test file per production class (1-to-1 rule).** For every production class in this section, there should be exactly one test file named `<ClassName>Tests.cs`:

| Production | Test file |
|------------|-----------|
| `<Section>Service.cs` | `<Section>ServiceTests.cs` |
| `<Section>Repository.cs` | `<Section>RepositoryTests.cs` |
| `<HelperClass>.cs` | `<HelperClass>Tests.cs` |

Two production classes sharing one test file (e.g. `AuditEvent` + `AuditEventTextualizer` → `AuditEventRenderTests.cs`) is drift — reviewers expect a 1-to-1 map. Exception: a single test file may exercise a tightly-coupled production pair if the pair is a documented helper-aggregator (rare; surface to user).

A production class with **no** test file at all (e.g. `<Section>Repository.cs` covered only by `<Section>ArchitectureTests`) is drift — the architecture test pins shape, not behavior. Add a unit test file or document why none is needed (e.g. "pure pass-through repository — coverage flows through `<Section>ServiceTests`").

**A3.2 Coverage map against section doc.** Read `docs/sections/<Section>.md`. Build a coverage map:
- Each **Invariant** bullet → at least one test asserting it holds
- Each **Negative Access Rule** → one test asserting the rejection
- Each **Trigger** → one test asserting the side effect fires (audit row, notification, cascade)
- Each public service method → at least one happy-path test (edge cases proportional to complexity, not exhaustive)

Missing coverage on invariants/negatives/triggers = Phase 2 add. Missing happy-path on a method = Phase 2 add if the method is non-trivial; skip if it's a one-line pass-through with the underlying call already tested.

**A3.3 Redundancy / over-testing flags.** Scan for:
- Near-duplicate tests with minor parameter variation that doesn't add value (shotgun parameter coverage where one or two cases suffice)
- Same scenario tested at multiple layers (unit + integration + controller) without rationale — pick the layer where the invariant lives
- Tests of framework or library behavior (EF Core, ASP.NET, NodaTime internals) rather than our code
- "No exception thrown" assertions with no meaningful invariant check
- Mock-graph spaghetti where the test asserts the mock was called rather than an observable outcome
- Tests coupled to implementation details (private method existence, internal call order) that break on innocent refactors

Each flag is a Phase 3 prune candidate. The goal is to reduce test maintenance overhead where test value is already captured by a more direct test.

**A3.4 Test-to-section ratio (sanity check).** Eyeball: section LOC vs test file count and test LOC.
```bash
find src/Humans.Application/Services/<Section>/ src/Humans.Infrastructure/Repositories/<Section>/ src/Humans.Domain/Entities/<SectionEntity>*.cs -name '*.cs' | xargs wc -l | tail -1
find tests -path '*<Section>*' -name '*.cs' | xargs wc -l | tail -1
```
Order-of-magnitude check only. Outliers worth investigating:
- Section LOC ~1k but 50+ test files → probably over-tested, Phase 3 prune candidate
- Section LOC ~7k but <10 test files → probably under-tested, Phase 2 invariants-and-negatives add

Not a budget. Judgment call surfaced to the user.

**A3.5 Brittleness signals.** Tests that:
- Reach into private state via reflection
- Depend on specific clock values rather than `IClock` / `FakeClock`
- Depend on test execution order
- Take >1s without an explicit `[Slow]` marker
- Hit real external services without sandbox/mock

Each is a Phase 3 candidate (prune or rewrite).

**A3.6 Mutation testing signal (Stryker.NET, optional but preferred).** The repo has Stryker.NET wired (`docs/testing/mutation-testing.md`). Use the **Profile-section probe pattern** to get section-targeted under/over-test signal without paying the cost of a full-suite run:

1. Look for an existing report at `local/stryker-runs/<section>/reports/mutation-report.json`. If recent (< 30 days) and matches current HEAD, use it. If stale or absent and the section is significant, surface to user: "Run Stryker against this section before Phase 3? (~N minutes)" with the section-narrowed config command:

   ```powershell
   Push-Location tests/Humans.Application.Tests
   dotnet tool run dotnet-stryker --config-file stryker-<section>-config.json `
     --output ..\..\local\stryker-runs\<section>
   Pop-Location
   ```

   If no `stryker-<section>-config.json` exists, propose creating one mirroring `stryker-profiles-config.json` with a narrowed `mutate` glob targeting this section's `src/` paths.

2. Run `scripts/analyze-test-utility.ps1` against the report to produce the `High-Confidence Test-Debt Candidates` queue:

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts/analyze-test-utility.ps1 `
     -Top 50 `
     -StrykerReport local/stryker-runs/<section>/reports/mutation-report.json
   ```

   Output lands under `local/test-utility/`. The Markdown report has two queues — `High-Confidence Test-Debt Candidates` is the better starting point for Phase 3 pruning.

3. Interpretation:
   - **Surviving mutants** = under-tested production code. Phase 2 adds a behavior test that kills the mutant.
   - **High utility-debt score + weak assertion + no policy value** = Phase 3 deletion candidate. Confirm by inspection — the score is heuristic.
   - Architecture ratchets and DI cycle checks may look "useless" by score but exist for explicit policy reasons — exclude from deletion.

Stryker is **a signal, not an authority**. Treat results as a triage queue. The skill never auto-deletes a test based on Stryker output alone.

### Plan file skeleton

```markdown
# Section Align — <target>
**Run started:** <date> | **Mode:** <mode> | **Worktree:** <path>
**Branch:** `<branch>` (off `<base>` @ `<sha>`)
**Canonical section name proposal:** <name>

## Axis 1 — boundary integrity
1. Name consistency: <findings>
2. Controller existence: <findings>
3. URL surface: <routes table with violation flags>
4. Views folder: <findings>
5. ViewModel placement: <findings>
6. Controller-base leak: <findings>
7. Extensions placement: <findings>
8. Role surface: <violations>
9. Inbound cross-section DB access (reads + writes): <list with protocol disposition>
10. Inbound EF navs: <list>
11. Outbound cross-section access: <list with protocol disposition>
12. Controller→DbContext: <list>
13. Migrations: <findings>
14. Invariant doc: <gaps>
15. Prior review items: <list with comment IDs>

## Axis 2 — internal cohesion
1. EF leakage from service: <findings>
2. Caching placement: <findings>
3. DI lifetimes: <findings>
4. Repository pattern: <findings>
5. ViewComponent presence + reuse: <findings>
5a. Redundancy vs system-level shared components: <duplicates with disposition; user-display family checked first>
6. Interface budget + segregation: <conflicts and trims>
7. Architecture test coverage: <missing tests>

## Axis 3 — test focus
1. Test folder placement: <canonical home + scatter list>
1a. 1-to-1 map (production class → test file): <table; missing tests; bundled-class drift>
2. Coverage map (invariants / negatives / triggers / methods): <coverage holes>
3. Redundancy flags: <list of prune candidates>
4. Test-to-section ratio: <LOC ratio + verdict>
5. Brittleness signals: <list>
6. Mutation signal (Stryker.NET): <report path + summary, or "no recent report; propose run">

## Test-attribute gate (per `docs/testing/mutation-testing.md`)
- Baseline as of last gate update: <count from doc>
- This run's net delta: +<added> / -<deleted> = <net>
- Justification if net > 0: <explain or refuse>

## Stop conditions tripped
<none or list with proposed decisions>

## Follow-up /section-align targets
<list of other sections this run surfaced as needing their own pass, with reason>

## Phase plan
- Phase 1 (axis 1 + axis 3 mechanical — create/move surfaces and test folders): <list>
- Phase 2 (axes 1, 2, 3; boundary-fix protocol; add missing arch tests + invariant tests): <list, blocking items flagged>
- Phase 3 (simplify / internal cohesion / prune redundant tests): ~<N> fixes
- Phase 4 (docs): <list>
```

Surface to user. If `--inventory-only`, stop. Otherwise: "Phase 0 complete. Proceed to Phase 1?"

---

## Phase 1 — Surface alignment (axis 1)

Sonnet subagents. Build must stay green after each step.

**Tooling discipline (applies to all Phase 1+):**
- **`/reforge`** before any rename, move, or interface change. The reforge skill returns the exact callers, references, and impact list so the subsequent Edit/Write is one-shot rather than iterative. Use it for: rename impact, find-references for a method, trace-call-chain when moving a route handler. **Always call /reforge before the Edit, never instead of testing after.**
- **Long-running processes run in the background, scoped to the phase.** At phase start, kick off `dotnet build` + `dotnet test` watchers in background via `Bash run_in_background: true`; tail with `BashOutput` between commits. At phase end, stop the background processes with `KillShell`. Pattern:
  ```bash
  # Phase start (background)
  dotnet watch --project Humans.slnx test -v quiet     # tail across commits
  ```
  Always inside the worktree. Don't leak background processes across phases or sessions.

### Inbound-link sweep — MANDATORY after every rename/move

`/reforge` finds C# symbol references but **misses string-typed references** (Razor tag helpers, allow-lists, route strings, doc links). Renames that ship without sweeping these break user-facing navigation silently — the build is green, the code is broken. This has bitten us twice (e.g. PR 505: `ApplicationController` → `GovernanceApplicationsController` left 6 `asp-controller="Application"` view links and a stale `MembershipRequiredFilter.ExemptControllers` entry).

**After every rename or move of a controller / service / view / route / role / config key, run the full sweep below before committing.** Both OLD and NEW name; OLD must return zero matches.

```bash
OLD=Application       # e.g. controller name minus 'Controller' suffix
NEW=GovernanceApplications

# 1. Razor tag-helper attributes (controllers, views)
git grep -nE "asp-controller=\"${OLD}\"" src/
git grep -nE "asp-(action|route|page)=\"[^\"]*${OLD}[^\"]*\"" src/

# 2. Url.Action / Url.RouteUrl / Url.Page / redirects (controller name as string arg)
git grep -nE "Url\.(Action|RouteUrl|Page)\([^)]*\"${OLD}\"" src/
git grep -nE "RedirectToAction\([^)]*\"${OLD}\"" src/
git grep -nE "RedirectToRoute\([^)]*\"${OLD}\"" src/

# 3. Allow-lists / filter sets keyed by controller name string
git grep -nE "\"${OLD}\"" src/Humans.Web/Authorization/ src/Humans.Web/Middleware/ src/Humans.Web/Filters/

# 4. ViewComponent invocations (tag-helper + Html.InvokeAsync forms)
git grep -nE "<vc:${OLD,,}|Component\.InvokeAsync\(\"${OLD}\"" src/

# 5. Test fixtures referencing route paths or controller names
git grep -nE "\"/${OLD}/" tests/
git grep -nE "\"${OLD}\"" tests/Humans.Web.Tests/ tests/Humans.Integration.Tests/

# 6. Config keys, localization resources, navigation menus, sitemaps
#    Whole-word match — bare ${OLD} would false-positive when NEW contains OLD as a substring
#    (e.g. OLD=Application, NEW=GovernanceApplications).
git grep -nE "\b${OLD}\b" src/Humans.Web/appsettings*.json src/Humans.Web/Resources/ src/Humans.Web/ViewComponents/*Nav*.cs

# 7. Docs that name the symbol
git grep -nE "\b${OLD}(Controller|Service|Repository)?\b" docs/ memory/
```

For service / interface renames, swap to grep `I${OLD}Service` / `${OLD}Service` and check DI registrations, decorators, and ctor params.
For entity / DbSet renames, additionally check `[Table(...)]` attributes, EF configuration files, and `OnModelCreating` — but per `architecture_no_drops_until_prod_verified`, prefer keeping the DB table name and renaming only the C# symbol.

**Verification gate:** before staging the rename commit, the OLD-name grep across all 7 buckets must return zero matches (or each remaining match has an explicit "intentional — historical reference" rationale captured in the commit message). String references don't fail CI; they fail in production navigation. "I'll catch the rest when CI fails" is not an acceptable plan.

If the sweep finds non-trivial inbound work (>10 fixes, or fixes that touch other sections' views), surface as a stop-condition decision: bundle into this PR vs. flag the consumer section as a follow-up /section-align target. Bundling is usually correct for view-link fixes (mechanical, reviewers expect renames to be complete); a follow-up is correct only when the consumer needs a deeper structural change.

**Create what's missing** (not only rename):
1. **Invariant doc** — `git mv docs/sections/<Old>.md docs/sections/<New>.md` if renamed.
2. **Controller** — if `<Section>Controller.cs` doesn't exist and routes live on foreign controllers, create it. Migrate route handlers off `BoardController` / `GoogleController` / etc. **Run the inbound-link sweep (above) before committing** — controller renames must update `asp-controller=`, `Url.Action`, `RedirectToAction`, allow-lists in auth filters/middleware, and test fixtures. `/reforge` does not catch these.
3. **Views folder** — if `Views/<Section>/` doesn't exist and page views live in `Views/Shared/<Section>.cshtml`, create it and move the page views. Keep genuine cross-section partials in Shared.
4. **ViewModels file** — extract section types out of `Models/AdminViewModels.cs` or other grab-bag files into `Models/<Section>ViewModels.cs`.
5. **Controller-base helpers** — move section-specific helpers off `HumansControllerBase` into `<Section>Controller` (or section-local helpers).
6. **Folders** — `git mv` Application, Infrastructure, Views if rename triggered.
7. **Namespaces** in lockstep.
8. **Symbols** — `I<Section>Service`, `<Section>Controller`, etc. via `/reforge` + bulk Edit.
9. **Route attributes** — class-level to `/<Section>/*`; admin under `/<Section>/Admin/*`.
10. **Role names** — `*Admin` suffix unless Phase 0 justified exception.
11. **Config keys** — update `appsettings*.json`.
12. **Extensions** — `git mv Extensions/<Section>*.cs Extensions/Sections/`.
13. **Test folder** — if the section's tests are scattered, `git mv` them into the canonical home picked in A3.1. Rename test files to `<ClassUnderTest>Tests.cs` 1-to-1 if they aren't already (one production class → one test file).
14. **Entities** — only if prefix is awkward post-rename; surface to user, default keep.
15. **DB tables** — leave alone (`architecture_no_drops_until_prod_verified`); use `[Table("legacy_name")]` if entity renamed.

After each rename/move group:
```bash
dotnet build Humans.slnx -v quiet && dotnet test Humans.slnx -v quiet
```

Commit every 3–5 logical units. Update PR body if routes changed. Thread-reply prior comments addressed (`feedback_codex_thread_replies`):
```bash
gh api repos/peterdrier/Humans/pulls/<n>/comments/<id>/replies -X POST -f body="Addressed in <sha> — ..."
```

Push at end of phase. Bot-review sub-loop until clean (`feedback_done_means_codex_clean`).

---

## Phase 2 — Fix arch violations + prior review items (both axes)

Sonnet by default; Opus for nuanced items.

**Boundary-fix protocol applies to every cross-section finding.** State the protocol disposition in the commit message ("Producer-side fix: added IFooService.GetByIdsAsync for the X consumer; flagging X for follow-up /section-align").

Axis 1 items:
- **Inbound cross-section DB access (producer-side)** — ensure our public API satisfies the need. Add the method if missing (budget bump if interface ≥10, add `InterfaceMethodBudgetTests.Budgets` entry). The caller's section becomes a follow-up /section-align target; do not touch their call site in this PR unless trivial (one or two lines).
- **Outbound cross-section access (consumer-side)** — if the supplier section has the public API, switch to it. If not, document the gap, leave the code, flag their section as follow-up. Never reach into another section's repository or DbContext.
- **Controller-DI-DbContext** — extract service+repo layer.
- **Hand-edited migration** — `dotnet ef migrations remove` + redo `dotnet ef migrations add`. Verify snapshot is byte-for-byte EF output.
- **URL aliases / cross-section URL exposure** — remove; surface downstream consumer risk.
- **Prior review threads** — fix or reply with reasoning via `gh api .../comments/<id>/replies`; never top-level.

Axis 2 items:
- **EF leak in service layer** — extract repository methods. Add the missing arch test.
- **Caching in repo or controller** — move into service or its caching decorator.
- **DI lifetime mismatch** — fix in `<Section>SectionExtensions`.
- **Interface budget** — add `InterfaceMethodBudgetTests.Budgets` entry with exact count; run the test.
- **Architecture test gaps** — add tests catching the section's worst violations (no controller→DbContext, no service→DbContext, only repo touches the DbSet, append-only repo shape).
- **Docstring vs reality** — align to what's enforced; no DB triggers unless ConsentRecord-grade reason (`feedback_db_enforcement_minimal`).

Axis 3 items:
- **Missing coverage** — for each invariant / Negative Access Rule / Trigger from A3.2 without a test, add one. Keep terse: one focused test per item.
- **Missing 1-to-1 test file** — if `<Section>Service` exists but `<Section>ServiceTests.cs` doesn't, create it (even if just stub + happy path). 1-to-1 helps reviewers find the test surface for a given class fast.
- **Bundled-class test files** — split `AuditEventRenderTests.cs` (covers 2 classes) into per-class files unless the pair is a documented helper-aggregator.
- **Surviving Stryker mutants** (if A3.6 report available) — add behavior tests that kill each high-value surviving mutant. Don't try to kill them all; focus on mutants that map to invariants/negatives/triggers in the section doc.
- **Brittle tests** that are easy to rewrite to assert observable behavior instead of mock-graph internals — rewrite as part of the fix that touches them.

Build + tests green. Push at end of phase. Bot-review sub-loop until clean.

---

## Phase 3 — /simplify pass (internal cohesion)

Opus. Volume scales to section LOC (`feedback_simplify_scope_to_section_size`): ~6–10 fixes for 7k LOC, ~2–3 for 1k LOC.

Common targets:
- Duplicated logic across controllers.
- LINQ-on-EF in services (push to thick repos, `feedback_no_linq_at_db_layer`).
- Unnecessary `.ToList()`.
- `Cached*` names (`feedback_caching_transparent`).
- Extensions on owned types (`feedback_no_extensions_for_owned_classes`).
- View-component caches (`feedback_viewcomponent_no_cache`).
- **Interface trimming** — methods on the section's main service that exist only for one in-section consumer; move private or to the consumer.
- **Read-shape consolidation** — if Service and ViewerService both expose the same read names with different return shapes, move all UI reads to ViewerService and trim Service.
- **Swap reinvented UI for shared components (A2.5a)** — replace section-local user/profile/role/search markup with the canonical `<vc:human>` / `<vc:profile-card>` / `_RoleBadge` / `_HumanSearchInput` etc. State the swap in the commit message ("Replace inline user-row markup in `Views/<Section>/Index.cshtml` with `<vc:human>` — collapses N call sites onto the shared component"). If a shared component needs a small parameter add to fit the section's case, do the parameter add as a Phase 2 producer-side fix and the call-site swap here.
- **Test pruning** — delete redundant/over-tested cases flagged in A3.3, brittleness flagged in A3.5, and high-confidence test-debt candidates from the Stryker utility report (A3.6). Prefer deletion over refactoring: if a test isn't asserting something a reviewer would catch in code review, it's pulling its weight only if its absence would let a real regression slip. State the deletion rationale in the commit message ("redundant with X test; mock-graph assertion not behavior; Stryker survived no mutant"). Respect the test-attribute gate (`docs/testing/mutation-testing.md`): the net delta should trend down across Phase 3 commits.

Push at end of phase. Bot-review sub-loop until clean.

---

## Phase 4 — Doc polish

Opus.

- **Invariant doc** — verify all `SECTION-TEMPLATE.md` sections. Correct any false claims surfaced in Phase 0 (e.g., "only repo writes the table" claims that turned out false until Phase 2 fixed them — restore once truly true).
- **Cross-Section Dependencies** — record any §6 violations still pending an upstream API (consumer-side gaps not fixable in this PR), naming the supplier section as the follow-up target.
- **`docs/architecture/dependency-graph.md`** — update the section's inbound/outbound edges to reflect actual state after Phase 2. Add new dependencies introduced; remove ones eliminated by the cross-section fixes. The dependency graph is the at-a-glance map reviewers consult; if it's stale, alignment work is invisible.
- **Feature specs** (`docs/features/*.md`) — match implementation; rename if section name changed.
- **`data-model.md`** — update owned-entity index.
- **`todos.md`** — per `feedback_todos_update_after_commits`.
- **`maintenance-log.md`** — if a recurring task ran.
- **About page** — if dependencies changed.
- **`/freshness-sweep`** — if catalog covers touched docs.

Push. Final bot-review loop until merge-ready. Report: PR/branch URL, commits per phase, **follow-up /section-align targets**, one-line status. Do not offer `/schedule` follow-ups (`feedback_no_schedule_offers`).

**Re-run /section-align at the end** (still in the same worktree) as the closing audit. If the second run finds anything beyond the documented follow-up targets, that's leftover Phase 1–3 work — go back and finish before merging. Ideal end-state: the closing run returns "clean except for `<list of pending suppliers>`."

---

## Loop discipline

**"Done" = bot-review-clean**, not pushed+green (`feedback_done_means_codex_clean`).

- Push every 3–5 commits within a phase; trigger-review push at phase boundary.
- Phase 0 plan file: commit AND push without asking — it's a checkpoint artifact the user reads in the browser to decide whether to greenlight Phase 1. After pushing, give the user the GitHub URL for the plan file (`https://github.com/peterdrier/Humans/blob/<branch>/docs/plans/<file>.md`) so they can review it inline.
- Subsequent phase pushes: push without asking (standing approval). Push at each phase boundary so bot review fires.
- Never push to `main`; never `--no-verify`.
- **Renames/moves require the inbound-link sweep (see Phase 1) before commit.** Green build ≠ correct rename. Skipping the sweep has broken navigation in shipped PRs.
- Each sub-loop: push → wait for Codex + Claude → thread-reply each finding (fix or reject with reasoning) → commit + push → repeat until clean.

---

## Resume behavior

Plan files are durable, committed artifacts under `docs/plans/` — one per run, date-prefixed. On startup, glob `docs/plans/*-section-align-<target>.md`. If any exists for the current target, take the newest, read it, find last completed phase, ask: "Found plan from <date>. Last complete: <phase>. Resume or start fresh?" Update the plan file at end of each phase. Commit + push the plan file with Phase 0's commit so the user can review it via the PR/branch URL.

---

## Rule references

- `architecture_no_admin_url_section` · `feedback_no_url_aliases` · `feedback_admin_superset`
- `architecture_no_hand_edited_migrations` · `architecture_no_drops_until_prod_verified`
- `architecture_interface_budget_ratchet_down_only` · `feedback_db_enforcement_minimal`
- `feedback_no_linq_at_db_layer` · `feedback_caching_transparent` · `feedback_no_extensions_for_owned_classes` · `feedback_viewcomponent_no_cache`
- `feedback_codex_thread_replies` · `feedback_pr_review_both_repos` · `feedback_done_means_codex_clean`
- `feedback_simplify_scope_to_section_size` · `feedback_dotnet_verbosity` · `feedback_always_use_worktree`
- `feedback_no_direct_to_main` · `feedback_no_schedule_offers` · `feedback_todos_update_after_commits`
- Design Rule §2a (no controller→DbContext) · §2c (no cross-service table access) · §6 (no cross-domain `.Include`) · §8 (table ownership) · §11 (auth) · §12 (append-only) · §15 (caching pattern) — `docs/architecture/design-rules.md`
- `docs/sections/SECTION-TEMPLATE.md`
- `docs/testing/mutation-testing.md` (Stryker.NET setup, test-attribute gate)
- `scripts/analyze-test-utility.ps1` (Stryker → high-confidence test-debt queue)
