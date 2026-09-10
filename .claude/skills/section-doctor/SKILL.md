---
name: section-doctor
description: "Daily per-section review cycle driving a section toward the smallest, clearest form that still does everything it does today. Selects its section live each run (scripted: reforge surface score, middle-out, then changed-since-last-run — no stored plan), inventories every file in the section, derives the target shape before running any scan, then works parallel threads — shape, behavior/bugs, freshness, conformance, tests, prose/nav, inbox — into one ranked list and strikes it on a 2-3h budget. One PR per run; each run's report + Needs-Peter queue lives in its own docs/health/runs/ file; 'resume' applies Peter's answers later. Use for the morning section-improvement run, 'doctor <section>', or 'run section doctor'."
argument-hint: "[resume] [--section=<Name>] [--budget=2.5h] [--upstream-issues] [--mutation]"
---

# Section Doctor

Full design: `docs/superpowers/specs/2026-08-17-section-doctor-design.md`.

**Only Peter edits this file.** A run proposes — it records lessons in its run file and its
Needs-Peter block — and never amends its own instructions, in a sweep or otherwise. It is
instructions, not a record: no shas, no dated post-mortems, no accounts of past runs. That
history lives in the run files and the design spec. An issue reference earns its place only by
naming a live contract or a baseline a phase is bound to — never as provenance for a rule that
already stands on its own.

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
  **net across every section the run touched**, not a per-section floor. That figure is GitHub's
  diff stats on the run's PR; the run file never restates it (Phase 5).
- **Was every file actually looked at?** A finding-driven pass finds only what sits next to what
  it already suspects. Full coverage is what makes this a review rather than a sweep — and it is
  where the bugs come from.
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
| `--upstream-issues` | opt-in upgrade: include `nobodies-collective/Humans` in the Inbox issue review (default: fork only) |
| `--mutation` | opt-in upgrade: section-scoped Stryker in the Tests thread (default: invariant matrix + test quality only) |

The two opt-in flags exist because the standard cloud environment supports neither — no
upstream-repo GitHub scope, no Stryker. **Without its flag, a run never attempts, probes for,
mentions, or records-as-skipped either capability.** The default run is complete without them.

## Phase 0: Setup

```bash
REPO_ROOT=$(git rev-parse --show-toplevel)
TS=$(date -u +%Y-%m-%dT%H%M%SZ)                 # the run's identity: branch, run dir, run file
RUNDIR="${TMPDIR:-/tmp}/section-doctor/$TS"     # scratch — OUTSIDE the working tree
mkdir -p "$RUNDIR"
```

Parse args; record start time (`date -u`).

**Run scratch never lives in the worktree.** The phase log and the open-PR JSON are working notes,
not deliverables, and a strike that runs `git add -A` commits anything sitting in the tree. The
`.gitignore` entries for `/.phase-log` and `/.prs.json` stay as a backstop.

**None of these variables survive between tool calls** — re-set `$TS`, `$RUNDIR` and `$WORKTREE`
at the top of any call that uses them. Every one derives from `$TS` alone, and `$TS` is also the
branch name: `section-doctor/$TS` is recoverable with `git rev-parse --abbrev-ref HEAD`.

**Shell rules that break a run when missed:**

- Write multi-line content — commit messages, run files — with `git commit -F <file>` or a
  file-write tool, or a **quoted** heredoc. An unquoted heredoc executes backticks inside it, and
  PowerShell here-string syntax (`@'…'@`) silently becomes part of the subject line under Git Bash.
- Never run `dotnet build` and `dotnet test` against the same worktree at once — the test host
  holds the output DLLs and the build burns MSB3026 retry rounds on locked files. One at a time.
- Resolve every asserted path from the worktree root. A bare basename test reports a live file
  missing and invites a repo-wide "fix" for a file that was never gone.
- Build/test output stays out of the transcript: every `dotnet build` / `dotnet test` takes
  `-v quiet -clp:ErrorsOnly` per `memory/process/dotnet-verbosity-quiet.md`; if output is still
  long, redirect to a file under `$RUNDIR` and Read it — never pipe through `tail`/`head`/`grep`.
  Every line of build noise in the transcript is re-read by every later turn of the run.

Getting a toolchain is the *environment's* job, not this skill's — a local run and the
scheduled cloud run both start with the SDK, `dotnet-ef` and reforge already there. Never
install one. Mutation scoring (Stryker) runs **only under `--mutation`**: without the flag the
Tests thread is the invariant matrix and test-quality work, complete in itself — no run installs
Stryker, probes for it, mentions it, or records it as skipped or as a degraded analysis. With
the flag, run section-scoped Stryker as one of Phase 3d's background tool threads — from the
worktree, after selection, never here in Phase 0 — with `concurrency: 16` and
`coverage-analysis: off` per `memory/process/stryker-concurrency-coverage.md` (the environment
must already have Stryker — never install). **This paragraph outranks the prompt that invoked the
run.** A scheduled or hand-written prompt saying Stryker is absent, skip the mutation half and
record it skipped-with-reason is stale wording, not a second instruction: without `--mutation`
there is no mutation half to skip and nothing to record. Follow this paragraph, and raise the
prompt's wording as a Needs-Peter item (Phase 6) instead of choosing between the two.

