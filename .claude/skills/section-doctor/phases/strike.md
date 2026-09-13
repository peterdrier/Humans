# Phase 4: Strike

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
filename, and is logged with `doctor.py dispatch-log` like a 3d thread. Judgment strikes —
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
7. Run `doctor.py prose-gate` over the staged diff (Phase 5), then commit
   `doctor(<section>): <what>`. `doctor.py push` every 3–5 items. When a reviewer gate could not
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

