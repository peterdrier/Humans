# Search — Target Shape

Derived fresh each section-doctor run, before any scan. History rows at the bottom.

## 1. What the section does

One page, reached from the magnifying-glass button in the header. A signed-in member types a
few characters and gets back everything in the app whose own name (or, for people, public
profile text) contains them — people, teams, camps, shift rotas and, when events are switched
on, events — grouped by kind, best matches first within each group, with a row of chips to
narrow the page to one kind. Pasting an id instead of a name jumps straight to that thing. The
page finds; it never decides who may open what it found — that is each destination page's job.

The section owns nothing: no tables, no scores, no row markup, and it names no other section.
Every section that wants to be found contributes a category; this section asks each category
the same question and lays the answers out.

## 2. The shapes

| Question shape | Asked of | Answered by |
|---|---|---|
| "Which of your things match this text or id, and how well?" | every contributed category | the category's own search delegate, returning pre-scored hits (id, sort key, score, optional match context) |
| "Draw one of your rows for this hit" | the same category | the row view component the category declares, invoked by type |
| "Are you switched on, and what are you called?" | the same category | its enabled check and its localized label |
| "Order the page for display" | the controller | categories by weight then key; hits by score, then sort key |
| "Which buckets get searched?" | the orchestrator | query under 2 characters: none; a filter chip: only that one; a disabled category: never listed |

## 3. Structure

The shapes imply today's layout:

- **One controller, one action** (`GET /Search?q=&filter=`), which sorts and assembles the
  view-model and turns a failed search into a usable page.
- **One internal orchestrator** behind `ISearchService`, one method: trim, gate, fan out over
  `ISearchContributor` categories, carry each category's hits through untouched.
- **One results record pair, one view-model, one view** that hands each row to its owner's
  component through `Component.InvokeAsync`.
- **The header nav item** (an icon-only `ISectionNav` entry), a resource marker with one resx
  per supported culture, `_ViewImports`.
- **No `Contracts/` content, no `Data/`, no decorator, no jobs**, and a single project
  reference: `Humans.Base`.

## 4. Invariants

- A query under 2 characters after trim lists every enabled category with an empty bucket and
  calls no category's search — `src/Sections/Humans.Search/Services/SearchService.cs:26`.
- A filter that names an enabled category searches only that category; the others are listed
  but not called. An unknown or disabled key searches everything —
  `src/Sections/Humans.Search/Services/SearchService.cs:21`.
- A disabled category is neither listed nor searched, filter or not —
  `src/Sections/Humans.Search/Services/SearchService.cs:19`.
- This section never rescores: each category's hits pass through as returned —
  `src/Sections/Humans.Search/Services/SearchService.cs:33`.
- Display order is the controller's: categories by weight then ordinal key, hits by score
  descending then sort key case-insensitive — `src/Sections/Humans.Search/Controllers/SearchController.cs:51`.
- Text queries reach only each owner's public surface; people are matched on
  `PersonSearchFields.PublicAll`, a constant, so no viewer, admin included, gets more here —
  `src/Sections/Humans.Users/Section.cs:424`.
- Every match is returned: each contributor passes `int.MaxValue` —
  `src/Sections/Humans.Teams/Section.cs:228`.
- An id query skips the text-visibility filters (Teams shown here; Camps and Shifts the same),
  and for people still requires a non-rejected profile —
  `src/Sections/Humans.Teams/Services/CachingTeamService.cs:173`,
  `src/Sections/Humans.Users/Data/CachingUserService.cs:238`.
- `Features:Events` off removes the Events category end to end —
  `src/Sections/Humans.Events/Section.cs:163`.
- A failed search renders the page with query and filter kept and an "unavailable" notice, never
  zero-count chips or "no results" — `src/Sections/Humans.Search/Controllers/SearchController.cs:41`;
  a cancelled request propagates — `src/Sections/Humans.Search/Controllers/SearchController.cs:31`.
- Anonymous visitors cannot reach `/Search` — `src/Sections/Humans.Search/Controllers/SearchController.cs:12`;
  the nav item shows to any signed-in user, profile or not — `src/Sections/Humans.Search/Section.cs:23`.

## 5. Seams

- **Privileged search** (nobodies-collective/Humans#693): if it comes, it is a per-category
  scope, not a role branch in this section. Reserved, not built.

## 6. Deliberately not done

- **No Search-level cache.** Every category but Shifts is served from its owner's warm
  snapshot; a cache here caches a cache and duplicates invalidation.
- **No display fields on `SearchHit`.** The owner's component renders the row.
- **No cross-type ranking, no cross-modal pull-ins, no autocomplete.**
- **No result cap** — one was tried and hid people.
- **No visibility check on the id path** — removed under the nobodies-collective/Humans#985
  ruling; the destination page decides.
- **No `Humans.Search.Contracts` project** — nothing outside the section names a Search type.

## Load-bearing weirdness

- **`SearchResource` sits at the project root in `namespace Humans.Search`** — the manifest
  name derives from the marker's namespace; moving it makes every string fall back to its key.
- **`ISearchService` is internal and exists for two reasons**: it carries the `IOrchestrator`
  marker the analyzers police, and the controller tests substitute it.
- **`SearchService` takes `IServiceProvider`** only to hand it to the contributed delegates.
- **The controller keeps the raw query when it is too short**, so the box shows what was typed.
- **Shifts is the only database-backed category**; the others match in memory in their owners'
  caching services.
- **`InternalsVisibleTo("DynamicProxyGenAssembly2")`** lets NSubstitute proxy the internal
  service interface in the section's own tests.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-06 | First doctor pass — the code is at target; the docs are not: the Shifts read-split, the nav's move to `ISectionNav`, and two rulings that closed as designed are all recorded as still pending, and every line-number citation had drifted | peterdrier/Humans#1602 |
| 2 | 2026-11-26 | Re-doctor after the contributor-seam rebuild: target regenerated; a failed search now always shows the unavailable notice; the two-character minimum stated once; run-on test names renamed; doc drift fixed | peterdrier/Humans#2631 |
