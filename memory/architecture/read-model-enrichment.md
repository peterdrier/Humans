---
name: Read-Model Enrichment Before New Surfaces
description: When a caller needs stable facts about an aggregate already exposed through a bounded/cached canonical read model (UserInfo, TeamInfo, TicketOrderInfo, …), prefer adding scalar fields to that DTO over a new interface/repository/service method/DI registration.
---

<!-- freshness:triggers
  src/Humans.Application/TicketOrderInfo.cs
  src/Humans.Application/UserInfo.cs
-->
<!-- freshness:flag-on-change
  Rule: Read-Model Enrichment Before New Surfaces
  Flag only if the code this rule constrains changed in a way that makes the rule
  wrong or unenforceable — a renamed/removed symbol, a moved namespace, a dropped
  analyzer. Routine edits to these files are the rule being followed, not drift.
-->

If a caller needs stable facts about an aggregate already exposed through a bounded or cached canonical read model, prefer adding scalar fields to that DTO over adding a new interface, repository, service method, implementation, or DI registration. A few numbers/strings on `UserInfo`, `TeamInfo`, `TicketOrderInfo`, etc. are usually cheaper and clearer than a one-off read surface.

Only add a new durable surface when the data is unbounded, permission-specific, expensive to materialize through the read model, transactional, sensitive, not a stable aggregate fact, or would pollute the canonical DTO with screen-only concerns. Before proposing a new interface/repository for a read projection, explicitly check whether enriching the existing read DTO eliminates it.

**Related:** [[reuse-first-change-discipline]], [[interface-method-additions-are-debt]], [[derived-predicates-on-userinfo]]
