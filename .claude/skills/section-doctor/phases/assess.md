# Phase 3: Assess

Five stages, in this order. **The order is the point.** The target is derived *before* any scan
runs, because a target written after a linter run is a summary of the linter run (`/simplify`,
Pass 2) — an "ideal shape" that restates the reforge score is the failure this order exists to
prevent.

Phase 2's selector script already built the solution on a normal run; only when it was skipped
(`--section`) start `dotnet build Humans.slnx -v quiet` in the background now —
reforge needs a built solution and 3d's tool threads need the build. Do not look at its output until 3d.

**A re-doctor reads the diff, not the section.** With a `BASE:` from Phase 2 (or, under
`--section`, the `origin/main` commit that added the section's newest run file), 3a's inventory is
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

**Dispatch contract.** A dispatched thread's prompt is short: line one `thread: <Name>` (the
cost report names the row by it), then the section, its slice of the 3a inventory (members,
routes and keys listed, **never counted** — a count typed from a regex over `public` lines is
wrong, and every thread inherits it), its deadline, and the **absolute** paths of the files it
reads itself: `$WORKTREE/.claude/skills/section-doctor/threads/CONTRACT.md` (return format,
never-edit, absence-verdict proof), its lens file from the table, and the target
`$WORKTREE/src/Sections/Humans.<X>/Docs/health.md` — so the 3c trace gate runs before any
dispatch. Every path in the prompt, inventory included, is rooted at `$WORKTREE`: a subagent
does not inherit the run's cwd, and on a local machine a relative path resolves against the
main checkout's stale copy, or no copy at all. It returns a **structured findings list plus a disposition for every
file it claimed**, never prose, and **never edits anything**. At dispatch, log it:
`doctor.py dispatch-log <Name> <model> [agent-type]` — Phase 5's `## Threads` model column is
copied from `$RUNDIR/assessment/threads.md`, never recalled.

**Absence verdicts carry their proof** (`threads/CONTRACT.md`); one reported `unverified` is
re-grepped on main before any strike built on it.

**`opus low` rows dispatch as the `doctor-reader` agent type** (`.claude/agents/doctor-reader.md`)
— an agent definition is the only place effort can be pinned; a bare Agent call's `model`
parameter cannot set it. Haiku rows use the plain model tag (default effort). Where the
environment lacks the agent type, fall back to the plain model tag and note it in `## Threads`.

**A dispatched thread that misses its deadline does not block the strike loop:** work its
lens on the main thread and label it self-run in the run file. That is the degrade path —
files are never silently dropped, because the coverage block still demands a disposition for
each one.

| Thread | Runs as | Lens |
|---|---|---|
| **Shape** | main | below |
| **Behavior & bugs** | main | below |
| **Freshness** | subagent (opus low) | `threads/freshness.md` |
| **Conformance** | background + subagent (haiku) | `threads/conformance.md` |
| **Tests** | subagent (opus low) | `threads/tests.md` |
| **Prose & surface** | background + subagent (haiku) | `threads/prose-surface.md` |
| **History** | subagent (opus low) | `threads/history.md` |
| **Comments** | subagent (opus low) | `threads/comments.md` |
| **Inbox** | subagent (opus low) | `threads/inbox.md` — includes the open-issue review |

The main-thread lenses:

- **Shape** — `/simplify`'s method against the target: shape mismatches, duplicated pipelines,
  pass-throughs, over-general options, dead and over-exposed surface, per-method
  external-caller counts.
- **Behavior & bugs** — does it do what it claims? Walk each flow against the target's
  invariants. Where the section consumes authored content (markdown, resx, templates, seed
  data), run the **real shipped content through the real pipeline** — a defect whose trigger is
  the shape of an input file is invisible to every code-reading thread. Read the section's auth
  paths by hand: a doc-code contradiction on gating is invisible to grep and to every other
  thread.

**Every thread that does not run says so in the run file, with why.** A silent skip leaves a whole
dimension unmeasured with nothing flagging it. A thread earns removal from this table only when
several runs record it as "ran, found nothing".

**Inbox's open-issue review** (its lens file) is bound by rules the strike loop also
enforces: a run never mutates an existing GitHub issue (Phase 4's skip-and-queue list, Standing
constraints), and each verdict is one numbered finding whose `## Needs Peter` entry cites the
number and adds no prose (Phase 5). Record the pass as ran or skipped in `## Threads` like every
other thread, with its repo scope.

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
write the assessment's outputs to `$RUNDIR/assessment/` — the ranked list (finding numbers,
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

