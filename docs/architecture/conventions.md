<!-- freshness:triggers
  src/Humans.Web/Controllers/**
  src/Humans.Web/ViewComponents/**
  src/Humans.Web/Views/**
  src/Humans.Interfaces/ViewComponents/**
  src/Humans.Interfaces/Views/**
  src/Sections/**
  Directory.Build.props
-->
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

Cross-repository orchestrations that must commit atomically wrap the calls in an ambient `TransactionScope` (`TransactionScopeAsyncFlowOption.Enabled`, `ReadCommitted`) — e.g. `AccountMergeService.RejectAsync`'s paired pending-email delete + request-status update. Each repository still creates its own short-lived `DbContext` via `IDbContextFactory`; Npgsql auto-enlists those connections in the ambient scope, so the writes commit or roll back together without sharing a `DbContext` across repositories.

<!-- wheat: docs/superpowers/plans/2026-06-06-account-merge-consolidation.md §Deviations from spec -->
Not every multi-step cross-repository orchestration needs one, though. `AccountMergeService.MergeAsync` (the account-merge fold) deliberately runs its steps — the `IUserMerge` fan-out, then the pending-email settle, then the tombstone write — with no wrapping scope: each step commits independently and is safely retryable (re-applying a done step is a no-op), and the tombstone write is the single observable commit point. Reach for this ordered-no-transaction shape when every step is idempotent and a clear final commit point exists; reach for `TransactionScope` when the steps are not independently safe to retry.

## Cross-Section FK Columns

<!-- wheat: docs/plans/2026-06-13-q3-transition-plan.md §FK-cut carve-out -->
A bare `Guid` cross-section FK column gets no index for free. EF auto-created `IX_<table>_<column>` for each cross-section relationship and removed it along with the constraint in nobodies-collective/Humans#992, so any cross-section FK column a query filters or sorts on needs an explicit `HasIndex(...)` in its configuration — `CampMemberConfiguration`, `CampaignGrantConfiguration`, `BudgetAuditLogConfiguration` and `FeedbackReportConfiguration` are the pattern. A missing index here is a production regression that no test surfaces.

## Caching

See [`design-rules.md`](design-rules.md) §15 for the structured caching pattern (caching decorator owning a `TrackedCache` / `ConcurrentDictionary`; §4–§5 there describe the retired store + decorator predecessor).

Two controller-level rules still apply at this layer:

- Controllers must not populate, invalidate, or read cache entries for domain data.
- Controllers must not contain fallback logic like "read cache, else query db." That belongs in a service or its caching decorator.

Do not add speculative caching because something "might be slow." At this project scale, clarity beats premature cache spread.

## Authorization

See [`design-rules.md`](design-rules.md) §11 for the resource-based authorization pattern and handler inventory.

Two general rules apply at this layer:

1. Web boundary protection for routes/pages/API endpoints via `[Authorize]` attributes or resource-based authorization handlers.
2. Service/domain enforcement when violating the rule would create invalid state or bypass workflow policy — but service methods are otherwise auth-free; they trust the caller. The only auth exception is the §11 full-Admin destructive-delete guard.

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

External systems stay behind an interface in the owning section's `Contracts` (project or folder) with the implementation in that section's `Services/` — `IHoldedClient` in `Humans.Holded.Contracts`, the client in `Humans.Holded/Services/`; same shape for Google, Stripe, TicketTailor, Mailer and Email.

Do not leak raw provider concerns through multiple layers.

Controller code should talk in product language, not vendor API language.

Non-production stub implementations are preferred over scattered environment checks in business logic.

## Versioning


Version strings are derived from git tags via [MinVer](https://github.com/adamralph/minver) at build time — there is no hardcoded `Version` / `FileVersion` / `AssemblyVersion` in `Directory.Build.props`. Tag prefix is `v` (e.g., `v0.8.0`). Between tags MinVer emits `0.8.1-alpha.0.N` where N is commits-since-tag. The `+<hash>` suffix on `InformationalVersion` comes from the existing `SourceRevisionId` MSBuild target.

`_VersionInfo.cshtml` still exists but is **rendered nowhere** — `_Layout.cshtml`'s footer was removed ("About and Privacy are in the avatar dropdown menu"), and no view includes the partial. The only version string the app surfaces today is the 8-char commit hash, as a tooltip on the navbar brand, and only for full Admins. Production releases are still cut as GitHub Releases — `gh release create vX.Y.Z -R nobodies-collective/Humans --generate-notes` after merging to upstream — but the reason is the tag MinVer reads and the generated changelog, **not** an in-app link: there is no longer a footer pointing at the release page. A bare `git tag` still isn't enough, because it produces no release notes.

## Time and Configuration

For time:

- use `IClock`
- use NodaTime types (`Instant`, `LocalDate`, etc.)
- do not introduce new workflow logic based on `DateTime.UtcNow`

For configuration:

- bind and register settings at startup
- keep configuration access centralized
- do not scatter raw environment-variable reads through feature code unless the existing pattern already requires it at composition time

## Date/Time Formatting

Date/time format strings live in **one home**: `Humans.Application.Extensions.DateFormattingExtensions`. Render dates by calling a named method on the home — `ToDate` / `ToDateTime` / `ToWeekdayDayMonth` for culture display, `ToInvariantDate` / `ToInvariantTimestamp` / `ToIso8601` for machine output. For an `Instant` in a request/view, the ambient overloads in `Humans.UI.Extensions.DateTimeDisplayExtensions` resolve the user's timezone from session.

Never inline a custom format string at the call site (`ToString("d MMM yyyy")`, interpolation `{x:MMM d}`, NodaTime `*Pattern.Create("…")`). If no method fits, add one to the home rather than hand-rolling a literal. Enforced by analyzer **HUM0030** (build error in production assemblies). See [`memory/architecture/datetime-format-single-home.md`](../../memory/architecture/datetime-format-single-home.md) and [`memory/code/datetime-display-formatting.md`](../../memory/code/datetime-display-formatting.md).

## Rendering

Server-rendered Razor is the default rendering approach for all pages.

<!-- wheat: docs/plans/2026-06-11-q3-ui-refactoring-plan.md §Strategic call -->
A full SPA/Blazor rewrite was considered and rejected. The product is content- and form-centric on a single server, which is what server-rendered MVC is for; an SPA adds a build pipeline, a second state model, and an API layer the layering rules do not want. The consolidation surface is the existing ViewComponent layer (the section-agnostic ones in `src/Humans.Interfaces/ViewComponents`, the rest in `src/Humans.Web/ViewComponents` or the owning section project; stable `<vc:>` call sites either way) — a component's template can be redesigned without touching its call sites, so a visual overhaul does not require re-opening the view corpus.

Default rule:

- page content is rendered server-side using Razor views, tag helpers, and view components
- slow data loads use the partial-via-AJAX pattern: render the page frame server-side, load the slow section by fetching a Razor partial from an AJAX call

Razor provides:

- compile-time type safety
- tag helpers and `asp-*` route generation
- automatic HTML encoding (no manual `escapeHtml`)
- localization via `IStringLocalizer`
- view components for reusable data-fetching UI

### View Components vs Partials


A **view component fetches its own data**. A **partial is pure presentation** and takes a typed model. Choose by data-source ownership, not by reuse count.

Promote a partial to a view component when any of these apply:

- The controller has to fetch data *solely* to pass it through to the partial (the partial's data fetch is a controller concern leaking upward).
- The same data assembly is duplicated across two or more controllers that render the partial.
- The partial embeds an inline `<script>` block that would duplicate if rendered twice on one page.
- The partial needs to be reusable on pages whose controllers have no reason to know about its data domain (e.g. shift cards on the homepage *and* on the dedicated shifts page).

Keep as a partial when the rendering is genuinely pure (badges, alerts, validation script tags, language chooser, role/dietary badges driven from a model already loaded by the page).

**Caching at this scale:** view components must **not** inject or use `IMemoryCache` — a documented project rule ([`../../memory/code/viewcomponent-no-cache.md`](../../memory/code/viewcomponent-no-cache.md)), upheld by review rather than tooling: a dedicated analyzer is described in [`roslyn-analysis.md`](roslyn-analysis.md) §"View components may not inject `IMemoryCache`" but is **not yet built** (`Current coverage: none`). When a view component renders an aggregate count that warrants a short cache, the cache lives inline in the owning service (1–2 minute TTL) and that service owns invalidation on writes that affect the aggregate; the view component is a thin pass-through. (`NavBadgesViewComponent` works this way — the voting / issue counts cache inside `ApplicationDecisionService` / `IssuesService`. The feedback count follows the same pattern, cached inside `FeedbackService`, but since #977 it is rendered by `AdminNavTree` via `PillCounts.FeedbackQueue` rather than by `NavBadgesViewComponent`, which no longer has a `feedback` queue.)

**Conventions:**

- Class: `{Name}ViewComponent.cs` under `ViewComponents/` — of `Humans.Interfaces` if the component is section-agnostic, of the owning section project if it is not, `Humans.Web` otherwise. Section-project view components are discovered by `SectionViewComponentFeatureProvider`.
- View: `Views/Shared/Components/{Name}/Default.cshtml`
- ViewModel: `{Name}ViewModel.cs` under `Models/`
- Invocation: `<vc:{kebab-name} param="…">` tag helper or `@await Component.InvokeAsync("{Name}", new { param = value })`
- Responsive table+card pairs render both layouts and toggle via `d-none d-md-block` / `d-md-none` rather than branching per device.
- authorization tag helpers for role-based visibility

Do not use client-side `fetch()` + JavaScript DOM construction to build page content when Razor can render the same output. That pattern requires manual HTML escaping, duplicated rendering logic, projection DTOs solely for JSON serialization, and string-based URL construction that breaks on route constraint changes.

### Valid exceptions

Client-side JavaScript with `fetch()` is appropriate for:

- **Autocomplete/search inputs** that need instant feedback on keystrokes (profile search, member search, volunteer search, shift volunteer search)
- **Dynamic form field population** that responds to parent field changes (team Google resource dropdown)
- **Progressive enhancement** for inline actions that avoid full page reloads (notification dismiss/mark-read, feedback detail panel loading)
- **Utility behaviors** that are not page content (timezone detection, notification popup, profile popover on hover)
- **Interactive maps** whose whole surface is a client-rendered canvas driven by a JSON API (the city-planning barrio, container and overview maps)

These patterns use `fetch()` to enhance an already server-rendered page, not to replace server rendering entirely.

### Current exceptions list

All pages are server-rendered with Razor. The following use `fetch()` for the specific justified purposes listed above:

| File | Purpose | Exception type |
|------|---------|----------------|
| `Humans.Interfaces/Views/Shared/Components/HumanSearch/Default.cshtml` (`<vc:human-search>`) | Person picker (inline autocomplete) — canonical inline pattern, see `memory/architecture/person-search.md` | Search input |
| `Sections/Humans.Users/Views/Shared/_HumanSearchResults.cshtml` | Person search results (page-style cards) — canonical page pattern, see `memory/architecture/person-search.md` | Search results |
| `Humans.Interfaces/Views/Shared/_VolunteerSearchScript.cshtml` | Volunteer search autocomplete (shift-volunteer, exempt from person-search consolidation) | Search input |
| `Humans.Teams/Views/Shared/_TeamGoogleAndParentFields.cshtml` | Google resource dropdown on team change | Dynamic form field |
| `Humans.Teams/Views/TeamAdmin/Roles.cshtml` | Role-grid save without reload | Progressive enhancement |
| `Humans.Shifts/Views/ShiftAdmin/Index.cshtml` | Shift volunteer search + tag creation | Search input + inline action |
| `Humans.Shifts/wwwroot/js/shifts.js` | Day-signup toggle without reload | Progressive enhancement |
| `Humans.Notifications/Views/Notifications/Index.cshtml` | Dismiss/mark-read without reload | Progressive enhancement |
| `Humans.Feedback/Views/Feedback/Index.cshtml` | Master-detail panel loading | Progressive enhancement |
| `Humans.Issues/Views/Issues/Index.cshtml` | Master-detail panel loaded via Razor partial (`?partial=true`) | Partial-via-AJAX |
| `Humans.GoogleIntegration/Views/Google/Sync.cshtml` | Tab content loaded via Razor partial (slow Google API) | Partial-via-AJAX |
| `Humans.Scanner/wwwroot/js/scanner/tickets.js` (`Views/Scanner/Tickets.cshtml`) | Ticket card loaded via Razor partial (`_TicketCard`) on barcode hit | Partial-via-AJAX |
| `Humans.Gate/wwwroot/js/gate/gate.js` (`Views/Gate/Index.cshtml`) | Verdict card loaded via Razor partial (`_VerdictCard`) on barcode scan; decision POST returns the final card | Partial-via-AJAX |
| `Humans.Users/Views/Profile/Edit.cshtml` | Burner-name collision count on keystroke | Search input |
| `Humans.Users/Views/Profile/CommunicationPreferences.cshtml` | Per-preference toggle POST without reload | Progressive enhancement |
| `Humans.Web/Views/Guest/CommunicationPreferences.cshtml` | Same toggles on the tokenless guest page | Progressive enhancement |
| `Humans.Web/Views/Shared/Components/HelpWidget/Default.cshtml` (`<vc:help-widget>`) | In-place feedback submit without leaving the page | Progressive enhancement |
| `Humans.Agent/wwwroot/js/agent/widget.js` | Streamed answer from `/Agent/Ask` (SSE) | Progressive enhancement |
| `Humans.CityPlanning/wwwroot/js/city-planning/**` | Barrio, container and overview maps read and write `/api/city-planning/*` | Interactive map |
| `Humans.Web/wwwroot/js/site.js` | Timezone, notification popup, profile popover | Utility |

Paths are relative to `src/` (`src/Sections/` for the section projects).

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
- controller injects an application `DbContext` (any per-section context)
- controller calls `SaveChangesAsync()`
- controller owns cache logic
- controller contains the only enforcement of a business rule
- query logic for a major screen lives in the web layer

**Service / persistence smells:**
- a new service placed in `Humans.Web/Services/` instead of the owning section project's `Services/`
- a service that injects an application `DbContext` (any per-section context) directly instead of going through its owning repository
- a service that injects another domain's repository (should call the other domain's `I{Section}ServiceRead` interface instead)
- a `.Include()` that navigates across a domain boundary (Profile → User, Team → Profile, Camp → Profile, etc.)
- a repository method that takes or returns another domain's type
- a repository method that returns `IQueryable<T>`
- inline `IMemoryCache.GetOrCreateAsync` caching canonical domain data inside a service method instead of the §15 caching decorator (short-TTL request-acceleration counts are the sanctioned exception)
- a cache is added without a clear invalidation owner
- a cross-domain nav property being added to an entity (e.g., `TeamMember.User`)

**Cross-cutting smells:**
- a provider SDK type leaks across multiple layers
- a job re-implements a workflow that should be in a service
- audit logging implemented as a decorator instead of an in-service call (audit needs actor + before/after + same transaction)
