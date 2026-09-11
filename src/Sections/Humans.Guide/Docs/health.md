# Guide — Health

## 1. What the section does

Shows the volunteer handbook inside the app. Someone opens the Guide and reads a page about
the part of the app they use; the page they get is the same page that lives in the repository's
guide folder, with the paragraphs written for roles they do not hold removed before it reaches
them. Nobody writes or edits a guide page here — that happens by pull request against the
repository, and an admin can tell the running app to pick up a merged change without waiting
for a redeploy. Links inside a page that point at another guide page or at a page of the app
navigate in-app; everything else opens away from the app.

## 2. The shapes

| Shape | The question it answers | Where it is answered |
|---|---|---|
| S1 — read a page | "What does the guide say about X, for me?" | `GET /Guide`, `GET /Guide/{name}` — one pipeline; the home page differs only in which stem it fixes and which view wraps it |
| S2 — re-read from GitHub | "Pick up the guide change that just merged." | `POST /Guide/Refresh`, admin only |
| S3 — which sections have help | "Does this section have a guide page written for it?" | `SectionAnnotations`, published into `ISectionCatalog` and read by `/Debug/Sections` |
| S4 — is the upstream reachable | "Can the app still reach GitHub?" | `SectionHealthChecks` registering the `github` check |

S1 is the section. S2 is S1's cache primed deliberately instead of on demand — the same
`PopulateAsync`, differing only in what gets logged. S3 and S4 are one-class seams that carry
no request.

Inside S1 the content passes through these steps, in order, each a pure function of its input.
Cached, once per file: **fetch** (Base's GitHub markdown source) → **segment** (each
`## As a …` block becomes a `GuideSegment` carrying its role and its parenthetical's privilege
tokens; everything else is an unscoped segment) → **cache**. Then per request: **filter** (drop
the segments this reader can't see and join what's left) → **render** (Markdig) → **rewrite**
(sibling `.md` links become `/Guide/<stem>`, app paths in inline code become links, everything
else opens in a new tab against GitHub).

The filter runs *before* Markdig, which is what keeps the role model in one place: a reader's
render only ever sees content they may read, and no role metadata survives into the HTML.

## 3. Structure

```
Humans.Guide/
  Section.cs                 DI only
  SectionAnnotations.cs      S3
  SectionHealthChecks.cs     S4
  Health/                    the check S4 registers
  Controllers/               S1, S2 — route, resolve stem, pick view
  Services/
    GuideFiles                the stem set: routing, fetching and the sidebar all read it
    IGuideContentService      fetch + segment + cache + per-request filter/render, the façade
    GuideDocument             the cached unit: a file as ordered role-scoped segments
    GuideSegmenter            markdown → segments (static — no state to hold)
    IGuideRenderer            Markdig → rewrite, pure
    GuideHtmlPostprocessor    rewrite
    GuideFilter               per-request segment selection (static — no state to hold)
    IGuideRoleResolver        claims + Teams + Camps → GuideRoleContext
    GuideRolePrivilegeMap     parenthetical text → privilege token (static)
  Models/                     sidebar + page view model
  Views/                      the section's pages and its layout partial
  Contracts/                  empty by design
```

No repository, no `DbContext`, no migrations, no resource set. The layout above is the layout
today; the derivation matches because the section is small and each step of the pipeline has
exactly one home.

## 4. Invariants

1. A stem outside `GuideFiles.All` is a 404, in any casing
   (`GuideController.RenderAsync`, `Controllers/GuideController.cs:42`; `GuideFiles.TryCanonical`,
   `Services/GuideFiles.cs:61`).
2. Everything before the first `## As a …` heading, and everything from the next non-`As a`
   `##` heading onward, is an unscoped segment visible to everyone — only role-scoped segments
   are ever dropped (`GuideSegmenter.Segment`, `Services/GuideSegmenter.cs:109`;
   `GuideFilter.IsSegmentVisible`, `Services/GuideFilter.cs:35`).
