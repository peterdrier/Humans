---
name: section-doctor
description: "Daily per-section review cycle driving a section toward the smallest, clearest form that still does everything it does today. Selects its section live each run (scripted), inventories every file, derives the target shape before any scan, works parallel reading threads into one ranked list and strikes it on a 2-3h budget. One PR per run; each run's report and Needs-Peter queue live in its own docs/health/runs/ file; 'resume' applies Peter's answers later. Use for the morning section-improvement run, 'doctor <section>', or 'run section doctor'."
argument-hint: "[resume] [--section=<Name>] [--budget=2.5h] [--mutation]"
---

# Section Doctor

Design record: `docs/superpowers/specs/2026-08-17-section-doctor-design.md`.

**Only Peter edits this skill** — this file, `threads/`, the scripts and the two `doctor-*` agent
definitions. A run never edits them. This file is instructions, not a record: no shas, no dates,
no accounts of past runs; a rule stands on its own or it is not here.

## Intention

Humans is run by volunteers and, increasingly, read and changed by agents. Every section will be
extended, debugged and rewritten many times by people and models who were not there when it was
written, and each of them pays, in attention and in tokens, for every line, comment, doc claim and
test still sitting there. This process keeps that price honest. Once a day one section is read
whole, as it stands today, by something with the time to read all of it; whatever no longer earns
its place is removed, whatever the docs claim is checked against what the code does, and any real
defect met on the way is fixed. The measure is not a score or a diff size: it is that whoever
opens the section next finds it smaller, truer, and doing exactly what it did — and that Peter can
trust what the run says it did without re-reading the section himself.

When a call is ambiguous, ask: *does this make the section truer and lighter for its next reader
without changing what it does for its users, and will Peter be able to see and verify it?* Yes to
both: do it. Anything that changes behaviour, widens surface, retires a guardrail, or cannot be
verified is Peter's call, not the run's.

A run is judged on this: did the section get smaller and clearer without losing anything
(net across every section touched — the PR's own diff stats are the figure, never restated in
prose); was every file actually looked at (coverage is what makes this a review rather than a
sweep, and it is where the bugs come from); is the section still doing exactly what it did.

**Contract: business functionality does not change.** That is what buys the latitude to rewrite
anything else.

## Precedence

`docs/architecture/peters-hard-rules.md` and `peters-working-rules.md` outrank everything. The
prompt that invoked the run outranks this skill: a problem that cannot wait for a skill edit is
fixed in the routine's prompt, so where the two conflict the prompt wins and the run file says which
instruction it followed. This skill outranks a run's own judgment about process.

## Invocation

| Form | Behavior |
|---|---|
| *(none)* | daily run: select a section live (Phase 2), doctor it |
| `resume` | no new work — work the Needs-Peter queue (see Resume) |
| `--section=<Name>` | skip the selector, doctor this section (the blocked set still applies) |
| `--budget=<duration>` | override budget (default 2.5h); wall-clock, checked between items with real `date` reads |
| `--mutation` | run section-scoped Stryker in the Tests thread (`memory/process/stryker-concurrency-coverage.md`) |

Without `--mutation` there is no mutation half: a run never probes for Stryker, mentions it, or
records it as skipped. Nothing installs a toolchain; the environment provides the SDK, `dotnet-ef`
and reforge or it does not. **No compiler is a docs-only run, not a failed one**
(`memory/process/section-doctor-no-sdk.md`): reading threads run, strikes stay in docs, comments and
resx, code findings queue, the run file's header and the PR body say so.

## Tooling

| File | Governs |
|---|---|
| `doctor.py` | the mechanics: `rundir`, `mark`, `push` (origin gate), `commit` (prose gate), `prose-gate`, `dispatch-log`, `inventory`, `runfile`, `check-run-file`, `resolve-check`; the extractors `comments`, `history`, `trace`, `blast`, `review-pack` — each replaces reading the tree for one question |
| `select-section.py` | Phase 2's selection maths — never re-derived in-band |
| `cost-report.py` | Phase 7's cost table from the phase log and the `thread:` markers |
| `threads/CONTRACT.md` + `threads/<lens>.md` | what a dispatched thread reads and returns; one file per lens |
| `.claude/agents/doctor-reader.md`, `doctor-reviewer*.md` | the reading-thread agent and the reviewer tiers (the only place model and effort are pinned); `REVIEW_TIERS` in `doctor.py` maps a section to its tier |

