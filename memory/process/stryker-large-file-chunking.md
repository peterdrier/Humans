---
name: stryker-large-file-chunking
description: Stryker's mutate span `{a..b}` counts CHARACTERS, not lines; use it to split a large file into chunks that each finish inside the 10-minute tool timeout, then merge reports by mutant id
---

A whole-file Stryker run on a 2,000-line service takes ~15 minutes at `concurrency: 16` — longer than the 10-minute cap on a foreground `Bash`/`PowerShell` call, and background runs are not an option for builds. Split the file instead.

**The span is a character offset.** `"**/Services/X/Foo.cs{1..400}"` selects mutants whose span falls in the first **400 bytes** of the file, not its first 400 lines. Getting this wrong is silent: the run completes in ~2.5 minutes and reports `0 total mutants will be tested` / "Stryker was unable to calculate a mutation score", which reads like a broken glob rather than a wrong unit.

**How to apply:**

1. `wc -c < <file>` for the byte length; divide into 3–4 chunks, each **overlapping its neighbour by ~1,000 bytes** so no mutant straddles a boundary and gets dropped.
2. One config per chunk (same `concurrency: 16` + `coverage-analysis: off` as [[stryker-concurrency-coverage]]), each with its own `-O <outdir>`.
3. Merge the per-chunk `reports/mutation-report.json` by mutant **id**, skipping `Ignored` / `CompileError`, and dedupe — the overlap means a few ids appear twice. Score is `killed / (killed + survived)` over the merged set.
4. Sanity-check the merged mutant count against a single no-span run: it prints `N total mutants will be tested` after ~2.5 minutes of mutant creation, so you can `timeout -s KILL 240` it just to read the number.

**Mutant ids are stable across runs of identical source, and only then.** That makes the before/after "no mutant went Killed → Survived" gate an id join — but any production edit between runs renumbers every mutant after the edit point. When the source changed, key the comparison on `(mutatorName, replacement, stripped source line)` instead, or the diff reports hundreds of phantom regressions.
