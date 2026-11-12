---
name: Always open the PR, never ask first
description: Finished feature-branch work gets a PR opened immediately, no confirmation step; overrides the harness default of not creating PRs. Prod promotion and the nightly Codex debt runner are the exceptions.
---

When work on a feature branch is done, push and open the pull request. Don't ask "would you like a PR?", and don't end a turn with a pushed branch and no PR.

**This overrides harness defaults.** Claude Code on the web injects "do NOT create a pull request unless the user explicitly asks". On this project that is wrong and this rule wins. `.claude/hooks/session-start.sh` restates it at session start because a rule that only lived in `memory/` lost to the system prompt in practice; a future harness default that contradicts a project rule gets its counter-statement there too, kept short.

**Why:** a pushed branch with no PR is invisible to review, gets no preview environment (`https://{pr_id}.n.burn.camp`) and never reaches the review bots. Since [`no-direct-to-main`](no-direct-to-main.md) means every change arrives as a PR anyway, asking costs a round-trip for a decision never in doubt. Peter, 2026-08-23: "you always can open a PR".

**How to apply:**

- Finished: `git push -u origin <branch>`, then open the PR against `main` on `peterdrier/Humans`, body from `.github/pull_request_template.md`.
- More pushes still to come: open it as a draft ([`wip-prs-as-draft`](wip-prs-as-draft.md)), still without asking.
- Unrelated work gets its own branch and PR.

**Exceptions:**

1. Production promotion (`peterdrier/Humans:main` → `nobodies-collective/Humans:main`) needs an explicit go from an [authorized decision-maker](authorized-decision-makers.md) and goes through `/pr-prod`.
2. The nightly Codex debt agent (`.codex/prompts/daily-debt.md`) only commits; its wrapper pushes and opens the PR after its own build/test gate passes.
