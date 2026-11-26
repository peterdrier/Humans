---
name: names-short-no-and
description: Naming or renaming a method: a verb phrase of at most 6 words, no "And", nothing about the caller, the return shape or saving.
---

A method name is a short verb phrase that says the one thing the method does: at most 6 words not counting `Async`, and no "And".

**Why:** Peter (2026-11-24): names had grown into sentences ("this would make a German proud"), and he reads a long name as a sign the code does too much. Earlier calls in the same line: `RemoveAsync` over `UnassignFromAllRolesAsync` and `EmptySlotsAsync`.

**How to apply:**
- A name that needs "And" names two actions: split the method, or name the one outcome the caller wants (`RedeemSignupToken`, not `VerifyAndConsumeSignupToken`). "Or" is fine (`GetOrCreate`).
- Leave out what the class, the parameters and the return type already say, and who is calling (`ForFinanceCorrection`), what comes back (`WithResult`) or that it saves (`AndSave`).
- Pairs match (`Add`/`Remove`, `Start`/`Stop`). No coined words.
- The build enforces the word limit and "And" (HUM0042). Old names sit in the project's `NameBaseline.txt`; never add a line to it. When you rename or delete a listed method, delete its line.

The full standard: `docs/architecture/design-rules.md`, *Naming*. Related: [[test-names-one-rule]], [[no-hostile-api-design]].
