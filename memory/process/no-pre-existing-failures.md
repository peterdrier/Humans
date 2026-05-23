---
name: Test failures on main block merges — "pre-existing, not my PR" is dead
description: HARD RULE. A red test on `main` blocks the merge. The only two valid responses are repair the test or `Skip = "tracked-by: nobodies-collective/Humans#NNN"` with a real follow-up issue. CI fails any `Skip=` string missing an issue ref.
---

"Pre-existing failures on `main`, doesn't block the merge" is no longer a valid sentence. A red test on `main` — integration or unit — blocks the merge, full stop.

When you hit a failing test (yours or one you find already failing on `main`), there is **no third option**. Pick one:

1. **Repair it.** Find the root cause and fix the test or the code under test.
2. **Quarantine it with a tracking issue.** Mark it `[HumansFact(Skip = "<reason>. tracked-by: nobodies-collective/Humans#NNN")]` (or `[HumansTheory(Skip = ...)]`) where `#NNN` is a **real, open** follow-up issue that owns getting the test back to green. File the issue first; put its number in the skip string.

What is **not** allowed:

- Merging around a red test by attributing it to "pre-existing" / "unrelated to my PR."
- Skipping a test with a skip string that has no `nobodies-collective/Humans#NNN` ref. CI enforces this — see below.
- Deleting a failing test to make CI green (that's not repair, it's hiding the signal).

**Why:** Broken tests left on `main` train everyone to ignore the test signal. Once "some failures are just pre-existing" is accepted, the suite stops being a gate and every new failure gets the same pass. The skip-with-issue-ref discipline keeps every quarantined test attached to an owner and a closure path, so the skip backlog stays visible and finite instead of growing silently.

**How CI enforces it:**

The `code-quality` job in `.github/workflows/build.yml` greps `tests/` for `Skip = "..."` attribute strings and fails the build if any skip string is missing a `nobodies-collective/Humans#NNN` reference. The fix when CI flags you is to add the issue ref to the skip string (after filing the tracking issue), not to suppress the check.

**How the skip backlog stays visible:**

`scripts/count-skipped-tests.sh` reports every skipped test and the tracking issue it points at, sorted oldest-issue-first. The recurring `/maintenance` sweep runs it (see the "Skipped-test sweep" row in `docs/architecture/maintenance-log.md`) so the oldest quarantined tests resurface for repair instead of accumulating.

**Related:** [`docs/features/test-system-reliability.md`](../../docs/features/test-system-reliability.md) (P5 — quarantine discipline; parent program nobodies-collective/Humans#761).