3. A `##` line inside a fenced code block is sample text, not a heading: it neither opens a
   segment nor closes the one it sits in. Fence state is an authorization concern here — an
   untracked fenced `##` ends the role block around it and serves the rest of that block to
   everyone. The inverse leaks too: CommonMark forbids a backtick in a backtick fence's info
   string, so `` ```md`x `` opens nothing, and a segmenter that entered fence state there would
   swallow the real heading below it and leave *that* block unscoped
   (`GuideSegmenter.FenceDelimiter`, `Services/GuideSegmenter.cs:31`, `:54` and `:88`;
   `GuideSegmenterTests.Segment_FencedH2InsideRoleBlock_DoesNotEndTheBlock`,
   `GuideSegmenterTests.Segment_BacktickOpenerWithABacktickInItsInfoString_IsNotAFence`).
4. Anonymous sees Volunteer blocks and nothing else
   (`GuideFilter.IsVisible`, `Services/GuideFilter.cs:49`; `GuideRoleContext.Anonymous`,
   `Services/GuideRoleContext.cs:9`).
5. A team coordinator sees every Coordinator block; a camp lead sees only the Coordinator
   blocks whose parenthetical names `Camp Lead`
   (`GuideFilter.IsCoordinatorVisible`, `Services/GuideFilter.cs:58` and `:64`).
6. Within one file, anyone who can see a Board/Admin block can see every Coordinator block in
   that file — including a domain admin who reached the Board/Admin block through its
   parenthetical (`GuideFilter.Apply`, `Services/GuideFilter.cs:19`; `Services/GuideFilter.cs:45`).
7. Refresh is admin-only and reading is anonymous
   (`Controllers/GuideController.cs:14`, `:19`, `:24`).
8. A stem that is cached is served from cache without touching GitHub, so no fetch failure can
   take it away — on refresh a failed stem keeps the copy it already had, because only successes
   overwrite. A stem that is *not* cached and whose fetch fails is a 503, never an empty page,
   however many other stems are cached: `hasStale` only suppresses `PopulateAsync`'s own throw,
   and the requested stem is still missing when the caller looks again
   (`GuideContentService.GetDocumentAsync`, `Services/GuideContentService.cs:61` and `:73`;
   `PopulateAsync`'s overwrite, `Services/GuideContentService.cs:110`;
   `GuideController.RenderAsync`, `Controllers/GuideController.cs:55`).
9. A page that cannot be *rendered* fails the same way as one that cannot be fetched — the 503
   view, never a raw 500. Rendering runs per request, outside `PopulateAsync`'s per-file catch,
   so `GuideHtmlPostprocessor`'s timeout-bounded regexes are translated at the call site; the
   cached segments stay put, because the document is fine and only one reader's filtered slice
   of it was not (`GuideContentService.GetPageAsync`, `Services/GuideContentService.cs:44`;
   `GuideContentServiceTests.GetPageAsync_RenderTimesOut_SurfacesAsUnavailableNotAnUnhandledThrow`).
10. `guide:<stem>` cache entries are written by `GuideContentService` and nothing else
    (`Services/GuideContentService.cs:17`).
11. The stem set and the markdown folder match exactly in both directions — a file with no stem
    is unreachable, a stem with no file fails its fetch on every refresh
    (`GuideArchitectureTests.EveryGuideMarkdownFileIsRegistered_AndEveryRegisteredStemExists`).
12. Every `## As a …` heading in the shipped corpus is one the segmenter recognises, and every
    parenthetical in it resolves to a privilege token — the two ways a block fails *open* or
    fails *silent* (`GuideSegmenterTests.Segment_EveryRoleHeadingInShippedContent_OpensAScopedSegment`,
    `GuideArchitectureTests.EveryRoleHeadingParentheticalResolvesToAPrivilege`).
13. Segmenting is lossless: the segments of a file rejoin to that file exactly, so a reader who
    can see everything gets the file as written
    (`GuideSegmenterTests.Segment_EveryShippedFile_RejoinsToTheOriginal`,
    `GuideShippedContentFilterTests.Admin_ReceivesEveryFileWholeAndUnaltered`).

## 5. Seams

- ~~**Filtering markdown instead of HTML.**~~ Taken 2026-09-11. The cached unit is now the
  segmented `GuideDocument`, the filter selects segments before Markdig runs, and the
  `data-guide-*` round trip and the regex over rendered HTML are gone with it — along with the
  class of defect where the shape of a content file defeated the filter.