Every subcommand derives the run from its branch, so nothing depends on shell state surviving
between tool calls. **Every commit is `doctor.py commit` and every push is `doctor.py push`** —
a run never calls `git commit` or `git push` directly. `commit` stages nothing: `git add` first,
and it refuses an empty index. Non-zero exit from any script means stop and look, never work
around.

## Standing constraints

- Business functionality does not change.
- No EF migrations, schema changes or data backfills — queue them. No analyzer suppressions.
  Never touch `[DontFix]`.
- Public-surface additions need Peter; dead-surface deletion is the job (reviewer-gated).
  Retiring or weakening a guardrail — a test under `tests/**/Architecture/`, an analyzer, a
  baseline, a ratchet — needs Peter's go on a brief (`memory/process/brief-before-retiring-guardrails.md`);
  the reviewer subagent is never that go.
- Explicit tagged model on every subagent, a `thread:` marker as its first line, and a
  `doctor.py dispatch-log` entry. Never leave the branch red between commits.
- **A run touches only:** the section's files (and callers where a play requires), the section's
  `Docs/health.md` and `Docs/debt.yml`, its own `docs/health/runs/<date>-<Section>.md`, the debt
  ledger that owns a debt it found, and a `memory/` atom it writes. It reads no other section's run
  files. Never `docs/architecture/maintenance-log.md`, never `docs/architecture/section-conformance.yml`
  (rows change only at Peter's direction; propose in Needs-Peter), never this skill. Scratch lives
  in `$RUNDIR`, outside the tree.
- **Existing GitHub issues are read-only to every run** — no close, edit, relabel or comment,
  ever; the Inbox review recommends, Peter enacts. A run's GitHub writes are its own PR and, under
  the bar in "When the skill is wrong", a new issue.
- N unattended days are N open PRs that must merge in any order. A ledger or `memory/` append is
  the only write another run may share; an overlap is one hand-resolved hunk.

## The run

Marks: `doctor.py mark <phase-id> <label>` once per phase before the work starts, and **once per
strike item** in Phase 4 — the cost report buckets the transcript by them, so an unmarked phase
prices into the row above it.

### Phase 0–1: Setup and workspace

```bash
REPO_ROOT=$(git rev-parse --show-toplevel)
TS=$(date -u +%Y-%m-%dT%H%M%SZ)          # the run's identity: branch, run dir, run file
RUNDIR=$(python .claude/skills/section-doctor/doctor.py rundir --ts $TS)
git fetch origin main
if [ "$CLAUDE_CODE_REMOTE" = "true" ]; then   # ephemeral container: no worktree
  git checkout -b section-doctor/$TS origin/main; WORKTREE=$REPO_ROOT
else
  git worktree add $REPO_ROOT/.claude/worktrees/section-doctor-$TS -b section-doctor/$TS origin/main
  WORKTREE=$REPO_ROOT/.claude/worktrees/section-doctor-$TS    # EnterWorktree; everything runs inside
fi
python .claude/skills/section-doctor/doctor.py mark phase1 worktree
```

Scope is frozen at the branch point; never reconcile against `origin/main` mid-run. Locally, scope
every Glob/Grep to `$WORKTREE`. Build and test output stays out of the transcript: `-v quiet
-clp:ErrorsOnly`, long output redirected to `$RUNDIR` and read from there. Never run `dotnet build`
and `dotnet test` against the same worktree at once. Multi-line content goes through `-F <file>`
or a quoted heredoc. Environment caveats are dated per-session lines in the run file, never banners.

### Phase 2: Select

```bash
gh pr list --repo peterdrier/Humans --state open --limit 200 --json number,headRefName,title > "$RUNDIR/prs.json"
python .claude/skills/section-doctor/select-section.py --prs "$RUNDIR/prs.json" | tee "$RUNDIR/selection.txt"
exit "${PIPESTATUS[0]}"
```

(Without `gh`, write the same `[{number, headRefName, title}]` shape from the GitHub MCP tools;
the script reads each PR's files from git itself.) The script needs only git — no build, no
reforge — and computes the blocked set (sections with an open `section-doctor/` PR or a recent
pushed branch), the feature-active down-rank, and the pick: the section changed since its last
run with the highest age-plus-churn priority. A section with no run yet ranks from the commit
that created it, the whole section as churn, after a one-week cool-down. It prints `SECTION:` /
`BASE:` / `RATIONALE:` / `UPCOMING:`. Obey its verdicts — never pick by judgment: `ALL BLOCKED`
(exit 3) and `NOTHING CHANGED` (exit 2) end the run with nothing written (locally, remove the
worktree). `--section` skips the pick but still runs `--blocked-only`; a blocked section stops
the run.

Then, before any reading: `doctor.py runfile <Section> --invocation "<how this run was invoked>"`
writes `docs/health/runs/<yyyy-mm-dd>-<Section>.md` (header, empty blocks, the coverage and
thread tables); `git add` it, `doctor.py commit` it alone and `doctor.py push` — that file's path
is how the next selector sees this run.
Rename the session `section-doctor: <Section> — <yyyy-mm-dd>` via `set_session_title` where the
tool exists; skip silently otherwise.

### Phase 3: Assess

In this order; the order is the point — a target written after the scans is a
summary of the scans. A re-doctor (`BASE:` present, or under `--section` the commit that added
the section's newest run file) keeps the full inventory but reads `git diff BASE..HEAD` in full and
skims the rest; the previous target and `health.md` history say what was already judged.

**3a Inventory.** `doctor.py inventory <Section>` — every tracked path of the section, its
Contracts leaf, its test project and its guide page, with `*.Designer.cs` and
`*DbContextModelSnapshot.cs` tagged generated (migration `.cs` files are not). Assign every other
path to at least one thread; a file no thread claims is a hole in the thread set, not a file to
skip. `check-run-file` (Phase 7) refuses a run file that leaves a path without a disposition.

**3b Behavior first, tool-free.** Read the section for what it *does*, in its user's words: the
external surface grouped into question-shapes (not listed — the grouping is what makes collapse
items visible); owned tables, cross-section calls in and out, config it reads; what its docs, guide
page and specs said it would be — stated-but-unbuilt and built-differently-than-stated are deltas
no tool reports.

**3c Target.** One page in `src/Sections/Humans.<X>/Docs/health.md`, the required parts ("none"
where empty): what the section does (no code nouns); the shapes as a table; the structure those
shapes imply, written fresh; invariants, each stated so a violation is recognisable; seams
(specified-but-unbuilt — reserved, not built, not ranked); deliberately not done (abstractions a
reader would reach for and shouldn't, including ones Peter declined). Plus a load-bearing weirdness
list: essential complexity and settled decisions, with why — large-and-blessed code is recorded
here, never as debt. No generated-by subtitle; the History table is the only dated content.

*Trace gate, before 3d and again before Phase 4:* `doctor.py trace <health.md>` resolves every
backticked name, route, path and `file:line` the target names against the tree — a `MISS` is a
false claim unless the name sits in deliberately-not-done, a `CHECK` (a route whose literal is
not in the code) is read by hand; every invariant
cites the `file:line` that enforces it — a bullet with no enforcement site is not an invariant
and moves to seams, deliberately-not-done, or out. A claim taken from the section's own prose is traced like any
other. Part 1 never restates an invariant part 4 owns. A test this run adds that contradicts an
invariant line is a hard stop: one of them is wrong, find out which. Regenerate the target every
run and diff it against the previous one; the run file says whether the section moved or the
earlier target was wrong.

**3d Threads.** Each thread is a lens over the same inventory and returns a disposition for every
file it claims. Tool runs — reforge `surface-score --format compact --group <Section>`, the
`section-conformance.yml` detectors, InspectCode — execute as background commands on the main
thread; their output is written under `$RUNDIR/assessment/` and handed to the thread that
classifies it, never re-run inside a subagent. The same for the extractors: `doctor.py comments`
and `doctor.py history` write the Comments and History threads' whole input there, so those
threads read a few hundred lines instead of every source file. Reading threads dispatch (small main-thread context
is worth more than any model swap); Shape and Behavior & bugs stay on main.

| Thread | Runs as | Lens |
|---|---|---|
| Shape | main | `/simplify`'s method against the target: shape mismatches, duplicated pipelines, pass-throughs, over-general options, dead and over-exposed surface, per-method external-caller counts |
| Behavior & bugs | main | walk each flow against the invariants; run real shipped content (markdown, resx, templates, seed data) through the real pipeline; read the auth paths by hand |
| Freshness | `doctor-reader` (opus low) | `threads/freshness.md` |
| Conformance | detectors on main + haiku | `threads/conformance.md` |
| Tests | `doctor-reader` (opus low) | `threads/tests.md` |
| Prose & surface | background + haiku | `threads/prose-surface.md` |
| History | `doctor-reader` (opus low) | `threads/history.md`, over `doctor.py history <Section>` |
| Comments | `doctor-reader` (opus low) | `threads/comments.md`, over `doctor.py comments <Section>` |
| Inbox | fetch on main + `doctor-reader` (opus low) | `threads/inbox.md` |

A dispatched prompt is short: line one `thread: <Name>`; the section; its inventory slice (members,
routes and keys listed, never counted); a deadline; and the absolute `$WORKTREE` paths of
`threads/CONTRACT.md`, its lens file, the target, and any input file main pre-fetched (detector
output, the issue dump). A subagent has no GitHub tool and does not inherit the cwd. Log each
dispatch with `doctor.py dispatch-log`. A thread that misses its deadline is worked on main and
labelled self-run; a thread that does not run says so in the run file with why. An absence
verdict reported `unverified` is re-grepped on main before any strike builds on it, and a grep
whose empty result the run will state as a fact is never truncated.

**3e Rank, check, checkpoint.** One value-ranked list across all threads — value is bug surface,
concepts and reader cost removed; effort is a column, never the sort key. A carry-forward item from
this section's previous run file or from the Inbox thread enters the list only after main has grepped the branch
for its distinguishing terms and confirmed it still holds. Then the independence check: if every
item traces to a tool, score or grep, or none cites a shape mismatch, a spec-vs-reality delta, or a
partial abstraction, 3c was reverse-engineered from the scans — re-derive it from 3b and re-rank.
Write the literal verdict, `Independence check: pass` or `Independence check: fail (re-derived)`,
plus one sentence naming which items came from the target.

Last, write everything to `$RUNDIR/assessment/`: `ranked-list.md` (number, one line, source
thread, intended play), one findings file per thread, the inventory with dispositions so far, and
the thread table. **From here disk is the authoritative state.** Every later statement about the
run's own conduct — which threads ran on what model, counts, what was struck, whether a compaction
happened — is read back from `$RUNDIR/assessment/`, the phase log or `git log`, never restated
from a compaction summary. After any compaction, re-read this file's remaining phases.

**3f Verify and close existing debt.** `doctor.py mark phase3f verify-debt`. The run has just read
the whole section, so it is the cheapest moment there will be to notice that a debt row is no longer
true — and the producer of these rows is the only honest closer of them. Before Phase 4 files
anything, read `src/Sections/Humans.<X>/Docs/debt.yml` and give every row that is not already closed
one verdict, oldest `added:` first:

| Verdict | Action |
|---|---|
| still true | leave the row untouched (whether it also enters the ranked list is a separate call) |
| fixed | delete the row and log the closure |
| partially fixed | narrow `what:` to the part still broken, set `status: partial`, log the narrowing |
| cannot tell | leave the row, log it unverified with what would decide it |

**Evidence bar — the whole phase rests on it.** A row is closed or narrowed only on a specific
`file:line` or a named test that decides it, cited in the log. A row saying "no test pins X" closes
only by naming the test that now pins X; "unlocalized in every culture" closes only by naming the
keys and the cultures now carrying them; "duplicated in A and B" closes only by reading both sites.
An absence the run will act on is re-grepped on main, never taken from a thread's `unverified`
verdict. Anything short of that is *cannot tell*, which is a normal outcome and costs nothing — an
unattended run that closes rows on plausibility is worse than one that never closes any.

Log every verdict in the run file under `## Debt verified`, one line per row:
`<id> — still-true|closed|narrowed|unverified — <reason in a phrase> — <file:line or test name>`.
`still-true` is a positive confirmation ("I checked and it is still a real defect") and is
distinct from `unverified` ("I could not tell") — never conflate the two. Prose and cites only:
no counts, no totals (the prose gate refuses them).

**Ids are permanent.** Rows carry stable ids under a `next_id:` header
(`memory/process/debt-ledger-additions.md`). Never renumber, never recycle, never lower `next_id:` —
a closure removes its row and leaves the header alone. A row whose `root:` points at a row this pass
closes is verified on its own merits in the same pass, never closed by inheritance.

**Budget.** 3f is capped at a tenth of the run's budget — roughly fifteen minutes on the 2.5h default
— and it is timeboxed, not completed. Verifying the oldest rows to the evidence bar beats
half-reading all of them: stop at the cap, leave the remaining rows untouched, and say under
`## Debt verified` which row the cap stopped at.

### Phase 4: Strike

Drain the list; stopping early with strikeable items left is a failure. Rank is value order,
execution order is `cut → delete → dedup → collapse → rearch`, each green before the next; one
item or tight cluster per commit, `doctor(<section>): <what>`, `doctor.py push` every 3–5 items.

**Who executes.** A strike whose whole scope its checkpoint entry names — dead-code deletion,
doc-drift fixes, comment strikes, mechanical renames — goes to a per-strike **sonnet** executor
(`thread: strike <what>`), one at a time, given the checkpoint text, absolute `$WORKTREE` paths and
the rules below it will touch. The executor's brief is the finding's *error class in the same
files*: fix the listed findings and any instance of the same false claim in those files, widen to
no other file and no other class, and return the additions as an explicit list (file, line,
finding generalised, before/after). It runs no git command (`rg -n` in place of `git grep`), never
builds or tests, and returns a summary the main thread ignores in favour of `git diff`. Main
verifies every addition against the source as it would a review finding
(`memory/process/review-finding-triage.md`), reverts any that is not a clean instance of a listed
class, and records the kept ones in the run file beside the findings they extend. Judgment strikes
— `collapse`, `rearch`, any deletion whose safety depends on cross-file context — stay on main.

**Before a strike names a symbol,** bound its blast radius with `doctor.py blast <symbol>`
(repo-wide, word-bounded, code and docs), never by eye; a `dedup` greps the literal expression; a cut reads the whole file. A `dedup` or `collapse`
checks the shape it collapses *into* against `docs/architecture/code-review-rules.md` and the
section's load-bearing weirdness, and runs the linter that owns the shape (`.claude/razor-lint.sh`
for views). Fix it right, reuse first, and name in the commit the `memory/` rule governing the shape
being written.

**Gates, per commit.** The section's test project (`dotnet test tests/Humans.<X>.Tests -v quiet`;
the solution build where there is none) after the last edit the commit carries; the full
`dotnet test Humans.slnx -v quiet` before each push. A test the run adds is covered only if a CI
job selects it: `Humans.Integration.Tests` is excluded from CI by design
(`memory/process/integration-tests-are-not-ci-tests.md`) — a test that must run lives in
`tests/Humans.<Section>.Tests/`, and one that runs nowhere is said to run nowhere. Non-mechanical
changes (deletions beyond plainly-dead code, structural moves) go through the reviewer gate:
`doctor.py review-pack <Section> "<what>" --finding "<checkpoint entry>"` captures the
uncommitted diff, the blast grep of every name it removes and the head of every touched file
under `$RUNDIR/review/`, and prints which reviewer this section gets — `doctor-reviewer-critical`
(fable high) where a wrong approval costs the most, `doctor-reviewer` (opus high) by default,
`doctor-reviewer-light` (opus medium) for small low-stakes sections. Dispatch that agent
(`thread: review <what>`, the pack's absolute path; a plain subagent on the same model with
`threads/review.md` if the type is unavailable): score-blind, default-reject, judging the pack.
**No strike edits while a verdict is pending** — the reviewer judges the tree it was shown or its
approval means nothing. Reject: rework
once, then revert and record. APPROVE-with-correction is applied before the commit and named in
the run file; an approval covers the claims the reviewer tested and nothing else, so say what it
checked.

**Sweeps.** A strike that removes, renames or corrects a claim about a route, type, method or path
greps the exact string repo-wide — abbreviations too — and fixes or enumerates every hit, and
updates the freshness trigger in the same pass. A delete greps each removed name across `*.cs` and
`*.md` before it commits, then re-reads the header comment of every file it cut from. A section
doc opened at all is read end to end against the code; its Cross-Section Dependencies come from the
`.csproj`, and every line `docs/sections/SECTION-TEMPLATE.md` requires stays. **When the doc and the
code disagree and the code looks wrong, change neither** — the pair goes to Needs-Peter.

**UI strikes.** An unattended cloud run never boots the app — no Postgres, no Docker, no
placeholder credentials. A JS strike is exercised in headless Chromium against a fake DOM; a
cshtml/resx strike gets the build plus `.claude/razor-lint.sh`; the run file records as a dated
session line that no live render happened, and the preview deploy is where a render is checked.
A local run with a database renders the changed page before the PR.

**Formats the build alone catches.** resx/XML edits use XML tooling, never line-based sed
(language variants are multi-line). Full build before any `dotnet ef migrations` command. With no
compiler, a C# doc-comment edit adds no `<see cref>` and is checked for XML tag balance. A
comment-only `.cs` edit is production LOC, never "docs only".

**Skip and queue, never block:** schema or EF changes, public-surface additions, privilege
changes, guardrail retirement, mutating an existing issue, anything needing Peter's judgment.
A queued item naming a symbol carries its repo-wide `git grep -n`. If in-flight feature work on
this section surfaces mid-run, stop striking and ship the assessment-only PR. Debt found and not
fixed goes to a ledger, not the run file (`memory/process/debt-ledger-additions.md`): in-section
to `src/Sections/Humans.<X>/Docs/debt.yml`; off-section straight to the owning section's
`Docs/debt.yml`, or `docs/architecture/debt-ledger.yml` when no one section owns it. **Dedupe against the ledger
before filing** — 3f has just read every row, so use it: a row that already covers the finding is
never re-filed, a finding that elaborates an existing row references it (`root: <id>`) instead of
restating it, and only a genuinely new lane takes a fresh id from `next_id:` and increments the
header. A ledger entry is prose: no reforge figures, no counts, no line
numbers.

**Needs-Peter admission test.** An item is admitted only if *two reasonable implementers would do
different things* **and** *the choice sits inside this section*. One obvious answer: do it. Choice
in another section: that section's ledger. A finding, not a fork: the findings list. A lesson about this
skill: never (see "When the skill is wrong").

### Phase 5: Bookkeeping

Re-read this phase and Phase 7 before writing — by now the run may have been compacted. The
writes, in this PR:

- **`Docs/health.md` history row** — run, date, headline, PR link. Never a score: every later
  commit moves it and the PR's surface report publishes it against the head that shipped.
- **The run file** `docs/health/runs/<yyyy-mm-dd>-<Section>.md` (`-<HHMMZ>` suffix if the path
  exists at the branch point): header; assessment summary; `## Findings` (the one prose
  description of each finding, numbered once at 3e and never renumbered; a later finding takes the
  next unused number); `## Worked`; `## Skipped` with why, including sections passed over as
  blocked; `## Debt verified` (3f's verdict lines, the rows left unverified, and where the cap
  stopped the pass); `## Retro`; `## Needs Peter` (`- [ ]` unanswered, `- [x]` applied, one per line, each
  `<finding #> — <the question, in a phrase>`, citing the number and adding no prose a ruling could
  invalidate); `## File coverage` and `## Threads`: `doctor.py runfile <X>`
  regenerates both from git and the dispatch log — `generated` and `changed` per path, how each
  thread ran and on what — and keeps what the run wrote by hand: `reviewed` on a path (every name
  the file carries resolves, not merely opened) and the findings count per thread, plus why a
  thread did not run. A disposition contains `reviewed`, `changed` or `generated` (a qualifier
  like `changed (new)` is fine); `check-run-file` rejects a row with none of them. No cost column, no diff-size block, no line counts, no reforge score: the
  PR carries those. `doctor.py check-run-file <path> --section <X>` says what is missing.

The prose gate runs inside every `doctor.py commit` (`memory/process/no-derived-aggregates-in-docs.md`):
a typed count of the list beneath it, a parenthesised count, a total row or a count in a heading
refuses the commit; other numerals near plurals are printed as advisory. A count is wrong the first
time the list changes.

### Phase 6: Retro

The retro questions in the run file, a paragraph each and no more: what the selector got wrong, what
was wasted motion, what striking revealed that the assessment missed, what the target diff says.
A durable project rule is written in this PR as its `memory/` atom plus INDEX line. Lessons about
this skill are not Needs-Peter items and not atoms; the bar for them is below.

### Phase 7: PR

Self-review the run's own new prose against the gates: `doctor.py prose-gate --base origin/main`,
`doctor.py trace <run file> <health.md> --section <X>` (every symbol, route and path resolves; every "only",
"never" and "always" is checked by hand; each `file:line` cite prints its source line — a cite written
in 3c and moved by the strike lands on a brace or the statement next door, so trace runs last, after
the tree stops moving), and the render rule (a claim about what a page shows traces to the `.cshtml`). Then
`doctor.py check-run-file <run file> --section <X>`, `dotnet format whitespace Humans.slnx --verify-no-changes`, the full
test run, `doctor.py push`, and the PR against `peterdrier/Humans` `main`:

- Title `doctor(<Section>): <headline>` — something a user or reader would notice, never the
  run's bookkeeping.
- Body: header paragraph (run, section, run-file link, target link) ending with the `UPCOMING:`
  forecast from `$RUNDIR/selection.txt` (omitted under `--section`); assessment summary;
  worked/skipped bullets; `## Needs Peter`, numbered, citing findings by number, answerable in a
  word or two. The PR body is the authoritative queue while the PR is open.
- Cost: `python .claude/skills/section-doctor/cost-report.py`, posted as one PR comment right
  after creation (`gh pr comment --body-file`, or the MCP comment tool), never into the body or the
  run file, never compared to another run. On failure it prints `Cost: unmeasured (<error>)`;
  post that line all the same.
- Backfill the PR number over every `pending` reference, commit, push. That is the last
  bookkeeping push: from here a push changes code, tests or a doc a reader depends on, never a
  corrected figure alone.

### Phase 8: Inline round (interactive runs only)

If Peter is present, present the Needs-Peter items inline — terse, numbered, plain prose, never
AskUserQuestion — and apply answers as commits, ticking each in the PR body and the run file under
the Resume grep gate. Unattended runs skip this; unanswered items carry forward, never re-asked.

### Phase 9: Stand down

Stop working; the worktree stays until the PR is merged or closed, and everything a later session
needs is derivable from the branch name and the run file. Review rounds run under
`.claude/skills/steward/SKILL.md` and `memory/process/review-round-budget.md`: a bot finding is a
sample of a class — grep the branch for its siblings before fixing the reported line; every push is
`doctor.py push` followed by a check that the PR head advanced; a thread is resolved only after
`doctor.py resolve-check <sha>` passes for the commit the reply cites and the named line reads as
claimed. At the round cap, unfixed findings are listed in the cap comment and the run file's
`## Needs Peter`, never resolved. No scheduled self check-ins (`memory/process/no-scheduled-pr-checkins.md`).
Teardown is `git worktree remove` when the PR is terminal, locally only; a cloud run has nothing to
tear down.

## Resume

`resume` gathers the queue from both places an item lives — the `## Needs Peter` block of each
open `section-doctor/*` PR body (authoritative for unmerged runs) and unticked entries in
`docs/health/runs/*.md` on `origin/main` — presents them inline, and applies each answer. **A
ruling is applied only when a grep says so:** grep the branch for the finding's distinguishing terms
across `.cs` and `.md`; a ruling that makes the claim false (case abolished, method gone, defect
fixed) corrects every hit; `keep`, `not a defect` or `deferred` leaves them and lands on the
finding's description as its state. Then tick. Open-PR items: commits on that PR's branch, ticked
in both the PR body and the branch's run file. Merged items: one fresh branch and one PR per run
file, carrying all of that file's answers — one writer per run file, so these PRs cannot conflict
with each other or with concurrent runs. No new assessment or strike work.

## When the skill is wrong

A run files an issue on `peterdrier/Humans` about this skill only when one of these happened on
this run: a script crashed or returned wrong output; two of the skill's rules contradicted each
other on a real decision; a gate refused a change the run could show was correct. **At most two
per run.** Before filing, search open issues for the script or rule by name and cite the match in
the run file instead of filing again. An issue is under fifteen lines — what happened, the file,
the one-line change proposed — with no acceptance-criteria essay. "Would be better if" is a retro
sentence, not an issue and not a Needs-Peter item; Peter reads the retro on the PR.
