---
name: Governance is tier applications + board voting only
description: The Governance section owns Colaborador/Asociado applications and Board voting — nothing else. Do not file features (nav groups, docs, code) under Governance because Board members happen to use them; audience is not ownership.
---

The Governance section is exactly: **tier applications** (Colaborador/Asociado, the `Application` entity) and **Board voting** on them. Its tables are `applications`, `application_state_history`, `board_votes`.

Repeated failure mode: filing something under Governance because its users are the Board or it feels "governance-y". **The Board uses every feature in the app** — Board usage carries ZERO filing signal, and a `BoardOrAdmin` policy describes the *audience*, never the owning section. Past wrong examples: the **Audit log** (a Crosscut — see [[crosscut-purity]] — owned by no vertical) and **Surveys** (its own section; Board is merely its main user today).

**How to apply:** before filing anything under Governance — in `AdminNavTree`, section docs, or code — check the owner in `docs/sections/_Index.md`. If the owner isn't the Governance section, it doesn't go under Governance, even presentationally.
