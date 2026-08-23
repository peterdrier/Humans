---
name: books-start-2026
description: Nobodies Collective's Holded accounting books begin in 2026 — a single fiscal year, so no cross-year reasoning (key collisions, rollover, prior-year comparatives) applies.
---

Nobodies Collective's Holded books **start in 2026**. There is only one fiscal year of accounting data.

**Why:** reasoning that assumes multiple years of history is wrong here. A phantom row once appeared in the creditor ledger cache and was misdiagnosed as a cross-fiscal-year collision on a natural key (Spanish daybooks normally restart entry numbering each ejercicio). Peter: "we started in 2026, don't get distracted by 2025." The real cause was simpler — the cache never deletes, so a line reclassified out of the creditor account range lives forever.

**How to apply:** before reaching for a multi-year explanation (key collisions across years, fiscal-year rollover, prior-year comparatives, year-over-year trends), remember there is no prior year. Ask what single-year explanation fits first.
