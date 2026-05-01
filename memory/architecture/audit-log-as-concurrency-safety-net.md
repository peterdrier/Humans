---
name: Audit log is the concurrency safety net (not optimistic concurrency)
description: At ~500-user scale, the audit log makes lost-update races tolerable. Don't reach for `IsConcurrencyToken`, row versioning, or repo-redesign-around-races. Service-level audit is the architectural defense.
---

At Humans' scale (~500 users, single-server, admin-heavy writes), the audit log is the architectural defense against concurrent-write races — **not** optimistic concurrency, not property-level change tracking, not lock primitives.

Every business-meaningful mutation (role assignments, feedback status/assignment changes, team membership edits, budget changes, etc.) writes an `AuditLog` row. If two admins both mutate the same entity and one silently clobbers a field the other just set, the audit log still shows exactly what each admin changed and when. The intended state is reconstructible. Operators can detect the race post-hoc and re-apply the overwritten change.

**Why:** This combines with `CLAUDE.md`'s no-concurrency-tokens policy to explain *how* the system stays correct at this scale without optimistic concurrency. Peter raised this explicitly when rejecting a Codex P1 that flagged `Attach + State = Modified` as a lost-update race: "this is why we have the audit log on things we care about."

**How to apply:**

- When a code reviewer (human or AI) flags a multi-admin lost-update race on an entity with audit logging, the answer is "audit log catches it; we won't redesign around it." Reply explaining the audit-log safety net, not just the scale.
- When designing a new mutation path on a tracked entity, make sure the *business-meaningful* writes log to `IAuditLogService.LogAsync(...)`. That is the correctness guarantee, not the repo's change-tracking shape.
- If a future race actually manifests in production, the right fix is a narrow write lock keyed by entity id — NOT `IsConcurrencyToken()`, `[ConcurrencyCheck]`, row versioning, or a repo/service contract redesign to route mutations through delegates.
- Does not apply to entities without audit logging. If the race would silently corrupt state with no reconstructible trail, that's a different conversation.

**Related:** `CLAUDE.md` "Scale and Deployment Context" → no-concurrency-tokens rule.
