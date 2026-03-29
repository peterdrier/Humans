---
name: spec-review
description: "Review code changes against linked GitHub issue specs to catch implementation drift — building something plausible but wrong. Checks each acceptance criterion against actual code."
argument-hint: "PR 64 | #264 #265 | (no args = review current branch)"
---

# Spec Compliance Review

Review whether the code changes actually implement what the linked GitHub issues specify. This is NOT a code quality review — it answers "did you build the right thing?"

Follow the full process documented in `.claude/agents/spec-compliance-reviewer.md`.

## Input

`$ARGUMENTS` can be:
- `PR <number>` — review an existing PR on peterdrier/Humans
- `#NNN #NNN ...` — review current changes against specific issues on nobodies-collective/Humans
- Empty — scan recent commit messages for issue references and review against those

## Execution

1. Identify issues (from PR body, arguments, or commit messages)
2. Fetch each issue from `nobodies-collective/Humans` via `gh issue view <number> --repo nobodies-collective/Humans`
3. Extract all acceptance criteria and behavioral requirements from each issue
4. Read the actual code changes (PR diff or local diff)
5. For each criterion, check the CODE (not the PR description) — quote evidence for PASS, explain divergence for FAIL
6. Detect systemic drift (wrong data source, wrong audience, missing conditional branches)
7. Produce the structured report from the agent doc

## After Review

Present the report. Do NOT make any code changes. If there are BLOCKING issues, clearly state what needs to change before the PR can merge.
