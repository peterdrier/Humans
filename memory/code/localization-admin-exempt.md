---
name: Localization — admin and operator-only pages do not require it
description: Existing `@Localizer[...]` calls can stay, but don't add new resource keys for a page only admin or operator roles can reach — `/Admin/*`, `/TeamAdmin/*`, `/Shifts/Dashboard`, `/Monitor/*` and their like. Only views a member can reach require localization.
---

**A page only admin or operator roles can reach does not require localization.** Existing localized strings there can stay, but do not add new `@Localizer[...]` calls or resource keys for them until further notice.

**The test is who can reach the page, not where it is routed.** If every role that can open it is an admin or operator role, the page is exempt wherever it lives. Known surfaces:

- `/Admin/*` and `/TeamAdmin/*`
- `/Shifts/Dashboard` — coordinator-facing, an admin function
- `/Monitor/*` — `BoardOrAdmin` and `HumanAdminBoardOrAdmin` operator pages

That list is illustrative, not the gate: a new operator-only page is exempt without being added to it, and one of these moving to another route stays exempt.

**A member-facing view is never exempt**, wherever it is routed — including a partial or modal opened from an admin page. The reader is a member either way, and the reader is what the rule is about ([`img-alt-is-a-user-facing-string`](img-alt-is-a-user-facing-string.md)).
