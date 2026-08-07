---
name: Every human has an email address
description: HARD RULE — every human has an email, period. Never design for, defend against, or file findings about an emailless user; treat a no-email code path as unreachable.
---

Every human in this system has an email address. Period. It is not optional, not nullable-in-practice, not a case to handle.

**Why:** Sign-in is Google OAuth or magic link — both require a working address to exist at all. `IUserEmailService.GetNotificationTargetEmailsAsync` reinforces it: when no `UserEmail` row is flagged as a notification target, it falls back to `User.Email` from the Identity record. There is no realistic population left after that fallback.

**How to apply:**

- Do not write "what if they have no email" branches, warnings, or fallbacks in new code.
- Do not raise, escalate, or ask Peter about an emailless scenario. A reviewer finding of the form "user X has no deliverable email, therefore Y breaks" is a **false positive** — close it, don't fix it. Codex raised exactly this on PR #1185 (a notification with no link and no reply text "for reporters without email"); it cost a round-trip and was never a real case.
- Existing `if (no email) { log warning }` branches are harmless leftovers. Leave them where they are — do not start a sweep to remove them, and do not add more. Cleaning them is not worth anyone's time.
- This is about the *human*, not about delivery. Bounces, suppression, and send failures are real operational concerns and stay in scope; "this person has no address" does not.

Related: [`humans-terminology`](../product/humans-terminology.md).
