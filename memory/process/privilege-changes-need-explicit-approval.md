---
name: privilege/permission/role changes need explicit per-change approval
description: triggers when a change grants users new or elevated capabilities — Drive roles, auth scopes, role memberships, admin flags, default permission tiers, public-route allowlists. Stop and get Peter's explicit per-change approval; never autonomously execute, regardless of issue tier or sprint plan.
---

Any change that grants users new or elevated capabilities requires Peter's explicit per-change approval. Stop and ask before implementing — do not autonomously execute, regardless of how the issue is sized, what tier the sprint plan assigned, or how mechanical the diff looks.

Examples that fire this rule:
- Bumping a default permission tier (e.g. Drive `Contributor` → `ContentManager`, `writer` → `fileOrganizer`)
- Granting a new role / role group access to a route, controller, or feature
- Adding origins to a CORS allowlist beyond clearly-internal dev origins
- Lowering an `[Authorize]` requirement (e.g. `Admin` → `Coordinator`, `Authenticated` → `AllowAnonymous`)
- Adding a new admin flag, override switch, or "trust this user" boolean
- Changing the default access level granted on a newly-created resource
- Expanding what a role can see/edit/delete on existing resources
- Adding bypass paths to authorization or rate-limiting

The fact that the diff is "small" or "mechanical" or "follows the issue spec exactly" does NOT make it safe — privilege changes are evaluated by *what they grant*, not by LOC.

**Why:** PR #398 (2026-05-03) shipped a global default bump from Drive `Contributor` to `ContentManager` for every team member, autonomously, because one user (`fb:0545bf5e`) asked to be able to delete files. The triage system created a clean "bug" issue, the sprint sized it as XS/lightweight, the orchestrator dispatched it to a worker, and the worker shipped a PR that gave every user in the system move/delete on shared Drive content. Peter caught it at PR review — but every layer of the autonomous pipeline failed to escalate. Privilege escalations are categorically different from bugs; they need a human deciding "should this group of people have this power?" before any code is written.

**How to apply:** When you (or a subagent you dispatch) are about to touch a default permission, role grant, allowlist, `[Authorize]` attribute, or any switch that controls what a user can do — STOP. Ask Peter explicitly: "This grants `<who>` the ability to `<what>` on `<which resources>`. Confirm before I proceed?" Wait for an explicit "yes, proceed" — a question, a "what do you think?", or silence is not approval.

This applies inside `/triage`, `/sprint`, `/execute-sprint`, batch-worker prompts, and any agent dispatch chain. If the issue body looks like a privilege/permission change, route it to Peter for approval BEFORE planning, BEFORE batching, BEFORE dispatching a worker — not after the PR is open.

If you're a worker and you discover mid-implementation that the change crosses into privilege territory (e.g. the "small fix" turns out to bump a default role), STOP, do not push, report back to the orchestrator, and let the orchestrator escalate. Never assume "the spec said do it, so it's authorized" — the spec might have been laundered through an unsafe pipeline.

**Related:** [`user-feedback-spec-changes-need-review`](user-feedback-spec-changes-need-review.md) — adjacent rule: any user-originated feedback that proposes a behavioral/spec change (not just a mechanical fix) needs Peter's review before it enters the autonomous pipeline.
