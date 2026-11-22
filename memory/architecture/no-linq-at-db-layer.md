---
name: no-linq-at-db-layer
description: Prefer hand-written repo methods materializing at the boundary over `IQueryable` composed across services — LINQ on EF-mapped properties scatters DB-shape concerns.
---

A query lives in one repository method that runs it and returns a materialized `List` /
`IReadOnlyList`. Repositories never return `IQueryable<T>`, and services never compose predicates over
entity properties.

**Why:** when `User.Email` became a computed property and its column was unmapped, every scattered LINQ
filter on `u.Email` failed at translation time, in several unrelated services. Peter: *"this is why
your overuse of LINQ at the db layer is causing us so many problems."* With thick repositories a
mapping change is one fix in one place, and at this scale materializing is cheap.

**How to apply:**
- A service needs filtered or projected data → a repo method that takes the parameters and returns the
  result.
- LINQ inside the repo method is fine. The rule is one site per query and a materialized return, not
  "no LINQ".
- "Include, `ToListAsync`, then filter client-side" in a caller is a stopgap; move the query into the
  repo.
- When reworking a column-coupled query, ask what the caller actually wants to know and write that
  query, rather than handing back a column to filter on.

**Related:** [`design-rules.md`, *Repository layer*](../../docs/architecture/design-rules.md#repository-layer) ·
[`repository-required-for-db-access`](repository-required-for-db-access.md)
