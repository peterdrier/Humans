---
name: Issue home routing — which repo gets a new issue
description: Both repos accept issues. Community feedback and project-direction issues go to nobodies-collective/Humans; bugs found during fixes, tech debt, arch migrations, and anything agent-created go to peterdrier/Humans. Existing issues stay where they are.
---

Both repos have issues enabled. Route new issues by kind:

- **Upstream (`nobodies-collective/Humans`)** — community/user feedback (from `/triage`), project-direction and product decisions, anything teammates need to see or vote on.
- **Fork (`peterdrier/Humans`)** — bugs discovered while fixing things, tech debt, architecture migrations, and anything a cloud/agent session creates.

Existing issues stay where they are — no migration.

The two lists can overlap for agent-created content; capability decides: a cloud agent files on the fork even for feedback/direction material (it cannot create upstream issues) and flags it for Peter to re-home, while sessions with upstream access route by content.

**Why:** Cloud sessions can't create issues on upstream (no write access), and Peter's engineering backlog doesn't need teammate visibility. Direction-level issues stay where Daniel and teammates watch.

**How to apply:**

- When creating an issue, pick the home repo by the list above and pass `--repo` explicitly.
- When listing/searching the backlog (`/triage`, `/sprint`), sweep **both** repos.
- Unqualified refs are never allowed, anywhere — always `owner/Humans#N` per [[issue-refs-qualified]]. Consumers (triage, sprint, spec review) must never default a bare `#N` to either repo: stop and resolve it. Auto-close keywords are the sharp edge — a bare `Fixes #N` in a fork commit re-resolves against upstream when promoted via `/pr-prod` and can close the wrong issue; `Fixes peterdrier/Humans#N` closes correctly from either repo.
- Labels are cloned from upstream (2026-08-23); [[issues-need-section]] applies on both repos.
