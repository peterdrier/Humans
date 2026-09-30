---
name: Issue readiness — work only on `ready` issues; agent-filed issues are `unverified`
description: Before picking up any issue, check its label: only `ready` issues are agent work; label every issue you file `unverified`; never fix an `unverified` issue.
---

Work only on issues labelled `ready`, unless Peter asks for a specific issue in the session. Label every issue you file `unverified`, and never fix an `unverified` issue.

**Why:** Codex reported an issue and Claude fixed it blindly; the result was a complicated change for an absurd case nobody had checked. Peter's judgement is the gate between "reported" and "worth doing" — the issue-level twin of [[review-finding-triage]].

**How to apply:**

- `unverified` — reported by an agent or a review; nobody has confirmed it is a real problem. Leave it alone.
- `blocked:needs-design` — real, but needs a design or a decision from Peter first.
- `ready` — confirmed and designed; an agent can run it unattended.
- `now` — on the short priority list.
- Only Peter sets `ready` and `now`. No readiness label means unsorted: ask, don't start.
- Applies in both repos (`peterdrier/Humans` and `nobodies-collective/Humans`).

**Related:** [[review-finding-triage]], [[issue-home-routing]], [[issue-fetch-protocol]]
