---
name: Governance is tier applications, board voting, and assembly votes
description: "Governance owns only Colaborador/Asociado applications, Board voting on them, and binding assembly votes. Don't file a feature under Governance because the Board uses it — audience isn't ownership."
---

The Governance section is exactly: **tier applications** (Colaborador/Asociado, the `Application` entity), **Board voting** on them, and **assembly votes** (binding, recorded votes of the Asociados on motions — spec `src/Sections/Humans.Governance/Docs/features/assembly-votes.md`, decided by Peter 2026-09-10). Its tables are `applications`, `application_state_history`, `board_votes`, and the `assembly_*` tables (`assembly_votes`, `assembly_vote_options`, `assembly_vote_roster`, `assembly_ballots`, `assembly_vote_peeks`, `assembly_ballot_history`).

Association-level things that live in their own sections — Working Groups, Surveys — do not move into Governance; they surface on `/Governance` through the Shell chrome slot `ChromeSlots.GovernanceDashboard`, so Governance references none of them (Peter 2026-09-10, during the Workgroups design).

Repeated failure mode: filing something under Governance because its users are the Board or it feels "governance-y". **The Board uses every feature in the app** — Board usage carries ZERO filing signal, and a `BoardOrAdmin` policy describes the *audience*, never the owning section. Past wrong examples: the **Audit log** (a Crosscut — see [[crosscut-purity]] — owned by no vertical) and **Surveys** (its own section; Board is merely its main user today, and its secret-ballot "Asociado vote" mode stays in Surveys — it is not an assembly vote).

**How to apply:** before filing anything under Governance — in `AdminNavTree`, section docs, or code — ask two questions: is it association-level rather than event-level, and does Governance actually own its tables? Grep the generated per-section maps, `src/Sections/*/Docs/data-access.md`, for the service, repository or table. Association-level but owned elsewhere → own section, tile on the Governance page via the seam. Event-level → never Governance, even presentationally.
