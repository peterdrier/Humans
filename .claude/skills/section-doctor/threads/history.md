# History

Runs as a subagent, `opus low` (`doctor-reader`), over `$RUNDIR/assessment/history.txt` — the output
of `doctor.py history <Section>`, `path:line: text`. Open a source file only where the verdict needs
the code around the line.

Prose narrating a prior state: a deleted/renamed project or type, a migration/lane number, "used
to live in X", "the first section to Y", a dated run post-mortem, rationale for a decision no
longer contested.

**Cut test: keep only if it changes what a reader does** — a live constraint, a non-obvious
invariant, a landmine that bites if reverted. A load-bearing "why" moves to the issue, linked,
not narrated in the file.

**Dated design records** (`src/Sections/*/Docs/20*.md`) are history by design
(`current-state-docs-no-history`): judged on dangling references and false *current* claims
only; deletion is proposed only when the record is over a month old **and** every fact it
carries is confirmed present elsewhere in the corpus.
