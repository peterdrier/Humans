---
name: Display sort belongs at the presentation layer, not in services or repositories
description: Display ordering is a presentation concern. Sorting in controllers, views, view-model assembly, or `@Html` partials is fine. Sorting in Application services, repositories, or DB-layer code is a layer leak. Repository-layer `OrderBy`/`OrderByDescending` is allowed only for pagination tie-breakers, top-N selectors, and identity-ordered chronological sequences — each marked with an inline `// arch:db-sort-ok <reason>` comment.
---

Display ordering is presentation. Anything **above** the service boundary may sort: controllers, views (`.cshtml`), view-model assembly, partials, tag helpers — all fine. Anything **at or below** the service boundary should not: a repo file (`src/Humans.Infrastructure/Repositories/**/*.cs`) or an Application service (`src/Humans.Application/Services/**/*.cs`) calling `.OrderBy(...)` / `.OrderByDescending(...)` for display ordering is a layer leak.

**Why:** Presentation concerns leaking into the data/business layer make queries brittle (changing the UI requires a query change), prevent cache reuse (two views sorting the same dataset differently double the cache footprint), and mix concerns (a repo method is no longer "give me the data" — it's "give me the data the way View X needs it"). Repos return materialized lists per the project's thick-repo doctrine; the consumer chooses the order. Services orchestrate domain logic; "in what order should the UI display this list" is not their job.

**Exceptions** — must be marked with an inline `// arch:db-sort-ok <reason>` comment on the same or immediately preceding line:

- **Pagination tie-breakers.** Stable paging requires deterministic ordering at the SQL boundary. `OrderBy(x => x.Id)` after a primary sort is a tie-breaker, not display ordering.
- **Top-N selectors.** `OrderByDescending(x => x.CreatedAt).Take(10)` is semantically a *selector* — "the 10 most recent" — not a "sort for display." The order is part of the query result definition.
- **Identity-ordered chronological streams.** Audit log, consent records, append-only event streams that are conceptually ordered by their identity column. Reading them out of order makes no sense.

**How to apply:**

- New repo or service method needs ordering for display? Don't add it. Return the materialized list and let the controller / view sort.
- Sorting in a `.cshtml` `@foreach` is fine. Sorting on a view model assembled in the controller is fine. Sorting in a partial that already has the data is fine.
- Existing repo method's `OrderBy` is an exception per the list above? Add `// arch:db-sort-ok <one-line reason>` so the ratchet knows to allow it.
- Find yourself wanting to add a `sortBy` parameter to a repo or service method? That's the smell — push the sort up to the caller.

**Scope of the ratchet:** today's `DisplaySortInControllersRule` scans only `src/Humans.Infrastructure/Repositories/**/*.cs`. The "no sort in Application services" half of the policy is enforced by review, not by the ratchet, so review should still flag service-layer `OrderBy` for display.

**Related:** [`no-linq-at-db-layer`](no-linq-at-db-layer.md) — same shape (data shape leaks across the repo boundary).
