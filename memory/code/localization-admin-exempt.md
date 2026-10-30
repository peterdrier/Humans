---
name: Localization — admin and operator-only pages do not require it
description: Admin/operator-only pages (`/Admin/*`, `/TeamAdmin/*`, `/Shifts/Dashboard`, `/Monitor/*`) don't need new resx keys — a page without that gate is never exempt.
---

**A page only admin or operator roles can reach does not require localization.** Existing localized strings there can stay, but do not add new `@Localizer[...]` calls or resource keys for them until further notice.

**The test is the gate on the view, not where it is routed.** If every role that can open it is an admin or operator role, the page is exempt wherever it lives. ("Member" is no help here — the glossary makes every person in the system one, Board and UsersAdmin included.) Known surfaces:

- `/Admin/*` and `/TeamAdmin/*`
- `/Shifts/Dashboard` — coordinator-facing, an admin function
- `/Monitor/*` — `BoardOrAdmin` and `UsersPolicies.Read` operator pages
- City Planning's map-admin surfaces (`CityPlanningMapAdmin`: Admin, CampsAdmin, or the city-planning team) — a section's managing team are that section's admins (Peter, 2026-09-25)

The access pill (`_AuthorizationPill`, its role titles and `IDescribedRequirement.Admits` labels) is admin content and stays English on every page it appears on (Peter, 2026-10-29, peterdrier/Humans#2187).

That list is illustrative, not the gate: a new operator-only page is exempt without being added to it, and one of these moving to another route stays exempt.

**A view without that gate is never exempt**, wherever it is routed — including a partial or modal opened from an admin page. The gate on the rendered markup decides, not the page that opened it ([`img-alt-is-a-user-facing-string`](img-alt-is-a-user-facing-string.md)).
