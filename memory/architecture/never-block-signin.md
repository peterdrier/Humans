---
name: never-block-signin
description: HARD RULE. Sign-in must always succeed once Identity authenticates the user. Never propose flows that block sign-in for data-cleanup or conflict-resolution reasons.
---

**HARD RULE.** Never block sign-in. If Identity has authenticated the user (External or magic-link), the user gets signed in. Any data anomalies — cross-user email collisions, rename conflicts, duplicate-account situations, orphaned rows — are handled out-of-band via audit + admin tooling, never by failing the sign-in.

**Why:** sign-in is the user's entry point. Blocking it strands users with no recourse and damages trust. Data state can always be reconciled after the fact; locked-out users cannot.

**How to apply:** when proposing fixes that touch OAuth callbacks, magic-link flows, or any code path that runs during authentication — the auth itself always completes. Any rename/rewrite/merge logic that fails just gets logged and audited; the user proceeds. Self-heal next time, or an admin resolves it later.
