---
name: No URL aliases except Barrios↔Camps
description: Single canonical URL per page. The only sanctioned alias is Barrios↔Camps (Spanish UX). No singular/plural variants, no second-controller route splits.
---

When adding new pages to an existing section, route them on the existing controller using the existing route convention. Do **NOT** add singular-form aliases (`/Camp/Admin` alongside `/Camps/Admin`), do **NOT** split into a new controller just to vary the URL, do **NOT** introduce any second route prefix.

The single sanctioned alias is **`Barrios` ↔ `Camps`** (Spanish-language UX equivalent). Every camp-section controller that exposes `[Route("Camps")]` also exposes `[Route("Barrios")]`, and `[Route("Camps/Admin")]` is paired with `[Route("Barrios/Admin")]`. That's it.

**Why:** Peter's words: "NEVER ANY FUCKING ALIASES (except barrios for camps)." Aliases create URL surface area that has to be maintained, indexed by search, and explained to users. A second route attribute that resolves to the same action is technical debt with no upside. Single canonical URL per page; one well-known alias for the bilingual UX.

**How to apply:**

- New pages in the Camps section → existing `CampController` (plural, public + per-camp slug routes) or existing `CampAdminController` (plural, global admin) — match the page's scope.
- Same convention by analogy in other sections (Profiles, Teams, etc.) — single canonical URL, no singular/plural aliasing.
- If a brief or prior review claims "URL must change to X" and X isn't the section's existing convention, verify with Peter before refactoring routes.
- The only acceptable controller-level multi-route is the Barrios alias.

**Related:** [`no-admin-url-section`](../architecture/no-admin-url-section.md) — `/<Section>/Admin/*` is the convention for admin URLs going forward.
