<!-- freshness:triggers
  src/Sections/Humans.Guide/**
  src/Humans.Base/Services/GitHubGuideContentSource.cs
  src/Humans.Base/Configuration/GuideSettings.cs
  docs/guide/**
-->
<!-- freshness:flag-on-change
  Guide rendering pipeline, role-filtering rules, refresh route, or anonymous-access policy may have shifted.
-->

# 39 — In-App Guide

## Business context

The Humans app has a comprehensive end-user guide under `docs/guide/` — one
file per stem in `GuideFiles.All`, covering the sections of the app plus
plain-language common-question pages. Before this feature, humans read it on
GitHub — one click away from the app itself. Embedding the guide at `/Guide`
makes it available from the signed-in user menu and lets links inside the
guide navigate in-app to the pages they describe.

GitHub remains the authoring source: guide changes go through pull-request
review (no in-app CMS). The app pulls the current content from
`nobodies-collective/Humans:main:docs/guide/` on demand, caches it in
memory, and re-renders it for each request with role-aware filtering.

## User stories

- As a volunteer, I can open the Guide from the user menu and read a
  human-friendly explanation of the parts of the app I use, with in-app
  links that take me directly to the right page.
- As a team coordinator, I see coordinator-specific sections in the guide
  that explain the management features I have access to.
- As a domain admin (e.g. TeamsAdmin), I see admin guidance for my domain
  but not for domains I don't manage.
- As an admin, I can click "Refresh from GitHub" after merging a guide
  change, without waiting for the app to redeploy.

## Data model

None. The feature is stateless relative to the database: all content is
cached in-memory from GitHub. No migrations, no new tables.

## Workflows

### Reading a guide page

1. Human navigates to `/Guide/<Page>`.
2. `GuideRoleResolver` builds a `GuideRoleContext` from claims,
   `ITeamServiceRead` (team coordinator) and `ICampLeadDirectory` (camp lead).
3. `GuideContentService` returns the fully rendered HTML for the page,
   fetching and rendering every stem in `GuideFiles.All` on cache miss.
4. `GuideFilter` strips role-scoped `<div>` blocks the user can't see.
5. Filtered HTML rendered inside `_GuideLayout.cshtml` with sidebar.

### Refreshing from GitHub (Admin)

1. Admin submits `POST /Guide/Refresh` (CSRF-protected).
2. `GuideContentService.RefreshAllAsync` re-fetches every stem in
   `GuideFiles.All` and re-renders; existing cache entries are overwritten.
3. Admin is redirected back to `/Guide` with a status flash.

## Authorization

- `GET /Guide` / `GET /Guide/{name}` — `[AllowAnonymous]`. Role filtering
  applied server-side; anonymous users see Volunteer sections only.
- `POST /Guide/Refresh` — `[Authorize(Policy = PolicyNames.AdminOnly)]`.

## Role filtering rules

See `src/Sections/Humans.Guide/Docs/Guide.md` §Invariants and §Actors for the authoritative
role-filtering rules (per-block visibility, within-file superset,
parenthetical parsing).

## Related features

- Legal documents (feature 04) use a similar GitHub-sync pattern but with
  DB persistence and versioning. Guide content has no such requirement
  and is memory-only.

## Out of scope for v1

- Glossary-term hover tooltips.
- Scheduled background refresh (TTL + manual refresh cover the cases).
- In-app markdown editing.

Localization was on this list and is no longer deferred: the guide is
English-only by decision, not pending — see `Docs/health.md` §6.
