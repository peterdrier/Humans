---
name: A guard test must assert something only that guard produces
description: A test pinning a guard clause must assert the message or pick an input no other path can reach — a type-only assertion on a shared exception type still passes with the guard deleted. Triggers when writing or reviewing a test for a throw-on-bad-input guard.
---

A test that pins a guard must fail when that guard is deleted. Assert the guard's message, or choose an input that no other path in the method can reach.

**Why:** Guards in one method usually throw the same exception type as their neighbours. Assert only the type, and the test passes on a build with the guard removed — the next validation two lines down throws it instead. The test then records that the method rejects the input, not that *this* guard does, and the rule it was written to protect is unguarded while the suite stays green.

**How to apply:**

- Assert on the message text, or on whatever the guard alone sets.
- Or pick a case the sibling paths cannot reach: an input that passes every other validation and trips only this one.
- Reviewing: for every `Assert.Throws<T>` on a guard, ask which other line in the method also throws `T`. If any does, the test is not discriminating.

**Related:** [`no-tests-for-absences`](../architecture/no-tests-for-absences.md) — what is worth pinning in the first place.
