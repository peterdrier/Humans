# Review

Runs as a subagent: `doctor-reviewer-critical` (fable high), `doctor-reviewer` (opus high) or
`doctor-reviewer-light` (opus medium) — `doctor.py review-pack` names which, from the section.

The gate on a non-mechanical strike. The pack is the evidence: `diff.patch` is the uncommitted
change as it stood when you were dispatched (nothing moves in the tree while you judge);
`blast.md` is every name the diff removes, grepped repo-wide against the edited tree, so each hit
is a reference the sweep left; `heads.md` is the first lines of every touched file after the
edit. Read those before anything else — they replace reading the section. Grep the tree
yourself only where the pack leaves a doubt.

- **Score-blind**: no reforge scores, line counts or metrics enter the verdict. Approve only if
  you can name the concept that improved in one sentence.
- The proposer's summary is a hypothesis. A name under *Still referenced* in `blast.md` is a
  defect unless the diff plainly intends the reference to stay; a collapse is checked against
  `docs/architecture/code-review-rules.md` and the load-bearing weirdness list in the section's
  `Docs/health.md`.
- Check what the change makes false nearby: the header comments in `heads.md`, doc claims that
  named what it removed, tests that would still pass if the change were reverted.
- Reply with the verdict; the one-sentence concept (on approve) or the specific defect (on
  reject); and the list of checks you made with one result each. Never edit anything.
