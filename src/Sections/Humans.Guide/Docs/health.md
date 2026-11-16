# Guide — Health

## 1. What the section does

Shows the volunteer handbook inside the app. Someone opens the Guide and reads a page about the
part of the app they use; the page is the one that lives in the repository's guide folder, with
the paragraphs written for roles they do not hold removed before it reaches them. The page
furniture around it (titles, sidebar, error pages) follows the reader's language; the handbook
text itself stays as written. Nobody writes or edits a guide page here: that happens by pull
request, and an admin can tell the running app to pick up a merged change without a redeploy.
Links inside a page that point at another guide page or at a page of the app navigate in-app;
everything else opens away from the app.

## 2. The shapes

| Shape | The question it answers | Where it is answered |
|---|---|---|
| S1 — read a page | "What does the guide say about X, for me?" | `GET /Guide`, `GET /Guide/{name}`: one pipeline; the home page differs only in which stem it fixes and which view wraps it |
| S2 — re-read from GitHub | "Pick up the guide change that just merged." | `POST /Guide/Refresh`, admin only |
| S3 — which sections have help | "Does this section have a guide page written for it?" | `ISectionAnnotations` on `Section`, read by `/Debug/Sections` |
| S4 — is GitHub reachable | "Can the app still reach GitHub?" | `ISectionHealthChecks` on `Section`, registering the `github` check |

S1 is the section. S2 is S1's cache primed on purpose instead of on demand: the same
`PopulateAsync`. S3 and S4 are one-method seams that carry no request.

Inside S1 the content passes through these steps, in order. Cached, once per file: **fetch**
(Base's `IGuideContentSource`) → **segment** (each `## As a …` block becomes a `GuideSegment`
carrying its role and its parenthetical's privilege tokens; everything else is an unscoped
segment) → **cache**. Then per request: **filter** (drop the segments this reader can't see and
join the rest) → **render** (Markdig) → **rewrite** (sibling `.md` links become `/Guide/<stem>`,
app paths in inline code become links, everything else opens in a new tab against GitHub).

The filter runs before Markdig, which keeps the role model in one place: a reader's render only
ever sees content they may read, and no role metadata survives into the HTML.

## 3. Structure

```
Humans.Guide/
  Section.cs                 DI, config keys, S3, S4
  GuideResource(.*.resx)     sidebar groups, page names, nav label
  Health/                    the check S4 registers
  Controllers/               S1, S2: route, resolve stem, pick view, build the sidebar
  Services/
    GuideFiles                the stem set: routing, fetching, annotations and the sidebar read it
    IGuideContentService      fetch + segment + cache + per-request filter/render, the facade
    GuideDocument             the cached unit: a file as ordered role-scoped segments
    GuideSegmenter            markdown → segments, headings found by Markdig's block parse
    IGuideRenderer            Markdig → rewrite
    GuideHtmlPostprocessor    rewrite, over AngleSharp's parse of the HTML
    GuideFilter               per-request segment selection (static: no state)
    IGuideRoleResolver        claims + Users + Teams + Camps → GuideRoleContext
    GuideRolePrivilegeMap     parenthetical title → privilege token, backed by Auth's RoleCatalog
  Models/                     sidebar + page view model
  Views/                      the section's pages and its layout partial
  Contracts/                  empty by design
```

No repository, no `DbContext`, no migrations. The layout above is the layout today; the
derivation matches because each step of the pipeline has exactly one home.

## 4. Invariants

1. A stem outside `GuideFiles.All` is a 404, in any casing
   (`src/Sections/Humans.Guide/Controllers/GuideController.cs:48`; `src/Sections/Humans.Guide/Services/GuideFiles.cs:59`).
2. Everything before the first `## As a …` heading, and everything from the next non-`As a`
   `##` heading onward, is an unscoped segment visible to everyone; only role-scoped segments
   are ever dropped (`src/Sections/Humans.Guide/Services/GuideSegmenter.cs:65`;
   `src/Sections/Humans.Guide/Services/GuideFilter.cs:29`).
