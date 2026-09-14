---
name: Governance is tier applications + board voting only
description: Governance owns only Colaborador/Asociado applications and Board voting. Don't file a feature under Governance just because the Board uses it — audience isn't ownership.
---

The Governance section is exactly: **tier applications** (Colaborador/Asociado, the `Application` entity) and **Board voting** on them. Its tables are `applications`, `application_state_history`, `board_votes`.

Repeated failure mode: filing something under Governance because its users are the Board or it feels "governance-y". **The Board uses every feature in the app** — Board usage carries ZERO filing signal, and a `BoardOrAdmin` policy describes the *audience*, never the owning section. Past wrong examples: the **Audit log** (a Crosscut — see [[crosscut-purity]] — owned by no vertical) and **Surveys** (its own section; Board is merely its main user today).

**How to apply:** before filing anything under Governance — in `AdminNavTree`, section docs, or code — find the real owner by grepping the generated per-section maps, `src/Sections/*/Docs/data-access.md`, for the service, repository or table in question. If the owner isn't the Governance section, it doesn't go under Governance, even presentationally.
