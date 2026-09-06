# Search — Target Shape

Derived fresh each section-doctor run, before any scan. History rows at the bottom.

## 1. What the section does

One page. A signed-in member types a few characters and gets back everything in the app
whose own name (or, for people, public profile text) contains them — people, teams, camps,
shift rotas and, when events are switched on, events — grouped by kind, best matches first
within each group, with a row of chips to narrow the page to one kind. Pasting an id instead
of a name jumps straight to that thing. The page finds; it never decides who may open what
it found — that is each destination page's job.

The section owns nothing: no tables, no scores of its own, no row markup. It asks five other
sections the same question and lays their answers out.

## 2. The shapes

| Question shape | Asked of | Answered by |
|---|---|---|
| "Which of your entities match this text or id, and how well?" | Users, Teams, Camps, Shifts, Events | each section's `SearchAsync` on its read interface — four return `(Id, Name, Score)`, Users returns the match context too |
| "Draw one of your rows for this id" | the same five | each section's `<vc:…-search-result>` view component |
| "Order this bucket for display" | the controller | score desc then name asc; people by the shared relevance order |
| "Should this bucket exist at all?" | the orchestrator and the view | query under 2 chars → nothing; a filter chip → one bucket; `Features:Events` off → no Events bucket, chip or heading |

Vocabulary: `SearchResultType` (the five kinds), `GlobalSearchResult` (a key and an
ordering handle, nothing displayable), `GlobalSearchResults` (five buckets and the echoed
query).

## 3. Structure

The shapes imply exactly today's layout, and nothing more:

- **One controller, one action** (`GET /Search?q=&filter=`), which sorts and assembles
  the view-model — the only logic it holds.
- **One internal orchestrator** behind `ISearchService`, one method: trim, gate, fan out,
  carry each section's key/score through untouched.
- **One results record, one view-model, one view plus one partial** that switches a row
  onto its owner's component.
- **A member-nav item, a resource marker with six resx files, `_ViewImports`** binding
  five publishers' tag helpers.
- **No `Contracts/` content, no `Data/`, no decorator, no jobs.**

## 4. Invariants

- Under 2 characters after trim: no section is called, every bucket is empty.
- A filter calls one section and skips four — skipped means not called, not called-and-discarded.
- Text queries reach only each section's public-visibility surface, and people are matched
  on `PersonSearchFields.PublicAll` — a constant, not a role branch. No viewer, admin
  included, gets more through this page.
- An id query bypasses text-visibility filters in Teams, Camps and Shifts; in Users it
  still requires a non-rejected profile. Search returns it; the destination decides.
- Every match is returned — no cap in this section, an effectively-unbounded cap passed down.
- Scores are the owning section's; this section never rescores, and display order lives in
  the controller, never the service.
- `Features:Events` off means the Events section is never called and the chip and heading
  never render.
- A service exception renders the page shell with the query preserved, never a 500; a
  cancelled request propagates.
- Anonymous cannot reach `/Search`; the nav item shows to any authenticated user, profile
  or not.

## 5. Seams

- **Privileged search** (nobodies-collective/Humans#693): if it comes, it is a per-bucket
  scope, not a global enum — the field mask constant and the per-section filter calls are
  where it would plug in. Reserved, not built.
- **Rota reach from a past event** and **`/Camps/{slug}` season gating**: both ruled
  destination-page matters (nobodies-collective/Humans#993, #998, closed as designed).
  Nothing here changes for them.

## 6. Deliberately not done

- **No Search-level cache.** Four buckets are already served from their owners' warm
  snapshots; a cache here caches a cache and duplicates invalidation.
- **No display fields on `GlobalSearchResult`.** Title/subtitle/url were removed on purpose
  (nobodies-collective/Humans#1062); the owner's component renders the row.
- **No cross-modal pull-ins**, no unified cross-type ranking, no autocomplete.
- **No `Humans.Search.Contracts` project** — nothing outside the section names a Search type.
- **No result cap** — one was tried and hid people.
- **No visibility check on the id path** — removed under the nobodies-collective/Humans#985 ruling; re-adding it is a regression.
- **No dedicated view-model per bucket** and no per-bucket partial — one record, one partial
  switching on `Type`.

## Load-bearing weirdness

- **Whole-project references to the five publishers**, not just their leaves: `@addTagHelper`
  binds only against a referenced assembly. An orchestrator referencing what it orchestrates
  is expected; only cycles are forbidden.
- **`_ViewImports` carries one `@addTagHelper` per publisher.** Dropping one ships that
  bucket as inert literal markup on a green build; the integration render tests are the guard.
- **Two localizers in the view**: the section's own 12 keys, plus `SharedResource` for
  `Search_MinChars`, which `/Profile/Search` also renders — carving it would split that
  page's message set.
- **`SearchResource` sits at the project root in `namespace Humans.Search`** — the manifest
  name derives from the marker's namespace; moving it silently breaks every lookup.
- **`ISearchService` is internal and exists for two reasons**: it carries the `IOrchestrator`
  marker the analyzers police, and the controller tests substitute it.
- **Shifts is the only DB-backed bucket** (Postgres `ILike` in the Shifts repository); the
  other four match in memory in their caching decorators.
- **`Features:Events` is read twice** — by the service (skip the call) and by the view (hide
  the chip and heading). The view-model does not carry the flag.
- **The Events gate reads `_eventsFeatureEnabled && onlyType is null or Event`** — a
  pattern combinator, so it parses as `flag && (null or Event)`; the feature-off test pins it.
- **`InternalsVisibleTo("DynamicProxyGenAssembly2")`** lets NSubstitute proxy the internal
  service interface in the section's own tests.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-06 | First doctor pass — the code is at target; the docs are not: the Shifts read-split, the nav's move to `ISectionNav`, and two rulings that closed as designed are all recorded as still pending, and every line-number citation had drifted | peterdrier/Humans#pending |