3. Only a line Markdig's block parse calls a level-2 heading can open or close a role segment, so
   `##` sample text inside fenced or indented code, HTML comments or raw HTML neither opens a
   block nor ends the one around it (`src/Sections/Humans.Guide/Services/GuideSegmenter.cs:34`, `:46`;
   `GuideSegmenterTests.Segment_FencedH2InsideRoleBlock_DoesNotEndTheBlock`,
   `GuideSegmenterTests.Segment_HeadingTextInsideHtmlBlock_DoesNotExposeRestrictedTail`).
4. Anonymous sees Volunteer blocks and nothing else
   (`src/Sections/Humans.Guide/Services/GuideRoleResolver.cs:18`; `src/Sections/Humans.Guide/Services/GuideFilter.cs:46`).
5. A team coordinator sees every Coordinator block; a camp lead sees only the Coordinator blocks
   whose parenthetical names `Camp Lead` (`src/Sections/Humans.Guide/Services/GuideFilter.cs:54`, `:57`).
6. Within one file, anyone who can see a Board/Admin block can see every Coordinator block in that
   file, including a domain admin who reached the Board/Admin block through its parenthetical
   (`src/Sections/Humans.Guide/Services/GuideFilter.cs:15`, `:39`).
7. Refresh is admin-only and reading is anonymous
   (`src/Sections/Humans.Guide/Controllers/GuideController.cs:20`, `:25`, `:30`;
   `GuideArchitectureTests.RefreshIsAdminOnly_AndReadingIsAnonymous`).
8. A cached stem is served without touching GitHub, and only a successful fetch overwrites a
   cache entry, so a failed refresh keeps the copy already cached. A stem that is not cached and
   whose fetch fails is a 503, however many other stems are cached
   (`src/Sections/Humans.Guide/Services/GuideContentService.cs:59`, `:112`, `:71`;
   `src/Sections/Humans.Guide/Controllers/GuideController.cs:61`).
9. A page that cannot be rendered fails the same way as one that cannot be fetched: the 503 view,
   never a raw 500, and the cached segments stay
   (`src/Sections/Humans.Guide/Services/GuideContentService.cs:41`;
   `GuideContentServiceTests.GetPageAsync_RenderTimesOut_SurfacesAsUnavailableNotAnUnhandledThrow`).
10. A cancelled request stops a fetch without publishing a partial refresh: the per-file catch
    lets cancellation through before any cache write
    (`src/Sections/Humans.Guide/Services/GuideContentService.cs:96`;
    `GuideContentServiceTests.RefreshAllAsync_CancelledFetch_StopsWithoutReplacingWarmCache`).
11. `guide:<stem>` cache entries are written by `GuideContentService` and nothing else
    (`src/Sections/Humans.Guide/Services/GuideContentService.cs:18`).
12. The stem set and the markdown folder match exactly in both directions
    (`GuideArchitectureTests.EveryGuideMarkdownFileIsRegistered_AndEveryRegisteredStemExists`).
13. Every `## As a …` heading in the shipped corpus opens a scoped segment, and every
    parenthetical in it resolves to a privilege token: the two ways a block fails open or fails
    silent (`GuideSegmenterTests.Segment_EveryRoleHeadingInShippedContent_OpensAScopedSegment`,
    `GuideArchitectureTests.EveryRoleHeadingParentheticalResolvesToAPrivilege`).
14. Segmenting is lossless: a file's segments rejoin to the file exactly, so a reader who can see
    everything gets the file as written
    (`GuideSegmenterTests.Segment_EveryShippedFile_RejoinsToTheOriginal`,
    `GuideShippedContentFilterTests.Admin_ReceivesEveryFileWholeAndUnaltered`).
15. Every sidebar entry has a localized name: a page name comes from a shared section label when
    one exists and from `Guide_Page_<stem>` otherwise
    (`src/Sections/Humans.Guide/Controllers/GuideController.cs:117`;
    `GuideControllerTests.Document_LocalizesNavigationWhilePreservingContentAndRoutes`).

