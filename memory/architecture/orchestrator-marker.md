---
name: Orchestrators own no tables and carry IOrchestrator (sibling of IApplicationService)
description: An Orchestrator coordinates ≥2 sections, owns no tables, and injects no repository. Its marker IOrchestrator is a SIBLING of IApplicationService, never a child — IApplicationService grants own-lane repo access, which an orchestrator is banned from. Owns-a-table ⇒ it is a Section, not an orchestrator.
---

Role vocabulary: [`CONTEXT.md`](../../CONTEXT.md) (Section / Crosscut / Orchestrator).

An **Orchestrator** exists because an action genuinely crosses multiple sections — GDPR export fans out to every `IUserDataContributor`; Onboarding sets up user/profile + gets the person into shifts + runs consent sign-offs. It coordinates sections through their public service interfaces and holds only coordination logic.

**The bright line: an Orchestrator owns no tables, and therefore injects no repository.** The moment a service owns a table / injects an `I*Repository`, it is a **Section**, not an Orchestrator — no matter how much it coordinates. (`AgentService` is labelled "orchestrator" in design-rules §15i but owns `agent_*` and injects `IAgentRepository`; by this rule it is a Section. The label is wrong.)

**Marker — `IOrchestrator` is a SIBLING of `IApplicationService`, never a child.** `IApplicationService` grants own-lane repository access; an Orchestrator is banned from any repository, so inheriting it would hand over the exact capability the orchestrator must not have. A service is one or the other, not both.

**Width is a cost.** The number of sections an Orchestrator touches is a liability to minimize, not a feature.

**Homed ≠ owns.** An Orchestrator may be *homed* in a section's namespace for controller-wiring convenience while *owning* no lane. Code location is not table ownership.

**Enforcement (to build):** an analyzer (next free `HUM####`) fails the build when a type implementing `IOrchestrator` injects any `I*Repository`. Until the marker + analyzer land this is convention; creating `IOrchestrator` + the analyzer is the follow-up. Keeping crosscuts pure is the sibling rule — see [[crosscut-purity]].
