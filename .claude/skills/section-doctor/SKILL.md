---
name: section-doctor
description: "Daily per-section review cycle driving a section toward the smallest, clearest form that still does everything it does today. Selects its section live each run (reforge surface score, middle-out, via a focused selector subagent — no stored plan), inventories every file in the section, derives the target shape before running any scan, then works parallel threads — shape, behavior/bugs, freshness, conformance, tests, prose/nav, inbox — into one ranked list and strikes it on a 2-3h budget. One PR per run; each run's report + Needs-Peter queue lives in its own docs/health/runs/ file; 'resume' applies Peter's answers later. Use for the morning section-improvement run, 'doctor <section>', or 'run section doctor'."
argument-hint: "[resume] [--section=<Name>] [--budget=2.5h]"
---

# Section Doctor

Full design: `docs/superpowers/specs/2026-08-17-section-doctor-design.md`. This file is
self-amending — run sweeps apply merged run lessons here; keep those edits terse and dated.

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
at once; every one must merge cleanly in any order. The only shared-file writes are each run's
sweep commit (Phase 5), idempotent by construction.

## Invocation

| Form | Behavior |
|---|---|
| *(none)* | daily run: select a section live (Phase 2), doctor it |
| `resume` | no new work — work the Needs-Peter queue (see Resume mode) |
| `--section=<Name>` | skip the selector, doctor this section |
| `--budget=<duration>` | override budget (default 2.5h); wall-clock, checked between items |

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

Start the phase log now, and append one line at the start of each later phase (2, 3, 4, 5, 7) —
Phase 7's cost report buckets the session transcript by these timestamps:

```bash
echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) phase1" >> $WORKTREE/.phase-log   # never committed
```

## Phase 2: Select the section

Selection is computed live every run — nothing is stored. (`docs/health/plan.md` and the
replan machinery are gone, 2026-08-22: a checked-in plan went stale whenever merges paused for
long enough, and the runs must keep going unattended.)

**Blocked set first** (main thread, cheap):

```bash
gh pr list --repo peterdrier/Humans --state open --limit 200 --json number,headRefName,title \
  --jq '.[] | select(.headRefName | startswith("section-doctor/"))'
```

