# Humans — Ubiquitous Language

Canonical vocabulary for the Humans codebase. This glossary is the single source of truth for terms; where prose elsewhere uses a different word for one of these concepts, that prose is wrong (and the hard rules win over this glossary where they overlap). Definitions only — no implementation detail.

## Architecture roles

> Organizing idea (from the 2026-05-25 grill): a service's role is defined by its **width** — how many lanes it touches — not by its position in a top/bottom stack. Width is a cost, not a feature.

**Lane**:
One section's domain — its tables plus the logic over them. A service's reach is measured in lanes.

**Width**:
How many lanes a service touches. Lower is better; more is worse. A **Section** is width-1; a **Crosscut** is full-width but logic-free; an **Orchestrator** is width-few.

**Section**:
A vertical slice that owns its domain model and database tables and is the sole gateway to them. Touches exactly one lane — its own.
_Avoid_: vertical, vertical slice, module

**Foundational** (descriptor, not a role):
A **Section** that every member necessarily has — **User** and **Profile**. Universality gives it maximal in-degree (almost everything calls it), so to avoid service loops its outbound width into other sections is pinned to **zero**: it may call only Crosscuts, never up into another Section.
_Avoid_: foundational layer (it's not a layer/tier — just a Section with an outbound-width-zero constraint)

**Crosscut**:
A section whose service every other section may call and which carries **no** section-specific logic — a tool (Audit, Email, Notification, Metrics). It owns its **own** data (e.g. the audit log) but reaches into no other section's. (Owning *no* data is the **Orchestrator**, not the Crosscut.)
_Avoid_: horizontal, cross-cutting service, leaf, utility

**Orchestrator**:
A service that **owns no tables** and exists because the action crosses multiple sections; it coordinates ≥2 sections through their interfaces and holds only coordination logic — reaching data only through other sections' services, never a repository. Width (lanes coordinated) is a cost to minimize. Examples: GDPR export (fans out to every `IUserDataContributor`), Onboarding (user/profile + shifts + consent). The moment a thing owns a table it is a **Section**, not an Orchestrator — no matter how much it coordinates.
_Avoid_: director

**Homed** vs **Owns**:
*Homed* is where a service's code lives (namespace / controller proximity); *owns* is table ownership. An Orchestrator may be **homed** in a section's lane for controller-wiring convenience while **owning** no lane. "Which lane is X in?" must be answered as homed-or-owns, never conflated.

## Flagged ambiguities

- The same concept has three names across docs and must converge here:
  - **Crosscut** ← "horizontal" (hard-rules), "cross-cutting service" (design-rules), "leaf" (memory atoms).
  - **Section** ← "vertical" / "vertical slice" (hard-rules), "section" (design-rules / atoms).
  - **Orchestrator** ← "director" (memory atoms).
- `AgentService` is labelled "orchestrator" in design-rules but owns `agent_*` tables → by this glossary it is a **Section**, not an Orchestrator. The label is wrong.
- **EventParticipation** reads as an Events concept but its EF config lives in `Configurations/Shifts/` → the `event_participations` table is owned by the **Shifts** Lane. Name is a smell; ownership follows the config. (Surfaced 2026-05-25 grill — confirm the true owner with Peter.)
- Headwords above are the grill's opinionated picks; final naming is Peter's (hand-edit).
- **Orphan** is *not* a role. It named the instinct "this could stand alone instead of being bolted onto User" (the merge-id mistake). Resolved: cross-lane needs go to an **Orchestrator** (e.g. an Audit orchestrator that gathers merged ids and calls Audit *with* the list), keeping the Crosscut pure. Don't re-introduce "orphan" as a role.

## Example dialogue

> **Dev:** "Audit needs the member's display name — can `AuditLogService` call `IUserServiceRead`?"
> **Architect:** "No. Audit is a **Crosscut** — every **Section** calls it, so it must hold no section logic and reach into no **Section**. Put the cross-lane work in an **Orchestrator**: it gathers the data (the display name, or the merged-account ids) and calls Audit *with* the list. Audit never reaches out."
> **Dev:** "So a Crosscut is just a Section that everyone uses?"
> **Architect:** "Essentially yes — a Crosscut owns its own **Lane** (the audit log) but carries no *other*-section logic. The role that owns **no** lane at all is the **Orchestrator**. The moment a Crosscut carries section-specific logic, it has stopped being a Crosscut."
