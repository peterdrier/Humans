<!-- freshness:flag-on-change
  Cross-cutting conventions (domain invariants, transaction boundary, caching, authorization, time, configuration, rendering, testing, exceptions). Flag if any architectural pattern shift in src/** invalidates a stated convention.
-->

# App Conventions

Cross-cutting conventions for how we build this app.

Persistence, service boundaries, caching, repositories, layering, and authorization rules live in [`design-rules.md`](design-rules.md). This document covers everything else — domain invariants, transactions, integration, time and configuration, rendering, testing, exceptions, and the smell checklist used during code review.

> **History:** This file was originally `docs/architecture.md`, a broader architecture position paper written 2026-04-03. As of 2026-04-15 the persistence / service-layer content was migrated into `design-rules.md` (which now holds the repository, store, and decorator doctrine), and this file was trimmed to the cross-cutting conventions that remain. See git history for the original.

## Domain Owns Local Invariants

If a rule is inherent to an entity state transition, it belongs on the entity or a domain-adjacent type.

This is already the right pattern for:

- `Application`
- `TeamJoinRequest`
- `ShiftSignup`

Continue that approach.

Do not leave important workflow transitions as ad hoc property mutation if the entity itself can protect the invariant.

## Transaction Boundary

The default transaction boundary is the service method handling the use case.

A controller action should usually call one primary mutation method, and that method should own the write boundary — including repository writes, store updates, audit log calls, and any outbox entries that must commit atomically with the primary mutation.

Do not make the controller the coordinator of:

- load entity A
- mutate entity B
- call service C
- save twice
- enqueue side effects manually

That is service work.

## Caching

See [`design-rules.md`](design-rules.md) §4–§5 for the structured caching pattern (store + decorator).

Two controller-level rules still apply at this layer:

- Controllers must not populate, invalidate, or read cache entries for domain data.
- Controllers must not contain fallback logic like "read cache, else query db." That belongs in a service or its caching decorator.

Do not add speculative caching because something "might be slow." At this project scale, clarity beats premature cache spread.

## Authorization

See [`design-rules.md`](design-rules.md) §11 for the resource-based authorization pattern and handler inventory.

Two general rules apply at this layer:

1. Web boundary protection for routes/pages/API endpoints via `[Authorize]` attributes or resource-based authorization handlers.
2. Service/domain enforcement when violating the rule would create invalid state or bypass workflow policy — but service methods are otherwise auth-free; they trust the caller.

Do not rely on hidden buttons or view-only checks for anything important.

## Action Naming

Controller action names should describe the operation in the controller's domain. The audit at [`controller-architecture-audit.md`](../controller-architecture-audit.md) flags actions that violate these heuristics.

- **`Index` is a listing of the controller's resource.** If the action does something else (a single dashboard, a settings page, a one-off form), pick a more specific name.
- **Don't repeat the controller name in the action.** `TeamController.TeamDetail` reads as `Team/TeamDetail` — the `Team` prefix is redundant. Use `TeamController.Detail` (`Team/Detail`).
- **Avoid bare plural-noun action names that collide with the controller.** `TeamController.Teams` is ambiguous; `Index` or `List` is clearer.
- **Avoid generic verbs.** `View`, `Show`, `Process`, `Handle` say nothing. Pick a verb that describes the operation: `Approve`, `Reject`, `Withdraw`, `Resync`, `Backfill`.
- **Use the conventional form-handler pattern.** `Create` (GET form + POST submit), `Edit` (GET form + POST submit), `Delete` (POST), `Confirm`, `Cancel` are the established verbs across this codebase. Match them when the operation is the same shape.

These are heuristics, not laws — a clearer name that violates one of them beats a literal-conformance one. The audit doc flags suspected violations; the rename is a judgment call.

## Integration

External systems stay behind `Humans.Application` interfaces and `Humans.Infrastructure` implementations.

Do not leak raw provider concerns through multiple layers.

Controller code should talk in product language, not vendor API language.

Non-production stub implementations are preferred over scattered environment checks in business logic.

## Time and Configuration

For time:

- use `IClock`
- use NodaTime types (`Instant`, `LocalDate`, etc.)
- do not introduce new workflow logic based on `DateTime.UtcNow`

For configuration:

- bind and register settings at startup
- keep configuration access centralized
- do not scatter raw environment-variable reads through feature code unless the existing pattern already requires it at composition time

## Rendering

Server-rendered Razor is the default rendering approach for all pages.

Default rule:

- page content is rendered server-side using Razor views, tag helpers, and view components
- slow data loads use the partial-via-AJAX pattern: render the page frame server-side, load the slow section by fetching a Razor partial from an AJAX call

Razor provides:

- compile-time type safety
- tag helpers and `asp-*` route generation
- automatic HTML encoding (no manual `escapeHtml`)
- localization via `IStringLocalizer`
- view components for reusable data-fetching UI
- authorization tag helpers for role-based visibility

Do not use client-side `fetch()` + JavaScript DOM construction to build page content when Razor can render the same output. That pattern requires manual HTML escaping, duplicated rendering logic, projection DTOs solely for JSON serialization, and string-based URL construction that breaks on route constraint changes.

### Valid exceptions

Client-side JavaScript with `fetch()` is appropriate for:

- **Autocomplete/search inputs** that need instant feedback on keystrokes (profile search, member search, volunteer search, shift volunteer search)
- **Dynamic form field population** that responds to parent field changes (team Google resource dropdown)
- **Progressive enhancement** for inline actions that avoid full page reloads (notification dismiss/mark-read, feedback detail panel loading)
- **Utility behaviors** that are not page content (timezone detection, notification popup, profile popover on hover)

These patterns use `fetch()` to enhance an already server-rendered page, not to replace server rendering entirely.

### Current exceptions list

All pages are server-rendered with Razor. The following use `fetch()` for the specific justified purposes listed above:

| File | Purpose | Exception type |
|------|---------|----------------|
| `_HumanSearchInput.cshtml` | Profile autocomplete | Search input |
| `_MemberSearchScript.cshtml` | Member search autocomplete | Search input |
| `_VolunteerSearchScript.cshtml` | Volunteer search autocomplete | Search input |
| `_TeamGoogleAndParentFields.cshtml` | Google resource dropdown on team change | Dynamic form field |
| `ShiftAdmin/Index.cshtml` | Shift volunteer search + tag creation | Search input + inline action |
| `Notification/Index.cshtml` | Dismiss/mark-read without reload | Progressive enhancement |
| `Feedback/Index.cshtml` | Master-detail panel loading | Progressive enhancement |
| `Google/Sync.cshtml` | Tab content loaded via Razor partial (slow Google API) | Partial-via-AJAX |
| `site.js` | Timezone, notification popup, profile popover | Utility |

When adding a new page that needs client-side data loading, add it to this list with justification. If a page has no entry here, it must be server-rendered.

## Testing

This project should test behavior primarily at the service boundary.

Default expectations by change type:

- business rule change: add or update a service test
- controller-only routing/view change: add integration coverage if routing/auth/model binding matters
- startup/filter/auth wiring change: add integration coverage
- critical end-user journey or repeated regression path: add or update e2e coverage
- bug fix: add the narrowest regression test that would have caught it

A change that alters workflow behavior without any test update should be unusual and should justify why.

Preferred test order:

1. Domain test if the rule lives on an entity.
2. Service test if the rule spans data access and orchestration.
3. Integration test if HTTP/auth/startup behavior matters.
4. E2E only when cross-page behavior is the thing being protected.

Do not default to e2e when a service test would cover the rule more directly.

## Exception Rule

Exceptions to any rule in this doc or in `design-rules.md` are allowed, but the burden is on the exception.

An exception should state:

- which default rule it is breaking
- why the normal pattern is worse here
- why the exception is contained

Weak reasons:

- "it was faster"
- "the controller already had the db context"
- "making a service felt heavy"
- "adding a repository felt like over-engineering"

Stronger reasons:

- transitional refactor with a clear follow-up path
- truly trivial admin/diagnostic behavior where introducing a new service would add noise without reducing risk
- staged persistence required by external semantics, with comments explaining why

## Smell Checklist

Stop and reconsider when a change introduces any of these:

**Web layer smells:**
- controller injects `HumansDbContext`
- controller calls `SaveChangesAsync()`
- controller owns cache logic
- controller contains the only enforcement of a business rule
- query logic for a major screen lives in the web layer

**Service / persistence smells:**
- a new service placed in `Humans.Infrastructure/Services/` instead of `Humans.Application/Services/`
- a service that injects `HumansDbContext` directly instead of going through its owning repository
- a service that injects another domain's repository or store (should call the other domain's `I{Section}Service` interface instead)
- a `.Include()` that navigates across a domain boundary (Profile → User, Team → Profile, Camp → Profile, etc.)
- a repository method that takes or returns another domain's type
- a repository method that returns `IQueryable<T>`
- inline `IMemoryCache.GetOrCreateAsync` inside a service method instead of a store + decorator
- a cache is added without a clear invalidation owner
- a cross-domain nav property being added to an entity (e.g., `TeamMember.User`)

**Cross-cutting smells:**
- a provider SDK type leaks across multiple layers
- a job re-implements a workflow that should be in a service
- audit logging implemented as a decorator instead of an in-service call (audit needs actor + before/after + same transaction)
