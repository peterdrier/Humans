---
name: Never drop hard storage in the same PR that ships its replacement
description: HARD RULE. DB columns/tables/indexes/constraints, persistent filesystem data, and external persisted state must wait for a separate follow-up PR AFTER the replacement has shipped and been verified in production. Code-only deletions are exempt.
---

**Hard-storage drops** wait for a separate PR after the replacement has shipped and been verified in production. Code drops (classes, methods, files) are exempt — code rolls back via `git revert` + redeploy.

**In scope (require split):**
- DB columns, tables, indexes, unique constraints, check constraints, FKs
- Persistent filesystem data (uploaded user files, blob storage, mounted volumes)
- External persisted state (S3 objects, KMS keys, queue contents — anything where deletion is one-way)
- EF migrations whose `Up()` performs any of the above

**Out of scope (can ship in same PR as replacement):**
- C# code: classes, methods, properties, interfaces, files
- Entity property removals AS LONG AS the underlying column stays (`b.Ignore(...)` the property; column drop is a separate follow-up)
- Razor views / static assets / DI registrations / localization resources / tests / docs

**Cut sequence for hard storage:**
1. PR A ships the new functionality, stops *using* the old hard-storage thing (read sweeps, replacement writes, `b.Ignore(...)` on EF properties so the column is unmapped). The hard storage stays in place.
2. PR A ships to QA, then production, and is **verified live in prod** (Peter judges the soak window).
3. PR B (follow-up) drops the column / deletes the file / removes the persisted object.

**Why:** Code can roll back via `git revert` + redeploy in minutes; a dropped column is gone forever (or requires hours of hand-restore from backup, with possible data loss). Single-PR hard-storage drops collapse the safety window. Even when static analysis shows no remaining callers, runtime paths, scheduled jobs, deserialization shims, or external integrations may still depend on the column. Production is the only honest oracle. Peter has been burned multiple times — severe damage.

**How to apply:**

- When writing a plan that touches hard storage, split DROPs into a separate follow-up PR.
- When writing a spec that lists hard-storage drops alongside replacement code, **flag it and split** — even if previously approved.
- When generating a prompt for another agent session, never instruct PR N to drop hard storage that PR N also replaces.
- When asked "should we also drop column X while we're here?" the answer is **no** — open a follow-up issue.
- An EF migration in PR N may contain `AddColumn`, `AddIndex`, `CreateTable`, etc. It must NOT contain `DropColumn`, `DropTable`, `DropIndex`, `DropUniqueConstraint` for anything PR N is replacing.
- Drops of things ALREADY unused for many releases (pure janitorial) are OK if Peter explicitly approves.

If a plan or spec appears to violate this rule, **STOP and ask Peter.** Do not rationalize "this case is small" or "the replacement is obviously equivalent."

**Related:** [`no-column-drops-for-decoupling`](no-column-drops-for-decoupling.md) — DB-column-specific instance.
