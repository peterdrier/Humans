---
name: trim-tests
description: "Stryker-driven test cull: delete slop, consolidate redundants, fill mutation-coverage gaps. Score up, test count and lines down. Use when the user says 'trim tests', '/trim-tests', or wants to clean up LLM-generated test bloat. No args picks the worst-scored file; argument scopes to a section, service, or file."
argument-hint: "[section | service | --file <path>]"
---

# Trim Tests

LLM-generated test corpora accumulate slop — tests that touch lines without constraining behavior. Stryker is the arbiter for whether a deletion is safe. `scripts/analyze-test-utility.ps1` is the arbiter for which tests are slop candidates.

**Goal per run:** mutation score same-or-higher, test count and line count lower.

The skill is iterative. Each phase has a verify-and-gate step; if the gate fails, the skill bisects, restores, or skips rather than committing a regression. The outer loop retries the whole flow up to 3 times with adjusted parameters before giving up on a file.

## Hard constraint — concurrency `16`, coverage-analysis `off`

Run every Stryker config at `concurrency: 16` and `coverage-analysis: off`. There are TWO independent failure modes here; both were demonstrated empirically on 2026-05-23 (see `docs/testing/mutation-testing.md`):

- **Concurrency 16, not 24.** At 24 the machine is saturated; the test host gets starved and trips Stryker's timeout watchdog on innocent mutants (e.g. *string-literal* mutations "timing out", which is impossible — a string can't hang). On a TeamService probe, 24 threads produced 221 timeouts and an **inflated 87.43%**; 16 threads produced 4 timeouts and the honest **~47%**. ~40% of the "kills" at 24 were false. Never raise above 16 to go faster.
- **coverage-analysis `off`, never `perTest`/`all`.** `perTest` is NONDETERMINISTIC in this xUnit-v3/MTP environment: two runs over *identical* code gave **105 vs 356 killed** (a 250-mutant swing). `off` over the same input gave **358 vs 358** (deterministic to ±1 timeout). A before/after gate can't use a tool that swings 250 mutants between identical runs — a bad run fakes a massive regression. `off` runs every test against every mutant (no coverage map to corrupt), so it's slower but reliable. We therefore have NO per-test attribution — the gate is "re-run and compare score + per-mutant kill diff," not a per-test kill table. (Two perTest runs once happened to agree with `off`, which briefly looked fine — it isn't; sample it enough and it breaks.)

## Argument routing

- `(no args)` — daily mode. Run `analyze-test-utility.ps1` to get the latest debt queue, pick the top "High-Confidence Test-Debt Candidate" not in the trimmed-file log.
- `<section>` — `shifts`, `camps`, `teams`, etc. Filter the debt queue to files matching that section's namespace.
- `<service-name>` — single service like `CampService`. Scope to its production `.cs` + matching `*Tests.cs`.
- `--file <path>` — explicit production file.

## What the utility script already detects

`scripts/analyze-test-utility.ps1` computes a `DebtScore` per test file based on signals that strongly correlate with slop: low assertion density, weak-assertion patterns (`Should().NotBeNull()`, `Should().BeOfType()`, `Assert.True()`), heavy mocking, reflection-shape checks, DI-shape checks, large size relative to assertions, missing production subject. It already classifies architecture ratchets and DI-cycle safety nets and excludes them from the high-confidence queue.

Don't reinvent these heuristics. Run the script, take its output, apply LLM judgment on individual tests inside the high-score files.

## Workflow

### Phase 0 — Target select

```powershell
powershell -ExecutionPolicy Bypass -File scripts/analyze-test-utility.ps1 -Top 50 -StrykerReport local/stryker-runs/<latest>/reports/mutation-report.json
```

If no recent Stryker mutation report exists for the area, run one first (Phase 1 below) then re-run the utility with `-StrykerReport`. Otherwise the utility runs on file signals alone — fine but weaker.

Output lands in `local/test-utility/test-utility-<timestamp>.{json,csv,md}`. Read the JSON. From "High-Confidence Test-Debt Candidates," pick the target per the argument routing rules.

### Phase 1 — Baseline

Find or generate a scoped Stryker config (existing ones live at `tests/Humans.Application.Tests/stryker-*-config.json`). If you must generate one, put it under `local/stryker-trim/` (gitignored). **Use `coverage-analysis: off` and `concurrency: 16`** (see the Hard constraint above). Example:

```json
{
  "stryker-config": {
    "project": "Humans.Application.csproj",
    "test-runner": "mtp",
    "coverage-analysis": "off",
    "mutation-level": "Standard",
    "mutate": ["**/Services/<Area>/<File>.cs"],
    "concurrency": 16,
    "reporters": ["Json"],
    "thresholds": {"high": 80, "low": 60, "break": 0}
  }
}
```

Run:

```powershell
Push-Location tests/Humans.Application.Tests
dotnet tool run dotnet-stryker --config-file <path>
Pop-Location
```

Parse `StrykerOutput/<latest>/reports/mutation-report.json`. Record:

- `mutationScore` (percentage)
- Total mutants, killed count, surviving count
- For each surviving mutant: id, file, line, mutator, original source snippet, replacement

The surviving-mutants list drives Phase 4. The score is the gate everywhere else. (With `coverage-analysis: off` there is no `NoCoverage` bucket — untested mutants are reported as `Survived`.)

### Phase 2 — Delete slop (with bisection)

From Phase 0's candidate file, identify the test methods inside that match slop patterns under LLM judgment (the file is already high-debt-score; we're picking which tests inside to drop). Patterns to flag:

