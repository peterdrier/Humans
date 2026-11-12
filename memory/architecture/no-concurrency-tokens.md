---
name: No concurrency tokens; the audit log is the safety net
description: HARD RULE. A reviewer flags a lost-update race, or you reach for `IsConcurrencyToken`, `[ConcurrencyCheck]`, a row version or a lock: don't. The audit log covers it.
---

Never add `IsConcurrencyToken()`, `[ConcurrencyCheck]`, `RowVersion` fields or row versioning to any entity, and don't redesign a repository to route writes through delegates. Ask Peter before adding any.

**Why:** one server and a small, admin-heavy user base. Conflicts don't happen, and optimistic concurrency adds a `DbUpdateConcurrencyException` path to every service for nothing. When two admins do clobber each other, the audit log shows what each changed and when, so the intended state can be re-applied. Peter, rejecting a Codex P1 on `Attach + State = Modified`: "this is why we have the audit log on things we care about."

**How to apply:**

- A review finding about a multi-admin lost update on an audited entity gets the reply "the audit log catches it; we won't redesign around it."
- A new mutation on a tracked entity must write its business-meaningful change through `IAuditLogService.LogAsync`. That is the correctness guarantee.
- If a race ever shows up in production, the fix is a narrow write lock keyed by entity id.
- An entity with no audit trail, where a race would silently corrupt state, is a different conversation: raise it.

**Related:** [`db-enforcement-minimal`](db-enforcement-minimal.md)
