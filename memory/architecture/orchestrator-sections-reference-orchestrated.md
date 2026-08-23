---
name: orchestrator-sections-reference-orchestrated
description: A section that orchestrates others (Search, Dashboard) referencing the sections it orchestrates is expected and fine — the only hard constraint is no cycles; don't flag the edge as a smell.
---

A section that orchestrates other sections referencing those sections is 100% fine and expected — for example `Humans.Search → Humans.Users`. The only hard constraint is **no cycles**.

**Why:** Peter, emphatically, correcting a repeated false-positive: "search referencing users is 100% fine and expected. stop thinking it's not. we can not have loops obviously, but search (an orchestrator) is very likely going to connect to the damn things it's orchestrating." An orchestrator that couldn't reference its subjects wouldn't be an orchestrator.

**How to apply:** when auditing the project reference graph, classify a section→section edge as a finding **only** if it creates a cycle, or if a *leaf* (`.Contracts`) gains an outbound reference that breaks the terminal-chain rule. A section project referencing another section project it orchestrates needs no justification, no queue entry, no "disclosed cost" paragraph.

For how a rendered page composes results across the orchestrated sections (view component vs. partial, who owns the data-fetch), see [`view-components-vs-partials`](../code/view-components-vs-partials.md) — the same session settled the rule as: the orchestrator holds only ids, and each owning section publishes its own view component keyed by id, rather than one shared partial fed by several producers.

See also [`orchestrator-marker`](orchestrator-marker.md).
