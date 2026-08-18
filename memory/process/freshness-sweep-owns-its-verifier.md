---
name: The freshness sweep owns and may repair its own verifier
description: `/freshness-sweep` may fix `docs/scripts/freshness-checks/**` in-run without asking, even though those files sit outside the catalog and editorial trees.
---

`/freshness-sweep` **owns** `docs/scripts/freshness-checks/**` and `docs/scripts/generate-*.sh`. When a run finds its own verifier broken, it repairs the script in the same PR and reports the fix — it does not stop to ask permission first.

**Why:** these scripts are not documentation, so they fall outside the catalog's `mechanical` entries and `editorial_trees` (which walk `.md` only). Two consecutive sweeps therefore treated a broken verifier as out-of-scope and paused for approval. That is backwards: a verifier that under-reports is the single most expensive defect the sweep can carry, because its failures are silent — a dead trigger, or a check that exits early, makes a stale doc look *clean* rather than *unchecked*. Leaving it for the next run guarantees the next run repeats the same blind sweep. Peter authorised this standing 2026-08-18.

**How to apply:**

- Fix the script in the sweep's own PR, alongside the doc changes it enabled. Name the defect and its blast radius in the report and the commit message.
- Always re-run `bash docs/scripts/freshness-checks/diff-mode.sh` after touching it, and confirm the test you changed actually **prints** a PASS or FAIL line. A test that emits nothing is not passing — under `set -euo pipefail` a failed `grep`, an unquoted `&&`, or a trailing `if` will abort the script mid-run and take every later test with it.
- Widening a check almost always means widening its *scope* too: confirm the walk reaches `src/Sections/*/Docs/` and every catalog `editorial_trees` entry, not just `docs/`.
- This covers repair only. Adding a **new** CI check still needs Peter's permission ([[no-new-ci-checks-without-permission]]) — `diff-mode.sh` is deliberately not CI-wired.

Related: [[maintenance-log-update]] — record what the verifier missed and why, so the next sweep can tell a real green from a silent one.
