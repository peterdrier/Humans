# In-App Guide Section — Design Spec

**Issue:** [#531](https://github.com/nobodies-collective/Humans/issues/531)
**Date:** 2026-04-21
**Status:** Draft — awaiting implementation plan

## Purpose

Embed the end-user guide (the 17 markdown files under `docs/guide/`) inside the app at `/Guide`, with:

- GitHub as the authoring source (PR-reviewed, no in-app CMS).
- Role-aware filtering: users only see content for roles they hold.
- Live link rewriting so sibling `.md` links navigate in-app and app-path links remain real routes.
- Images served from the GitHub raw URL.

## Scope

**In scope:**

- `/Guide` + `/Guide/{name}` routes rendering the 17 files from `nobodies-collective/Humans:main:docs/guide/`.
- Memory-only cache with lazy load + TTL + admin manual refresh.
- Role-based section hiding using a heuristic on `## As a …` headings + parenthetical parsing.
- Link rewriting (sibling `.md`, image paths, external target).
- Sidebar navigation mirroring the README's structure.

**Out of scope for v1 (explicit follow-ups possible):**

- Localization (the guide is English-only today; sync service leaves room for per-language files later).
- Glossary-term hover tooltips.
- DB persistence of guide content.
- Admin in-app markdown editing.
- Scheduled Hangfire sync job (not needed — frequent deploys + TTL + manual refresh cover the case).

## Non-functional constraints

- Single-server deployment, ~500 users total (per `CLAUDE.md`).
- In-memory caching preferred over DB queries at this scale.
- No new EF migrations.
- Must follow Services-own-data rule: the Guide service owns its own cache; nothing else touches guide content.

## Architecture

Three Infrastructure components + one Web controller.

```
┌────────────────────────────────────────────────┐
│ GuideController  (Humans.Web)                  │
│  GET  /Guide          → Document("README")     │
│  GET  /Guide/{name}   → Document(name)         │
│  POST /Guide/Refresh  → Admin only             │
└──────────┬───────────────────────┬─────────────┘
           │                       │
           ▼                       ▼
┌──────────────────┐    ┌────────────────────────┐
│ IGuideRole-      │    │ IGuideContentService   │
│   Resolver       │    │  (memory cache owner)  │
│  → role context  │    │  GetRenderedAsync(name)│
└──────────────────┘    │  RefreshAllAsync()     │
                        └──────────┬─────────────┘
                                   │
                                   ▼
                        ┌────────────────────────┐
                        │ IGuideRenderer         │
                        │  Render(markdown)      │
                        │  → HTML with role-     │
                        │    section <div>s      │
                        └──────────┬─────────────┘
                                   │
                                   ▼
                           GitHub (Octokit)
                           docs/guide/*.md
```

**Filter flow per request:**

1. Controller resolves the user's role context (`isTeamCoordinator`, `systemRoles`) via `IGuideRoleResolver`.
2. Controller calls `IGuideContentService.GetRenderedAsync(name)` → HTML with role-section `<div data-guide-role="...">` wrappers.
3. Controller (or a small utility) strips `<div>`s whose `data-guide-role` the user can't see, based on role context.
4. View renders the filtered HTML inside the guide layout.

## Caching model

**Memory-only**, using `IMemoryCache`.

- **Key:** `guide:{filename-stem}` (e.g., `guide:Profiles`).
- **Value:** fully rendered HTML, with role-section blocks wrapped in `<div data-guide-role="volunteer|coordinator|boardadmin" data-guide-roles="…">`.
- **TTL:** 6 hours sliding.
- **Cold start:** first request for any file fetches all 17 files in parallel, renders each, populates cache. Subsequent requests from this deploy hit cache directly.
- **Manual refresh:** admin POST to `/Guide/Refresh` clears all `guide:*` entries and repopulates.
- **17 cache entries total.** Role filtering runs per request on the cached HTML (cheap substring/regex pass on ~30KB).

**Cache miss + GitHub unreachable** → controller returns a friendly "Guide temporarily unavailable" page. Logged as a warning. If cache had previously been populated and TTL expired while GitHub is down, we serve stale content (cache refresh swallowed, TTL reset shortened so we retry sooner).

**Authoring convention for cache invalidation:** admins use `/Guide/Refresh` after merging a guide update. Cache also naturally clears on deploy restart.

## Role filtering

### User role context

`IGuideRoleResolver.ResolveAsync(User)` → `GuideRoleContext`:

```csharp
public sealed record GuideRoleContext(
    bool IsAuthenticated,
    bool IsTeamCoordinator,
    IReadOnlySet<string> SystemRoles);
```

- `IsTeamCoordinator`: user has any `TeamMember` row with `Role == TeamMemberRole.Coordinator` and `LeftAt == null`.
- `SystemRoles`: the subset of `RoleNames.*` the user holds, sourced from `User.IsInRole(...)` against the known constant list.

### Section detection

Markdown pre-pass during render wraps each `## As a …` block in a `<div data-guide-role="..." data-guide-roles="...">`.

Regex applied line-by-line (case-insensitive):

```
^##\s+As\s+an?\s+(?:\[)?(?<head>Volunteer|Coordinator|Board)[^\n]*?(?:\((?<paren>[^)]+)\))?\s*$
```

- The head keyword (Volunteer / Coordinator / Board) fixes the primary audience bucket.
- `paren` (optional) is split on `,`, trimmed, and space-stripped to match system role constants (e.g., `Camp Admin` → `CampAdmin`, `Consent Coordinator` → `ConsentCoordinator`). Unknown tokens are logged and ignored.

The wrapper `<div>` carries:

- `data-guide-role` = one of `volunteer` / `coordinator` / `boardadmin` (the head keyword).
- `data-guide-roles` = comma-separated role names from the parenthetical, if any.

Content **above** the first `## As a …` heading (i.e., `## What this section is for`, `## Key pages at a glance`) is always visible. Content **at or below** any non-`As a …` `##` heading (e.g., `## Related sections`, `## Key contacts`) is also always visible — the role filter is scoped to `As a …` blocks only.

Files with no `## As a …` headings (`README.md`, `Glossary.md`, `GettingStarted.md`) render in full for all viewers.

### Visibility rules

Per-block, evaluated against the user's `GuideRoleContext`:

Evaluated per block, per file. Rules:

1. **`As a Volunteer`** — visible to everyone, including anonymous.
2. **`As a Coordinator`** — visible if **any** of:
   - user `IsTeamCoordinator`,
   - user is `Board` or `Admin` (global superset),
   - parenthetical names a `*Coordinator` system role the user holds (e.g., `(Consent Coordinator)` → `ConsentCoordinator`),
   - user also sees **this same file's** Board/Admin block (within-file superset — handles the TeamsAdmin-on-Teams.md case).
3. **`As a Board member / Admin`** — visible if **any** of:
   - user is `Board` or `Admin`,
   - parenthetical names an `*Admin` system role the user holds (e.g., `(Camp Admin)` → `CampAdmin`).

The within-file superset in rule 2 means a TeamsAdmin reading `Teams.md` sees both the Coordinator block and the Board/Admin block (since the latter parenthetical `(Teams Admin)` matches them); but on `Tickets.md` they see neither (they don't match the `(Ticket Admin)` parenthetical, and aren't `IsTeamCoordinator`). This matches the existing auth convention ("domain admins are supersets within their domain").

Implementation: the filter drops a `<div>` when the user doesn't satisfy any of the matching rules. Simple substring/regex pass on the cached HTML, no Markdig involvement at request time.

### Content updates required

Add parentheticals to these existing files so the filter knows which domain admin to admit:

| File | Heading becomes |
|---|---|
| `docs/guide/Teams.md` | `## As a Board member / Admin (Teams Admin)` |
| `docs/guide/Profiles.md` | `## As a Board member / Admin (Human Admin)` |
| `docs/guide/Shifts.md` | `## As a Board member / Admin (NoInfo Admin)` |
| `docs/guide/Feedback.md` | `## As a Board member / Admin (Feedback Admin)` |
| `docs/guide/Budget.md` | `## As a Board member / Admin (Finance Admin)` |
| `docs/guide/Onboarding.md` | `## As a Board member / Admin (Human Admin)` |

Files already parenthetical (no change): `Camps.md`, `CityPlanning.md`, `Tickets.md`.

Files left bare (truly Board/Admin-only): `Admin.md`, `Governance.md`, `Campaigns.md`, `Email.md`, `GoogleIntegration.md`, `LegalAndConsent.md`.

These content updates ship in the same PR as the feature — the filter is meaningless without them.

## Link and image rewriting

Applied during Markdig render (once per refresh), via a custom AST walker.

### Inline links

| Pattern | Rewrite |
|---|---|
| `Profiles.md` | `/Guide/Profiles` |
| `Profiles.md#edit` | `/Guide/Profiles#edit` |
| `Glossary.md#coordinator` | `/Guide/Glossary#coordinator` |
| `../sections/Teams.md` | `https://github.com/nobodies-collective/Humans/blob/main/docs/sections/Teams.md` (new tab) |
| `../../LICENSE` | `https://github.com/nobodies-collective/Humans/blob/main/LICENSE` (new tab) |
| `/Profile/Me/Edit` | left as-is (app-internal) |
| `http(s)://…` | add `target="_blank" rel="noopener"` |
| `mailto:…` | left as-is |

Sibling `.md` resolution is case-insensitive and only matches files known to the sync service (guards against typos turning into internal 404s).

### Image refs

| Pattern | Rewrite |
|---|---|
| `img/foo.png` | `https://raw.githubusercontent.com/{owner}/{repo}/{branch}/docs/guide/img/foo.png` |
| `docs/guide/img/foo.png` | same as above |
| `http(s)://…` | leave as-is |

`{owner}`, `{repo}`, `{branch}` come from `GitHubSettings` (already used by `LegalDocumentSyncService`).

## Routing and authorization

| Route | Auth | Notes |
|---|---|---|
| `GET /Guide` | `[AllowAnonymous]` | Renders `README.md`, role filter applied |
| `GET /Guide/{name}` | `[AllowAnonymous]` | Renders `{name}.md`, role filter applied; unknown name → 404 view |
| `POST /Guide/Refresh` | `[Authorize(Roles = "Admin")]` | Clears cache and repopulates; redirects back with a status flash |

Anonymous users get Volunteer-section content only. Authenticated users get content matching their role context.

## Navigation

- **Main nav bar:** add "Guide" link pointing at `/Guide`. Visible to everyone (anonymous included).
- **Sidebar** (on all guide pages): mirrors `README.md` structure.
  - **Start here:** Getting Started
  - **Section guides:** the 15 section files in README order
  - **Appendix:** Glossary
- **Breadcrumb:** `Guide > <Page>` above content on non-index pages.
- **Active state:** current page highlighted in the sidebar.

## Data flow for one request

```
GET /Guide/Profiles

GuideController.Document(name="Profiles", User)
  │
  ├─ IGuideRoleResolver.ResolveAsync(User)
  │    → GuideRoleContext { IsTeamCoordinator, SystemRoles }
  │
  ├─ IGuideContentService.GetRenderedAsync("Profiles")
  │    ├─ MemoryCache hit → return HTML (role-annotated)
  │    └─ miss → RefreshAllAsync() populates all 17 entries
  │              (parallel GitHub fetch, Markdig render, cache set)
  │
  ├─ GuideFilter.Apply(html, roleContext)
  │    → stripped HTML
  │
  └─ View("Document", new GuideViewModel { Title, Html, SidebarModel })
```

## Files to create / modify

### New — Application

- `src/Humans.Application/Interfaces/IGuideContentService.cs`
- `src/Humans.Application/Interfaces/IGuideRenderer.cs`
- `src/Humans.Application/Interfaces/IGuideRoleResolver.cs`
- `src/Humans.Application/Models/GuideRoleContext.cs`
- `src/Humans.Application/Models/RenderedGuidePage.cs`

### New — Infrastructure

- `src/Humans.Infrastructure/Services/GuideContentService.cs` — cache owner, GitHub fetch orchestrator
- `src/Humans.Infrastructure/Services/GuideRenderer.cs` — Markdig wrapper with link/image rewriting + role-block wrapping
- `src/Humans.Infrastructure/Services/GuideRoleResolver.cs` — team-coordinator DB check + system-role collection
- `src/Humans.Infrastructure/Configuration/GuideSettings.cs` — TTL, file list (or a constant)

### New — Web

- `src/Humans.Web/Controllers/GuideController.cs`
- `src/Humans.Web/Models/GuideViewModel.cs`
- `src/Humans.Web/Models/GuideSidebarModel.cs`
- `src/Humans.Web/Services/GuideFilter.cs` — per-request role-section stripping (static utility)
- `src/Humans.Web/Views/Guide/Index.cshtml`
- `src/Humans.Web/Views/Guide/Document.cshtml`
- `src/Humans.Web/Views/Guide/NotFound.cshtml`
- `src/Humans.Web/Views/Shared/_GuideLayout.cshtml`

### New — Docs

- `docs/features/39-in-app-guide.md` — feature spec
- `docs/sections/guide.md` — section invariants (actors, invariants, triggers, cross-section deps)

### New — Tests

- `tests/Humans.Infrastructure.Tests/Services/GuideRendererTests.cs`
- `tests/Humans.Infrastructure.Tests/Services/GuideContentServiceTests.cs`
- `tests/Humans.Web.Tests/Services/GuideFilterTests.cs`

### Modified

- `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs` — DI registrations (`IGuideContentService`, `IGuideRenderer`, `IGuideRoleResolver`, settings binding).
- `src/Humans.Web/Views/Shared/_Layout.cshtml` (or the existing nav partial) — add "Guide" link.
- `docs/guide/Teams.md`, `Profiles.md`, `Shifts.md`, `Feedback.md`, `Budget.md`, `Onboarding.md` — parentheticals on Board/Admin heading.

## Testing

### Unit tests

- **`GuideRendererTests`**
  - Role-section blocks wrapped with correct `data-guide-role` / `data-guide-roles`.
  - Sibling `.md` link rewritten to `/Guide/<stem>`, fragment preserved.
  - Unknown sibling `.md` link → left alone or logged.
  - App-path link left alone.
  - External link gets `target="_blank" rel="noopener"`.
  - Image `img/foo.png` rewritten to `raw.githubusercontent.com` URL with settings-driven `{owner}/{repo}/{branch}`.
  - Parenthetical parser maps `Camp Admin` → `CampAdmin`, `Consent Coordinator` → `ConsentCoordinator`.
  - Unknown parenthetical tokens logged and ignored.
- **`GuideFilterTests`**
  - Anonymous sees Volunteer only; Coordinator + Board/Admin sections stripped.
  - Team-coordinator user sees Volunteer + Coordinator (bare); not Board/Admin.
  - `ConsentCoordinator` system-role holder sees Volunteer + Coordinator block when parenthetical lists `Consent Coordinator`; not when the block is bare (unless they're also a team coordinator).
  - `TeamsAdmin` sees Volunteer + Coordinator + Board/Admin on `Teams.md` (within-file superset via `(Teams Admin)` match); sees only Volunteer on `Tickets.md`.
  - `CampAdmin` sees Volunteer + Coordinator + Board/Admin on `Camps.md`; only Volunteer on `Tickets.md`.
  - `Admin` sees everything.
  - `Board` sees all Board/Admin blocks regardless of parenthetical.
- **`GuideContentServiceTests`**
  - Cache hit returns cached HTML without GitHub call.
  - Cache miss fetches all 17 files and populates.
  - `RefreshAsync` clears and repopulates.
  - GitHub failure on cold cache → throws a domain exception that the controller converts to the unavailable view.
  - GitHub failure on TTL expiry (warm cache) → serves stale, logs warning.

### Smoke test on QA

- `/Guide` loads as anonymous; only Volunteer sections visible.
- Log in as a plain Volunteer; same visibility.
- Log in as a team coordinator (Primary Lead / department coordinator); Coordinator blocks now visible.
- Log in as a `CampAdmin`; Camp Admin's parenthetical-scoped Board/Admin block is visible on `Camps.md`, but not on `Tickets.md`.
- Log in as `Admin`; all blocks visible everywhere.
- Click a sibling `.md` link on a page; in-app navigation works, fragment preserved.
- Click an app-path link; goes to the real app route.
- Click an external link; opens in a new tab.
- Confirm images load from `raw.githubusercontent.com`.
- As Admin, click "Refresh from GitHub"; status flash indicates success.

## Acceptance criteria mapping

| Criterion (from issue #531) | Met by |
|---|---|
| `/Guide` section with top-level nav link; all 17 files reachable | Routes + sidebar + nav link |
| Content loads from `nobodies-collective/Humans:main:docs/guide/` on scheduled sync | GitHub fetch via `GuideContentService` (not Hangfire-scheduled — TTL + refresh button per agreed design trade-off) |
| Sibling `.md` links resolve to `/Guide/<File>`; fragment anchors work | Link rewriting in `GuideRenderer` |
| App-path links render as active navigation | Left as-is (`/Profile/Me/Edit` is a real route) |
| Image references load from GitHub raw URL | Image rewriting in `GuideRenderer` |
| Anonymous users see only Volunteer content | Role filter with `IsAuthenticated == false` |
| Authenticated users: role sections above their highest role hidden | Per-block visibility rules |
| Glossary entries reachable via anchors | `/Guide/Glossary#<term>` routing + no role filter on Glossary |
| Manual "refresh from GitHub" action available to Admin | `POST /Guide/Refresh` |
| README served at `/Guide`; GettingStarted linked prominently | `Index.cshtml` renders README; sidebar "Start here" calls out GettingStarted |

## Open questions / follow-ups

- **`LegalAndConsent.md`:** left bare (Board/Admin only) in v1. If `HumanAdmin` should see that block, add `(Human Admin)` parenthetical later.
- **Localization:** not handled in v1. Sync service structured so per-language files can be added in a follow-up PR (mirror `LegalDocumentSyncService`'s folder-based language discovery).
- **Glossary tooltips:** out of scope for v1; file a follow-up issue if desired after v1 ships.
- **Scheduled refresh:** no Hangfire job in v1. If content-freshness complaints surface in practice, add a daily job matching `legal-document-sync` (04:00 cron).
