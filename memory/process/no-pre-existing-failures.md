---
name: no-pre-existing-failures
description: failures on main block merges; "pre-existing, doesn't block the merge" is not a valid excuse — repair or quarantine with a tracking issue ref
---

A test failing on `main` blocks merges. "It was already failing before my change" is not a valid reason to leave it red. Either repair the test in the same PR, or quarantine it with `[BrokenFact("... Tracked at nobodies-collective/Humans#NNN.")]` (or an unconditional `Skip = "..."` on `[HumansFact]`/`[HumansTheory]` naming the same issue) and file the tracking issue if one doesn't exist yet.

**Why:** A red `main` that everyone shrugs at stops being a signal. Once "pre-existing failure, not my problem" is accepted, failures accumulate silently and CI stops meaning anything. Requiring a tracking issue on every skip keeps quarantined tests discoverable and owned instead of rotting invisibly.

**How to apply:**
- CI enforces this mechanically: `scripts/check-skip-attribute-tracking.sh` (wired into the `code-quality` job in `.github/workflows/build.yml`) fails the build if a quarantine's reason doesn't contain `nobodies-collective/Humans#NNN`. It covers every way xUnit v3 lets a test or a single theory row be skipped: `[BrokenFact(...)]`; an unconditional `Skip=` on `[HumansFact]`/`[HumansTheory]`; per-row `Skip=` on `[InlineData]`/`[MemberData]`/`[ClassData]`; a programmatic `new TheoryDataRow(…).WithSkip("…")`; and a body-level `Assert.Skip("…")`. Attributes are matched in every legal spelling — the `…Attribute` suffix, generic type arguments (`ClassData<TRows>`), a namespace qualifier or attribute target (`[method: Humans.Testing.BrokenFact(…)]`), and any position in a shared list (`[Trait(…), HumansFact(…)]`).
- A `Skip=` paired with `SkipUnless=`/`SkipWhen=` (a runtime-conditional gate — e.g. `DebuggerOnlyFactAttribute`, the opt-in localization sweep) is not a quarantine and is exempt; those tests are deliberately gated, not broken. The exemption applies per data row too, since `DataAttribute` carries the same two properties. `SkipType=` on its own does **not** exempt: in xUnit v3 it only names the type the `SkipUnless`/`SkipWhen` property is read from, so without one of those the skip is still unconditional.
- For a runtime gate inside a test body, use `Assert.SkipWhen`/`Assert.SkipUnless` — they take the condition explicitly and are exempt. A bare `Assert.Skip("…")` is read as an unconditional quarantine and needs the issue reference.
- The reason must be a plain, verbatim (`@"…"`) or raw (`"""…"""`) string literal. A `Skip=` set from a const, `nameof`, or concatenation is rejected — the gate can't read the issue number out of it, so it can't vouch for it.
- `/maintenance` sweeps monthly via `scripts/check-skip-attribute-tracking.sh --list`, which prints every quarantine in the tree with its file, line and reason — tracked ones included — so a quarantine whose tracking issue has since closed surfaces instead of aging out of sight.
- Don't weaken the CI gate to accommodate an offending test — fix the test or add the missing issue reference.
