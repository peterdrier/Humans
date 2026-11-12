# Comments

Runs as a subagent, `opus low` (`doctor-reader`), over `$RUNDIR/assessment/comments.txt` — the output
of `doctor.py comments <Section>`, `path:line: text`. Open a source file only where the verdict needs
the code around the line.

Every comment in the section's inventory, rewritten or deleted. Cut what restates the next line,
decision history, hedging, reassurance addressed to the next agent.

**Cut test: a comment survives only if it carries something the code cannot say.**

Where a comment and the code disagree about what the code does *now*, the code is the truth for
the behaviour and the comment may be recording the intent the code drifted from: report both,
never rewrite the comment to match the code.
