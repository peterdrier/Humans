# Guide — Health

Last assessed: 2026-08-17 @ acb86a911 (section-doctor, `--section=Guide`)

## Scorecard

| Axis | State |
|---|---|
| Reforge (section) | 8 — the lowest of any section. Only items: `Wrap` (CC 20, 55 LOC) and the legitimate `GuideRoleResolver <- ITeamServiceRead`. No structural work is warranted |
| Tests | 95 in `Humans.Guide.Tests` (86 before run) + 3 render tests in `Humans.Integration.Tests`, all sub-second. This run added four content-pinning tests over the real `docs/guide/` files, the section's first controller tests, and the first coverage of the Refresh authorization rule. Remaining gap: the TTL floor clamp (`Math.Max(1, …)`) is unobservable through `IMemoryCache` |
| Docs vs code | Invariants doc is strong and specific — it was right where the code was wrong. Three drift fixes this run: dead `src/Humans.Infrastructure/**` freshness triggers (project deleted at G5 lane 5b-6) and the same claim in the feature spec, Contracts README and csproj comments; plus `IGuideContentService.RefreshAllAsync` documented as *evicting* the cache when it overwrites and deliberately retains stale entries |
| Comments / slop | Clean and constraint-stating. One auto-named local (`settings1`) inlined this run |
| GUI / nav | Sidebar groups + breadcrumb are sound. `NotFound`/`Unavailable` are dead ends — they sit at `offset-md-3` where the sidebar would be, but never render one |
| Translations | None. Every UI string in the four views is hardcoded English — consistent with the section, since the content it wraps is English-only markdown from GitHub |
| Arch conformance | Clean. Everything `internal`, no tables, no repository, no DbContext, no grandfathers, no obsolete. Contracts folder deliberately empty. `IMemoryCache` injection is an allowlisted §15 deviation with a recorded rationale |

## Ideal shape

The section's *shape* is already right: a stateless read section with no tables, no repository,
and a service that is the cache. A rewrite would keep all of that.

What a rewrite would not keep is **the role model expressed three times in three vocabularies**.
A role block is written as heading prose (`## As a Board member / Admin (Camp Admin)`), then
re-encoded as HTML attributes (`data-guide-role` / `data-guide-roles`) by the preprocessor, then
re-parsed out of rendered HTML by a regex in `GuideFilter`. Round-tripping access-control data
through an HTML attribute and back is what makes the two defects found this run possible: a
heading the regex does not recognise is silently *unfiltered* rather than rejected, and a `<div>`
Markdig emits inside a block silently truncates it. Both fail open, and both were invisible
until real content was run through the pipeline.

The from-scratch design filters **markdown blocks before rendering**: cache the markdown, split
on `##` headings, drop the blocks this viewer cannot see, then render what remains. One
representation of a role, no attributes, no regex over HTML, and no way for content shape to
defeat the filter. The reason it is not built that way is that the cache holds *rendered* HTML
per file, so filtering has to happen after rendering. At 28 small files and ~500 users, rendering
per request is affordable and that constraint is not real.

That is the one substantial move available here, and it is a genuine rewrite of the pipeline —
ranked below, not taken this run.

## Opportunities (ranked by value)

1. **Filter markdown, not HTML** (the ideal-shape move above). Removes `GuideFilter`'s regex and
   the `data-guide-*` attributes entirely, and closes the fail-open class structurally rather
   than by the pinning tests added this run. Needs Peter: it changes the caching unit from
   rendered HTML to markdown.
2. `Wrap` is the section's only complexity finding (CC 20, 55 LOC) — a hand-rolled line scanner
   with an `inBlock` flag. Subsumed by opportunity 1; not worth refactoring on its own first.
3. `NotFound` / `Unavailable` render no sidebar, so a mistyped guide URL strands the reader with
   one link home. Both reserve the sidebar's column width and then leave it empty.
4. nobodies-collective/Humans#1035 — `(Camp Coordinator)` parentheticals resolve to nothing
   because the display name is not in `GuideRolePrivilegeMap`. Adjacent to the resolver fix made
   this run, but it is a privilege decision, not a drift repair.

## History

| Date | Reforge | Tests | Outcome | PR |
|---|---|---|---|---|
| 2026-08-17 | 8 | 89 | Feedback.md admin block was leaking to anonymous (unwrapped heading) — fixed + pinned; resolver's probe list derived from the privilege map, restoring Events/Store Admin visibility; three duplicated stem lookups folded into `GuideFiles.TryCanonical`; dead `Humans.Infrastructure` doc paths corrected | peterdrier/Humans#1354 |
