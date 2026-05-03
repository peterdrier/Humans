---
name: Interface method-count budget — strict down-only ratchet
description: HARD RULE. Adding a method to a budgeted interface requires removing one from the SAME interface in the SAME PR. No raises, no splits to dodge, no broader-replacement tricks. Only Peter authorizes exceptions.
---

The `tests/Humans.Application.Tests/Architecture/InterfaceMethodBudgetTests.cs` ratchet is a **consolidation mechanism**, not a paperwork-and-justification mechanism. Down-only. Strict.

**Why:** The audit-surface skill kept finding bloat that accrued one "+1 justified" PR at a time. A "split it" workaround would just redistribute the same surface across two budgets and unlock fresh growth runway in each. The point is to make budgeted interfaces *smaller over time* — not stable, not redistributed. Past agent justifications and split-and-grow patterns are the failure modes that put the ratchet in place.

**How to apply:**

- **No raises.** Don't increase a budget number for any reason. If you've added a method and the test is red, remove a method from the same interface to bring it back to budget — don't raise.
- **No splits to dodge.** Don't propose splitting a budgeted interface (e.g., extracting `ICampMembershipService` from `ICampService`) as a way to make room. Splits move methods into a fresh interface with a fresh budget — defeats the consolidation goal.
- **No replacement-by-broader-method tricks.** Don't replace a method with a more-general bag-of-options + flags one so the count drops by 1 while the surface area grows.
- **Add → remove from the SAME interface, same PR.** A new method on `ICampService` requires removing another method from `ICampService` in the same PR.
- **Net delta on the PR is ≤ 0.** When the net is negative, lower the budget number to match the new count exactly (`Budgets_are_tight_and_not_padded` enforces this — no headroom).
- **Hit a wall? STOP and ask Peter.** If a feature genuinely can't be expressed without growth, present the case and let him decide. Don't raise, split, or work around in the meantime.

**Scope:** all currently-budgeted interfaces (ITeamService, ICampService, IShiftManagementService, IProfileService, IUserService — verify the test file for the current set). The list of budgeted interfaces can grow (bringing more under the ratchet); no number on an existing entry goes up.
