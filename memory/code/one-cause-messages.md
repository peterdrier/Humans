---
name: one-cause-messages
description: Writing a log, exception or user-facing failure message: name the one cause that happened, never "X or Y"; make the code able to tell the cases apart.
---

A failure message states the single cause that occurred. Never join causes with "or" ("invalid or expired token", "not found or not allowed"). If the code can't tell the causes apart, make it able to: a result enum or refusal key per cause, or a step variable naming the step that failed. If it truly can't, state only the fact it knows ("Token didn't match any survey link").

**Why:** Peter, 2026-11-24, on the log "Survey answer link could not be resolved (invalid or expired token)": "which is it, expired or invalid". A joined message hides the one thing the reader needs to act on. Sweep: PR #2606.

**How to apply:** Each failure branch gets its own message (log text, exception text, resx key). A `bool` return that collapses several refusals becomes a result type carrying the specific key.

**Exceptions:**
- Deliberate non-disclosure, where naming the cause would leak whether something exists (the Backdoor tasks 403, issue access).
- An upstream that itself can't tell the causes apart (a Google 404 means not found or no access).
- Requirement or instruction text listing valid options ("enter a phone or an email").

**Related:** [[always-log-problems]], [[log-file-debugging]]
