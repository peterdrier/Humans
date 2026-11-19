---
name: orchestrator-marker
description: An Orchestrator coordinates ≥2 sections, owns no tables or a repository. `IOrchestrator` is a sibling of `IApplicationService`, never a child — a table owner is a Section.
---

An **Orchestrator** exists because an action genuinely crosses sections (GDPR export fanning out to
every contributor; Onboarding walking a person through profile, consent and shifts). It coordinates
sections through their public interfaces and holds only coordination logic. Vocabulary:
[`CONTEXT.md`](../../CONTEXT.md).

- **It owns no tables and injects no repository.** The moment a service owns a table it is a Section,
  however much it coordinates (`AgentService` owns `agent_*`, so it is a Section).
- **`IOrchestrator` is a sibling of `IApplicationService`, never a child.** `IApplicationService`
  grants own-lane repository access, the one capability an orchestrator must not have. A type carries
  one or the other.
- **Width is a cost.** The number of sections it touches is a liability to minimize.
- **Homed ≠ owns.** An orchestrator may sit in a section's namespace for wiring convenience and still
  own no lane.

**Enforcement** (marker in `Humans.Base.Interfaces`):
- **HUM0026:** an `IOrchestrator` may not inject an `I*Repository`, a section DbContext or an
  `IDbContextFactory<T>`. No grandfathering: a violation means the marker is wrong or the access belongs
  in the owning section's service.
- **HUM0027:** `IOrchestrator` xor `IApplicationService`.

`IInvalidator` and `IFanout` are capability markers that sit beside the role markers
([`crosscut-purity`](crosscut-purity.md)).

**Known gap, deliberately open:** no analyzer flags an orchestrator-shaped `IApplicationService`.
Plenty of legitimate services inject no repository (same-section façades, external-API adapters), and
"coordinates sections as its primary purpose" is a judgment about role, not constructor shape. Catch
these by audit.
