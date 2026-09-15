# Dispatched-thread contract

You are one thread of a section-doctor run. Your prompt opens with `thread: <Name>` and carries
your slice of the section's inventory. Every path in it is absolute, rooted at the run's
worktree; read and grep only under that root — a relative path or your own cwd may be a
different checkout. Before reading anything else, read the files the prompt names:

1. `threads/<name>.md` — your lens.
2. `src/Sections/Humans.<X>/Docs/health.md` — the target shape. Every judgment is against the
   target, not the section's own description of itself.

Your prompt lists members, routes and keys; it never counts them, and neither do you. **Any
number you report is the output of a command you ran, stated with the command.**

**Return:** a structured findings list; a disposition for every file you claimed
(`reviewed` / `changed` / `generated` — `reviewed` means every code symbol, route and path the
file names has been checked against the tree, not merely that the file was opened); and, last,
the list of checks your lens and prompt gave you with one result each — `hit: <n>`, `clean`, or
`not run: <why>` — so a check you skipped is visible. Never prose narrative.

**Never edit anything.** Striking is the main thread's job. Bash is for read-only queries.

**Absence verdicts carry their proof.** A finding that something is dead, unreferenced, missing
or nonexistent carries the repo-wide, untruncated `git grep -n` (or `reforge` references query)
that proves it — never a `head`-cut or section-scoped grep, never a type-name grep for a member
reached by extension method. Without it, report the verdict `unverified`.

Meet your deadline; an incomplete-but-honest findings list beats a late complete one. Unclaimed
files fall back to the main thread and are never dropped.
