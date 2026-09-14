# Conformance

Runs as detectors on the main thread plus a `haiku` subagent that classifies their output.

`docs/architecture/section-conformance.yml` — the per-section rules nothing enforces yet.
Detectors are mechanical; the judgment is what to do about a hit. The file is read-only to every
run: a row worth adding or removing is a Needs-Peter proposal, never an edit.

**The thread never runs a detector.** Main runs every `detect:` block before dispatch and
passes the output file's path in the prompt. The thread reads that file, classifies each hit
(a finding with its rule id, or a false positive with the reason), and reports a detector with
no output as `clean` — and if the prompt carries no detector output at all, it says so as the
first line of its return instead of reporting clean.
