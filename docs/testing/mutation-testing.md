# Mutation Testing

This repository uses Stryker.NET for mutation-test probes.

The test projects use xUnit v3, so Stryker should run through the Microsoft
Testing Platform runner rather than the default VSTest runner.

## Required settings — `concurrency: 16`, `coverage-analysis: perTest`

Every Stryker config in this repo must set `concurrency: 16` and
`coverage-analysis: perTest`. Both were established empirically on 2026-05-23
against a `TeamService` probe (700 testable mutants), and both correct a
previously-recorded but unverified recommendation to keep `coverage-analysis`
`off`.

**Concurrency 16, never higher.** This machine has ~16 effective cores. At
`concurrency: 24` the test host is starved and trips Stryker's timeout watchdog
on mutants that cannot possibly hang — e.g. *string-literal* mutations, which
never alter control flow. Stryker counts a Timeout as a kill, so these false
timeouts silently inflate the score:

| threads | coverage | Killed | Survived | NoCoverage | Timeout | Score |
| ------: | -------- | -----: | -------: | ---------: | ------: | ----: |
| **24**  | off      | 391    | 88       | —          | **221** | **87.43%** |
| 16      | off      | 327    | 369      | —          | 4       | 47.29% |
| 16      | perTest  | 325    | 187      | 186        | 2       | 46.71% |

At 24 threads, ~40% of the "kills" (281 of 700) were false. The honest score is
~47%. **Any mutation score in this repo recorded at concurrency 24 — including
older entries in `docs/architecture/maintenance-log.md` — is inflated and should
be distrusted.**

**`coverage-analysis: perTest`, not `off`.** At 16 threads `perTest` matches
`off`'s score within noise (46.71% vs 47.29%) *and* matches its kill count
(325 vs 327) — i.e. coverage capture is reliable here and does **not**
misclassify covered mutants as `NoCoverage`. It also runs ~22% faster and splits
undetected mutants into `Survived` (a test runs but doesn't assert hard enough)
vs `NoCoverage` (no test touches the code at all) — the exact signal the
`/trim-tests` gap-fill phase needs. The earlier "keep it off, capture is
unreliable" note was written while runs were at concurrency 24, where
host-starvation timeouts made everything look misclassified; the cause was the
thread count, not the coverage mode.

## Setup

```powershell
dotnet tool restore
```

## Small Domain Run

Run the small domain suite first when validating Stryker locally:

```powershell
Push-Location tests/Humans.Domain.Tests
dotnet tool run dotnet-stryker --config-file stryker-config.json
Pop-Location
```

## Application Runs

The application suite is large. Use the smoke config when checking that Stryker
is wired correctly before attempting broader application mutation runs:

```powershell
Push-Location tests/Humans.Application.Tests
dotnet tool run dotnet-stryker --config-file stryker-smoke-config.json
Pop-Location
```

Stryker's MTP runner currently does not reliably honor `test-case-filter` in
this project. Prefer a narrow `mutate` glob plus a clean full initial test run.

Profile-section probe:

```powershell
Push-Location tests/Humans.Application.Tests
dotnet tool run dotnet-stryker --config-file stryker-profiles-config.json `
  --output ..\..\local\stryker-runs\application-profiles
Pop-Location
```

## Test Utility Review

Use the manual utility analysis script to find test files that are more likely
to be maintenance debt than useful signal:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/analyze-test-utility.ps1 `
  -Top 50 `
  -StrykerReport local/stryker-runs/domain/reports/mutation-report.json
```

The script writes JSON, CSV, and Markdown under `local/test-utility/`. The
Markdown report has two queues:

- `Top Review Candidates` includes every kind of test, including architecture
  tests and manual ratchet helpers.
- `High-Confidence Test-Debt Candidates` excludes architecture ratchets and is
  the better starting point for deletion, consolidation, or replacing
  implementation-shape tests with behavior tests.

The score is intentionally heuristic. Treat it as a triage queue, then inspect
the candidate file before removing anything.

## Manual Test-Count Gate

Until this is wired into CI, use `Test attributes` from the utility report as
the manual test-count ratchet. As of the 2026-05-10 manual gate setup, the
baseline is:

```text
Test attributes: 2139
```

For normal PRs, the expected rule is no net increase in test attributes unless
the PR explains why the new test surface is worth the added maintenance cost.
Prefer one of these outcomes:

- Delete duplicate or low-value test methods outright.
- Add behavior coverage while deleting or consolidating lower-value shape tests.
- Keep a small number of intentional safety-net tests, such as DI cycle
  resolution checks, even when they look mock-heavy or reflection-heavy.

Use Stryker results as a quality signal, not as a deletion counter. Surviving
mutants usually mean production behavior is undertested; they do not prove that
existing tests are redundant. Good deletion candidates are files that combine a
high utility debt score, weak behavioral assertions, and no clear mutation or
policy value after manual inspection.
