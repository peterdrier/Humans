---
name: section-doctor
description: "Daily per-section review cycle driving a section toward the smallest, clearest form that still does everything it does today. Reads the global plan at docs/health/plan.md (replanning over every section when exhausted or stale), inventories every file in the section, derives the target shape before running any scan, then works parallel threads — shape, behavior/bugs, freshness, conformance, tests, prose/nav, inbox — into one ranked list and strikes it on a 2-3h budget. One PR per run; each run's report + Needs-Peter queue lives in its own docs/health/runs/ file; 'resume' applies Peter's answers later. Use for the morning section-improvement run, 'doctor <section>', or 'run section doctor'."
argument-hint: "[resume] [--section=<Name>] [--budget=2.5h] [--replan]"
---

# Section Doctor

Full design: `docs/superpowers/specs/2026-08-17-section-doctor-design.md`. This file is
self-amending — replan sweeps apply run lessons here; keep those edits terse and dated.

## Purpose

**Every section converges, run over run, on the smallest and clearest form that still does
everything it does today — and is correct.**

Feature work deposits sediment: duplicated helpers, comments that outlived their decisions, docs
describing a version that shipped two refactors ago, contracts wider than any caller, tests that
assert the mock. Nothing else in this repo removes it — reviews look at diffs, and sediment is in
no single diff. This is the only process that reads a section *as it now stands* rather than as it
was last changed.

A run is judged on three things:

- **Did the section get smaller and clearer without losing anything?** Line count is a fair proxy
  for the token weight every future reader, human or agent, pays to work here. Growth needs a
  stated reason — and cross-section consolidation is a good one, so the figure that matters is the
  **net across every section the run touched**, not a per-section floor.
- **Was every file actually looked at?** A finding-driven pass finds only what sits next to what
  it already suspects. Full coverage is what makes this a review rather than a sweep — and it is
  where the bugs come from: Guide's and Finance's defects both surfaced because something read
  everything, not because anything hunted for them.
- **Is the section still doing exactly what it did?** The constraint that buys all the latitude
  above.

**Contract: business functionality does not change.** That constraint is what buys the latitude
to rewrite anything else about the section.

