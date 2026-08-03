---
name: Sections are logical units — tables are not a requirement
description: A section is a logical unit of the app that can be worked on independently; owning DB tables is NOT a requirement. Never propose demoting/merging a section because it is "thin" or table-less. (Peter, 2026-08-03 inventory freeze.)
---

A **section** is a logical unit of the application that can be worked on independently. Owning database tables is **not** a requirement for being a section — thin sections are fine when they represent a logical construct the rest of the app uses, plus a small amount of GUI/agent/service code.

**Why:** During the 2026-08-03 G0 inventory freeze, proposals to demote table-less sections (Guide, Debug, Scanner, Cantina) were rejected by Peter: "having tables isn't a requirement for sections. They're logical units of app which can be worked on independently." Cantina (zero tables today) will in fact *gain* scope — the food-preference bits move there from Users/Profiles as the identity overload thins out into sections keyed off `UserId`.

**How to apply:**

- Don't propose folding a section into another because it owns no tables or "is just a read-composition."
- Gate predicates that are table-keyed (G2 schema, G4 own-DbContext) are simply n/a for table-less sections — that's not a reason to remove the section from the ladder.
- When auditing, score table-less sections on the predicates that do apply (boundaries, tests, docs).

**Related:** [`vendor-connectors-own-sections`](vendor-connectors-own-sections.md), [`orchestrator-marker`](orchestrator-marker.md).
