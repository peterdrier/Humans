---
name: An easy fix gets fixed, never offered as "file an issue"
description: When a found bug or gap is small enough to fix in the PR at hand, fix it there (surface it per drive-by-fixes-ok) — never offer "file an issue / do it later" as an option for it.
---

When you find something small enough to fix inside the work in front of you, the only options you put to Peter are "fix it in this PR" or "fix it in the next PR already planned". Deferral (an issue, a ledger row, a follow-up) is not a menu item for an easy fix.

**Why:** Peter, 2026-10-17, on an orphan-account bug offered as "file an issue / fix in PR 3 / ignore": "stop offering to defer things that are easy to fix. You're making more work to track things than just to fix them." Tracking a small fix costs more than making it: an issue to write, triage, pick up, PR, review, close.

**How to apply:**

- Easy = a few lines, one section, obvious correct behaviour, no new public surface. That is the `drive-by-fixes-ok` size class; the approval step there still applies when it is unrelated to the PR, but the ask is "fix here, yes?", not "fix or defer?".
- Found while building a multi-PR feature: it goes in the PR of that feature that touches the same code, stated in that PR's body.
- Deferral stays right for the big things: a new page, a schema change, a contract change, anything needing a design call. Say why it is big, not just that it is.
- `debt-ledger-additions` is for code judged to be debt, not for a bug you can fix now.

**Related:** [[drive-by-fixes-ok]], [[debt-ledger-additions]].
