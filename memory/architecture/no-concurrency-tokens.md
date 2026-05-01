---
name: No concurrency tokens — single server, ~500 users
description: HARD RULE. Don't add `IsConcurrencyToken()`, `[ConcurrencyCheck]`, or row versioning to any entity. At single-server scale, conflicts don't happen and optimistic concurrency only causes bugs. Never add without explicit user permission.
---

**Do NOT** add `IsConcurrencyToken()`, `[ConcurrencyCheck]`, or row versioning to any entity. At single-server scale with ~500 users, concurrency conflicts don't happen and optimistic concurrency only causes bugs.

**Why:** The audit log is the architectural defense for the rare admin-clobbers-admin case (see [`audit-log-as-concurrency-safety-net`](audit-log-as-concurrency-safety-net.md)). Adding optimistic concurrency creates `DbUpdateConcurrencyException` paths that have to be handled in every service touching the entity, for a problem that doesn't manifest at this scale. Net result: more code, more bugs, no benefit.

**How to apply:**

- Don't add `b.Property(x => x.RowVersion).IsConcurrencyToken()` in EF configurations.
- Don't add `[ConcurrencyCheck]` attributes on properties.
- Don't add `byte[] RowVersion` or similar token fields to entities.
- If a code reviewer flags a "lost update race," reply citing the audit-log safety net + this rule, not by adding a token.
- **If you genuinely need this, ask Peter first** — there are no current entities that warrant it.

**Related:** [`audit-log-as-concurrency-safety-net`](audit-log-as-concurrency-safety-net.md), [`db-enforcement-minimal`](db-enforcement-minimal.md).
