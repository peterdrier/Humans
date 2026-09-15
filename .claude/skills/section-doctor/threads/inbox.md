# Inbox

Runs as a subagent, `opus low` (`doctor-reader`).

Section-tagged `debt-ledger.yml` items, open GitHub issues, in-app issues. Work or rank them —
and **review** the open issues (below). Off-section finds are reported as findings; main writes
them to the owning ledger.

## Open-issue review

Nothing else checks whether the section's open issues are still *correct* — and a run that has
just read the section end to end is the best-informed reader of that backlog anyone gets.
Throwing that away is how a backlog drifts: issues describing files that moved, asking for
behavior that shipped, contradicting each other or the section doc, or predating a project
split that changed the answer.

Review each one against **this run's target shape and inventory**, on four lenses:

- **Validity** — does it still describe real code? Do its paths, types, routes and project
  names resolve? Was it shipped already?
- **Consistency** — does it contradict the section doc, another open issue, or a hard rule?
- **Freshness** — does it predate a change (section split, read-split, a deleted context) that
  changes the answer or the scope?
- **Spec quality** — are the acceptance criteria still meaningful? Is the section label present?

Output is a **recommendation, never an action.** Each reviewed issue is one finding carrying the
issue ref, a verdict of `close` / `edit` / `relabel` / `keep`, and the one-sentence reason —
that is the finding's one prose description. Cap the pass at the section's open issues: one line
each, never a budget.

**A run may not mutate an existing GitHub issue.** No close, no edit, no relabel, no comment on
another issue — including issues this run's own findings duplicate, and including a `keep`.
Peter enacts every verdict after review. A carry-forward item you report as still open is a
candidate only: main re-checks it against the code before it is ranked.

**Issue scope is whatever the main thread proved it can read.** The thread has no GitHub tool:
it reads the issue dump main wrote to `$RUNDIR/assessment/issues.md` (path in the prompt) and
never queries GitHub itself. Main builds that file before dispatch:

1. **Prove reach per repo** — read an issue whose number you already hold, never one
   discovered by listing (a search against an out-of-scope repo returns 0 **silently**, which
   is indistinguishable from a clean backlog):

   ```bash
   gh issue view --repo nobodies-collective/Humans 1118
   gh issue view --repo peterdrier/Humans 1494
   ```

   (the GitHub MCP `issue_read` where `gh` is absent; the issue need not still be open, the
   read only has to prove access). A probe that fails for **any** reason — scope, auth,
   network, rate limit, missing tool — marks that repo `not covered: <reason>`; don't reason
   about the cause, and don't infer one repo's reach from the other's.
2. **List the section's open issues** in every repo that proved reachable, into the file:
   one status line per repo at the head (`peterdrier/Humans: covered` /
   `nobodies-collective/Humans: not covered: <reason>`), then each issue's ref, title, labels
   and body — the labels are what the spec-quality lens's section-label check reads.

Both repos are in scope whenever they are readable; the environment decides, not a flag. The
thread's return names the repos it reviewed exactly as the status lines say, and `## Threads`
carries the same lines. An empty list under a `covered` repo is a clean backlog; under a
`not covered` one it is nothing, and is recorded as nothing.
