# Freshness

Runs as a subagent, `opus low` (`doctor-reader`).

The section's docs vs code: claims that no longer hold, `freshness:triggers` globs that still
resolve, and triggers that watch *everything the doc asserts about* — including another
section's file where the doc names it. A fixed claim gets swept everywhere it appears.

- A doc claiming a test that does not exist is a doc to fix, never a test to write; the test
  would be an absence assertion — `memory/architecture/no-tests-for-absences.md`.
- A trigger that resolves is not a trigger that works: check each path actually carries the
  claim, not merely that it is live.
- Read a feature doc's "Out of scope" list against its route table every time; it ages worse
  than the body.
