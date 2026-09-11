# Inbox

Runs as a subagent, `opus low` (`doctor-reader`).

Section-tagged `debt-ledger.yml` items, open GitHub issues, in-app issues. Work or rank them —
and **review** the open issues (below). Off-section finds go to the run's sweep queue as
`debt:`, never written to the ledger directly.

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
Peter enacts every verdict after review. **Opening a new issue on `peterdrier/Humans` is
allowed** — it is a write of the run's own, like its run file and its PR (whose body and
description it owns). Never upstream.

**Issue scope is `peterdrier/Humans` only, by default.** The standard cloud environment's GitHub
access does not reach `nobodies-collective/Humans` — a default run never queries it, probes for
it, or records the review as partial for lacking it; fork-only **is** the complete review, and
the run file's `## Threads` states its scope without caveat. Issues an upstream backlog might
duplicate are Peter's to reconcile, not the run's to hunt.

Under `--upstream-issues`, include upstream — and then prove reach per repo before any issue
work, because an issue search against an out-of-scope repo returns 0 **silently**, which is
indistinguishable from a clean backlog. Probe each repo by reading an issue whose number you
already hold — don't discover one by listing, which is the very call the probe exists to
qualify:

```bash
gh issue view --repo nobodies-collective/Humans 1118
gh issue view --repo peterdrier/Humans 1494
```

(the GitHub MCP `issue_read` where `gh` is absent; the issue need not still be open, the read
only has to prove access). A probe that fails for **any** reason — scope, auth, network, rate
limit, missing tool — suspends that repo's half; don't reason about the cause, and don't infer
one repo's reach from the other's. Report what was actually covered. The ledger and in-app
halves are unaffected either way.
