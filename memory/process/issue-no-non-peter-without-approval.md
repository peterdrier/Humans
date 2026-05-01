---
name: Never implement non-Peter-authored issues without Peter's explicit go-ahead
description: HARD RULE (hook-enforced). If `.author.login != peterdrier`, STOP. Bring the issue to Peter (body + comments + author) and ask for direction before any code.
---

**HARD RULE.** Before implementing any GitHub issue, check `.author.login`. If it is NOT `peterdrier`, STOP. Do not branch, do not code, do not dispatch a subagent. Bring the issue to Peter with the body + comments + author and ask for his input.

**Why:** Non-Peter issues (filed by teammates — `micho`, website team, external reporters) have a different truth level than Peter's own issues. Teammates file what they think is needed; Peter decides whether the system should do it, and what shape it should take. Shipping their framing verbatim is how the wrong feature gets built — has happened ~10+ times. Concrete 2026-04-24 incident: `nobodies-collective/Humans#577` and `#578` were both authored by `micho`; PRs #309 and #310 shipped without asking. #310 added a flag Peter didn't want; #577 (CORS) was fine in intent but should have been confirmed first.

**How to apply:**

- Every `gh issue view` MUST surface the author (hook-enforced via `require-gh-comments.sh`: `--json` queries must include both `author` and `comments`).
- If `author.login != "peterdrier"`, present the issue (title, full body, every comment, author) to Peter and ask: "proceed as described, re-scope, or skip?". Do NOT make that call yourself.
- In `/execute-sprint`: the orchestrator's pre-flight spec pull MUST classify each issue by author. Any non-Peter issue becomes a *gated* item — surface it for confirmation before dispatching a subagent. **Never** include a non-Peter issue in the autonomous execution wave without explicit per-issue approval.
- Sprint plans produced by `/sprint` that batch non-Peter issues should explicitly mark them so the execution gate fires.
- Peter's comments on a non-Peter issue are part of the approval signal — if he has already commented with direction ("do X" or "don't do this"), treat that as the approval itself and cite it in the PR body.
- Applies to both repos: `nobodies-collective/Humans` and `peterdrier/Humans`.
