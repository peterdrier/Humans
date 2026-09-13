# Phases 5–6: Bookkeeping and retro

## Phase 5: Bookkeeping

**Re-read this file and `pr.md` before writing anything below.** By Phase 5 the run is hours
past the skill load and may have been auto-compacted; a compaction summary keeps the goals and
sheds the verbatim mechanics, which is exactly how runs have half-done this bookkeeping. The
re-read costs a few thousand tokens and re-anchors either way.

**A run's shared-file writes are confined to the sweep commit below.** In the same
worktree/PR, three bookkeeping writes:

- The section's `Docs/health.md` history row (per-section; the blocked set guarantees at most
  one open run per section, so it cannot collide). **The row is run, date, headline and PR link —
  never a score.** A score written here is stale by construction: every commit after it, every
  answered Needs-Peter item and every review round moves the number it claims, and Phase 7 rightly
  forbids the correcting commit that would chase it. The PR the row links to carries the score
  against the head that shipped.
- **This run's own file** — `docs/health/runs/<yyyy-mm-dd>-<Section>.md` (UTC date from the run
  timestamp; if the path already exists at the branch point, suffix `-<HHMMZ>`). Sections:
  run header (invocation, anchor commit, budget, `PR: pending`), assessment summary, the ranked
  findings list, worked, skipped + why (including sections passed over as blocked), retro
  (Phase 6), `## Needs Peter` checklist — **`- [ ]` unanswered, `- [x]` answered and applied,
  one item per line** — holding Phase 4's skipped classes, 3d's open-issue recommendations and
  Phase 6's proposed edits, each `<finding #> — <the question, in a phrase>` — and `## Sweep queue`
  (`debt:` / `memory:` items as plain bullets — a later run's sweep applies them after this run
  merges; nothing ever ticks them). Lessons about this skill are **not** sweep-queue items: they
  are Needs-Peter findings and nothing else (Phase 6).

  **One prose description per finding, where it was first written, and nowhere else.** For a 3e
  finding that is the ranked list; for one raised later — a Phase 4 skip, a Phase 6 lesson, a
  Phase 7 measurement gap — the block that raised it. Every other mention (assessment summary,
  `## Skipped`, `## Needs Peter`, the PR body) cites the number and adds nothing a later ruling
  could invalidate. A Needs-Peter ruling is a state change to a finding — "not a defect", "done",
  "filed", "deferred" — and it lands on that one description, or the copies drift.

  **A finding number is assigned once and never changes** — not on a reorder, not when an item is
  struck, not when a ruling abolishes it. 3e numbers the ranked list; a finding raised after 3e
  takes the next unused number as it is written, and no number is ever reused. Key Needs-Peter
  items and PR-body references to the finding number, never to queue position: the two diverge the
  moment either list is reordered, and a position-matched tick marks the wrong item.

  Plus two blocks that make the Purpose's tests answerable rather than assertable — the size
  test is answered by the PR's own diff stats:

  - **`## File coverage`** — a disposition for every path in the 3a inventory: `reviewed`,
    `changed` or `generated`. Not a summary; the list.
  - **`## Threads`** — one row per thread: how it ran (main / subagent / self-run after a missed
    deadline), its model **copied from `$RUNDIR/assessment/threads.md`** (3d), and its findings
    count. For each that
    did not run, why — a silent skip is a failed run, not a quiet one. The model column plus the
    PR's cost comment (Phase 7) are what make "did the cheaper thread lose findings?" answerable
    across runs (nobodies-collective/Humans#1465); a run that leaves the model blank has decided
    that question for every run after it. **There is no cost column**: per-row dollars are
    measured after this file is committed and live in one place only — the cost comment, whose
    subagent rows carry the same thread names. Don't write "see `## Cost`" or any other
    cost pointer that names a section this file does not have.

  **Prose gate — `doctor.py prose-gate`, before every commit that carries prose this run
  wrote** (run file, `health.md`, `debt.yml`, any `.md`, rewritten `.cs` comment blocks, the
  sweep's writes). `no-derived-aggregates-in-docs` binds the author of new prose, and the run
  is that author. The gate lists every added line that types a count. A hit that counts a list or set — one this file carries, one another doc carries, or one the
  code owns — is deleted or replaced by the list or "all of them"; a measurement with a
  generator (a reforge score, a date, a PR number, a line reference) and a plain "two branches
  differ" stand. A typed count is wrong the first time the list changes, and a wrong one
  points a refactor at the wrong method.

  **The run file never describes its own diff.** No size block, no insertions/deletions, no line
  count of the branch or of the file itself: the commit that writes such a figure is a commit the
  figure must count, so it is stale on write and no care fixes that. Link the PR instead —
  GitHub's additions/deletions and the PR Surface Report are recomputed on every push and cannot
  be wrong. The section's reforge score is the same case: the run measures it to steer itself, and
  the PR's surface report publishes it against the final head — writing it into `health.md` only
  freezes a mid-flight figure.

- **The sweep** — its own commit, and the only place a run touches shared files: for every
  `## Sweep queue` item in merged run files under `docs/health/runs/` on `origin/main`, apply
  it — `debt:` → the owning section's
  `src/Sections/Humans.<X>/Docs/debt.yml` where one section owns the fix and
  `docs/architecture/debt-ledger.yml` otherwise, `memory:` → the named
  atom + INDEX line. Idempotence is the only bookkeeping; there is no anchor window. **The
  skips, each a grep, before an item is written:**

  1. **Already carried** — its distinguishing phrase is in its target on `origin/main` *or on
     any open `origin/section-doctor/*` branch*
     (`git fetch origin 'refs/heads/section-doctor/*:refs/remotes/origin/section-doctor/*'`,
     then `git grep` each). An unmerged doctor PR carrying the item is carrying it; two runs
     writing it produce byte-different duplicates.
  2. **Already fixed** — the claim it makes no longer holds: the symbol, file or rationale it
     names is gone from the branch. A fixed debt retired from its ledger stays retired.
  3. **Not debt** — its text says the code is intended or blessed, or it carries reforge
     figures or counts. Apply the qualitative row only, or nothing; the sweep is an author,
     not a copier, and the prose gate runs over its writes too.

  **The sweep never edits this skill, and never carries a lesson about it.** A proposed amendment
  lives in one place only — the `## Needs Peter` block of the run that thought of it (Phase 6) —
  and reaches the skill through Peter's answer there, inline (Phase 8) or via `resume` later;
  as a sweep item it would be re-asked on every later run, blind to the tick that closed it.
  **Never edit the swept run files** — resume is their only post-merge editor, which is what
  keeps resume conflict-free. Two piled-up unmerged runs can occasionally sweep the same item;
  the cost is one hand-resolved conflict, not corruption (the no-locking trade of PR #1366).

The runs directory **is** the log and the newest file **is** the last report. There is no
`log.md`, `last-report.md`, or generated index — never recreate them — and daily runs never
touch `docs/architecture/maintenance-log.md`.

## Phase 6: Retro + propose amendments

Four questions, answered honestly in the run file: what did the selector/rubric get wrong, what was
wasted motion, what did the assessment miss that striking revealed, and **what does the target
diff say** — 3c regenerated the target and diffed it against the previous run's; a change means
either the section moved or the earlier target was wrong, and which one it was is worth a line.
Then:

- **Mechanical lessons** → `## Needs Peter`, as a one-line proposed edit **naming the phase it
  governs**, under its own finding number (Phase 5) — that block, and nowhere else; never the
  sweep queue. Recording a lesson is the run's job; applying it to this skill is Peter's — never
  edit the skill's files directly, mid-run or in a sweep. A lesson that names no phase is a war
  story: leave it in the run file, which is where a run's history belongs.
- **Judgment lessons** (rubric axes, thresholds, play choices) → the Needs-Peter block.
- **Durable project rules** → `## Sweep queue` as `memory: <bucket>/<name> — <rule>`.

Commit all Phase 5 + 6 edits before Phase 7 pushes — the only thing that lands after is
Phase 7's own PR-number backfill commit.

