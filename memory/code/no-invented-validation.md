---
name: no-invented-validation
description: Adding a guard, validation or fallback: only for a state the app can actually produce, checked once where it is owned — never per call site or for malformed admin settings.
---

Guard a state only when something in the app can produce it, and check it once, in the code that owns it.

**Why:** Peter (2026-11): no invented validation. The nightly sweep, once its ledger ran dry, spread an
erased-or-merged-account check from 6 files to 56 (the base controller already refuses that current user)
and made clients "stay resolvable" with malformed URLs and API keys. Each copy is a rule someone must keep
true, for a state that is rare or impossible.

**How to apply:**
- Before adding a check, name what produces the state. Nothing does → no check.
- The current user: use the base controller's `ResolveCurrentUserOr…Async`, never re-check `IsTombstone` by hand.
  Another person's account (a target, a recipient) is checked by the service that acts on it.
- A malformed setting only an admin can enter fails loudly where it is read. No tolerance code, no fallback.
- No duplicate, range or date guards nobody asked for, and don't ask whether to widen one.
- A guard you remove needs no replacement test.

**Related:** [[cancellation-token-propagation]], [[debt-sweep-standing-policy]]
