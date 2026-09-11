# Resume mode

`resume` gathers the queue from both places an item can live, then works it. No new assessment
or strike work.

1. **Open runs:** discover by branch-name prefix — `--search "head:..."` matches exact names,
   not prefixes:
   ```bash
   gh pr list --repo peterdrier/Humans --state open --limit 200 --json number,headRefName \
     --jq '.[] | select(.headRefName | startswith("section-doctor/"))'
   ```
   Each PR body's `## Needs Peter` block (authoritative for unmerged runs; their run files
   only exist on the PR branch).
2. **Merged runs:** unticked (`- [ ]`) `## Needs Peter` entries in `docs/health/runs/*.md` on
   `origin/main`.

Present the open items inline, then apply each answer. **A ruling is not applied until a grep
says it is** — before ticking, grep the branch for the finding's distinguishing terms (the issue
it was filed under, the method or type name, the abolished case) across **`.cs` as well as
`.md`**. The grep is a completeness gate, not a licence to edit: only a ruling that makes the
claim false — the case abolished, the method gone, the defect fixed — sends you to the hits, and
then every hit is corrected, not just the one that prompted the finding. A `keep`, `not a defect`
or `deferred` leaves those hits standing. **Every ruling lands on the finding either way** — the
ranked entry records what Peter decided, so a rejected finding stops asserting a defect and a
deferred one says it is deferred. That is the state change; the checklist only ticks. Doc comments
are documentation and drift exactly like it, and counting the copies from memory always
undercounts. Then tick the item — `- [ ]` becomes `- [x]`:

- **Open-PR item** — commits on that item's PR branch (reuse its worktree, or recreate from the
  branch). Tick the item in **both** places: the PR body *and* the branch's run file — an
  unticked run file would resurface as a merged-queue item after the PR lands and get re-asked
  or applied twice. Push.
- **Merged items** — group by run file: one fresh branch off `origin/main` **per run file** (its
  own worktree locally; sequentially in the repo root in a cloud run), applying all of that
  file's answers and ticking each entry, push, one PR per run file
  (an answer pushed to a branch with no PR is stranded), tear the worktree down. One writer per
  run file — several same-file answers in separate PRs would conflict with each other; grouped,
  these PRs cannot conflict with each other or with concurrent doctor runs.

