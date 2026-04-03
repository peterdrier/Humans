# Humans Architecture

Normative macro-level architecture guide for the application.

This document is intentionally opinionated. It describes the architecture that exists now, the direction we are steering it, and the default rules new code must follow.

Code that does not follow these rules should justify the exception in the PR or commit notes.

## Current Architectural Shape

Humans is a layered monolith:

```text
Web             Controllers, Razor views, view models, HTTP concerns
Application     Interfaces, DTOs, request/result contracts
Infrastructure  EF Core, service implementations, jobs, integrations
Domain          Entities, enums, value objects, local invariants
```

That shape is correct for this project and should be preserved.

We are not building:

- microservices
- a generic platform
- a framework for future hypothetical apps
- distributed coordination machinery

We are building one clear, maintainable product.

## Architectural Direction

The main existing smell is boundary leakage, not the top-level structure.

The recurring problem today is that some controllers still do too much:

- direct `HumansDbContext` usage
- direct `SaveChangesAsync()`
- query composition
- workflow orchestration
- business-rule enforcement

Going forward, new code should move in the opposite direction:

- thinner controllers
- clearer service boundaries
- business rules concentrated in services and domain methods
- persistence and integration work kept out of the web layer

## Core Decisions

### 1. Keep the layered monolith

Do not introduce service boundaries, event-driven architectures, or mediator-heavy patterns without a concrete problem that the current layered model cannot handle cleanly.

### 2. Use EF Core directly in infrastructure

`HumansDbContext` is already the data access abstraction for this app.

Default rule:

- use EF Core directly inside infrastructure services

Do not add by default:

- generic repositories
- generic unit-of-work wrappers
- CQRS/MediatR plumbing
- command/query class hierarchies for ordinary app work

Those patterns add indirection here more often than they add clarity.

### 3. Controllers are adapters only

Controllers own:

- routes
- auth attributes
- model binding
- input shape validation tied to HTTP
- choosing view vs redirect vs JSON response
- mapping service results into view models
- user-facing success/error messages

Controllers do not own:

- persistence orchestration
- business workflows
- integration calls that change system state
- cross-entity invariants
- caching

Strict default:

- new controllers should not inject `HumansDbContext`
- new controllers should not call `SaveChangesAsync()`
- new controllers should not contain the main query for a screen beyond trivial glue logic
- new controllers should not read from or write to cache directly

If a controller needs any of those, assume the code belongs in a service unless there is a very specific reason otherwise.

### 4. Services own use cases

A service method should correspond to a real business capability or a coherent read model.

Good examples:

- save a profile
- approve an application
- build a team page
- provision a workspace account
- sync ticket data

Bad examples:

- generic CRUD wrappers with no policy
- catch-all managers
- services that merely bounce through to EF without shaping behavior or a meaningful query contract

When a controller action becomes more than thin request/response glue, move the work into a service.

### 5. Domain owns local invariants

If a rule is inherent to an entity state transition, it belongs on the entity or a domain-adjacent type.

This is already the right pattern for:

- `Application`
- `TeamJoinRequest`
- `ShiftSignup`

Continue that approach.

Do not leave important workflow transitions as ad hoc property mutation if the entity itself can protect the invariant.

### 6. Application defines contracts, not mechanics

`Application` should contain:

- interfaces
- DTOs
- request/result records
- cross-layer contracts

`Application` should not contain:

- MVC concerns
- EF Core details
- `HttpContext`
- Razor view models
- provider SDK types leaking through the contract

### 7. Infrastructure implements persistence and integration

`Infrastructure` owns:

- EF Core queries and writes
- service implementations
- jobs
- integration clients
- cache implementation
- metrics/logging hooks

`Infrastructure` should be organized by business capability first, technical detail second.

## Layer Rules

### Domain

Owns:

- entities
- enums
- value objects
- local calculations
- state transition rules

Must not own:

- EF Core query logic
- HTTP concerns
- configuration access
- logging orchestration
- third-party SDK calls

### Application

Owns:

- use-case contracts
- read/write request shapes
- result shapes crossing boundaries

Must not own:

- persistence mechanics
- view concerns
- startup wiring

### Infrastructure

Owns:

- query composition
- data loading
- writes and `SaveChangesAsync()`
- outbox/integration behavior
- scheduled job execution
- cache population and invalidation