(`--limit` is mandatory — `gh pr list` fetches only 30 by default and the prefix filter runs
client-side in `--jq`, so an older open run silently drops out without it. `--search "head:..."`
matches exact branch names, not prefixes — don't use it.) The **blocked set** is the sections
named by those open PRs' titles (`doctor(<Section>): …`): an open run PR must be dealt with
before its section can be doctored again. If **every** section is blocked, report the open PRs
and go straight to Phase 9 teardown — nothing has been written at this point, so the worktree
is clean and `git worktree remove` succeeds without `--force`.

**Then dispatch the selector** — a focused **sonnet** subagent whose entire job is to return
one section name (measured 2026-08-22: ~270k fresh + ~1.1M cached input tokens over 19 calls,
≈$1.50 API-equivalent). It works read-only from `$WORKTREE`, reads nothing beyond the command
output below and the sections' `Docs/health.md` files (tier-2 dates), and gets the blocked set
passed in. Its brief:

1. `dotnet build Humans.slnx -v quiet` (an unbuilt solution silently under-reports Reforge
   scores; the build also serves Phase 3/4), then `reforge surface-score --format compact`.
2. Pool: every `src/Sections/` project, minus the blocked set. **Feature-active down-rank**:
   sections touched by an open non-doctor PR (`gh pr list --repo peterdrier/Humans --state
   open --limit 200 --json headRefName,files`, changed paths mapped to
   `src/Sections/Humans.<X>/`) drop to the bottom of whichever tier they land in — picked
   only when no non-active section is eligible there, but never excluded: an open feature PR
   must not keep a section from ever being doctored.
3. Tier: a section is previously-doctored iff `src/Sections/Humans.<X>/Docs/health.md` exists
   on `origin/main` (any newer copy lives on an open PR, and that section is blocked anyway).
   Never-doctored sections always outrank previously-doctored ones.
4. While any eligible never-doctored section remains: rank that tier by score (feature-active
   members set aside per step 2) and take the **median** — middle-out. The process proves
   itself on mid-sized sections; the biggest and smallest get their turn once the middle has
   been worked.
5. Once every eligible section has been doctored: read each section's `Docs/health.md` for its
   last-assessed date, then rank by days since that date combined with change volume since it
   (`git log --stat` over the section's paths) — more and bigger changes come sooner. Judgment
   call; no exact formula.
6. Return exactly three fields and nothing else: `SECTION: <Name>`,
   `TIER: never-doctored | re-doctor`, `RATIONALE:` (≤3 lines: pool size after exclusions,
   what was blocked/skipped, why this pick).

Sections passed over as blocked are noted in this run's run file under skipped
(`<section> — open PR #N`); they need no other bookkeeping — the tier/staleness scan returns
to them.

Take the selected section (or `--section`, which skips the selector but never the blocked
set). Sections are `src/Sections/` projects only.

**Never work a section in the blocked set.** A section with an open section-doctor PR has
unmerged strikes that today's run cannot see — re-doctoring it duplicates work and produces
conflicting PRs. A `--section` naming a blocked section stops like the all-blocked case — merge
the open PR first, or use `resume` to work its Needs-Peter queue.

## Phase 3: Assess

Five stages, in this order. **The order is the point.** The target is derived *before* any scan
runs, because a target written after a linter run is a summary of the linter run (`/simplify`,
Pass 2). This skill had it backwards until 2026-08-18, and the Finance run's "ideal shape" came
out as a restatement of its reforge score — the failure that rule exists to prevent.

The Phase 2 selector already built the solution on a normal run; only when it was skipped
(`--section`) start `dotnet build Humans.slnx -v quiet` in the background now — reforge needs a
built solution and 3d's tool threads need the build. Do not look at its output until 3d.

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

**`reviewed` means the file's names resolve, not that the file was opened.** For any file that
names things — a doc, comment-bearing source, a csproj — record `reviewed` only after every code
symbol, route and file path it names has been checked against the tree. This is mechanical: the
2026-08-18 Finance benchmark marked files reviewed that still said a controller "stayed in
Shell", carried an `IBudgetService` dependency the read-split had replaced, and pointed at a
folder a job had moved out of — every miss was a name that no longer resolved.

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
  main thread reads. Reforge's run is `surface-score --format compact --group <Section>`,
  scoped to the section being doctored — its `loc=`/`cogP95=`/`cogMax=` fields are the source
  for Phase 5's `## Size` snapshot, on every run, not only the selector's solution-wide call.
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
| **Prose & surface** | InspectCode Tier 1/2; docs that are 500 words where 50 would do; dead resources, missing translations, resource keys not prefixed with the section name (`resource-key-prefix`, cleanup — report the count, don't backfill unless the run is *for* that); nav quality — dead ends, missing backlinks, discoverability from `AdminNavTree` | background + main |
| **History** | Prose narrating a prior state: a deleted/renamed project or type, a migration/lane number, "used to live in X", "the first section to Y", a dated run post-mortem, rationale for a decision no longer contested. **Cut test: keep only if it changes what a reader does** — a live constraint, a non-obvious invariant, a landmine that bites if reverted. A load-bearing "why" moves to the issue, linked, not narrated in the file | main |
| **Comments** | Every comment in the section's inventory, rewritten or deleted. Cut what restates the next line, decision history, hedging, reassurance addressed to the next agent. **Cut test: a comment survives only if it carries something the code cannot say** | main |
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
way, as a literal line — `Independence check: pass` or `Independence check: fail (re-derived)` —
plus one sentence naming which items came from the target rather than a scan. The 2026-08-18
Finance benchmark had the evidence in its run file and never wrote the verdict.

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
5. **Doc fixes sweep the claim — by literal string, repo-wide**: when a strike removes or
   renames a route, type, method or path, or fixes a claim naming one, grep the whole repo for
   the exact string and fix or enumerate every hit in the run file. Sweeping only the docs you
   remember is how `POST /Finance/Creditors/Resync` survived in `authorization-inventory.md`
   and `controller-architecture-audit.md` after two 2026-08-18 runs each removed it from
   `Finance.md`.
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

**A run's shared-file writes are confined to the sweep commit below.** In the same
worktree/PR, three bookkeeping writes:

- The section's `Docs/health.md` history row (per-section; the blocked set guarantees at most
  one open run per section, so it cannot collide).
- **This run's own file** — `docs/health/runs/<yyyy-mm-dd>-<Section>.md` (UTC date from the run
  timestamp; if the path already exists at the branch point, suffix `-<HHMMZ>`). Sections:
  run header (invocation, anchor commit, budget, `PR: pending`), assessment summary, worked,
  skipped + why (including sections passed over as blocked), retro (Phase 6), `## Needs Peter`
  checklist, `## Sweep queue` (`lesson:` / `debt:` / `memory:` items as plain bullets — a
  later run's sweep applies them after this run merges; nothing ever ticks them).

  Plus three blocks that make the Purpose's three tests answerable rather than assertable:

  - **`## File coverage`** — a disposition for every path in the 3a inventory: `reviewed`,
    `changed` or `generated`. Not a summary; the list.
  - **`## Threads`** — which threads ran, and for each that did not, why. A silent skip is a
    failed run, not a quiet one.
  - **`## Size`** — line count against the run's anchor for every section touched, and the net.
    Growth is reported with its reason, and consolidation that grows this section while shrinking
    another is stated as the trade it is. Include the section's reforge metrics snapshot (`loc`,
    `cogP95`, `cogMax`) from 3d's reforge tool thread, so the run file states size/complexity at
    assessment time, not just the git-diff delta.

  `no-derived-aggregates-in-docs` applies to the run file and `health.md` too: never count a
  list the same file carries ("15 contract methods" above the table of them, "52 paths" above
  the coverage list). Measurements with a generator — Stryker scores, `git diff` line counts,
  reforge — stay; both 2026-08-18 Finance runs typed self-counts, and the one that was wrong
  nearly sent a refactor at a method with a live external caller.

- **The sweep** — its own commit, and the only place a run touches shared files: for every
  `## Sweep queue` item in merged run files under `docs/health/runs/` on `origin/main`, apply
  it — `lesson:` → this skill's Lessons, `debt:` → `debt-ledger.yml`, `memory:` → the named
  atom + INDEX line — skipping any item already present in its target (idempotence is the only
  bookkeeping; there is no anchor window). **Never edit the swept run files** — resume is
  their only post-merge editor, which is what keeps resume conflict-free. Two piled-up
  unmerged runs can occasionally sweep the same item; the cost is one hand-resolved conflict,
  not corruption (the no-locking trade of PR #1366).

The runs directory **is** the log and the newest file **is** the last report. There is no
`log.md`, `last-report.md`, or generated index — never recreate them — and daily runs never
touch `docs/architecture/maintenance-log.md`.

## Phase 6: Retro + self-amend

Four questions, answered honestly in the run file: what did the selector/rubric get wrong, what was
wasted motion, what did the assessment miss that striking revealed, and **what does the target
diff say** — 3c regenerated the target and diffed it against the previous run's; a change means
either the section moved or the earlier target was wrong, and which one it was is worth a line.
Then:

- **Mechanical lessons** → this run's `## Sweep queue` as `lesson:` one-liners; a later run's
  sweep applies them to this skill's files after this run merges. Never edit the skill's files
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

Body: assessment summary, worked/skipped bullets, a **`## Cost`** table (below), and a
**`## Needs Peter`** block — terse, numbered, answerable in a word or two. **The PR body is the
authoritative queue while the PR is open** (resume reads it from there); the run file's copy
carries it forward after merge. One PR per run; never merge.

**Cost report** — before creating the PR, run:

```bash
python .claude/skills/section-doctor/cost-report.py section-doctor/$TS $WORKTREE/.phase-log
```

It finds this run's own session transcript under `~/.claude/projects` (the model never sees its
own usage in-band, but the harness logs every API call's tokens there), buckets the main thread
by the phase log, adds one row per subagent transcript, and prints a markdown table with
API-equivalent $. The table is a **Phase 1 → PR-creation cutoff, not a run total** — the PR
create/backfill calls and any Phase 8 work land after measurement (the footer says so). Paste
it as `## Cost` into the PR body and the run file (run-file copy lands
with the backfill commit). The script never fails the run — on any discovery problem it prints
`Cost: unmeasured (...)`; use that line as the table. Cloud-environment transcript layout is
unverified — if the first routine run reports unmeasured, note it in Needs-Peter.

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
  `Docs/health.md`, its own `docs/health/runs/<date>-<Section>.md`, and — in the sweep commit
  only (Phase 5) — this skill's files, `docs/architecture/debt-ledger.yml`, `memory/`; never
  the run files it sweeps. Nothing writes `docs/architecture/maintenance-log.md`.
- **`docs/architecture/section-conformance.yml` is read-only to every run**, sweeps included.
  Rows are added and removed only at Peter's direction; a run that wants one proposes it in its
  Needs-Peter block.

## Lessons

(Applied here by run sweeps from merged run files' `lesson:` items — dated one-liners.)

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
