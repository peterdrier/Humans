<!-- freshness:triggers
  src/Sections/Humans.Tour/**
-->
<!-- freshness:flag-on-change
  Anonymous reachability of /Tour and the capability claims made in its copy — review when the Tour controller, view, or section registration changes, or when a capability the page advertises is added or removed elsewhere in the app.
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

None — content-only section. No tables, no DbContext, no repository, no migrations
(the Scanner shape: G5-SECTION-TEMPLATE.md preconditions).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anonymous visitor | View `/Tour`. |
| Any authenticated human | Same — the page has no privileged state. |

## Invariants

- `/Tour` renders anonymously (`[AllowAnonymous]`; `TourPageRenderTests`).
- The page shows no member data and calls no services — `TourController` injects nothing.
  Stats in the hero are static marketing copy, not live queries.
- The fixed header bar renders on every Tour page with a link back to `/`
  (`TourPageRenderTests`).
- All copy is deliberately hardcoded English (spec `docs/superpowers/specs/2026-08-12-burn-demo-pages-design.md`);
  the section carries no resource set.

## Negative Access Rules

- No actor **cannot** view the page — it is fully public by design. The section exposes no
  write surface at all.

## Triggers

None — this section is a pure read surface with no side effects.

## Cross-Section Dependencies

None. The page links to Shell's `/About` by URL; no service calls.

## Architecture

**Owning services:** None — one anonymous controller over static views.
**Owned tables:** None.
**Status:** (A) Migrated — born in `src/Sections/Humans.Tour` (burn-demo PR, 2026-08-12).

### Cross-section read interface

| Read interface | Methods | Notes |
|---|---:|---|
| — | — | Section is not cross-section-consumed |
