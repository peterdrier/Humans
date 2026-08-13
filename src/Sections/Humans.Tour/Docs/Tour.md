# Tour — Section Invariants

Public marketing page: what Humans is, in plain language, for visitors evaluating the platform.

## Concepts

- The **Tour page** is the anonymous-reachable capability overview at `/Tour`. Static content;
  the section owns no domain vocabulary beyond it.

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
