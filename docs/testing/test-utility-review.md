# Reviewing test utility

[`scripts/analyze-test-utility.ps1`](../../scripts/analyze-test-utility.ps1) ranks test **files** by heuristic signs of maintenance cost. Use it to find candidates. The review ledger links individual test methods to behaviors and records why each method is kept, sped up, consolidated, removed, or still unreviewed.

Generate the ledger from source and one existing TRX snapshot:

```powershell
python scripts/test-utility-review.py --results local/test-results --results-revision <commit-sha>
```

The TRX directory should contain one result file per test project from the same run. The script excludes `Humans.Integration.Tests` from both source and results. Omit `--results` to inspect source methods without timing data. Output goes to ignored `local/test-utility/`: `review.sqlite`, `methods.csv`, `behaviors.csv`, `evidence.csv`, and `summary.json`. Open the CSV files in a spreadsheet, or query the SQLite database. The summary records the results revision, checkout revision, and whether test sources have uncommitted changes. The per-method durations are summed test-case time, not suite wall time; parallel cases overlap.

[`test-utility-review.json`](test-utility-review.json) is the small, reviewed input. Each behavior names its section, owning source file, invariant, failure consequence, and method evidence. A method may protect several behaviors. Every recorded method decision needs a reason and must still resolve to one current source method; stale entries fail the generator. Methods without a recorded decision appear as `unreviewed` in the generated inventory. An empty `tests` list means the behavior is a reviewed gap, not proof that no other test could exercise its code.

For example, `SELECT key, seconds FROM methods WHERE decision = 'unreviewed' ORDER BY seconds DESC LIMIT 25;` finds expensive cases awaiting review. `SELECT behavior_id, method_key, decision FROM evidence ORDER BY behavior_id, method_key;` shows the reviewed many-to-many links. The JSON is the durable decision record; regenerate the database after source or result changes.

Review one behavior family at a time:

1. Read the owning implementation, invariant doc, existing tests, and recent failures. Use the file ranking, timings, and coverage as leads; execution alone does not establish assertion quality.
2. Link methods that protect the behavior. State the distinct failure each retained test catches. Mark overlap `consolidate` only when another retained assertion would detect the same regression.
3. Record gaps or deliberate lack of coverage with the consequence of failure. Keep a method `investigate` when the evidence is incomplete.
4. Make a small change only after the replacement protection is concrete. Update the reviewed input in the same change, regenerate the ledger, and compare test time with a matching baseline.

The current pilot covers Users search, Email outbox, and Workgroups caching. It is a decision record for those behaviors, not a percentage target for the repository. The current CI artifact used for the pilot came from the revision recorded in the generated summary. The generator adds no CI check or build gate.
