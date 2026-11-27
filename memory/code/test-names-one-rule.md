---
name: test-names-one-rule
description: Writing a test or adding an assert: name it `Method_Condition_Result` for the one rule it proves, max 10 words, no "And"; a new assert never lengthens the name.
---

A test is named `Method_Result` or `Method_Condition_Result` and states the one rule it proves, not the list of what it asserts.

**Why:** Peter (2026-11-24), on `Merge_RepointsScannerOverrideAndUndo_AndCarriesThePinToAPinlessSurvivor`: "this is like a sentence". A fifth of the test names had reached 12 words or more, because the name was treated as the assert list and grew with every assert.

**How to apply:**
- PascalCase, three parts at most, 10 words at most, no "And" after the first part, no lowercase sentences. The first part is the method under test, without `Async`.
- Adding an assert never changes the name. If the new assert proves a different rule, it is a second test.
- A name that needs "And" is two tests, or one rule named too literally (`Reassign_RepointsStaffColumns`, not one word per column).
- A "why" is a one-line comment above the test.
- The build enforces the shape (HUM0043). Old names sit in the test project's `NameBaseline.txt`; never add a line to it. When you rename a listed test, delete its line, and grep `docs/`, `memory/`, `src/**/Docs/` and `.github/workflows/` for the old name.

The full standard: `docs/architecture/design-rules.md`, *Naming*. Related: [[names-short-no-and]], [[comments-earn-their-place]].