- Only `.Received()` / `.DidNotReceive()` assertions — verifies the mock was called, not the outcome
- Only `Should().NotBeNull()`, `Should().HaveCount(n)`, `Should().BeOfType()`, `Should().Be(default)` — shape, not behavior
- Body under ~10 lines AND no DB observation, no entity field assertion, no exception expectation
- 3+ tests through the same branch with cosmetic input variation (same assertion shape, different params)
- Tests of trivial code (auto-property getters, single-line delegations, simple mappers)
- Names ending in `_DoesNotThrow` where the production code can't throw

**Trap to avoid — same output, different code path.** Two tests that produce the same observable output (same return value, same expected string) can still exercise different branches in the SUT (different `if`-arms, distinct early returns, distinct try/catch paths, distinct prefix-strip / fragment-handling branches). Before deleting a test as "cosmetic variant of another," briefly trace its inputs through the SUT. If it's the only test that enters a particular conditional branch, keep it — Stryker may not have a mutator covering that branch, and the score-delta gate will pass while the coverage gap quietly opens. Phrase the check explicitly to yourself: *"is this test the only one whose inputs satisfy condition X in the SUT?"* If yes, don't delete.

Form a deletion batch (start with everything flagged). Delete. Build. If build fails, the test was load-bearing in a way that wasn't visible — revert that specific test, try again.

Re-run Stryker. Compare both score AND per-mutant kill diff against baseline.

**Bisection gate — two conditions, BOTH must hold:**

1. **Score must not drop by more than 2 percentage points.** Score gate is necessary but not sufficient — timeout reclassification can inflate it while real kills regress.
2. **No mutant may go from Killed → Survived.** Compare mutant IDs across runs. A net-positive score with one or more Killed→Survived shifts means the deletion silently lost real coverage. The score-only gate misses this when timeouts shift in your favor.

If either gate fails, bisect:

1. Restore half the deleted tests (the half most likely to have killed a unique mutant based on assertion strength).
2. Re-run Stryker.
3. If gate still fails: keep bisecting (restore half of the remaining deletions).
4. If gate passes: the restored set contains the load-bearing test(s); freeze them and try to delete the other half again in a separate batch (sometimes the issue is one specific test, not the half).

After at most 3 bisection rounds, accept whatever deletion batch passes both gates. Commit:

```
test(<section>): drop N redundant tests in <ServiceName>Tests
```

That's the whole commit message. The numbers ARE the rationale.

### Phase 3 — Consolidate (with verify)

Read remaining tests. Group by conceptual behavior. Candidates for xUnit `[Theory]`/`[InlineData]`:

- N `_HappyPath_*` variants that differ only in input
- `_VariantA/B/C` tests with identical assertion shape
- Per-enum-value branches with the same shape

**Threshold:** consolidate only when the group has 4+ members. Two cosmetic variants stay as two tests — the `[Theory]` ceremony isn't worth it below that.

Merge. Build. Re-run Stryker. If any previously-killed mutant is now surviving (compare mutant ID lists), restore the individual tests for that mutant. Re-run. Continue until score holds.

Commit:

```
test(<section>): consolidate N tests into theories in <ServiceName>Tests
```

### Phase 4 — Gap fill (with verification)

From the Phase 1 surviving-mutants list, group by source location. For each cluster:

