---
name: Issue fetch protocol — always include comments + author, stop on unauthorized authors
description: HARD RULE. Before implementing ANY GitHub issue or PR, fetch it with both comments AND author included. The OP is often not Peter; his comments often flip OP intent. If `.author.login` is not an authorized author (`peterdrier`, `swombat`), STOP and get Peter's input first — never branch or code from an unauthorized issue without explicit per-issue approval.
---

Two coupled rules fire on every `gh issue view` (and on every batch dispatch that hands an issue to a worker). Both data points come from the same fetch, so they're covered together here.

## 1. Always fetch comments — never body-only

For every `gh issue view` / `gh pr view`, include comments:

```bash
gh issue view <N> --repo <owner>/Humans --json title,body,author,comments
# or for plain mode:
gh issue view <N> --repo <owner>/Humans --comments
```

**Why:** Implementing from the body alone has repeatedly produced wrong features. Concrete instance 2026-04-24: issue `nobodies-collective/Humans#578` OP proposed adding an `isConfirmedForYear` flag. Peter's comment said the `/year` URL filter should natively exclude camps not in that edition (i.e., fix the filter, not add a flag). Body-only implementation shipped PR #310 with the flag — had to be closed as a wrong feature.

No hook currently enforces this — it's discipline, not tooling. (`.claude/settings.json` registers hooks for EF output dir, Razor lint, and data-migration checks, but nothing that inspects `gh issue view` / `gh pr view` arguments.)

**How to apply:**

- Always pass `--json title,body,author,comments` (or `-c` for plain mode).
- When dispatching batch-worker subagents (from `/execute-sprint`), fetch comments in the orchestrator's pre-flight spec pull AND include the comment text **verbatim** in the subagent prompt — subagents don't have `gh` context or the instinct to re-fetch.
- Treat comments from Peter (`peterdrier`) as authoritative when they conflict with the OP body; the body captures the OP's perspective, comments capture the decision. Daniel's (`swombat`) comments carry the same decision authority — see [`authorized-decision-makers`](authorized-decision-makers.md) for how to calibrate.
- If comments materially change the spec, flag it in the PR body ("per `<repo>#NNN` comment from @peterdrier on DATE, scope is …") so reviewers can trace the intent.
- Applies to `gh pr view` too: review comments and discussion often contain the real ask.

## 2. Unauthorized authors require explicit per-issue approval

**HARD RULE.** Before implementing any GitHub issue, check `.author.login`. The authorized authors are `peterdrier` and `swombat` (Daniel — see [`authorized-decision-makers`](authorized-decision-makers.md): full decision authority; agents compensate for his lower architectural/reuse context by checking rules and existing surface themselves and raising findings with him). If the author is anyone else, STOP. Do not branch, do not code, do not dispatch a subagent. Bring the issue to Peter with the body + comments + author and ask for direction.

**Why:** Unauthorized issues (filed by teammates — `micho`, website team, external reporters) have a different truth level than Peter's own issues. Teammates file what they think is needed; Peter decides whether the system should do it, and what shape it should take. Shipping their framing verbatim is how the wrong feature gets built — has happened ~10+ times. Concrete 2026-04-24 incident: `nobodies-collective/Humans#577` and `#578` were both authored by `micho`; PRs #309 and #310 shipped without asking. #310 added a flag Peter didn't want; #577 (CORS) was fine in intent but should have been confirmed first.

**How to apply:**

- Every `gh issue view` MUST surface the author: `--json` queries must include both `author` and `comments`.
- If `author.login` is not an authorized author (`peterdrier`, `swombat`), present the issue (title, full body, every comment, author) to Peter and ask: "proceed as described, re-scope, or skip?". Do NOT make that call yourself.
- In `/execute-sprint`: the orchestrator's pre-flight spec pull MUST classify each issue by author. Any unauthorized-author issue becomes a *gated* item — surface it for confirmation before dispatching a subagent. **Never** include an unauthorized-author issue in the autonomous execution wave without explicit per-issue approval.
- Sprint plans produced by `/sprint` that batch unauthorized-author issues should explicitly mark them so the execution gate fires.
- Peter's comments on an unauthorized-author issue are part of the approval signal — if he has already commented with direction ("do X" or "don't do this"), treat that as the approval itself and cite it in the PR body.
- Applies to both repos: `nobodies-collective/Humans` and `peterdrier/Humans`.

**Related:** [`triage-protocol`](triage-protocol.md) — adjacent rule for user-feedback-originated issues that propose spec changes. [`privilege-changes-need-explicit-approval`](privilege-changes-need-explicit-approval.md) — narrower rule for capability grants.