**What is this skill's job is the run you get when there is no compiler** — which is a real
run, not a failed one. If `dotnet build` cannot run at all, this is a **docs-only run**: work
the reading threads, keep strikes to docs, comments and resx, queue every code finding for the
Needs-Peter block rather than editing C# you cannot compile, record each compiler-dependent
thread as skipped-with-reason (3d's rule), and let the PR's CI be the compile gate. A build
that *fails* is not this — that is a normal broken build, diagnosed like any other. Say so in
the run file's header and in the PR body — a run that could not build and does not say so
reads as a run that found nothing to build.

**Every environment caveat is a dated per-session line, never a standing banner** —
`2026-08-24 07:10Z session: no compiler in this container`. The caveat belongs to the session,
not the run: the next session on this branch gets a different environment, and a banner reading
"this run had no compiler" ends up sitting above compiler-confirmed strikes within the hour.

## Phase 1: Workspace

Per [`always-use-worktree`](../../../memory/process/always-use-worktree.md) — a worktree locally, the
repo root in a cloud run. `$WORKTREE` is the run's workspace either way; the rest of the skill
doesn't care which it is.

```bash
git fetch origin main   # $TS was fixed in Phase 0 — branch, run dir and run file share it
if [ "$CLAUDE_CODE_REMOTE" = "true" ]; then  # ephemeral single-session container — no worktree
  git checkout -b section-doctor/$TS origin/main
  WORKTREE=$REPO_ROOT
else
  git worktree add $REPO_ROOT/.worktrees/section-doctor-$TS -b section-doctor/$TS origin/main
  WORKTREE=$REPO_ROOT/.worktrees/section-doctor-$TS  # EnterWorktree here; all commands run inside
fi
```

Scope is frozen at the branch point — never reconcile against `origin/main` mid-run. Locally, scope
every Glob/Grep to `$WORKTREE`; in a cloud run `$WORKTREE` *is* the repo root and there is nothing
to scope away.

**Origin gate — run it in the same shell call as every `git push` of the run, Phase 2 through
Phase 9.** A cloud container can rewrite the remotes on any resume, so a check at setup proves
nothing about the push hours later:

```bash
git remote get-url origin | grep -qE 'github\.com[:/]peterdrier/Humans(\.git)?$' \
  || { echo "origin is not peterdrier/Humans — stop, raise in Needs Peter"; exit 1; }
```

On a mismatch nothing is pushed and the run stops; never repoint the remote and retry.

**Scope history checks to a named branch or ref, never `git log --all`** — on a run with a blocked
branch set, `--all` surfaces commit subjects from that set and is not blindfold-safe.

Start the phase log now. Phase 7's cost report buckets the session transcript by these
timestamps, and names each row by the **label**, not the phase id — a table of phase numbers
tells its reader nothing about where the run's money went:

```bash
RUNDIR="${TMPDIR:-/tmp}/section-doctor/$TS"   # re-derive; nothing carries over between calls
echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) phase1 worktree" >> "$RUNDIR/phase-log"
```

**Write the line out in full at every phase boundary, through Phase 7 — always the full
`$(date -u +%Y-%m-%dT%H:%M:%SZ) <mark>` form, one mark per shell call, appended *before* the
work it names starts.** The leading timestamp is what buckets the spend: `cost-report.py` skips
any line that does not start with one, so a bare `phase4 …` mark silently folds that phase's
cost into the row above it; two marks in one call share a timestamp and fold together; a mark
written after the work prices it into the row before. Shell state does not survive between
tool calls — a `mark()` helper defined here is gone by the next call, and so are `$RUNDIR`,
`$TS` and `$WORKTREE`. Re-derive the path (or paste it literally) each time rather than
relying on a variable set in an earlier call:

| Phase | Mark (after the timestamp) |
|---|---|
| 2 | `phase2 select section` |
| 3 | `phase3 assess` |
| 4 | `phase4 strike: <what>` — **once per strike item**, which turns the run's biggest row into a per-item breakdown |
| 5 | `phase5 bookkeeping` |
| 6 | `phase6 retro` |
| 7 | `phase7 PR` |

A phase that does not append is a phase nobody can price — its spend silently joins the row above
it. If a marker was missed, append it late rather than not at all and say so in `## Threads`; an
out-of-order log is still bucketed correctly (the report sorts by timestamp), a missing one is not.

## Phase 2: Select the section

Selection is computed live every run — nothing is stored. There is no `docs/health/plan.md` and
no replan machinery; never reintroduce either. A checked-in plan goes stale the moment merges
pause, and these runs must keep going unattended.

**Fetch the open-PR list once** (main thread, cheap):

```bash
gh pr list --repo peterdrier/Humans --state open --limit 200 \
  --json number,headRefName,title,files > "$RUNDIR/prs.json"   # scratch, outside the tree
```

(`--limit` is mandatory — `gh pr list` fetches only 30 by default, so an older open run
silently drops out without it. `--search "head:..."` matches exact branch names, not prefixes
— don't use it.) In a cloud session without `gh`, write the same JSON shape from the GitHub
MCP tools: `[{number, headRefName, title, files: [paths]}]` for all open PRs.

**Then run the selector script** — the selection maths is scripted, not a subagent; a subagent
remains only for the re-doctor judgment below:

```bash
python .claude/skills/section-doctor/select-section.py --prs "$RUNDIR/prs.json" \
  | tee "$RUNDIR/selection.txt"
exit "${PIPESTATUS[0]}"   # tee would otherwise mask the selector's exit code (2/3 below)
```

It computes the **blocked set** — sections named by open `section-doctor/` PRs' titles
(`doctor(<Section>): …`) **and by `origin`'s recent `section-doctor/*` branches that have no PR
yet** (read from the branch's run-file path; a run is still assessing for hours before its PR
exists) — the pool (every `src/Sections/` project), the **feature-active down-rank** (sections
touched by open non-doctor PRs sink to the tier bottom — picked only when nothing else is
eligible there, never excluded), the tiers (previously-doctored iff
`src/Sections/Humans.<X>/Docs/health.md` exists at the branch point; never-doctored always
outranks), builds the solution (an unbuilt solution silently under-reports Reforge scores; the
build also serves Phase 3/4), runs `reforge surface-score --format compact` (a section reforge
has nothing to report on scores 0, never "unknown, ranked last"), and picks:

- **never-doctored tier:** the **median** by score — middle-out: the process proves itself on
  mid-sized sections; the biggest and smallest get their turn once the middle has been worked.
- **re-doctor tier** (only once the never-doctored tier is empty): a section is eligible only
  if its files changed on `origin/main` since the commit that last wrote its `Docs/health.md`;
  ranked oldest last run first, ties by lowest score. The pick carries a `BASE: <sha>` line —
  Phase 3 diffs against it instead of re-reading the section cold.

It prints `SECTION:` / `TIER:` / `RATIONALE:` plus the full ranked table for the run file, and
an **`UPCOMING:`** line — the next 4 sections a repeat of this maths would pick, assuming each
pick blocks itself and nothing else changes. The `tee` to `$RUNDIR/selection.txt` is what lets
Phase 7 read that line hours later, after the selector's output has left context. The forecast
is purely informational (PR body header, Phase 7): no later run reads or honours it. The script
falls back to a LOC ranking (flagged in its output) when reforge is unusable. Act on its
verdicts — never re-derive the maths in-band, and never pick by judgment:

- **`ALL BLOCKED`** (exit 3) and **`NOTHING CHANGED`** (exit 2, every eligible section
  doctored and untouched since): report and stop. These are the paths that remove the worktree
  immediately (Phase 9) — nothing has been written yet, so it is clean and
  `git worktree remove` succeeds without `--force`. In a cloud run there is no worktree to
  remove: just stop.

Sections passed over as blocked are noted in this run's run file under skipped
(`<section> — open PR #N` or `— branch <name>`); they need no other bookkeeping.

Take the selected section (or `--section`, which skips the selector but never the blocked
set — check it with `select-section.py --prs "$RUNDIR/prs.json" --blocked-only`). Sections
are `src/Sections/` projects only.

**Make the run visible now.** Commit the run file's header alone
(`docs/health/runs/<yyyy-mm-dd>-<Section>.md`, Phase 5's naming; invocation, anchor commit,
branch, budget, `PR: pending`) and push the branch — origin gate first (Phase 1). That file's
path is what the next selector reads the section from; a branch with no push is invisible to
it. No CI runs on a branch push without a PR.

**Name the run after the section, here, once.** Every scheduled run is titled after the routine;
rename the session now — `set_session_title` on the claude-code-remote MCP server, with this
session's own id from `get_session` (no `session_id` describes the caller):

    section-doctor: <Section> — <yyyy-mm-dd>

Skip it without comment when either tool is unavailable; never rename again later.

**A low reforge score is not evidence the section is healthy.** The score measures structure, never
correctness — the lowest-scoring section in the solution was failing open on access control. Nothing
in the ranking rubric surfaces that, so never read a good score as a reason to look less hard.

**Never work a section in the blocked set.** A section with an open section-doctor PR or
branch has unmerged strikes that today's run cannot see — re-doctoring it duplicates work and
produces conflicting PRs. A `--section` naming a blocked section stops like the all-blocked
case — merge the open PR first, or use `resume` to work its Needs-Peter queue.

## Phase 3: Assess

Five stages, in this order. **The order is the point.** The target is derived *before* any scan
runs, because a target written after a linter run is a summary of the linter run (`/simplify`,
Pass 2) — an "ideal shape" that restates the reforge score is the failure this order exists to
prevent.

Phase 2's selector script already built the solution on a normal run; only when it was skipped
(`--section`) start `dotnet build Humans.slnx -v quiet` in the background now —
reforge needs a built solution and 3d's tool threads need the build. Do not look at its output until 3d.

**A re-doctor reads the diff, not the section.** With a `BASE:` from Phase 2 (or, under
`--section`, the last `origin/main` commit to the section's `health.md`), 3a's inventory is
still complete, but 3b–3d read `git diff BASE..HEAD -- <section paths>` in full and skim the
rest; the previous target and `health.md` history say what was already judged. A full cold
assess is for a never-doctored section only.

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
symbol, route and file path it names has been checked against the tree. This is mechanical, and
it is what catches the doc that still names a controller's old home, a dependency a read-split
replaced, or a folder a job moved out of.

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
later runs stop re-litigating them — including code that is large and *blessed*; that is where
such code is recorded, never as a `debt:` item.

The doc carries no generated-by subtitle or run provenance (`current-state-docs-no-history`);
its History table (Phase 5) is the only dated content.

**Trace gate — before 3d dispatches, and again before Phase 4.** The target is a claim about
what IS. Every identifier, route, policy, job id, audit action and file path it names is
`git grep`ped against the tree, and every §4 invariant cites the `file:line` that enforces it.
A name that does not resolve is corrected now; a claim the code cannot prove moves to §5 or §6
or is cut. A target written from the section's own docs inherits their errors, so a claim
taken from prose is traced to code like any other, and a line the run cannot trace is not
written.

**Regenerate the target every run, then diff it against the previous one** (it is in git; the
previous run's is the parent commit's copy). The diff is signal in both directions: the section
moved, or the earlier target was wrong. Record which in the run file. A target that never changes
across runs on a section that keeps changing is a target nobody is really deriving.

### 3d. Threads

Each thread is a lens over the **same complete inventory**, and each reports a disposition for
every file it claims. They run concurrently, but *how* a thread runs follows from what it is —
the wall-clock / token / fragility balance:

- **Tool threads run as background commands** — InspectCode, reforge, conformance
  detectors. No subagent context to duplicate, no idle-lane failure mode, and they run while the
  main thread reads. Reforge's run is `surface-score --format compact --group <Section>`,
  scoped to the section being doctored, on every run, not only the selector's solution-wide call.
  Its score and `loc=`/`cogP95=`/`cogMax=` fields are this run's own measurement: they steer the
  ranked list, and they do not go into a doc row (Phase 5). The PR's surface report is the
  published number, because it is recomputed against the head that actually shipped.
- **Dispatched threads are the default** (nobodies-collective/Humans#1465). A thread reads a lot
  and returns a little, and reading it on the main thread raises the price of every later turn
  in the run. **Small context dominates model choice**: moving a thread off main saves far more
  than swapping its model. Each dispatched thread gets an explicit tagged model (table below)
  and a deadline: the judgment-reading threads get **opus at low effort** (capability-per-dollar
  on this kind of reading, and fewer, more consolidated turns), the mechanical scans **haiku**.
- **Only the spine and the two judgment threads stay on main** — 3a–3c, Shape, Behavior & bugs,
  and 3e: the reading this run exists to do, where a wrong call costs a real finding. Whether
  they *must* stay is an open measurement; move them only on a run that dispatched them and
  came out cheaper without losing findings, never on a hunch.

**Dispatch contract** — every dispatched thread gets, verbatim: 3c's target (all six parts plus
the load-bearing weirdness list), its slice of the 3a inventory, and its row's lens from the table.
The brief lists members, routes and keys; it never counts them (a count typed from a regex over
`public` lines is wrong, and every thread inherits it). It returns a **structured findings list
plus a disposition for every file it claimed**, never prose, and **never edits anything** —
striking is Phase 4's job on main. Prompt line one is `thread: <Name>` so the cost report can
name the row, and at dispatch the thread's name, model and agent type are appended to
`$RUNDIR/assessment/threads.md` — Phase 5's `## Threads` model column is copied from there,
never recalled.

**Absence verdicts carry their proof.** A finding that something is dead, unreferenced, missing
or nonexistent carries the repo-wide, untruncated `git grep -n` (or `reforge` references
query) that proves it — never a `head`-cut or section-scoped grep, never a type-name grep for a
member reached by extension method. Without it the verdict is reported `unverified`, and main
re-runs the grep before any strike built on it.

**`opus low` rows dispatch as the `doctor-reader` agent type** (`.claude/agents/doctor-reader.md`)
— an agent definition is the only place effort can be pinned; a bare Agent call's `model`
parameter cannot set it. Haiku rows use the plain model tag (default effort). Where the
environment lacks the agent type, fall back to the plain model tag and note it in `## Threads`.

**A dispatched thread that misses its deadline does not block the strike loop:** work its
checklist on the main thread and label it self-run in the run file. That is the degrade path —
files are never silently dropped, because the coverage block still demands a disposition for
each one.

| Thread | Lens | Runs as |
|---|---|---|
| **Shape** | `/simplify`'s method against the target: shape mismatches, duplicated pipelines, pass-throughs, over-general options, dead and over-exposed surface, per-method external-caller counts | main |
| **Behavior & bugs** | Does it do what it claims? Walk each flow against the target's invariants. Where the section consumes authored content (markdown, resx, templates, seed data), run the **real shipped content through the real pipeline** — a defect whose trigger is the shape of an input file is invisible to every code-reading thread | main |
| **Freshness** | The section's docs vs code: claims that no longer hold, `freshness:triggers` globs that still resolve, and triggers that watch *everything the doc asserts about* — including another section's file where the doc names it. A fixed claim gets swept everywhere it appears | subagent (opus low) |
| **Conformance** | `docs/architecture/section-conformance.yml` — the per-section rules nothing enforces yet. Detectors are mechanical; the judgment is what to do about a hit | background + subagent (haiku) |
| **Tests** | The invariant coverage matrix — every invariant, negative access rule and trigger in the target mapped to a pinning test; redundant and asserting-the-mock tests | subagent (opus low) |
| **Prose & surface** | InspectCode Tier 1/2; docs that are 500 words where 50 would do; missing translations, resource keys not prefixed with the section name (`resource-key-prefix`, cleanup — report the count, don't backfill unless the run is *for* that); nav quality — dead ends, missing backlinks, discoverability from `AdminNavTree`. **Fixed step, not judgment:** list every key in the section's base resx, grep each literal across `*.cs`, `*.cshtml` and `*.resx` outside the set, exclude prefixes the code builds at runtime (`$"…"` or concatenation against the localizer), and report the zero-reference remainder as one finding with the list in `$RUNDIR` | background + subagent (haiku) |
| **History** | Prose narrating a prior state: a deleted/renamed project or type, a migration/lane number, "used to live in X", "the first section to Y", a dated run post-mortem, rationale for a decision no longer contested. **Cut test: keep only if it changes what a reader does** — a live constraint, a non-obvious invariant, a landmine that bites if reverted. A load-bearing "why" moves to the issue, linked, not narrated in the file. **Dated design records** (`src/Sections/*/Docs/20*.md`) are history by design (`current-state-docs-no-history`): judged on dangling references and false *current* claims only; deletion is proposed only when the record is over a month old **and** every fact it carries is confirmed present elsewhere in the corpus | subagent (opus low) |
| **Comments** | Every comment in the section's inventory, rewritten or deleted. Cut what restates the next line, decision history, hedging, reassurance addressed to the next agent. **Cut test: a comment survives only if it carries something the code cannot say** | subagent (opus low) |
| **Inbox** | Section-tagged `debt-ledger.yml` items, open GitHub issues, in-app issues. Work or rank them — and **review** the open issues for validity / consistency / freshness / spec quality (below); off-section finds go to the run's sweep queue as `debt:`, never written to the ledger directly | subagent (opus low) |

**Every thread that does not run says so in the run file, with why.** A silent skip leaves a whole
dimension unmeasured with nothing flagging it. A thread earns removal from this table only when
several runs record it as "ran, found nothing".

**Per-thread rules worth the line:**

- **Behavior & bugs** — read the section's auth paths by hand. A doc-code contradiction on gating
  is invisible to grep and to every other thread.
- **Tests** — always build the invariant matrix, including when the section looks well tested; the
  gaps it finds are the invariants nobody thought to doubt.
- **Freshness** — a doc claiming a test that does not exist is a doc to fix, never a test to
  write; the test would be an absence assertion — `memory/architecture/no-tests-for-absences.md`.
  A trigger that resolves is not a trigger that works: check each path actually
  carries the claim, not merely that it is live. Read a feature doc's "Out of scope" list against
  its route table every time; it ages worse than the body.

#### Open-issue review (Inbox)

The Inbox thread pulls the section's open issues to work or rank them. Nothing then checks
whether those issues are still *correct* — and a run that has just read the section end to end,
inventory and target and docs and invariants, is the best-informed reader of that backlog anyone
gets. Throwing that away is how a backlog drifts: issues describing files that moved, asking for
behavior that shipped, contradicting each other or the section doc, or predating a project
split that changed the answer.

So review each one against **this run's own target shape and inventory**, on four lenses:

- **Validity** — does it still describe real code? Do its paths, types, routes and project names
  resolve? Was it shipped already?
- **Consistency** — does it contradict the section doc, another open issue, or a hard rule?
- **Freshness** — does it predate a change (section split, read-split, a deleted context) that
  changes the answer or the scope?
- **Spec quality** — are the acceptance criteria still meaningful? Is the section label present?

Output is a **recommendation, never an action.** Each reviewed issue becomes a numbered finding
in the ranked list, carrying the issue ref, a verdict of `close` / `edit` / `relabel` / `keep`,
and the one-sentence reason — that is the finding's one prose description (Phase 5). The
`## Needs Peter` checklist then cites it by number and adds no prose of its own.

**Hard constraint: a run may not mutate an existing GitHub issue.** No close, no edit, no
relabel, no comment on another issue — including issues this run's own findings duplicate, and
including a `keep`. Every such verdict is enacted by Peter, after review; this sits on Phase 4's
skip-and-queue list beside schema changes and surface additions.

**Opening a new issue on `peterdrier/Humans` is allowed** — it is a write of the run's own,
like its run file and its PR (whose body and description it owns). Never upstream.

Cap the pass at the section's open issues — recommendations are per-issue one-liners, so a large
backlog costs the run one line each rather than a budget. Record the pass as ran or skipped in
`## Threads` like every other thread; a review that did not happen says so, with why.

**Issue scope is `peterdrier/Humans` only, by default.** The standard cloud environment's
GitHub access does not reach `nobodies-collective/Humans` — a default run never queries it,
probes for it, or records the review as partial for lacking it; fork-only **is** the complete
review, and `## Threads` states its scope without caveat. Issues an upstream backlog might
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

(the GitHub MCP `issue_read` where `gh` is absent — Phase 2's rule; the issue need not still
be open, the read only has to prove access). A probe that fails for **any** reason — scope,
auth, network, rate limit, missing tool — suspends that repo's half; don't reason about the
cause, and don't infer one repo's reach from the other's. `## Threads` then records what was
actually covered. The ledger and in-app halves are unaffected either way.

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
plus one sentence naming which items came from the target rather than a scan. Evidence in the
run file is not the verdict; write the verdict.

**Checkpoint, then shed the assessment.** The last required 3e step, before any strike executes:
`mkdir -p "$RUNDIR/assessment"` (Phase 0 created only `$RUNDIR`), then write the assessment's
outputs there — the ranked list (finding numbers,
one-line descriptions, source thread, intended play) as `ranked-list.md`, each thread's findings
list beside it (one file per thread), the independence verdict, the 3a inventory with each
path's disposition so far, and the thread-status table (how each thread ran, its model, its
findings count) — everything Phase 5's `## File coverage` and `## Threads` blocks will need.
Once all of it is on disk a compaction can no longer lose findings or coverage state, and
everything Phases 4–6 need is re-readable for a few K tokens.

The 3e→4 boundary is the run's context shed. Phases 4–6 are where most of a run's turns happen,
and they need the checkpoint files, 3c's target and the strike's own files — not the hundreds of
K of assessment reads behind them. From here, disk is the authoritative state: work each strike
from its checkpoint entry, re-read `$RUNDIR/assessment/` rather than relying on scrollback, and
treat a compaction at or after this boundary as costing nothing — the state it sheds is on disk.
**After any compaction, every statement about the run's own conduct — which threads ran and on
what model, findings counts, the ranked order, what was struck, whether a compaction happened —
is read back from `$RUNDIR/assessment/`, the phase log or `git log`, never restated from the
summary.** Never re-read a whole file to answer a question a targeted reforge query answers
("who calls this", "where is this defined"), and never re-open a file only to confirm what a
checkpointed finding already states.

## Phase 4: Strike

Work the ranked list until budget exhausted. **Drain the list — stopping early with strikeable
items remaining is a failure mode, not a judgment call.**

Rank is value order; *execution* order is `cut → delete → dedup → collapse → rearch`, each green
before the next (`/simplify`'s phase discipline). Cutting an unneeded behaviour turns whole
subtrees into dead code, so it precedes deletion; deletion is near-zero risk and shrinks
everything downstream of it. A `collapse` or `rearch` item routinely outranks a `delete` one —
a wrong abstraction costs every future session, a dead local costs one grep — but it is still
executed after it. Budget checks are real
`date` reads between items, never estimates.

**Mechanical strike classes execute in subagents; judgment stays on main.** A strike whose whole
scope is named by its checkpoint entry — dead-code deletion, doc-drift fixes, comment strikes,
mechanical renames — dispatches to a per-strike **sonnet** executor subagent, given only the
strike's checkpoint text, its target file paths — **absolute, rooted at `$WORKTREE`**; a
subagent does not inherit the run's cwd, and on a local machine a relative path can land in
another session's checkout — and the rules of this phase it will touch (the doc-sweep and
delete-sweep rules, the resx/XML rule, the build-output rule). It edits and validates under
`$WORKTREE` only, runs **no git commands** — where a handed rule prescribes `git grep` (the
delete sweep), the executor runs the same search with the Grep tool or `rg -n` rooted at
`$WORKTREE` instead — and returns a diff summary; the main thread reviews
**`git diff` in the worktree, never the executor's own summary,** and commits. One executor at a
time — Phase 0's one-build-per-worktree rule applies to them too. **Every Agent dispatch in this
phase — executor and reviewer alike — opens with a `thread:` marker as its whole first line**
(`thread: strike <what>`, the same `<what>` as the item's phase-log mark; `thread: review
<what>`) so the cost report names its row per 3d's convention instead of an opaque agent
filename, and is logged to `$RUNDIR/assessment/threads.md` like a 3d thread. Judgment strikes —
`collapse`, `rearch`, any deletion whose safety depends on cross-file context — and every
reviewer gate (step 4) stay on the main thread. The split is per strike *class*, never blanket:
cross-file context on main is what catches the miscounts a narrowly-briefed executor cannot see.

**Before any strike names a symbol, bound its blast radius by grep, not by eye:** the item
carries a repo-wide `git grep -n -- '<Symbol>'`; a `dedup` strike greps the literal expression
it is collapsing before striking; a cut reads the whole file it is in, not the flagged lines.

Per item (one item or tight cluster per commit):

1. Pick the play. `/simplify`'s *method* is absorbed into Phase 3 — do not call the skill from a
   run: it is audit-gated (its approval gate is a merged audit PR, then one item per PR) and that
   cannot fit inside one run, one PR. It stays invocable for repo-wide work. The rest of the
   toolbox is still called directly where it fits: `section-align`, `trim-tests`,
   `section-read-split`, `reuse-review` (against the section's own surface), the
   `.codex/skills/humans-refactor` lane process, a `debt-ledger.yml` item — or a direct fix.
2. Fix it right — no surgical fixes. Reuse-first. **Open the `memory/` rule governing the shape
   being written** — the doc, the test, the resx, the comment — not only the problem being
   solved, and name it in the commit. **A guardrail never retires on a subagent's approval:**
   a strike that deletes or weakens a test under `tests/**/Architecture/`, an analyzer, a
   baseline or a ratchet goes to Needs Peter with the four-line brief
   (`brief-before-retiring-guardrails`), never to step 4's reviewer.

   **A `dedup` or `collapse` item checks the shape it is collapsing *into*, not only the one it
   is collapsing.** "Two branches differ in one attribute" is a valid trigger and says nothing
   about whether the merged form is legal. Read the target form against
   `docs/architecture/code-review-rules.md`'s hard-reject list and the section's own load-bearing
   weirdness, and where a linter owns that shape (`.claude/razor-lint.sh` for views) run it on the
   changed file rather than trusting it to fire later.
3. Gate per `scoped-inner-loop-tests`: the section's test project
   (`dotnet test tests/Humans.<X>.Tests -v quiet`; `dotnet build Humans.slnx -v quiet` where
   there is none) gates each strike; the full `dotnet test Humans.slnx -v quiet` runs once
   before each push; and the section gate re-runs after the **last** edit to any file the
   commit carries — a test touched after the gate is an untested test.

   **A test the run adds is only covered if some CI job actually runs it — check the filters, not
   the suite.** Before writing "CI is the gate" about a new test, resolve its assembly against
   every workflow's `dotnet test` invocation and confirm one of them would select it:

   ```bash
   grep -n 'dotnet test' -A4 .github/workflows/*.yml | grep -i 'filter\|dotnet test'
   ```

   `build.yml` runs `--filter "FullyQualifiedName!~Humans.Integration.Tests"`. **That exclusion is
   deliberate and permanent** — `Humans.Integration.Tests` is the home of tests that cannot run
   under CI at all, because they integrate with external things CI does not have
   (`memory/process/integration-tests-are-not-ci-tests.md`). A test put there runs nowhere on any
   branch, and that is the correct home only for a test which genuinely needs a live external
   dependency. A test that must actually run belongs in `tests/Humans.<Section>.Tests/`.

   **Unreachable → move it, or say plainly in the run file and the commit that it does not run**;
   never report it as covered by CI. Never propose a CI job for that project, and never count its
   tests as a coverage gap — the rule above settles it.
4. Non-mechanical changes (deletions beyond plainly-dead code, structural moves) → second-opinion
   reviewer subagent — the `doctor-reviewer` agent type (`.claude/agents/doctor-reviewer.md`,
   fable; fall back to a plain opus subagent with its prompt where the type or model is
   unavailable) — score-blind, default-reject: "name the concept that improved in
   one sentence." **It reads the uncommitted working-tree diff**, so a reject shrinks the diff
   rather than adding a fixup commit. Reject → rework once; second reject → revert, record.
5. **Doc fixes sweep the claim — by literal string, repo-wide**: when a strike removes or
   renames a route, type, method or path, or fixes a claim naming one, grep the whole repo for
   the exact string and fix or enumerate every hit in the run file. Sweep the abbreviations too —
   clearing every full-name hit and leaving the initialism standing in the same file is the usual
   miss — and update the freshness trigger in the same pass.

   Once a section doc is open at all, read it **end to end** against the code: fixing its headline
   stale claim and leaving the smaller ones is not fixing the doc. Rebuild its Cross-Section
   Dependencies from the `.csproj` project references, never from the prose, and keep every
   line `docs/sections/SECTION-TEMPLATE.md` requires (the `**Status:**` line included). Verify any
   explanation of why an unused member exists before writing it down — "nothing reads these" is
   often both the true answer and a finding. Distrust "never crosses the boundary": for a section
   reading a shared read-model the honest form names what is carried, what this code reads, and
   what the output record exposes.

   **A delete sweeps its own symbols by literal name, before the strike commits.** Reading the
   diff does not discharge the rule above. For every member, type, route or table the strike
   removed, grep the exact name and clear or enumerate every hit:

   ```bash
   git grep -n -- '<DeletedName>' -- '*.md' '*.cs'
   ```

   Then **re-read the header of every file the strike cut from**. A file's class-level doc comment
   describes what the file holds, so cutting from the body changes the truth of the comment above
   it. The falsehood a delete creates sits nearest the delete.

   **When the doc and the code disagree and the code looks wrong, change neither** — the pair goes
   to Needs-Peter together. Editing the doc to match a suspected defect cements it.
6. **UI-affecting strikes get runtime verification**: render the changed page in the running app
   (`dotnet run` + browser/test-site) before the PR — a green build does not prove a cshtml/JS
   change works. Where the app cannot start (the cloud container has no database): a JS strike
   is exercised in headless Chromium loading the shipped modules against a fake DOM, asserting
   the changed path and no page errors; a cshtml/resx strike gets the build plus
   `.claude/razor-lint.sh`, and the run file says as a dated session line that no live render
   happened. Anything beyond that is a preview-deploy check after the PR opens.
7. Run the **prose gate** (Phase 5) over the staged diff, then commit `doctor(<section>): <what>`.
   Push every 3–5 items, origin gate (Phase 1) in the same call. When a reviewer gate could not
   be obtained, say so in the commit message as well as the run file — a commit that lands
   unreviewed should say so where the diff is read.

**File-format rules that only the build catches:**

- **resx/XML edits are structure-aware** (python/XML tooling), never line-based sed. Neutral resx
  is one entry per line but the language variants are multi-line, so sed corrupts them silently.
- **Full-build before `dotnet ef migrations add` or `remove`.** With `--no-build` they read
  whatever assembly the startup project last built, which generates empty migrations and lets
  `remove --force` walk back an already-merged one. Recover a mis-removal with `git checkout` of
  the Migrations folder, never by hand-editing.
- **With no compiler, a C# doc-comment edit is safe only** if it adds no `<see cref>` and the run
  verifies tag balance by parsing each `///` block as XML. CS1591 is suppressed, CS1574 is not,
  and `TreatWarningsAsErrors` is on.
- **A comment-only `.cs` edit is not score-neutral** — a doc comment on production code is
  production LOC. Never call such a change "docs only".

**Skip-and-queue classes** (never block the loop): schema/EF changes of any kind, public/interface
surface *additions*, privilege changes, **retiring or weakening a guardrail** (step 2's brief),
**mutating a GitHub issue** (closing, editing, relabelling or commenting on one — 3d's Inbox
review recommends, Peter enacts), anything needing Peter's judgment → skip, queue for Phase 7's
Needs-Peter block. A queued item naming a symbol carries its repo-wide `git grep -n`, so the
reader sees the blast radius the run saw. If in-flight feature work on this section
surfaces mid-run → stop striking, ship the assessment-only PR, note it in the run file.

**The Needs-Peter admission test.** An item is admitted only if **both** hold: *would two
reasonable implementers do different things?* and *is the choice inside this section?* Anything
failing either is not a decision, and a block padded with non-decisions buries the items that are:

| Fails because | Goes instead to |
|---|---|
| There is one obvious answer — the run is telling, not asking | the ranked list; do it |
| The choice sits in another section | this run's `## Sweep queue` |
| It is a finding, not a fork | the findings list and the assessment summary |

**Debt found and not fixed goes to a ledger, not a run file** — a run file is a dated artifact
nobody re-reads (`memory/process/debt-ledger-additions.md`). *In-section*: append to
`src/Sections/Humans.<X>/Docs/debt.yml`, creating it if absent. *Off-section*: this run's sweep
queue (`debt:`), never chased mid-assessment; Phase 5's sweep writes it to the **owning section's**
ledger after this run merges — debt belongs where the next reader of that section will meet it.
A queue item is ledger prose already: no reforge figures, no counts, no line numbers that a
fix will move; code the target blesses is not debt and goes to `health.md` (3c), never the
queue.

**Section ledgers have no single writer, by design.** A sweep writes the ledger of whichever
section owns the debt, so two runs can touch one ledger and their PRs can conflict. Appending to
a YAML list rarely collides, and a conflict here is one hand-resolved hunk — the same no-locking
trade the rest of the sweep machinery takes. Don't add locking, ownership checks, or a routing
detour to avoid it.

## Phase 5: Bookkeeping

**Re-read Phases 5–7 from this file before writing anything below.** By Phase 5 the run is hours
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

  **Prose gate — mechanical, before every commit that carries prose this run wrote** (run
  file, `health.md`, `debt.yml`, any `.md`, rewritten `.cs` comment blocks, the sweep's
  writes). `no-derived-aggregates-in-docs` binds the author of new prose, and the run is that
  author:

  ```bash
  git diff --cached -U0 | grep -niE '^\+.*\b([0-9]+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)\s+[a-z-]+s\b'
  ```

  A hit that counts a list or set — one this file carries, one another doc carries, or one the
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

## Phase 7: PR

**Self-review the run's own new prose first — only that prose, against the gates below.** Read
`git diff origin/main -- '*.md' '*.yml'` plus every `.cs` comment block the run rewrote: the
prose gate (Phase 5), the trace gate (3c — every symbol, route and path named resolves; every
"only", "never" and "always" is checked), and the render rule — a claim about what a page
shows traces to the `.cshtml` that renders it, not the DTO that feeds it. Text the run wrote
this session is the text most likely to be wrong, and a reviewer here is not free.

**Run `dotnet format whitespace Humans.slnx --verify-no-changes` before pushing, not after CI says
so.** A green build is not the formatting gate — collection-expression line breaks pass the build
and the full test run, and fail code-quality.

```bash
git remote get-url origin | grep -qE 'github\.com[:/]peterdrier/Humans(\.git)?$' || exit 1   # origin gate, same call
git push -u origin section-doctor/$TS
gh pr create --repo peterdrier/Humans --base main --title "doctor(<Section>): <headline>" --body ...
```

The title's headline names something a user or reader would notice — the fix, the false
claim corrected, the surface removed — never the run's bookkeeping.

Body: the opening header paragraph (run/section, run-file link, target-shape link) **ends with the
next-up forecast**, read from the `UPCOMING:` line in `$RUNDIR/selection.txt` — "Target shape:
`Docs/health.md` (new). Likely future sections: A, B, C, D." — never buried lower in the body;
omitted when the selector was skipped (`--section`). Then
assessment summary, worked/skipped bullets, and a
**`## Needs Peter`** block — terse, numbered, answerable in a word or two, **citing findings by
number rather than re-describing them** (Phase 5). **The PR body is the authoritative queue while
the PR is open** (resume reads it from there); the run file's copy carries it forward after merge.
One PR per run; never merge.

**Cost report** — before creating the PR, run:

```bash
python .claude/skills/section-doctor/cost-report.py section-doctor/$TS "$RUNDIR/phase-log"
```

It finds this run's own session transcript under `~/.claude/projects` (the model never sees its
own usage in-band, but the harness logs every API call's tokens there), buckets the main thread
by the phase log, adds one row per subagent transcript (named by the `thread:` marker its
prompt opens with), and prints a markdown table with per-row model and API-equivalent $ — plus
footer lines reporting the peak main-thread context and any compactions detected (a compaction
mid-run is exactly when Phase 5's re-read rule earns its keep; if one is reported, say so in the
run file's retro).

**Rows are named by what the run was doing, not by phase number** — each row takes the label from
its `mark` line, and the phase id is a trailing column. Phase 4's per-item marks give one row per
strike, so the largest bucket reads as a breakdown rather than a lump. Whatever the table's rows
are, they are what the reader gets; a run that marks lazily reports lazily.

The table is a **Phase 1 → PR-creation cutoff, not a run total** — the PR
create/backfill calls and any Phase 8 work land after measurement (the footer says so). **Post it
as a PR comment immediately after `gh pr create`** (`gh pr comment <PR#> --body-file` — never
inline shell-quoted; the GitHub MCP comment tool in a cloud run without `gh`). The comment is the
table's only home: one append-only write adjacent to the create call, costing no push, no CI run
and no review round. Never paste it into the PR body or the run file. The table stands on its
own — never compare it against another run's cost or pull in a prior run's figures; cross-run
reading is Peter's, done over the PRs. The script never fails the run — on any discovery problem
it prints `Cost: unmeasured (...)`; post that line as the comment all the same (a failed
measurement leaves a visible record, never silence) and note it in Needs-Peter.

Then backfill the real PR number over every `pending` reference (run file header, health history
row), commit, push again.

**That backfill is the last bookkeeping push.** From here a push must change code, tests, or a
doc a reader depends on. **Never push a commit whose entire content is a corrected figure or a
restated status about the branch** — such a correction rides along with the next substantive
commit, or is skipped. Every push costs a CI run, a preview deploy, a surface report and a review
quota. The same holds for the run file's account of itself: a render checked on the preview
deploy, a compaction the cost comment reports, is written in when a substantive commit next
goes out, or not at all — and never claimed before it happened.

## Phase 8: Inline round (interactive runs only)

If Peter is present, present the Needs-Peter items inline now (terse, numbered, plain prose —
never AskUserQuestion) and apply answers as new commits + push, ticking each answered item in
both the PR body and the run file — Resume mode's grep gate applies here too. Unattended morning
runs skip this; `resume` covers it. Unanswered items carry forward — never re-asked.

## Phase 9: Stand down — the worktree stays

**Stop working. Do not remove the worktree.** A run ends when its PR is merged or closed, not when
it is opened — review arrives after Phase 7 (a BLOCK, bot findings, Peter working the Needs-Peter
queue) and every one of those is answered by committing to this branch.

**A review bot's finding is a sample, not an instance.** Before fixing the reported line, grep the
branch for the class of claim it is an example of — the type, the method, the abolished case.
Fixing only the reported line leaves the siblings and looks resolved.

**Review rounds run under `review-round-budget` and `.claude/skills/steward/SKILL.md`**, and
each push in them takes the origin gate (Phase 1) in the same call, then confirms the PR's head
sha advanced (`pull_request_read`) — a push to the wrong repo is silent: no CI, no review, no
deploy.

**Resolve gate.** A thread is replied to and resolved only after, on the pushed head:
`git cat-file -e <sha>` succeeds for the commit the reply cites and
`git branch -r --contains <sha>` lists `origin/section-doctor/$TS`; and the line the finding
named is re-grepped and reads as the reply claims. A reply names a commit that exists on
`origin` or names none. At the round cap, every finding agreed and left unfixed is listed in the
cap comment and in the run file's `## Needs Peter` — never resolved, never marked fixed.

Phase 9 writes nothing. Phase 7's backfill commit is the run's last write, and everything a later
session needs is already derivable: the branch is `section-doctor/$TS`, its workspace is
`$REPO_ROOT/.worktrees/section-doctor-$TS` locally (`$REPO_ROOT` in a cloud run), and the PR
number is in the run file. `$RUNDIR` is
scratch; leave it for the OS to reclaim. **Leave the worktree clean** — an uncommitted edit here
never reaches the PR and makes the retained worktree dirty for whoever picks the review up.

**Teardown happens when the PR reaches terminal state** — by `/merged`, or by hand with
`git worktree remove $WORKTREE` from `$REPO_ROOT` (never a recursive delete). A cloud run has
nothing to tear down — `$WORKTREE` is the repo root and the container is reclaimed on its own, so
never run `git worktree remove` there.

Phase 2's **`ALL BLOCKED`** exit is the one case that tears down immediately: no PR, no branch
content, nothing to come back for.

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

## Standing constraints

- Business functionality does not change.
- No EF migrations, schema changes, or data backfills — queue them. No analyzer suppressions.
  Never touch `[DontFix]`.
- Public-surface additions need Peter; dead-surface deletion is the job (reviewer-gated).
  Guardrail retirement needs Peter's go on a brief (`brief-before-retiring-guardrails`); the
  reviewer subagent is never that go.
- Explicit tagged model on every subagent, and a `thread:` marker on every dispatch. Never
  leave the branch red between commits.
- **Every push is preceded by the origin gate in the same shell call**; on a mismatch the run
  stops. A push never goes by remote name to a remote the gate has not just checked.
- **A run touches only:** the section's files (+ callers where a play requires), the section's
  `Docs/health.md` and `Docs/debt.yml`, its own `docs/health/runs/<date>-<Section>.md`, and — in
  the sweep commit only (Phase 5) — the debt ledgers (central, and any
  section's whose debt the sweep is routing), `memory/`; never the run files it sweeps, and
  **never this skill's own files** (Peter edits those; a run proposes in Needs-Peter). Run
  scratch goes to `$RUNDIR`, outside the worktree entirely. Nothing writes
  `docs/architecture/maintenance-log.md`.
- **Every GitHub issue is read-only to every run.** No close, edit, relabel or comment, on any
  issue, ever — 3d's Inbox review recommends and Peter enacts. A run's only GitHub writes are its
  own PR.
- **`docs/architecture/section-conformance.yml` is read-only to every run**, sweeps included.
  Rows are added and removed only at Peter's direction; a run that wants one proposes it in its
  Needs-Peter block.

