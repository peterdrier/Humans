<!-- freshness:triggers
  src/Sections/Humans.Tour/**
  src/Humans.Web/Views/Home/Dashboard.cshtml
  src/Sections/Humans.Onboarding/Views/Welcome/Index.cshtml
  tests/Humans.Tour.Tests/**
  tests/Humans.Integration.Tests/Controllers/TourPageRenderTests.cs
-->
<!-- freshness:flag-on-change
  Anonymous reachability of /Tour, the three entry points that lead to it, and the capability claims made in its copy — review when the Tour controller, view, layout or nav contribution changes, when the Welcome link or the dashboard card moves, or when a capability the page advertises is added or removed elsewhere in the app.
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
- **Entry points.** A signed-out visitor gets a `Tour` link in the top nav (`SectionNav`, an
  `ISectionNav` contribution visible only while anonymous) and a "New here?" link on the
  Welcome landing page (Onboarding's `Views/Welcome/Index.cshtml`, by URL). A signed-in member
  gets a dashboard action card (Shell's `Views/Home/Dashboard.cshtml`, by controller name) and
  no nav slot — the signed-in nav is too busy for one.
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

- `/Tour` renders anonymously: `[AllowAnonymous]` at class scope on `TourController`
  (`TourControllerTests`).
- The page shows no member data and calls no services — `TourController` injects nothing and
  the view reads no model. Stats in the hero are static marketing copy, not live queries.
- The nav link is offered only while signed out (`SectionNavTests`); a member reaches the page
  from the dashboard card.
- The fixed header bar renders on every Tour page with a link back to `/`.
- All copy is deliberately hardcoded English (spec `docs/superpowers/specs/2026-08-12-burn-demo-pages-design.md`);
  the section carries no resource set and binds no localizer.
- The page degrades to fully visible content without JavaScript: the scroll-animation hidden
  state applies only under the `tour-js` class the layout's inline script sets. That script is
  CSP-legal only because `Views/_ViewImports.cshtml` registers `Humans.Base`'s tag helpers,
  whose `NonceTagHelper` stamps the request nonce on every `<script>`.

The rendered page, its header bar, the Welcome link and the dashboard card are pinned by
`TourPageRenderTests` in `Humans.Integration.Tests`, which runs locally only — never in CI.

## Negative Access Rules

- No actor **cannot** view the page — it is fully public by design. The section exposes no
  write surface at all.

## Triggers

None — this section is a pure read surface with no side effects.

## Cross-Section Dependencies

None — the project references only `Humans.Base`. The page links to Shell's `/About` by URL;
no service calls.

## Architecture

**Owning services:** None — one anonymous controller over static views.
**Owned tables:** None.
**Status:** (A) Migrated — the whole section lives in `src/Sections/Humans.Tour`.

### Cross-section read interface

| Read interface | Methods | Notes |
|---|---|---|
| — | — | Section is not cross-section-consumed |
