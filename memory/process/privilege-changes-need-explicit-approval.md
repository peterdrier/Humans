---
name: privilege-changes-need-explicit-approval
description: HARD RULE. Any change granting users new/elevated capability (Drive roles, auth scopes, role/admin grants, allowlist expansions) needs Peter's explicit approval first.
---

Any change that grants users new or elevated capability needs Peter's explicit approval for that change
*before* it is implemented, however the issue is sized and however mechanical the diff looks. A
privilege change is judged by what it grants, not by its size.

Fires on:
- Raising a default permission tier (a Drive role, the access level on a newly created resource).
- Granting a role access to a route, controller or feature; widening what a role can see, edit or
  delete.
- Lowering an `[Authorize]` requirement.
- Adding origins to a CORS allowlist beyond clearly internal dev origins.
- A new admin flag, override switch or "trust this user" boolean.
- Any bypass path around authorization or rate limiting.

**How to apply:**
- Stop and ask: "This grants `<who>` the ability to `<what>` on `<which resources>`. Confirm before I
  proceed?" Only an explicit yes counts; a question back or silence does not.
- Route it to Peter before planning, batching or dispatching a worker, not after the PR is open. This
  holds inside triage, sprint planning and execution, and any dispatch chain.
- A worker that discovers mid-implementation that the "small fix" changes a privilege stops, doesn't
  push, and reports back. "The spec said so" is not authorization.

**Why:** one user asked to be able to delete Drive files; the autonomous pipeline turned that into a
"bug", sized it small, and shipped move/delete rights on shared Drive content to every team member.
Every layer failed to escalate.

**Related:** [`triage-protocol`](triage-protocol.md)
