---
name: no-pre-existing-failures
description: failures on main block merges; "pre-existing, doesn't block the merge" is not a valid excuse — repair or quarantine with a tracking issue ref
---

A test failing on `main` blocks merges. "It was already failing before my change" is not a valid reason to leave it red. Either repair the test in the same PR, or quarantine it with `[BrokenFact("... Tracked at nobodies-collective/Humans#NNN.")]` (or an unconditional `Skip = "..."` on `[HumansFact]`/`[HumansTheory]` naming the same issue) and file the tracking issue if one doesn't exist yet.

**Why:** A red `main` that everyone shrugs at stops being a signal. Once "pre-existing failure, not my problem" is accepted, failures accumulate silently and CI stops meaning anything. Requiring a tracking issue on every skip keeps quarantined tests discoverable and owned instead of rotting invisibly.

**How to apply:**
- CI enforces this mechanically: `scripts/check-skip-attribute-tracking.sh` (wired into the `code-quality` job in `.github/workflows/build.yml`) fails the build if a `[BrokenFact(...)]` reason or an unconditional `Skip=` doesn't contain `nobodies-collective/Humans#NNN`. It covers `[HumansFact]`/`[HumansTheory]`, the `…Attribute`-suffixed spellings of both, and per-row `Skip=` on `[InlineData]`/`[MemberData]`/`[ClassData]` (xUnit v3 lets a single theory case be quarantined that way).
- A `Skip=` paired with `SkipUnless=`/`SkipWhen=` (a runtime-conditional gate — e.g. `DebuggerOnlyFactAttribute`, the opt-in localization sweep) is not a quarantine and is exempt; those tests are deliberately gated, not broken. `SkipType=` on its own does **not** exempt: in xUnit v3 it only names the type the `SkipUnless`/`SkipWhen` property is read from, so without one of those the skip is still unconditional.
- The reason must be a plain, verbatim (`@"…"`) or raw (`"""…"""`) string literal. A `Skip=` set from a const, `nameof`, or concatenation is rejected — the gate can't read the issue number out of it, so it can't vouch for it.
- `/maintenance` sweeps and reports outstanding `Skip=` occurrences so the oldest tracking issues surface instead of aging out of sight.
- Don't weaken the CI gate to accommodate an offending test — fix the test or add the missing issue reference.
