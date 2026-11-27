---
name: no-invented-validation
description: Adding a guard, validation or fallback: only for a state the app can actually produce, checked once where it is owned — never per call site or for malformed admin settings.
---

Guard a state only when something in the app can produce it, and check it once, in the code that owns it.

**Why:** Peter: no invented validation. [`review-finding-triage`](../process/review-finding-triage.md) already
declines review findings whose only fix is a guard for an unreachable state; this atom applies the same bar to work
an agent finds itself. Once its ledger ran dry, the nightly sweep copied the same `x is null || x.IsTombstone`
line into 37 service sites and added malformed-URL tolerance to clients. Each copy is a rule someone must keep true.

**How to apply:**
- Before adding a check, name what produces the state. Nothing does → no check.
- Users already resolves a merged id to its survivor, so a resolved read is tombstoned only for an erased account.
  The current user is checked by the base controller (`ResolveCurrentUserOr…Async`); don't re-check by hand.
  The same check repeated across callers belongs in one owner read, not in each caller.
- A malformed setting only an admin can enter needs no tolerance code. Let the call that uses it fail and log;
  never at boot or DI resolution ([`no-startup-guards`](../architecture/no-startup-guards.md)).
- No duplicate, range or date guards nobody asked for, and don't ask whether to widen one.

**Related:** [[cancellation-token-propagation]], [[debt-sweep-standing-policy]]
