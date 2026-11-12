---
name: Stryker settings and chunking
description: Running Stryker mutation tests: always `concurrency: 16` and `coverage-analysis: off`; chunk a large file by character span to fit the 10-minute cap.
---

**Every Stryker config runs `concurrency: 16` and `coverage-analysis: off`.** Never raise concurrency, never use `perTest`/`all`.

**Why:** measured 2026-05-23 on `TeamService`. At concurrency 24 the test host starves, the timeout watchdog fires, and Stryker counts each timeout as a kill: 87% reported against 47% honest. `perTest` is nondeterministic under xUnit v3/MTP: 105 vs 356 killed on identical code. Evidence: [`docs/testing/mutation-testing.md`](../../docs/testing/mutation-testing.md). Treat any score recorded at concurrency 24 as inflated.

**Chunking a large file.** A 2,000-line service takes ~15 minutes, past the 10-minute foreground cap.

1. The mutate span `{a..b}` counts **characters, not lines**. Get it wrong and the run reports `0 total mutants` after ~2.5 minutes, which looks like a broken glob.
2. `wc -c` the file, split into 3–4 spans overlapping by ~1,000 bytes, one config and one `-O <outdir>` per span.
3. Merge the `mutation-report.json` files by mutant id, skipping `Ignored`/`CompileError` and deduping the overlap. Score = killed / (killed + survived).
4. Mutant ids are stable only across runs of identical source. After a production edit, compare on `(mutatorName, replacement, stripped source line)`.
