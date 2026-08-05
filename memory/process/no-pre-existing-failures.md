---
name: no-pre-existing-failures
description: a failing test blocks the merge — fix it or delete it; "pre-existing on main, not my problem" is not an excuse
---

A failing test blocks the merge. "It was already failing on `main`" is not a reason to leave it red.

Fix the test, or delete it. A test nobody is going to fix isn't earning its keep, and deleting it is honest — leaving it red is not.

**Why:** A red `main` that everyone shrugs at stops being a signal. Once "pre-existing failure, not my problem" is accepted, failures pile up and CI stops meaning anything.

**How to apply:**
- Default is fix or delete. Skipping is the exception, not the third option.
- If you do skip one, name the tracking issue in the reason so it isn't invisible: `[BrokenFact("<what broke>. Tracked by nobodies-collective/Humans#NNN.")]`, or `Skip = "tracked by nobodies-collective/Humans#NNN"` on `[HumansFact]`/`[HumansTheory]`. Convention, not a gate — nothing enforces it.
