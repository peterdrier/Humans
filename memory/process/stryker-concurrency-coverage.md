---
name: stryker-concurrency-coverage
description: when running Stryker mutation tests, always concurrency 16 + coverage-analysis off; 24 inflates via false timeouts, perTest is nondeterministic here
---

Every Stryker config in this repo runs at `concurrency: 16` and `coverage-analysis: off`. Never raise concurrency to go faster; never switch coverage to `perTest`/`all`.

**Why:** Two independent failure modes, both demonstrated 2026-05-23 on a `TeamService` probe (700 mutants). (1) At `concurrency: 24` the test host starves and Stryker's timeout watchdog fires on mutants that cannot hang (e.g. string-literal mutations); Stryker counts a Timeout as a kill, so the score inflates — 24 threads gave 221 timeouts → 87.43%, 16 threads gave 4 timeouts → 47.29% honest (~40% of "kills" at 24 were false). (2) `coverage-analysis: perTest` is nondeterministic in this xUnit-v3/MTP environment — two runs over identical code gave 105 vs 356 killed (a 250-mutant swing), while `off` gave 358 vs 358. A before/after gate can't use perTest; `off` runs every test against every mutant (no coverage map to corrupt). Cost of `off`: no per-test attribution, no `NoCoverage` bucket. Full evidence + tables: [`docs/testing/mutation-testing.md`](../../docs/testing/mutation-testing.md).

**How to apply:** Set both keys in any committed or ephemeral Stryker config before running. Treat any mutation score recorded in `docs/architecture/maintenance-log.md` at concurrency 24 as inflated. Don't trust a lucky pair of agreeing perTest runs — sample it and it breaks. Fires for `/trim-tests`, `/maintenance` Stryker passes, and any manual `dotnet-stryker` run.
