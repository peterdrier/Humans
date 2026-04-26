# Admin Shell — Section Invariants

Frame-only section. Provides the shared admin sidebar, breadcrumb, and dashboard skeleton. Owns no tables.

## Concepts

- The **Admin Shell** is the persistent layout wrapper rendered for every `/Admin/*` page: top-nav, left sidebar, breadcrumb, and page container.
- The **Sidebar** is the left navigation panel inside the admin shell. It is divided into named groups; each group contains one or more items. Items and groups are filtered at render time by the current user's roles.
- The **Breadcrumb** is the per-page path strip rendered inside the admin shell header. Each page sets its own breadcrumb via the shared `AdminShell` layout.
- The **Dashboard skeleton** is the top-level `/Admin` landing page. It aggregates summary stats from multiple sections (humans in review, open feedback, pending shifts, recent audit events) via service calls.

## Data Model

This section owns no entities.

## Routing

The admin shell applies to all routes under `/Admin`. The `AdminLayout.cshtml` layout is selected via `_ViewStart.cshtml` for the `Admin` area. Per-page breadcrumb and page title are set via `ViewData["Title"]` and the `AdminBreadcrumb` view component.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Admin | Full access to all sidebar groups and items |
| Board | Access to Governance and Users sidebar groups |
| HumanAdmin | Access to Users, Profiles, and Onboarding sidebar groups |
| FinanceAdmin | Access to Budget sidebar group |
| TicketAdmin | Access to Tickets sidebar group |
| TeamsAdmin | Access to Teams sidebar group |
| CampAdmin | Access to Camps sidebar group |
| FeedbackAdmin | Access to Feedback sidebar group |
| NoInfoAdmin | Access to Users sidebar group (read-only admin view) |
| VolunteerCoordinator | Access to Onboarding review queue sidebar item |
| ConsentCoordinator | Access to Onboarding consent review sidebar item |

## Invariants

- The `Admin` top-nav link is visible only to users who hold at least one admin-shaped role (enforced by `PolicyNames.AdminOnly` on the `[Authorize]` attribute on the `AdminController` base or area filter).
- Sidebar items are filtered per-item by `IAuthorizationService.AuthorizeAsync`; an item the current user cannot access does not appear in the rendered HTML.
- Sidebar groups whose entire visible-item list is empty do not render.
- The admin shell adds no new authorization policies; it reuses existing `PolicyNames.*` constants defined in the Auth section.
- The `body.admin-shell` CSS class scopes all admin-shell styles — no styles bleed into member-facing pages.

## Negative Access Rules

- A user with no admin-shaped role **cannot** reach any `/Admin` route — existing `[Authorize(Policy = PolicyNames.AdminOnly)]` enforcement applies at the controller/area level before the shell renders.
- An admin-role user **cannot** see sidebar items they are not authorized for — items are individually gated, not globally shown.

## Triggers

None — this section is a pure rendering surface with no DB writes and no side effects.

## Cross-Section Dependencies

- **Profiles:** `IProfileService` — humans-in-review count for dashboard stat tile.
- **Onboarding:** `IOnboardingService` — pending consent review count for dashboard stat tile.
- **Feedback:** `IFeedbackService` — open report count for dashboard stat tile.
- **Shifts:** `IShiftManagementService` — pending shift signup count for dashboard stat tile.
- **Audit Log:** `IAuditLogService` — recent audit entries for dashboard activity feed.
- **Admin Dashboard:** `IAdminDashboardService` — aggregated stat DTO for the dashboard landing page. Reads only — no writes.

## Architecture

**Owning services:** None — frame only.
**Owned tables:** None.
**Status:** (A) Migrated — greenfield (admin-shell-impl, 2026-04-26).

- The admin shell is implemented as a Razor layout (`Views/Shared/_AdminLayout.cshtml`) plus the `AdminShell` partial/view component for the sidebar.
- **Decorator decision — no caching decorator.** Owns no data.
- **Cross-domain navs:** N/A — owns no entities.
- **Architecture test:** N/A — no service layer to pin. Sidebar authorization is covered by the integration tests for each section's admin pages.
