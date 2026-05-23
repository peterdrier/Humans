---
name: stryker-concurrency-coverage
description: when running Stryker mutation tests, always concurrency 16 + coverage-analysis perTest; 24 threads inflates the score with false timeout-kills
---

Every Stryker config in this repo runs at `concurrency: 16` and `coverage-analysis: perTest`. Never raise concurrency to go faster; never set coverage to `off`/`all`.

**Why:** Validated empirically 2026-05-23 on a `TeamService` probe (700 mutants). At `concurrency: 24` the test host starves and Stryker's timeout watchdog fires on mutants that cannot hang (e.g. string-literal mutations), and Stryker counts a Timeout as a kill — so the score inflates. 24 threads → 221 timeouts → 87.43%; 16 threads → 4 timeouts → 47.29% (honest). ~40% of the "kills" at 24 were false. Separately, `coverage-analysis: perTest` at 16 threads matched `off`'s score *and* kill count (so capture is reliable, not "misclassifying" as a now-corrected doc claimed), ran ~22% faster, and uniquely splits undetected mutants into `Survived` (assert harder) vs `NoCoverage` (write a new test). Full evidence + table: [`docs/testing/mutation-testing.md`](../../docs/testing/mutation-testing.md).

**How to apply:** Set both keys in any committed or ephemeral Stryker config before running. Treat any mutation score recorded in `docs/architecture/maintenance-log.md` at concurrency 24 as inflated. Fires for `/trim-tests`, `/maintenance` Stryker passes, and any manual `dotnet-stryker` run.
