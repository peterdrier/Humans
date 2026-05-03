---
name: Search endpoints return typed DTOs, not anonymous objects
description: Autocomplete/search JSON endpoints use stable typed records. Reuse shared mapping helpers across endpoints feeding the same client interaction.
---

Autocomplete/search JSON endpoints must use stable typed response models.

**Rule:**
- Return typed DTOs/records for JSON search results instead of anonymous objects
- Reuse shared mapping helpers when converting service-layer search results into web response shapes
- Keep property names stable once JavaScript consumers depend on them

**Why:** Anonymous JSON payloads drift easily and make it harder to reuse search behavior safely.
