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

**Enforcement (built — SP1, PR #805).** Marker `IOrchestrator` lives in `Humans.Application.Interfaces` as a sibling of `IApplicationService`. Two analyzers run in the `Humans.Application` compilation:

- **HUM0026 — Error.** A type implementing `IOrchestrator` may not inject any `I*Repository`, `HumansDbContext`, or `IDbContextFactory<HumansDbContext>`. No grandfather machinery; a violation means the role marker is wrong (relocate the access into the owning section's Section service).
- **HUM0027 — Error.** A type may carry `IOrchestrator` xor `IApplicationService`, never both. The role axis is exclusive — `IApplicationService` grants own-lane repository access, which an orchestrator is banned from.

**Capability marker, sibling of role markers.** `IInvalidator` (the cache-invalidator family, HUM0028 ratchet) and `IFanout` (terminology only, no analyzer) co-exist alongside `IOrchestrator` / `IApplicationService` on a per-type basis. See [[crosscut-purity]] for the sibling rule on keeping crosscuts pure.

**Roster (SP1 settle).** `IGdprExportService`, `IEarlyEntryService`, `IOnboardingService`, `IHumanLifecycleService`, `IAccountDeletionService` carry `IOrchestrator` and no longer carry `IApplicationService`. `AgentService` is **not** an orchestrator — it owns `agent_*` and injects `IAgentRepository`, so it remains a Section. The design-rules §15i "orchestrator" label on it is wrong.

`ISearchService` joined the roster in nobodies-collective#987 — the G0 audit (#980) found it still carrying `IApplicationService` despite matching this definition verbatim and being classified "Orchestrator" in the frozen inventory alongside Gdpr; neither analyzer catches an orchestrator-shaped `IApplicationService` (see the Enforcement gap note below), so this went undetected until a hand audit found it.

**Enforcement gap — no analyzer flags an orchestrator-shaped `IApplicationService`.** HUM0026/HUM0027 only fire once a type carries `IOrchestrator`; neither rule inspects an `IApplicationService` implementer's shape (no repository/DbContext injected) to suggest it should carry `IOrchestrator` instead. Deliberately not closed: plenty of legitimate `IApplicationService` implementers inject zero repositories by design — same-section façades that compose sibling same-section services (`GoogleAdminService`: seven service-interface params, zero repos, homed and used only within GoogleIntegration) and external-API adapters with no in-repo dependency at all (`TicketTailorService`, `StripeService`: `HttpClient`/`IOptions`/`ILogger`, no section calls whatsoever). The actual distinguishing signal — "coordinates ≥2 sections as its *primary purpose*, owning nothing itself" vs. "is this section's own internal composition" — is a semantic judgment about a type's role, not something constructor-parameter shape alone determines; a purely shape-based rule would flag both of those false positives. Catch the next instance by G0-style audit, not a build-time rule.
