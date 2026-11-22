# Issue drain

Daily unattended pass over the open issues on peterdrier/Humans. Read CLAUDE.md and AGENTS.md first and follow them: Peter merges, PRs go to `origin/main`, migrations and DB state are never hand-edited, and a destructive migration drop needs Peter's approval per drop.

1. **Pick.** List the open issues (not PRs). N = max(3, ceil(10% of open)). Skip issues labeled `question` unless Peter has commented since the label went on. Pick N, preferring bugs with evidence of real user impact, then the oldest. If none qualify, say so and stop.
2. **Vet each one** with the `vet` skill if it's available. Otherwise judge it on whether it is still true, a real requirement, plausible at this scale (Peter is the only admin, so multi-admin races are not real), backed by evidence, worth the cost, and not a dupe. Verify against the code, never the issue's own claims.
3. **Act on the disposition:**
   - Already fixed, or an exact dupe: close it with a comment citing the commit or the surviving issue.
   - Invalid or implausible: comment with the recommendation and the factor that failed, add `question`, and leave it open.
   - Fix: if it is small and the right fix is clear, fix it on its own branch with a test and open a PR that says "Fixes peterdrier/Humans#NNN". At most N PRs per run.
   - Needs approval or design: comment one focused question with a recommended answer, and add `question`.
   - Feature or epic: comment recommending a move to nobodies-collective/Humans, add `question`, and don't move it yourself.

   End every GitHub comment with the Claude Code attribution footer.
4. **Report** in at most 10 lines: each issue, its disposition and a link, with the items that need Peter first.
