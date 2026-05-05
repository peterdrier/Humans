---
name: Display sort belongs in controllers (or view-model assembly), not in repositories
description: Display ordering is a presentation concern. Repositories return data; controllers / view-model assembly sort it for the view. Repository-layer `OrderBy`/`OrderByDescending` is allowed only for pagination tie-breakers, top-N selectors, and identity-ordered chronological sequences — each marked with an inline `// arch:db-sort-ok <reason>` comment.
---

Repository methods return data; the UI/controller layer sorts it for display. A repo file (`src/Humans.Infrastructure/Repositories/**/*.cs`) calling `.OrderBy(...)` / `.OrderByDescending(...)` for display ordering is a layer leak.

**Why:** Presentation concerns leaking into the data layer make queries brittle (changing the UI requires a query change), prevent cache reuse (two views sorting the same dataset differently double the cache footprint), and mix concerns (a repo method is no longer "give me the data" — it's "give me the data the way View X needs it"). Repos return materialized lists per the project's thick-repo doctrine; the consumer chooses the order.

**Exceptions** — must be marked with an inline `// arch:db-sort-ok <reason>` comment on the same or immediately preceding line:

- **Pagination tie-breakers.** Stable paging requires deterministic ordering at the SQL boundary. `OrderBy(x => x.Id)` after a primary sort is a tie-breaker, not display ordering.
- **Top-N selectors.** `OrderByDescending(x => x.CreatedAt).Take(10)` is semantically a *selector* — "the 10 most recent" — not a "sort for display." The order is part of the query result definition.
- **Identity-ordered chronological streams.** Audit log, consent records, append-only event streams that are conceptually ordered by their identity column. Reading them out of order makes no sense.

**How to apply:**

- New repo method needs ordering for display? Don't add it. Return the materialized list and let the controller sort.
- Existing repo method's `OrderBy` is an exception per the list above? Add `// arch:db-sort-ok <one-line reason>` so the ratchet knows to allow it.
- Find yourself wanting to add a `sortBy` parameter to a repo method? That's the smell — controllers should sort.

**Related:** [`no-linq-at-db-layer`](no-linq-at-db-layer.md) — same shape (data shape leaks across the repo boundary).
