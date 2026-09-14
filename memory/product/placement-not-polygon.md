---
name: User-facing copy says "placement", not "polygon"
description: Member-facing copy calls a camp's map shape its "placement", never "polygon" — jargon. Code entities (`CampPolygon`) and admin GeoJSON specifics are unaffected.
---

In **member-facing** copy — guide docs, Razor views, emails, notifications — refer to a camp's shape on the City Planning map as its **placement** ("edit your camp's placement", "placements outside the limit zone"). Never "polygon".

**Why:** Peter, 2026-06-11 (guide accuracy review): «"camp's polygon" is technical jargon a regular user wouldn't know. this is the "camp's placement" for normal human speak».

**How to apply:**

- Guide docs and UI strings: "placement", "draw your placement", "move corners" (not "vertices").
- Entity and code names (`CampPolygon`, `CampPolygonHistory`) and developer docs stay as-is.
- Admin GeoJSON import/export specifics may stay technical where they describe file formats, but prefer "placement" wherever the sentence is about the camp's shape.
- Related: [[humans-terminology]], [[no-ranking-language]] — the org's other plain-speech/branded-vocabulary rules.
