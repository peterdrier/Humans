---
name: Don't grow a bloated service to silence a "logic in controller" bot warning
description: When a service interface already has many methods covering the same domain (e.g. ~20 email lookups on IUserEmailService), don't add another method to satisfy a "no business logic in controllers" warning from a review bot. Picking which field to display in a view is view-model assembly, which the rule explicitly excludes. Inline a short LINQ chain on an existing list-returning method instead.
---

The `no-business-logic-in-controllers` heuristic is a fuzzy boundary, not an absolute rule. Its own body excludes **view-model assembly** — and picking which field to display from a list of candidates *is* view-model assembly, even when the picker has an `??` chain.

**Why:** Bloated service surfaces are the larger smell. Every "+1 method to satisfy a bot warning" PR ratchets in the wrong direction. The audit-surface skill exists because past PRs grew these surfaces one well-justified method at a time. Past instance: PR #468 (issue #690) added `IUserEmailService.GetBestAvailableEmailAsync` to satisfy a Claude-bot warning, on top of the ~20 existing email-lookup methods (`GetVerifiedEmailAddressAsync`, `GetVerifiedEmailsForUserAsync`, `GetUserEmailsAsync`, `GetNotificationTargetEmailsAsync`, `FindVerifiedEmailWithUserAsync`, …). Reverted the same session: a two-line LINQ chain on `GetUserEmailsAsync` in the controller was the correct shape.

**How to apply:**

- Before adding a method to a service interface, look at how many methods already cover the same conceptual domain. If 10+, default to "inline a chain on an existing list-returning method."
- A short LINQ chain in a controller — `.FirstOrDefault(e => …) ?? .FirstOrDefault(e => …)` — is fine for view-model assembly. Two or three lines isn't a god class.
- The `no-business-logic-in-controllers` heuristic flags action methods over ~50 lines or cyclomatic ≥ 6. A two-line picker doesn't trip either threshold.
- Bot review warnings are advisory. When they conflict with another rule (consolidation, surface area), the bigger smell wins. Reply on the thread explaining why instead of complying mechanically.
- Hit a real wall? STOP and ask Peter. Don't grow the surface to silence a bot.

**Related:** [`interface-method-budget-ratchet`](interface-method-budget-ratchet.md), [`no-business-logic-in-controllers`](no-business-logic-in-controllers.md).
