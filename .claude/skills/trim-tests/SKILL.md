---
name: trim-tests
description: "Stryker-driven test cull: delete slop, consolidate redundants, fill mutation-coverage gaps. Score up, test count and lines down. Use when the user says 'trim tests', '/trim-tests', or wants to clean up LLM-generated test bloat. No args picks the worst-scored file; argument scopes to a section, service, or file."
argument-hint: "[section | service | --file <path>]"
---

# Trim Tests

LLM-generated test corpora accumulate slop — tests that touch lines without constraining behavior. Stryker is the arbiter. Heuristics and judgment propose; Stryker re-runs decide.

**Goal per run:** mutation score same-or-higher, test count and line count lower.

## Argument routing

- `(no args)` — daily mode. Find the worst-scored file across existing Stryker configs (or smoke-rank). Show top 3, pick #1 unless overridden.
- `<section>` — `shifts`, `camps`, `teams`, etc. Scope to that section's services.
- `<service-name>` — single service like `CampService`. Scope to its `.cs` + matching `*Tests.cs`.
- `--file <path>` — explicit production file.

## Slop heuristics

A test is a deletion candidate if it matches:

- Only `.Received()`/`.DidNotReceive()` assertions — verifies the mock was called, not the outcome
- Only `Should().NotBeNull()`, `Should().HaveCount(n)`, or `Should().Be(default)` — shape, not behavior
- Body under ~10 lines AND no DB observation, no entity field assertion, no exception expectation
- 3+ tests through the same branch with cosmetic input variation (same assertion shape, different params)
- Tests of trivial code: auto-property getters, single-line delegations, simple mappers
- Names ending in `_DoesNotThrow` where the production code can't throw

Heuristics propose. Stryker decides. A heuristic-flagged test is only deleted if Phase 1's per-test data shows it as redundant (every mutant it kills is killed by someone else) or dead (kills nothing).

## Phase 0 — Target select

Look at `tests/Humans.Application.Tests/stryker-*-config.json` for existing scoped configs. Reuse if matching, otherwise generate a config in `local/stryker-trim/` (gitignored — don't commit ephemeral configs):

```json
{
  "stryker-config": {
    "project": "Humans.Application.csproj",
    "test-runner": "mtp",
    "coverage-analysis": "perTest",
    "mutation-level": "Standard",
    "mutate": ["**/Services/<Area>/<File>.cs"],
    "concurrency": 24,
    "reporters": ["Json"],
    "thresholds": {"high": 80, "low": 60, "break": 0}
  }
}
```

**Use `coverage-analysis: perTest`** (not `off` like the existing configs). Per-test attribution is what makes safe deletion possible.

## Phase 1 — Baseline

```
cd tests/Humans.Application.Tests
dotnet stryker -f <config-path>
```

Parse `StrykerOutput/<latest>/reports/mutation-report.json`. Capture:

- Total mutation score
- Per-mutant: location, status, killed-by-test list
- Per-test: which mutants it kills (and which kills are unique to it)

Classify each test:
- **Precious** — kills at least one mutant nothing else kills
- **Redundant** — every kill is also covered by other tests
- **Dead** — kills nothing

Record baseline numbers for the report.

## Phase 2 — Slop deletion

Intersect the slop heuristics with `redundant ∪ dead`. That intersection is the safe-delete set.

Delete in one batch. Build (must compile). Re-run Stryker. If score drops by >2 points: find the mutant cluster that stopped being killed, restore the minimum test set to recover those kills.

Commit:

```
test(<section>): drop N redundant tests in <ServiceName>Tests
```

That's the whole message. No paragraph. The number IS the rationale.

## Phase 3 — Consolidate

Read remaining tests. Group by conceptual behavior. Candidates for `[Theory]`/`[InlineData]`:

- N `_HappyPath_*` variants that differ only in input
- `_VariantA/B/C` tests with identical assertion shape
- Per-enum-value branches with the same shape

Merge. Build. Re-run Stryker — same mutants must still be killed. Restore individual tests if any kill is lost.

Commit:

```
test(<section>): consolidate N tests into theories in <ServiceName>Tests
```

## Phase 4 — Gap fill

From the Phase 1 surviving-mutants list, group by source location. For each cluster, write a test that pins the missing behavior. Test must observe outcomes (DB rows, return values, exceptions) — never just `.Received()` on a mock unless the mock call IS the contract (e.g., `IEmailTransport.SendAsync`).

Skip mutants in: log messages, debug branches, `ToString`, generated code, anything in the project's Stryker exclusion list.

Cap new tests at half the number deleted. If that doesn't lift the score enough, that's fine — the corpus was bloated for a reason.

Commit:

```
test(<section>): add N tests for surviving mutants in <ServiceName>Tests
```

## Phase 5 — Report

Terse summary to user:

```
<ServiceName>Tests
  Mutation score: 67.3% → 81.2% (+13.9)
  Tests:         103 → 62 (−41)
  Lines:         1,247 → 718 (−529)
  Mutants:       18 surviving → 6 surviving

Branch: trim-tests/<service>-<date>
Commits: 3
```

Then ask: "Open PR? (y/n)". If yes, PR title `test(<section>): trim and consolidate <ServiceName>Tests`. Body is the report above. Nothing else.

## Anti-bloat rules — HARD

- **No paragraph commit messages.** One line + numbers.
- **No inline comments** explaining deleted tests or consolidations. The diff shows what changed.
- **No new docstrings or class-level XML doc** added during this skill.
- **No follow-up plan documents.** Output the report inline.
- **`[InlineData]` consolidation only when the test count would be 4+** otherwise. Two cosmetic variations stay as two tests.

If a change needs more than 2 lines of comment to explain, the change is too clever. Make the code more obvious instead.

## Workflow

- Worktree: `.worktrees/trim-tests-<area>` off `origin/main`
- One commit per phase
- One PR per service (or per small section)
- Use harness primitives — `ServiceTestHarness`, `ServiceLocatorBuilder`, `AuditLog`/`Notifier`/`ShiftAuthInvalidator`/`AdminAuthorization` properties. Don't add raw `Substitute.For<>` for those four interfaces.

## Daily mode — "find the next thing"

1. Run all existing `stryker-*-config.json` in parallel, capture scores from each `mutation-report.json`
2. Rank lowest-first
3. Print top 3 worst with score + test count
4. Default to #1; user can `/trim-tests <name>` to pick another

If no scoped configs exist for an area the user asked about, generate one in `local/stryker-trim/` and run that.

## Prerequisites

- `dotnet stryker --version` must work. If missing: `dotnet tool restore` (the repo has `.config/dotnet-tools.json` pinning Stryker).
- Working tree must be clean (skill manages its own commits).
- Magick.NET NuGet audit must not fail the build — pass `-p:NuGetAudit=false` to `dotnet build`/`dotnet test` invocations if it does (it's not on `main` anymore, but defensive).

## Out of scope

- Don't touch production source — test code only
- Don't modify Stryker thresholds in committed configs
- Don't commit ephemeral configs from `local/stryker-trim/`
- Don't touch `Humans.Integration.Tests` or `Humans.Web.Tests` — different shape, different skill if needed
- Don't change harness, builders, or other test infrastructure as part of this skill — separate concern