- ~~**An anonymous way in.**~~ Settled 2026-09-11: the link was missing, not the branch dead.
  `SectionNav.cs` contributes a `Guide` item to the member top nav through `ISectionNav`, with
  no `Visible` predicate, so a signed-out reader can reach the pages the filter's anonymous
  branch was always written to serve.

## 6. Deliberately not done

- **No resource set.** Every string in the four views is English, because the content they wrap
  is English-only markdown. Pinned structurally: the section has no `.resx` and
  `_ViewImports.cshtml` injects no localizer.
- **No repository, no `DbContext`.** The section owns no tables; the service *is* the cache.
  `IMemoryCache` is injected directly, allowlisted in `ApplicationServicesTakeNoMemoryCacheRule`.
- **No `Contracts` project.** Nothing outside the section reads a guide page. The folder stays
  empty rather than becoming a project.
- **`docs/guide/**` stays at the repo root.** The fetch path is a live API against
  `nobodies-collective/Humans@main`; moving the folder breaks every deployed instance until the
  move reaches production `main`.
- **No per-camp scope.** Camp-lead visibility is "leads some camp", matching the collapse Teams
  already makes for coordinators. A per-camp guide has never been asked for.
- **`IGuideContentSource` is not the section's.** It carries the Guide name and lives in Base:
  its signatures name only `string`, and most of its consumers are the Agent section's readers
  and Base's community-KB source. Moving it into Guide would force Base to reference a section.

## Load-bearing weirdness

- **A cache miss on one page fetches every page.** `PopulateAsync` is all-or-nothing by design:
  the guide is small, and a per-page fetch would make a GitHub rate-limit failure look like a
  half-broken guide instead of a stale one.
- **`GuideSegmenter`, `GuideFilter` and `GuideRolePrivilegeMap` are static.** They hold no state
  and take no dependency; making them injectable would buy a seam nothing needs.
- **The role model lives in the segmenter's heading regex and nowhere else.** Nothing
  downstream re-derives it: the filter reads the segment's `Role`, and the rendered HTML
  carries no trace of it. The corpus-wide pinning tests in §4 guard that single point.
- **The `github` health check probes `GitHubSettings`, which is the *legal-documents* repo**
  (`nobodies-collective/legal`), not the repo the guide is fetched from. It arrived here with
  the `github` monitoring key. Recorded, not blessed — see the run file.

## History

| Date | Outcome | PR |
|---|---|---|
| 2026-08-17 | Feedback.md admin block was leaking to anonymous (unwrapped heading) — fixed + pinned; resolver's probe list derived from the privilege map, restoring Events/Store Admin visibility; three duplicated stem lookups folded into `GuideFiles.TryCanonical`; dead `Humans.Infrastructure` doc paths corrected | peterdrier/Humans#1354 |
| 2026-08-20 | `(Camp Lead)` parentheticals resolved to no privilege, so camp-lead blocks reached only Board/Admin — `CampLead` token added, `IsCampLead` resolved from `ICampLeadDirectory`, and every parenthetical in the corpus pinned to the privilege map | peterdrier/Humans#1415 |
| 2026-09-11 | `/api/backdoor/*` was linkified into a dead link — wildcards now excluded from the inline-code rewriter, with a pinning test; target shape re-derived into the current six-part form; dead project names, an invented pinning test and stale consumer claims cut from the section's prose; the domain-admin negative rule corrected against the pinned superset behaviour; file-count claims replaced by the list that owns them; the 503 page given a way back out; an Events feature spec moved out of the section | peterdrier/Humans#1655 |
| 2026-09-11 | Guide reachable from the member top nav via `ISectionNav`, so the filter's anonymous branch has a way in; §5's markdown-filtering seam taken — the cached unit became the segmented `GuideDocument`, `GuideFilter` selects segments before Markdig, and the `data-guide-*` round trip plus the regex over rendered HTML are gone; a `##` inside a fenced code block was ending the role block around it and serving the rest of that block to anonymous readers (present in the old `Wrap` too, carried over verbatim) — fence state now tracked and pinned; the stale-cache invariant corrected (the cache is per stem, so another stem being cached never rescues the requested one); the 503 page's "way back out" pointed at the Guide home, which re-entered the same failing load; moving rendering onto the request path had taken it outside `PopulateAsync`'s per-file catch, so a render failure would have reached the reader as a raw 500 — now translated to the section's 503 | peterdrier/Humans#1655 |
