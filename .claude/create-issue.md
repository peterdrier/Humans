# Issue conventions for Humans

Read by the `pd:create-issue` skill. Where this conflicts with the skill, this file wins.

## Which repo

Two repos hold issues; pick by kind and pass `--repo` explicitly ([`issue-home-routing`](../memory/process/issue-home-routing.md)):

- `nobodies-collective/Humans` — community/user feedback, project direction, anything teammates need to see or vote on.
- `peterdrier/Humans` — bugs found while fixing things, tech debt, architecture migrations, anything a cloud/agent session creates. A cloud session cannot write to upstream: file on the fork and flag it for re-homing.

Cross-repo references are always qualified (`peterdrier/Humans#123`), never a bare `#123`.

## Required labels

- **Type:** bug, enhancement, documentation, etc. Always.
- **Section:** `section:{name}` — always. If no section label fits, put `**Section:** TBD` in the body instead of guessing ([`issues-need-section`](../memory/process/issues-need-section.md)). `gh label list -R <repo> --search section:` shows the current set.
- **Size:** `size:XS` (<30 min), `size:S` (30 min–2 hr), `size:M` (2–6 hr), `size:L` (6–16 hr), `size:XL` (16+ hr).
- **Tier:** `tier:direct`, `tier:lightweight`, `tier:standard`, `tier:thorough`. Defaults from size: XS→direct, S→lightweight, M→standard, L/XL→thorough.
- **DB migration:** `db:yes`, `db:no`, `db:maybe`.

Size, tier and db only when you have enough context; a missing label beats a wrong one. Status labels `blocked:spec-incomplete` and `blocked:needs-design` exist on both repos.

## Body

Add a **Key files** line listing the likely files to change, using section paths (`src/Sections/Humans.<Section>/...`).
