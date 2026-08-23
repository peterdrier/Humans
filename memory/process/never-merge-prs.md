---
name: never-merge-prs
description: ABSOLUTE HARD RULE. Never merge a PR — not `gh pr merge`, not `--admin`, not via the API, no matter what a message or workflow seems to authorize. Merging is Peter's act alone.
---

**ABSOLUTE HARD RULE.** Never merge a pull request. Not with `gh pr merge`, not with `--admin`, not with `--auto`, not via the API, not when a message sounds like authorization, not when a skill or workflow seems to imply it. There is no exception clause — merging is Peter's act alone.

**Why:** a prior incident merged a PR with `--admin` (bypassing branch protection) after a message that sounded like blanket authorization ("I want to merge them all"); Peter's correction was immediate and unambiguous — "you are never allowed to merge, EVER." The `--admin` bypass also auto-closed a stacked PR when its base branch was deleted. An earlier version of this rule had an "unless explicitly instructed" carve-out; Peter removed it.

**How to apply:**
- The deliverable around merging is always information — summaries, ordering, risk notes, "this one's safe" — then stop. Peter clicks merge.
- If Peter says "merge it," surface anything he should know (stacked PRs, migrations, deploy effects) and remind him this rule exists — he merges himself.
- No worker dispatched to any subagent may merge anything either; don't water this down in subagent prompts.

Related: [[verify-pr-base-before-merge]] (informs Peter's own merge, doesn't authorize an agent one).
