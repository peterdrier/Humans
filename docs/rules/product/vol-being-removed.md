---
name: /Vol/* surface is being removed
description: TRANSITIONAL. The /Vol/* controllers, views, and partials are slated for removal — don't invest in keeping them consistent with /Shifts. Don't add new UX to Vol surfaces.
---

The `/Vol/*` browse surface (`VolController`, `Views/Vol/*`, including `Vol/_ShiftRow.cshtml`, `Vol/_RotaCard.cshtml`, `Vol/Shifts.cshtml`, etc.) is being removed entirely.

**Why:** Peter confirmed during the `peterdrier#350` review (2026-04-29) that the `/Vol/` stack is on its way out. Originally a parallel browse path; the canonical browse lives on `/Shifts`.

**How to apply:**

- When reviewing/implementing changes that span both `/Shifts` and `/Vol/Shifts` (or other Vol routes), don't flag inconsistency between them as an outstanding issue or block merges on it.
- Don't propose extending new UX (avatar chips, social-proof headers, etc.) to Vol partials — they'll be deleted.
- Don't add new `VolController` features or new Vol views unless explicitly asked.
- When changes touch shared partials that Vol happens to consume, focus on the canonical (`/Shifts`) surface; if Vol breaks, that's acceptable cleanup.

**Status:** TRANSITIONAL. Delete this atom when the `/Vol/*` removal lands.
