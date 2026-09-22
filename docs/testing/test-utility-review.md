# Reviewing test utility

Generate a disposable inventory from current source and one existing non-integration TRX snapshot:

```powershell
python scripts/test-utility-review.py --results local/test-results --results-revision <commit-sha>
```

The script excludes `Humans.Integration.Tests`. It writes ignored `local/test-utility/methods.csv`, `inventory.sqlite`, and `summary.json`; no reviewed mapping or status file is maintained. Omit `--results` for a source-only inventory. The summary records the source and result revisions and whether test sources are dirty. Per-method durations sum case times, so parallel tests overlap; use the suite's wall time to judge the overall gate.

Compare two inventories from the same revision after separate test runs:

```powershell
python scripts/compare-test-timings.py local/run-a local/run-b
```

The comparison reports which methods cross the one-second threshold in both runs and which move in or out. A test's duration can rise under contention even when its code is unchanged; compare wall time before changing the parallelism setting.

Use [`analyze-test-utility.ps1`](../../scripts/analyze-test-utility.ps1) and the inventory to choose an expensive or repetitive test family. Then inspect the relevant implementation and tests. Rider's dotCover can show which tests execute particular code and reveal untouched paths or heavy overlap; coverage alone does not establish whether a test asserts the right behavior. Use dotTrace on a selected slow test or build/test process when timing data does not explain the wait. Profiling and coverage runs are investigations, not routine gates.

Record a decision to remove, combine, or add tests in the PR or issue that makes that change. Explain the unique regression each retained test catches and compare the affected project's wall time before and after. This keeps review judgments beside the change they justify, with no second codebase-wide ledger to synchronize.
