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

Inside S1 the content passes through five steps, in order, each a pure function of its input:
**fetch** (Base's GitHub markdown source) → **wrap** (each `## As a …` block gets a `<div>`
carrying its role and its parenthetical's privilege tokens) → **render** (Markdig) →
**rewrite** (sibling `.md` links become `/Guide/<stem>`, app paths in inline code become
links, everything else opens in a new tab against GitHub) → **cache**. Only the last step, the
per-request **filter** that drops blocks by role, runs outside the cache — which is why the
cached unit is rendered HTML and the role model has to survive rendering as an HTML attribute.

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
    IGuideContentService      fetch + render + cache, the section's only façade
    IGuideRenderer            wrap → Markdig → rewrite, pure
    GuideMarkdownPreprocessor wrap
    GuideHtmlPostprocessor    rewrite
    GuideFilter               per-request role strip (static — no state to hold)
    IGuideRoleResolver        claims + Teams + Camps → GuideRoleContext
    GuideRolePrivilegeMap     parenthetical text → privilege token (static)
  Models/                     sidebar + page view model
  Views/                      four pages and one layout partial
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
   `##` heading onward, is visible to everyone — only wrapped blocks are ever dropped
   (`GuideMarkdownPreprocessor.Wrap`, `Services/GuideMarkdownPreprocessor.cs:70`).
3. Anonymous sees Volunteer blocks and nothing else
   (`GuideFilter.IsVisible`, `Services/GuideFilter.cs:62`; `GuideRoleContext.Anonymous`,
   `Services/GuideRoleContext.cs:9`).
4. A team coordinator sees every Coordinator block; a camp lead sees only the Coordinator
   blocks whose parenthetical names `Camp Lead`
   (`GuideFilter.IsCoordinatorVisible`, `Services/GuideFilter.cs:71` and `:75`).
5. Within one file, anyone who can see a Board/Admin block can see every Coordinator block in
   that file — including a domain admin who reached the Board/Admin block through its
   parenthetical (`GuideFilter.Apply`, `Services/GuideFilter.cs:45`).
6. Refresh is admin-only and reading is anonymous
   (`Controllers/GuideController.cs:14`, `:19`, `:24`).
7. A fetch failure with anything already cached serves the stale copy; a fetch failure with a
   cold cache is a 503, never an empty page
   (`GuideContentService.PopulateAsync`, `Services/GuideContentService.cs:76`;
   `GuideController.RenderAsync`, `Controllers/GuideController.cs:53`).
8. `guide:<stem>` cache entries are written by `GuideContentService` and nothing else
   (`Services/GuideContentService.cs:16`).
9. The stem set and the markdown folder match exactly in both directions — a file with no stem
   is unreachable, a stem with no file fails its fetch on every refresh
   (`GuideArchitectureTests.EveryGuideMarkdownFileIsRegistered_AndEveryRegisteredStemExists`).
10. Every `## As a …` heading in the shipped corpus is one the wrapper recognises, and every
    parenthetical in it resolves to a privilege token — the two ways a block fails *open* or
    fails *silent* (`GuideMarkdownPreprocessorTests.Wrap_EveryRoleHeadingInShippedContent_LandsInsideARoleDiv`,
    `GuideArchitectureTests.EveryRoleHeadingParentheticalResolvesToAPrivilege`).

## 5. Seams

- **Filtering markdown instead of HTML.** Splitting on `##` before rendering would delete the
  `data-guide-*` round trip, `GuideFilter`'s regex over HTML, and the whole class of defect
  where the shape of a content file defeats the filter. It is blocked only by the cache holding
  rendered HTML per file; at this corpus size rendering per request is affordable. Not built;
  changing the cached unit is Peter's call.
- **An anonymous way in.** The section serves anonymous readers deliberately — the filter's
  anonymous branch is real code with real tests — but the app's only link to `/Guide` sits
  inside the signed-in user menu. Either the anonymous branch is dead in practice or the link
  is missing; the section cannot settle that on its own.

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

- **The role model is written three times** — as heading prose, as an HTML attribute, as a
  regex over rendered HTML. That is essential given §5's constraint, not sediment; the two
  corpus-wide pinning tests exist because of it.
- **A cache miss on one page fetches every page.** `PopulateAsync` is all-or-nothing by design:
  the guide is small, and a per-page fetch would make a GitHub rate-limit failure look like a
  half-broken guide instead of a stale one.
- **`GuideFilter` and `GuideRolePrivilegeMap` are static.** They hold no state and take no
  dependency; making them injectable would buy a seam nothing needs.
- **`Wrap`'s hand-rolled line scanner with an `inBlock` flag** is larger than the rest of the
  pipeline put together. It is the price of §5 not being taken; blessed until it is.
- **The `github` health check probes `GitHubSettings`, which is the *legal-documents* repo**
  (`nobodies-collective/legal`), not the repo the guide is fetched from. It arrived here with
  the `github` monitoring key. Recorded, not blessed — see the run file.

## History

| Date | Outcome | PR |
|---|---|---|
| 2026-08-17 | Feedback.md admin block was leaking to anonymous (unwrapped heading) — fixed + pinned; resolver's probe list derived from the privilege map, restoring Events/Store Admin visibility; three duplicated stem lookups folded into `GuideFiles.TryCanonical`; dead `Humans.Infrastructure` doc paths corrected | peterdrier/Humans#1354 |
| 2026-09-11 | Target shape re-derived into the current six-part form; dead project names and stale consumer claims cut from the section's prose; the domain-admin negative rule corrected against the pinned superset behaviour; file-count claims replaced by the list that owns them | peterdrier/Humans#pending |