1. Read the mutated code.
2. Identify what behavior is missing a constraint (the mutation tells you exactly what change in behavior should have failed a test).
3. Write a test that observes the outcome the mutation would change. Test must observe outcomes (DB rows, return values, thrown exceptions) — never just `.Received()` on a mock unless the mock call IS the contract (e.g., `IEmailTransport.SendAsync`).
4. **Verify the test actually kills the mutant.** Manually apply the mutation (change the source to the mutant's replacement), run the test, it must fail. Revert the source. The test must still pass on the unmutated code. This catches tests that pass for the wrong reason.

Skip mutants in: log messages, debug branches, `ToString`, generated code, anything in the project's Stryker exclusion list.

**Cap:** new tests at half the number deleted in Phase 2. If you can't lift the score that much within the cap, that's fine — the corpus was bloated for a reason.

Commit:

```
test(<section>): add N tests for surviving mutants in <ServiceName>Tests
```

### Phase 5 — Report

Print to user:

```
<ServiceName>Tests
  Mutation score: 67.3% → 81.2% (+13.9)
  Tests:         103 → 62 (−41)
  Lines:         1,247 → 718 (−529)
  Mutants:       18 surviving → 6 surviving (−12)

Branch: trim-tests/<service>-<date>
Commits: 3
```

Then ask: "Open PR? (y/n)". If yes, PR title `test(<section>): trim and consolidate <ServiceName>Tests`. Body is the report block above. Nothing else.

## Outer iteration loop

After Phase 5, check the success criteria:

- `score_delta >= 0` (score didn't drop)
- `test_count_delta < 0` (count went down)
- `line_delta < 0` (lines went down)

**All three must hold.** If they do, the run succeeded — commit, report, exit.

If one or more fail, this attempt didn't win. Don't commit the partial work. Decide the next move:

- **score dropped, count dropped:** deletion was too aggressive even after bisection. Restart with stricter slop heuristics (require 2+ matching patterns instead of 1) and re-run.
- **score held, count didn't drop:** the file's already lean. Move on — there's nothing to win here. Mark this file as "trimmed" in the daily-mode rotation so it doesn't get repicked tomorrow.
- **score dropped, count didn't drop:** something went wrong in consolidation or gap-fill produced flaky tests. Restore the Phase 2 deletions (those were validated), skip Phase 3-4 this iteration, accept the partial win.

Max 3 outer iterations per file. If none succeeds, leave the work-in-progress branch but don't open a PR — report what was tried and stop.

## Anti-bloat rules — HARD

- **No paragraph commit messages.** One line + numbers.
- **No inline comments** explaining deleted tests or consolidations. The diff shows what changed.
- **No new docstrings or class-level XML doc** added during this skill.
- **No follow-up plan documents.** Output the report inline.
- **`[InlineData]` consolidation only when the test count would be 4+** otherwise.

If a change needs more than 2 lines of comment to explain, the change is too clever. Make the code more obvious instead.

## Workflow

- Worktree: `.worktrees/trim-tests-<area>` off `origin/main`
- One commit per phase
- One PR per service (or per small section)
- Use harness primitives — `ServiceTestHarness`, `ServiceLocatorBuilder`, the harness stub properties (`AuditLog`/`Notifier`/`ShiftAuthInvalidator`/`AdminAuthorization`). Don't add raw `Substitute.For<>` for those four interfaces.

## Daily mode — "find the next thing"

1. Run `analyze-test-utility.ps1 -Top 50` (no Stryker report needed for the initial ranking)
2. Take the top 3 of "High-Confidence Test-Debt Candidates" from the markdown
3. Cross-reference against the trimmed-file log (see below). Skip any already-trimmed in the last 14 days.
4. Print top 3 to the user, default to #1; user can override with `/trim-tests <name>`.

### Trimmed-file log

After a successful run, append the trimmed file's path + date + score delta to `local/test-utility/trimmed-log.tsv` (gitignored). On daily-mode runs, read this file to skip recently-trimmed targets.

## Prerequisites

- `dotnet stryker --version` must work. If missing: `dotnet tool restore` (the repo has `.config/dotnet-tools.json` pinning Stryker 4.14.1).
- `pwsh` or PowerShell available for the utility script.
- `coverage-analysis: off` and `concurrency: 16` in every Stryker config used. Verify before running.
- Working tree clean (skill manages its own commits).

## Out of scope

- Don't touch production source — test code only
- Don't modify Stryker thresholds in committed configs
- Don't commit ephemeral configs from `local/stryker-trim/`
- Don't touch `Humans.Integration.Tests` or `Humans.Web.Tests` — different shape, different skill if needed
- Don't change harness, builders, or other test infrastructure as part of this skill — separate concern
- Don't raise `concurrency` above 16 to go faster — it starves the test host and manufactures false timeout-kills (see the Hard constraint).
- Don't enable `coverage-analysis: perTest`/`all` to get richer data or faster runs — it's nondeterministic here and corrupts the gate (see the Hard constraint).