## 5. Seams

- **An anonymous way in.** The read routes are `[AllowAnonymous]` and the filter has an anonymous
  branch, but since the member top-nav link was dropped a signed-out reader reaches `/Guide` only
  by URL; signed-in members use the user menu.

## 6. Deliberately not done

- **The handbook text is not translated.** The markdown is English-only; only the page furniture
  around it is localized.
- **No repository, no `DbContext`.** The section owns no tables; the service is the cache.
  `IMemoryCache` is injected directly, allowlisted in `ApplicationServicesTakeNoMemoryCacheRule`.
- **No `Contracts` project.** Nothing outside the section reads a guide page.
- **`docs/guide/**` stays at the repo root.** The fetch path is a live API against
  `nobodies-collective/Humans@main`; moving the folder breaks every deployed instance until the
  move reaches production `main`.
- **No per-camp scope.** Camp-lead visibility is "leads some camp", matching the collapse Teams
  already makes for coordinators.
- **`IGuideContentSource` is not the section's.** It lives in Base: its signatures name only
  `string`, and most of its consumers are the Agent section's readers and Base's community-KB
  source.

## Load-bearing weirdness

- **A cache miss on one page fetches every page.** `PopulateAsync` is all-or-nothing by design:
  the guide is small, and a per-page fetch would make a GitHub rate-limit failure look like a
  half-broken guide instead of a stale one.
- **Segmenting parses each file with Markdig but slices the original lines.** The parse decides
  which lines are headings; the segments keep the source bytes, which is what makes rejoining
  exact (invariant 14).
- **The role model lives in the segmenter's heading regex and nowhere else.** The filter reads the
  segment's `Role`, and the rendered HTML carries no trace of it. The corpus-wide pinning tests in
  §4 guard that single point.
- **The `github` health check probes `GitHubSettings`, the legal-documents repo**, not the repo the
  guide is fetched from. It arrived here with the `github` monitoring key.

## History

| Date | Outcome | PR |
|---|---|---|
| 2026-08-17 | Feedback.md admin block was leaking to anonymous (unwrapped heading) — fixed + pinned; resolver's probe list derived from the privilege map, restoring Events/Store Admin visibility; three duplicated stem lookups folded into `GuideFiles.TryCanonical`; dead `Humans.Infrastructure` doc paths corrected | peterdrier/Humans#1354 |
| 2026-08-20 | `(Camp Lead)` parentheticals resolved to no privilege, so camp-lead blocks reached only Board/Admin — `CampLead` token added, `IsCampLead` resolved from `ICampLeadDirectory`, and every parenthetical in the corpus pinned to the privilege map | peterdrier/Humans#1415 |
| 2026-09-11 | `/api/backdoor/*` was linkified into a dead link — wildcards now excluded from the inline-code rewriter, with a pinning test; target shape re-derived into the current six-part form; dead project names, an invented pinning test and stale consumer claims cut from the section's prose; the domain-admin negative rule corrected against the pinned superset behaviour; file-count claims replaced by the list that owns them; the 503 page given a way back out; an Events feature spec moved out of the section | peterdrier/Humans#1655 |
| 2026-09-11 | Guide reachable from the member top nav via `ISectionNav`, so the filter's anonymous branch has a way in; §5's markdown-filtering seam taken — the cached unit became the segmented `GuideDocument`, `GuideFilter` selects segments before Markdig, and the `data-guide-*` round trip plus the regex over rendered HTML are gone; a `##` inside a fenced code block was ending the role block around it and serving the rest of that block to anonymous readers (present in the old `Wrap` too, carried over verbatim) — fence state now tracked and pinned; the stale-cache invariant corrected (the cache is per stem, so another stem being cached never rescues the requested one); the 503 page's "way back out" pointed at the Guide home, which re-entered the same failing load; moving rendering onto the request path had taken it outside `PopulateAsync`'s per-file catch, so a render failure would have reached the reader as a raw 500 — now translated to the section's 503 | peterdrier/Humans#1655 |