**Concurrency contract (nobodies-collective/Humans#1069): a run writes no file that another
concurrent run also writes.** Runs never merge themselves, so N unattended days mean N open PRs
at once; every one must merge cleanly in any order. The only writers of shared files are replans,
which are single-writer by construction (see Phase 2).

## Invocation

| Form | Behavior |
|---|---|
| *(none)* | daily run: replan if needed, execute the next plan entry |
| `resume` | no new work — work the Needs-Peter queue (see Resume mode) |
| `--section=<Name>` | skip the plan, doctor this section |
| `--budget=<duration>` | override budget (default 2.5h); wall-clock, checked between items |
| `--replan` | force a fresh plan |

## Phase 0: Setup

`REPO_ROOT=$(git rev-parse --show-toplevel)`. Parse args; record start time (`date -u`).

## Phase 1: Worktree

```bash
git fetch origin main
TS=$(date -u +%Y-%m-%dT%H%M%SZ)
git worktree add $REPO_ROOT/.worktrees/section-doctor-$TS -b section-doctor/$TS origin/main
WORKTREE=$REPO_ROOT/.worktrees/section-doctor-$TS  # cd here; all commands run inside
```

Scope is frozen at the branch point — never reconcile against `origin/main` mid-run. Scope every
Glob/Grep to `$WORKTREE`.

## Phase 2: Plan check

**Find the live plan first.** Only replanning runs write `docs/health/plan.md`, and a replan
run's PR may still be open (runs never merge), so the newest plan may exist only on that branch.
Discover:

```bash
gh pr list --repo peterdrier/Humans --state open --limit 200 --json number,headRefName,title \
  --jq '.[] | select(.headRefName | startswith("section-doctor/"))'
```

(`--limit` is mandatory — `gh pr list` fetches only 30 by default and the prefix filter runs
client-side in `--jq`, so an older open run silently drops out without it. `--search "head:..."`
matches exact branch names, not prefixes — don't use it.) Read `docs/health/plan.md` from
`origin/main` and from each open run branch tip; the copy with the newest anchor date wins.
**Never commit another run's plan or run files into this run's branch** — a run commits only its
own writes.

Keep this result — the **blocked set** is the sections named by those open PRs' titles
(`doctor(<Section>): …`).

**Selection is fully derived — no tick marks, recompute it fresh every run:**

- A row is **done** when its section's `Docs/health.md` last-assessed date (read from
  `origin/main` — any newer copy lives on an open PR, and that section is blocked anyway) is on
  or after the plan's anchor date.
- A row is **blocked** while its section is in the blocked set.
- **Take the first plan row that is neither.** Done rows are finished for this cycle. Blocked
  rows are passed over today and picked up on a later run once their PR merges — unless the
  merge lands their `health.md` date past the anchor, which retires the row for this cycle,
  exactly as it should (the section was just doctored).

Rows passed over as blocked are noted in this run's run file under skipped
(`<section> — open PR #N`); they need no other bookkeeping — the date scan returns to them.

**Replan when:** no `plan.md` exists, no row is selectable, `--replan`, or a merged change since
the anchor materially reshapes an upcoming section (move/rename/major feature — routine churn is
not staleness). Exception: if **every** section has an open run PR, there is nothing to plan —
report the open PRs and go straight to Phase 9 teardown. Nothing has been written at this point,
so the worktree is clean and `git worktree remove` succeeds without `--force`.

**Replanning** (mid-level signals only — no deep reading). Replans are the only writers of
shared files, and they are rare: a full-cycle plan outlasts ~2 weeks of 2×/day runs, scheduled
invocations run one at a time, and a later run reads the open replan's plan from its branch
(newest anchor wins) rather than writing its own. **No locking** (Peter's call, PR #1366) — if
two replans ever do overlap, the cost is one hand-resolved conflict, not corruption.

1. `dotnet build Humans.slnx -v quiet` first — an unbuilt solution silently under-reports
   Reforge scores — then `reforge surface-score --format compact` for size + deltas. (The build
   also serves Phase 3/4.)
2. Order **every** `src/Sections/` project into one full-cycle table (~42 rows ≈ 2–3 weeks at
   2 runs/day): never-assessed first (score descending; seed last-served from merged run files
   under `docs/health/runs/`), then last-assessed ascending — stalest first, so a section merged
   yesterday sits at the end of the queue and comes around again next cycle. Tiebreak: score
   growth since last assessment (+10% outranks +3%), then open issues per section,
   `docs/architecture/debt-ledger.yml` items, churn under the section's paths.
3. **Include blocked sections** — their PRs merge mid-cycle, and their recent assessment dates
   put them at the end of the queue anyway. Exclude only sections with in-flight or
   imminently-planned feature work (check the active sprint plan).
4. Write anchor (commit + UTC date) and the full table to `docs/health/plan.md` — **no status
   column**; done-ness stays derived. The plan is advisory — run-day findings may extend a
   section's stay.
5. **Sweep merged run files by anchor window:** the previous plan's anchor commit (from the
   `plan.md` being replaced; if none exists, all of history) to this plan's new anchor bounds
   the sweep — `git diff --name-only <prev-anchor>..origin/main -- docs/health/runs/`. For every
   `## Sweep queue` item in those files, apply it — `lesson:` → this skill's Lessons, `debt:` →
   `debt-ledger.yml`, `memory:` → the named atom + INDEX line — skipping any item already
   present in its target (a late-merging run can straddle windows; idempotence beats
   bookkeeping). **Never edit the swept run files** — resume is their only post-merge editor,
   which is what keeps resume conflict-free. This sweep is the only path by which runs amend
   shared files.

Then select again from the new plan.

Take the selected section (or `--section`). Sections are `src/Sections/` projects only.

**Never work a section in the blocked set.** A section with an open section-doctor PR has
unmerged strikes that today's run cannot see — re-doctoring it duplicates work and produces
conflicting PRs. A `--section` naming a blocked section stops like the all-blocked case — merge
the open PR first, or use `resume` to work its Needs-Peter queue.

## Phase 3: Assess

Five stages, in this order. **The order is the point.** The target is derived *before* any scan
runs, because a target written after a linter run is a summary of the linter run (`/simplify`,
Pass 2). This skill had it backwards until 2026-08-18, and the Finance run's "ideal shape" came
out as a restatement of its reforge score — the failure that rule exists to prevent.

Start `dotnet build Humans.slnx -v quiet` in the background now: reforge needs a built solution
and 3d's tool threads need the build. Do not look at its output until 3d.

### 3a. Inventory — every file, assigned

```bash
git ls-files -- src/Sections/Humans.<X> src/Sections/Humans.<X>.Contracts tests/Humans.<X>.Tests
```
plus `docs/guide/<X>.md` where one exists. Drop nothing. Assign every path to at least one thread
from 3d.

Two things are exempt, both generated: `*.Designer.cs` and `*DbContextModelSnapshot.cs`. Migration
`.cs` files are **not** exempt.

**Coverage is a success criterion of the run, not an aspiration.** A file no thread claims is a
hole in the thread set, not a file to skip — widen a thread or add one, and say so in the run
file. The run file's `## File coverage` block records a disposition for every path: `reviewed`,
`changed`, or `generated`.

Why this is stage one: a finding-driven pass only finds what sits adjacent to what it already
suspects. The 2026-08-18 Finance run skipped ~20 of 55 files and two instances of its own
headline finding were sitting in two of them, reachable from no lane it ran.

### 3b. Behavior first, tool-free

No scores, no linters, no scans yet. Read the section for what it *does*, in words its user would
recognise:

- **The external surface — grouped, not listed.** Routes, contract methods, jobs, events. "N
  methods" is a list; "N methods over M question-shapes" is a grouping, and the grouping is what
  makes collapse items visible. Record the shapes.
- Owned tables, cross-section calls in and out, config it reads.
- **What the section said it would be** — its `Docs/*.md`, the guide page, the specs and design
  docs that named it. *Stated-but-unbuilt* and *built-differently-than-stated* are deltas, and no
  tool reports them.

### 3c. The target — written now, before scanning

One page, in `src/Sections/Humans.<X>/Docs/health.md`. Six parts, each required; write "none"
where genuinely empty:

1. **What the section does** — behavior, no code nouns.
2. **The shapes** — 3b's grouping as a table. Load-bearing; everything below follows from it.
3. **Structure** — the layout those shapes imply, written fresh. Not today's layout with fixes.
4. **Invariants** — stated so a violation is recognisable.
5. **Seams** — specified-but-unbuilt work. Don't build it, don't rank it; reserve its place,
   because items touching its future callers are shaped by it.
6. **Deliberately not done** — abstractions a reader would reach for and shouldn't, with the
   reason, including ones Peter has declined.

Plus a **load-bearing weirdness** list: essential complexity and settled decisions, with why, so
later runs stop re-litigating them.

**Regenerate the target every run, then diff it against the previous one** (it is in git; the
previous run's is the parent commit's copy). The diff is signal in both directions: the section
moved, or the earlier target was wrong. Record which in the run file. A target that never changes
across runs on a section that keeps changing is a target nobody is really deriving.

### 3d. Threads

Each thread is a lens over the **same complete inventory**, and each reports a disposition for
every file it claims. They run concurrently, but *how* a thread runs follows from what it is —
this is the wall-clock / token / fragility balance, and subagent lanes have historically been the
fragile part (two runs running, every dispatched lane missed the window):

- **Tool threads run as background commands** — Stryker, InspectCode, reforge, conformance
  detectors. No subagent context to duplicate, no idle-lane failure mode, and they run while the
  main thread reads.
- **Judgment threads run on the main thread** — shape, behavior, prose. They are the expensive
  reading this whole run exists to do.
- **Subagents only when a thread must read more than one context can hold**, and then with an
  explicit tagged model and a deadline. A thread that misses its deadline does not block the
  strike loop: work its checklist on the main thread and label it self-run in the run file.

| Thread | Lens | Runs as |
|---|---|---|
| **Shape** | `/simplify`'s method against the target: shape mismatches, duplicated pipelines, pass-throughs, over-general options, dead and over-exposed surface, per-method external-caller counts | main |
| **Behavior & bugs** | Does it do what it claims? Walk each flow against the target's invariants. Where the section consumes authored content (markdown, resx, templates, seed data), run the **real shipped content through the real pipeline** — a defect whose trigger is the shape of an input file is invisible to every code-reading thread | main |
| **Freshness** | The section's docs vs code: claims that no longer hold, `freshness:triggers` globs that still resolve, and triggers that watch *everything the doc asserts about* — including another section's file where the doc names it. A fixed claim gets swept everywhere it appears | main |
| **Conformance** | `docs/architecture/section-conformance.yml` — the per-section rules nothing enforces yet. Detectors are mechanical; the judgment is what to do about a hit | background + main |
| **Tests** | Mutation score (Stryker, section-scoped); the invariant coverage matrix — every invariant, negative access rule and trigger in the target mapped to a pinning test; redundant and asserting-the-mock tests | background + main |
| **Prose & surface** | InspectCode Tier 1/2; comments against `comments-stay-short`; docs that are 500 words where 50 would do; dead resources, missing translations; nav quality — dead ends, missing backlinks, discoverability from `AdminNavTree` | background + main |
| **Inbox** | Section-tagged `debt-ledger.yml` items, open GitHub issues, in-app issues. Work or rank them; off-section finds go to the run's sweep queue as `debt:`, never written to the ledger directly | main |

**Every thread that does not run says so in the run file, with why.** A silent skip is how the
2026-08-18 run left the whole mutation dimension unmeasured with nothing flagging it. A thread
earns removal from this table only when several runs record it as "ran, found nothing".

### 3e. Merge, rank, and check independence

One value-ranked list across all threads — value is bug surface removed, concepts removed, and
reader cost removed. Effort is a column, never the sort key.

**Independence check, before striking.** Walk the ranked list and mark where each item came from.
Either symptom is a fail:

- every item traces to a tool finding, a score, or a grep; or
- no item cites a shape mismatch, a spec-vs-reality delta, or an abstraction covering only part of
  its domain.

On a fail, 3c was reverse-engineered from the defect list. Re-derive the target from 3b and
re-rank — the scans are still good, the design isn't. Record the verdict in the run file either
way.

## Phase 4: Strike

Work the ranked list until budget exhausted. **Drain the list — stopping early with strikeable
items remaining is a failure mode, not a judgment call.**

Rank is value order; *execution* order is `cut → delete → dedup → collapse → rearch`, each green
before the next (`/simplify`'s phase discipline). Cutting an unneeded behaviour turns whole
subtrees into dead code, so it precedes deletion; deletion is near-zero risk and shrinks
everything downstream of it. A `collapse` or `rearch` item routinely outranks a `delete` one —
a wrong abstraction costs every future session, a dead local costs one grep — but it is still
executed after it. Budget checks are real
`date` reads between items, never estimates. Per item (one item or tight cluster per commit):

1. Pick the play. `/simplify`'s *method* is absorbed into Phase 3 — do not call the skill from a
   run: it is audit-gated (its approval gate is a merged audit PR, then one item per PR) and that
   cannot fit inside one run, one PR. It stays invocable for repo-wide work. The rest of the
   toolbox is still called directly where it fits: `section-align`, `trim-tests`,
   `section-read-split`, `reuse-review` (against the section's own surface), the
   `.codex/skills/humans-refactor` lane process, a `debt-ledger.yml` item — or a direct fix.
2. Fix it right — no surgical fixes. Reuse-first.
3. `dotnet build Humans.slnx -v quiet`; targeted tests for the touched area.
4. Non-mechanical changes (deletions beyond plainly-dead code, structural moves) → second-opinion
   reviewer subagent, opus-tier, score-blind, default-reject: "name the concept that improved in
   one sentence." Reject → rework once; second reject → revert, record.
5. **Doc fixes sweep the claim**: a wrong statement fixed in one doc must be grepped across
   `docs/guide/`, other section docs, and the access matrix — it rarely lives in one file.
6. **UI-affecting strikes get runtime verification**: render the changed page in the running app
   (`dotnet run` + browser/test-site) before the PR — a green build does not prove a cshtml/JS
   change works.
7. Commit `doctor(<section>): <what>`. Full `dotnet test Humans.slnx -v quiet` before each push;
   push every 3–5 items.

**Skip-and-queue classes** (never block the loop): schema/EF changes of any kind, public/interface
surface *additions*, privilege changes, anything needing Peter's judgment → skip, queue for
Phase 7's Needs-Peter block. Off-section debt discovered → this run's sweep queue (`debt:`),
never chased, never written to the ledger directly. If in-flight feature work on this section
surfaces mid-run → stop striking, ship the assessment-only PR, note it in the run file.

## Phase 5: Bookkeeping

**A run writes no shared file.** In the same worktree/PR, exactly two bookkeeping writes:

- The section's `Docs/health.md` history row (per-section; the blocked set guarantees at most
  one open run per section, so it cannot collide).
- **This run's own file** — `docs/health/runs/<yyyy-mm-dd>-<Section>.md` (UTC date from the run
  timestamp; if the path already exists at the branch point, suffix `-<HHMMZ>`). Sections:
  run header (invocation, anchor commit, budget, `PR: pending`), assessment summary, worked,
  skipped + why (including plan rows passed over as blocked), retro (Phase 6), `## Needs Peter`
  checklist, `## Sweep queue` (`lesson:` / `debt:` / `memory:` items as plain bullets — the
  next replan whose window covers this run's merge applies them; nothing ever ticks them).

  Plus three blocks that make the Purpose's three tests answerable rather than assertable:

  - **`## File coverage`** — a disposition for every path in the 3a inventory: `reviewed`,
    `changed` or `generated`. Not a summary; the list.
  - **`## Threads`** — which threads ran, and for each that did not, why. A silent skip is a
    failed run, not a quiet one.
  - **`## Size`** — line count against the run's anchor for every section touched, and the net.
    Growth is reported with its reason, and consolidation that grows this section while shrinking
    another is stated as the trade it is.

The runs directory **is** the log and the newest file **is** the last report. There is no
`log.md`, `last-report.md`, or generated index — never recreate them — and daily runs never
touch `docs/architecture/maintenance-log.md`. `plan.md` is written only by a replan (Phase 2).

## Phase 6: Retro + self-amend

Four questions, answered honestly in the run file: what did the plan/rubric get wrong, what was
wasted motion, what did the assessment miss that striking revealed, and **what does the target
diff say** — 3c regenerated the target and diffed it against the previous run's; a change means
either the section moved or the earlier target was wrong, and which one it was is worth a line.
Then:

- **Mechanical lessons** → this run's `## Sweep queue` as `lesson:` one-liners; the next replan
  applies them to this skill's files after this run merges. Never edit the skill's files
  directly mid-run — they are shared, and a concurrent run's edit is a guaranteed conflict.
- **Judgment lessons** (rubric axes, thresholds, play choices) → the Needs-Peter block.
- **Durable project rules** → `## Sweep queue` as `memory: <bucket>/<name> — <rule>`.

Commit all Phase 5 + 6 edits before Phase 7 pushes — the only thing that lands after is
Phase 7's own PR-number backfill commit.

## Phase 7: PR

```bash
git push -u origin section-doctor/$TS
gh pr create --repo peterdrier/Humans --base main --title "doctor(<Section>): <headline>" --body ...
```

Body: assessment summary, worked/skipped bullets, and a **`## Needs Peter`** block — terse,
numbered, answerable in a word or two. **The PR body is the authoritative queue while the PR is
open** (resume reads it from there); the run file's copy carries it forward after merge. One PR
per run; never merge.

Then backfill the real PR number over every `pending` reference (run file header, health history
row), commit, push again.

## Phase 8: Inline round (interactive runs only)

If Peter is present, present the Needs-Peter items inline now (terse, numbered, plain prose —
never AskUserQuestion) and apply answers as new commits + push, ticking each answered item in
both the PR body and the run file. Unattended morning runs skip this; `resume` covers it.
Unanswered items carry forward — never re-asked.

## Phase 9: Teardown

`cd $REPO_ROOT && git worktree remove $WORKTREE` (never `rm -rf`).

## Resume mode

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
2. **Merged runs:** unticked `## Needs Peter` entries in `docs/health/runs/*.md` on
   `origin/main`.

Present the open items inline, then apply each answer:

- **Open-PR item** — commits on that item's PR branch (reuse its worktree, or recreate from the
  branch). Tick the item in **both** places: the PR body *and* the branch's run file — an
  unticked run file would resurface as a merged-queue item after the PR lands and get re-asked
  or applied twice. Push.
- **Merged items** — group by run file: one fresh worktree + branch off `origin/main` **per run
  file**, applying all of that file's answers and ticking each entry, push, one PR per run file
  (an answer pushed to a branch with no PR is stranded), tear the worktree down. One writer per
  run file — several same-file answers in separate PRs would conflict with each other; grouped,
  these PRs cannot conflict with each other or with concurrent doctor runs.

## Standing constraints

- Business functionality does not change.
- No EF migrations, schema changes, or data backfills — queue them. No analyzer suppressions.
  Never touch `[DontFix]`.
- Public-surface additions need Peter; dead-surface deletion is the job (reviewer-gated).
- Explicit tagged model on every subagent. Never leave the branch red between commits.
- **A run touches only:** the section's files (+ callers where a play requires), the section's
  `Docs/health.md`, and its own `docs/health/runs/<date>-<Section>.md`. **A replanning run
  additionally touches**: `docs/health/plan.md`, this skill's files,
  `docs/architecture/debt-ledger.yml`, `memory/` — never the run files it sweeps. Nothing
  writes `docs/architecture/maintenance-log.md`.
- **`docs/architecture/section-conformance.yml` is read-only to every run**, replans included.
  Rows are added and removed only at Peter's direction; a run that wants one proposes it in its
  Needs-Peter block.

## Lessons

(Applied here by replan sweeps from merged run files' `lesson:` items — dated one-liners.)

- 2026-08-16: resx/XML edits must be structure-aware (python/XML tooling), never line-based sed
  — neutral resx was one-line-per-entry but all 5 language variants were multi-line; sed
  corrupted them and only the build caught it.
- 2026-08-16: keep a by-hand read of the section's auth paths in the assessment — the doc-code
  contradiction on phase gating was invisible to grep and to every lane.
- 2026-08-16 (retro round 2, Peter): the shakedown run stopped at 40 of 150 minutes with
  strikeable items still ranked — hence the drain-the-list rule; and absorbed abilities were
  going unused (Stryker, InspectCode, invariant matrix, claim sweep, runtime verify, inbox) —
  hence the expanded lanes above.
- 2026-08-17: **for a section whose input is content, run the real shipped content through the
  real pipeline during the assessment.** Guide's two defects (an admin block served to anonymous;
  two admin roles locked out of their own blocks) were both invisible to grep, to unit tests and
  to every code-reading lane — they only appeared when the 28 real `docs/guide/*.md` files went
  through the actual renderer and filter. Add a lane that feeds production content to the section.
- 2026-08-17: **a low reforge score is not evidence the section is healthy.** Guide scores 8, the
  lowest of any section, and was failing open on access control. The replan rubric ranks by score
  growth and staleness; nothing in it would ever have surfaced Guide. Treat the score as a measure
  of structure only, never of correctness.
- 2026-08-17: never run `dotnet build` and `dotnet test` against the same worktree concurrently —
  the test host holds the output DLLs and the build burns MSB3026 retry rounds on locked files.
  One at a time per worktree.
- 2026-08-17: commit messages via Bash must use `git commit -F <file>`; PowerShell here-string
  syntax (`@'…'@`) silently becomes part of the subject line under Git Bash.
- 2026-08-17: every dispatched lane missed the run window, and the Phase 4.4 reviewer gate could
  not be obtained **at all** — four attempts across three agents (two `general-purpose` opus, one
  `feature-dev:code-reviewer` opus), briefs shortened each time down to "reply with exactly three
  lines", every one idling without an answer. Don't let a lane block a strike: give each a
  deadline and work the ranked list meanwhile. When the gate does not report, work its checklist
  on the main thread and **label it self-review in the PR and the Needs-Peter queue** — never
  imply a review happened, and don't spend the run re-spawning reviewers.
- 2026-08-17: lanes that report *after* the PR opens are still worth working — the run's PR is
  open, so take a second pass and commit to it rather than dropping the findings. The tests lane's
  invariant matrix caught an untested negative access rule (`POST /Guide/Refresh`) that the whole
  first pass missed. **Always build the matrix**, even when the section looks well tested; the
  gaps it finds are the invariants nobody thought to doubt.
