---
name: ef-inmemory-is-fine
description: HARD RULE. EF-InMemory tests are fine in Humans — never propose migrating repository tests to a real Postgres fixture, and never flag EF-InMemory as a gap or gate failure.
---

**HARD RULE.** Never propose migrating tests off EF-InMemory to a real Postgres fixture, and never flag EF-InMemory usage as a defect, gap, or gate failure.

**Why:** the database "should never be doing anything complicated" — that's a design constraint, not an accident. See [`CLAUDE.md`](../../CLAUDE.md) → Scale and Deployment: a small user base, single server, and the project deliberately prefers loading whole datasets into RAM over query optimization. The things a real provider catches that EF-InMemory misses — SQL translation gaps, collation semantics, FK enforcement, concurrency — are all things this codebase deliberately does not do; cross-section FK constraints are being removed outright (see [`no-cross-section-ef-joins`](../architecture/no-cross-section-ef-joins.md)). A Postgres fixture buys nothing here and costs wall-clock plus flakiness.

**How to apply:**
- Don't raise it in sprint plans, code review, audits, design prompts, or issue triage.
- If a test genuinely needs provider-specific behavior, that's a signal the *query* is too complicated for this project, not that the test needs a real database.

Related: [[no-cheapest-wins]] (not in this migration — external memory only) — the objection is value, not effort; Postgres-fixture work is cheap-ish and still not worth doing.
