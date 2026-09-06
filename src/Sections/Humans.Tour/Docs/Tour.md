<!-- freshness:triggers
  src/Sections/Humans.Tour/**
  src/Humans.Web/Views/Home/Dashboard.cshtml
  src/Sections/Humans.Onboarding/Views/Welcome/Index.cshtml
  tests/Humans.Integration.Tests/Controllers/TourPageRenderTests.cs
-->
<!-- freshness:flag-on-change
  Anonymous reachability of /Tour, the routes into it, and the capability claims made in its copy — review when the Tour controller, views, nav item or section registration change, when Shell's dashboard tile or Onboarding's Welcome link moves, or when a capability the page advertises is added or removed elsewhere in the app.
-->

# Tour — Section Invariants

Public marketing page: what Humans is, in plain language, for visitors evaluating the platform.
Styled as a long-form landing page in the nobodies.team visual language (own `_TourLayout`,
full-bleed hero, scroll animations) rather than the Shell chrome.

## Concepts

- The **Tour page** is the anonymous-reachable capability overview at `/Tour`. Static content;
  the section owns no domain vocabulary beyond it.
- The section ships its own layout (`Views/Shared/_TourLayout.cshtml`) and static assets
  (`wwwroot/` css/js/img, served at `/_content/Humans.Tour/`). The layout's fixed header bar
  links back to `/` — the page must always offer the way back into Humans.
- Photos are event photography reused from the nobodies.team site (same organization).
- Copy names the event **Elsewhere** only — never "Nowhere" (no legal rights to that name).

## Data Model

None — content-only section. No tables, no DbContext, no repository, no migrations.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anonymous visitor | View `/Tour`. |
| Any authenticated human | Same — the page has no privileged state. |

## Invariants

- `/Tour` renders anonymously (`[AllowAnonymous]`; `TourPageRenderTests`, a local-only
  integration test — CI's `EndpointAuthorizationTests` pins only that *an* authorization
  attribute is present, not which).
- The page shows no member data and calls no services — `TourController` injects nothing.
  Stats in the hero are static marketing copy, not live queries.
- The fixed header bar renders on every Tour page with a link back to `/`
  (`TourPageRenderTests`, local-only).
- All copy is deliberately hardcoded English (spec `docs/superpowers/specs/2026-08-12-burn-demo-pages-design.md`);
  the section carries no resource set.
- With JavaScript off or blocked, every block is visible: the scroll-animation hidden state is
  gated on the `tour-js` class the layout's inline script sets. `_ViewImports.cshtml`'s
  `@addTagHelper *, Humans.Base` is what nonces that script under the CSP — remove it and the
  fade-ups die silently.

## Negative Access Rules

- Every actor may view the page — it is fully public by design. The section exposes no write
  surface at all.

## Triggers

None — this section is a pure read surface with no side effects.

## Cross-Section Dependencies

None at compile time — the project references `Humans.Base` only; no service calls.

Routes in (`GET /Tour`, `TourController.Index`): `SectionNav.cs` contributes the
anonymous-only top-nav link (`ISectionNav`, hidden once signed in); Onboarding's Welcome page
links `/Tour` for anonymous arrivals; signed-in members reach it from Shell's dashboard tile
(`src/Humans.Web/Views/Home/Dashboard.cshtml`), which names the section by string — the
signed-in top nav has no Tour slot.

Routes out: the header bar to `/`, and Shell's `/About` for the engineering story.

## Architecture

**Owning services:** None — one anonymous controller over static views, plus one `ISectionNav`
contribution.
**Owned tables:** None.
**Status:** (A) Migrated — born in `src/Sections/Humans.Tour`, never lived elsewhere.

### Cross-section read interface

| Read interface | Methods | Notes |
|---|---:|---|
| — | — | Section is not cross-section-consumed |
