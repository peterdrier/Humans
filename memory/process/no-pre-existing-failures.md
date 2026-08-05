---
name: no-pre-existing-failures
description: a test failing in CI gets fixed — "pre-existing on main, not my problem" is not an excuse; skipping is a separate mechanism and is not the answer to a failure
---

A test failing in CI gets fixed. We do not ignore failures.

"It was already failing on `main`" is not a reason to leave it red, and neither is "unrelated to this PR."

**Why:** A red `main` that everyone shrugs at stops being a signal. Once "pre-existing failure, not my problem" is accepted, failures pile up and CI stops meaning anything.

**Skipping is a separate thing.** There are real reasons to skip a test — a debugger-only test, an opt-in maintenance sweep, a gate that only runs in some environments — and those are fine and stay as they are. What a skip is *not* is a response to a test that started failing. Turning a failure into a skip is ignoring it with extra steps.

**How to apply:**
- Failing test → fix it.
- When a skip does stand in for something broken, name the tracking issue in the reason so it isn't invisible: `[BrokenFact("<what broke>. Tracked by nobodies-collective/Humans#NNN.")]`, or `Skip = "tracked by nobodies-collective/Humans#NNN"`. Convention — nothing enforces it.