Must not own:

- Razor view models
- route decisions
- page-local UI branching

### Web

Owns:

- controller entry points
- view models
- views
- API formatting
- redirects, status codes, temp-data messaging

Must not own:

- the source of truth for business rules
- database transaction boundaries
- cache ownership

## Read Rules

Default rule:

- read queries belong in services, not controllers

Use:

- `AsNoTracking()` for read-only queries
- projection to DTOs/summary records for list/detail screens
- explicit ordering and filtering

Avoid:

- returning large tracked graphs to drive UI screens
- shaping major read models directly in controllers
- sprinkling ad hoc includes across the web layer

Returning domain entities from a service is acceptable for narrow internal workflows. For page data and reports, prefer shaped results.

## Write Rules

Default rule:

- the service that owns the use case owns persistence

Use services to:

- load required entities
- enforce rules
- mutate state
- write audit/outbox side effects
- call `SaveChangesAsync()`

Avoid:

- controllers mutating entities and flushing directly
- write workflows split between controller and service
- hidden side effects that occur outside the main use-case boundary

If a workflow requires multiple persistence stages, that should be explicit and commented. It is an exception, not the default.

## Transaction Rule

The default transaction boundary is the service method handling the use case.

A controller action should usually call one primary mutation method, and that method should own the write boundary.

Do not make the controller the coordinator of:

- load entity A
- mutate entity B
- call service C
- save twice
- enqueue side effects manually

That is service work.

## Caching Rules

Caching is allowed. Controller-owned caching is not.

Default rule:

- caching belongs in infrastructure or service-level read paths

Controllers must not:

- populate cache entries
- invalidate cache entries
- decide cache lifetimes
- contain fallback logic like "read cache, else query db"

Allowed caching patterns:

- stable read models
- lookup/reference datasets
- expensive aggregate summaries used by multiple entry points

Requirements for any cache:

- clear ownership
- explicit invalidation path
- narrow, named purpose
- correctness if the cache is cold or empty

If a cache can become stale, the write path that makes it stale must own invalidation.

Do not add speculative caching because something "might be slow." At this project scale, clarity beats premature cache spread.

## Integration Rules

External systems stay behind `Application` interfaces and `Infrastructure` implementations.

Do not leak raw provider concerns through multiple layers.

Controller code should talk in product language, not vendor API language.

Non-production stub implementations are preferred over scattered environment checks in business logic.

## Authorization Rules

Authorization belongs in two places:

1. Web boundary protection for routes/pages/API endpoints.
2. Service/domain enforcement when violating the rule would create invalid state or bypass workflow policy.

Do not rely on hidden buttons or view-only checks for anything important.

## Time and Configuration Rules

For time:

- use `IClock`
- use NodaTime types
- do not introduce new workflow logic based on `DateTime.UtcNow`

For configuration:

- bind and register settings at startup
- keep configuration access centralized
- do not scatter raw environment-variable reads through feature code unless the existing pattern already requires it at composition time

## Testing Rules

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

Exceptions are allowed, but the burden is on the exception.

An exception should state:

- which default rule it is breaking
- why the normal pattern is worse here
- why the exception is contained

Weak reasons:

- "it was faster"
- "the controller already had the db context"
- "making a service felt heavy"

Stronger reasons:

- transitional refactor with a clear follow-up path
- truly trivial admin/diagnostic behavior where introducing a new service would add noise without reducing risk
- staged persistence required by external semantics, with comments explaining why

## Smell Checklist

Stop and reconsider when a change introduces any of these:

- controller injects `HumansDbContext`
- controller calls `SaveChangesAsync()`
- controller owns cache logic
- controller contains the only enforcement of a business rule
- query logic for a major screen lives in the web layer
- a new abstraction exists only to wrap EF mechanically
- a provider SDK type leaks across multiple layers
- a cache is added without a clear invalidation owner
- a job re-implements a workflow that should be in a service

## Rendering Rules

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

## Direction of Travel

We do not need a rewrite.

We do need steady pressure toward:

- no new db-heavy controllers
- no new controller-owned caching
- fewer mixed controller/service workflows
- richer domain methods for workflow-heavy entities
- clearer service-owned read models
- tests that move with behavior changes

If a change makes the business rules more local, the web layer thinner, and the write/read ownership clearer, it is moving in the right direction.
