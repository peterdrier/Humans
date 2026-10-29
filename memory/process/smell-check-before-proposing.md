---
name: Smell-check every fix before proposing it
description: Before proposing any fix, check what the code already handles, state the leftover risk in one line, and ask "is this too complicated, does it smell wrong?" A fix that grows interfaces or mechanisms for a rare, self-inflicted case is the wrong fix.
---

Before proposing a fix, first read what the code already does about the problem. Then state the leftover risk in one line and ask whether it is worth fixing at all. Only then propose the smallest fix, and ask of it: is this too complicated for what it prevents? Does it smell wrong?

**Why:** On peterdrier/Humans#2025 (double Stripe checkout) the agent proposed a return-type change, then a session-expiry call, idempotency hour-buckets and form salts, before noticing Store already refused Pay once the balance was paid or a payment was pending. The only gap left was a member paying in two tabs at once: their own doing, refunded by hand. Peter: "this was so clearly wrong that we shouldn't have wasted time on it."

**How to apply:**
- Lead with "already handled by X; what's left is Y" before any design.
- A user hurting only themselves through deliberate odd use (two tabs, double submits they ignore the result of) is not our problem to engineer away.
- If a fix needs a new interface member, a new mechanism, or a caveat to explain its caveat, stop: it is probably the wrong fix. Say so instead of refining it.
- Ask Peter a straight yes/no on whether the leftover risk matters, not a menu of mechanisms.

**Related:** `AGENTS.md` Taste ("Minimum code that solves the problem"), `process/reuse-first-change-discipline.md`.
