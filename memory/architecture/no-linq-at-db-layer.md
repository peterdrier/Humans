---
name: Don't overuse LINQ at the DB layer — thick repos return materialized lists
description: At ~500-user scale, prefer hand-written repo methods that materialize at the boundary over IQueryable composed across services. LINQ-against-EF-mapped-properties scatters DB shape concerns and breaks badly when mappings change.
---

Don't reach for LINQ-on-EF-entities (`db.Users.Where(u => u.Email.Contains(...))`, `db.Users.Select(u => u.Email)`) when designing service methods. Prefer **hand-written repository methods that materialize at the boundary** — the repo runs the query and returns a `List`/`IReadOnlyList` of plain DTOs (or domain objects with all needed data Include'd).

**Why:** Surfaced 2026-04-29 during PR 2 of the email-identity-decoupling work. When `User.Email` was overridden to compute from `UserEmails` and the column was `Ignore()`-d in EF mapping, every LINQ filter site that translated `u.Email` to SQL (5+ sites: `ProfileService` admin search, `DuplicateAccountService`, `DriveActivityMonitorRepository`, `DevLoginController`, etc.) broke at translation time. EF can't translate a C# property override body to SQL. Peter: "this is why your overuse of LINQ at the db layer is causing us so many problems."

The cost wasn't the rewrite — it was that LINQ-on-property-accessors made DB shape concerns invisible at every call site. When the column mapping changed, the failure mode was scattered runtime exceptions in disparate services. A thicker-repo pattern would have isolated the shape concern to the repository layer; the column-mapping change would have meant fixing one repo method instead of N services.

At 500-user scale this is doubly correct because (a) materializing the dataset is cheap, and (b) "service owns its data" + thick-repo aligns with the project's design rules.

**How to apply:**

- When designing a new service method that needs filtered/projected data, write a repo method that takes the parameters and returns the materialized result. Don't return `IQueryable<T>` from repos; don't compose `.Where(...)` chains in services across `db.Set<T>()`.
- When reviewing existing code, treat `db.Set<T>().Where/Select/...` inside an Application service as a smell. The LINQ belongs in the repo (or it's not a service method, it's a query).
- Hand-written queries against EF-mapped properties are still LINQ — that's fine; the rule is *one site per query*, *materialized result returned*, not "no LINQ at all."
- For `Include(UserEmails) + ToListAsync() + filter client-side` workarounds: that's a stopgap. The real fix is moving the query into a repo method and returning a materialized list.
- Don't conflate this with EF's primary-key lookups (`db.Find(id)`) or simple read-by-key — those are fine in services. The smell is composing predicates against entity properties.
- When refactoring a column-coupled query, ask "what does this caller actually want to know?" then write the repo query for that — not "give me the user's primary email and let me filter on it." (Example: `DuplicateAccountService` should join across **verified** `UserEmails`, not filter on primary email alone.)

**Related:** [design-rules.md §3](../../docs/architecture/design-rules.md#3-repository-layer) — repository entity-in/entity-out doctrine.
