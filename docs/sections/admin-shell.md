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

Sidebar groups: Operations, Members, Money, Governance, Integrations, Agent, People data, Diagnostics (and Dev — env-gated to `!IsProduction()`). Source of truth is `AdminNavTree.cs`; the per-role expected items below are pinned by `tests/e2e/tests/admin-shell.spec.ts` (`sidebarMatrix`).

| Actor | Capabilities |
|-------|--------------|
| Admin | Full access — every group and every item |
| Board | Operations (Tickets, Scanner), Members (Humans, Review), Governance (Voting, Board) |
| HumanAdmin | Members (Humans) |
| TicketAdmin | Operations (Tickets, Scanner) |
| FinanceAdmin | Money (Finance, Store catalog) |
| StoreAdmin | Money (Store catalog) |
| ConsentCoordinator | Members (Review) |
| VolunteerCoordinator | Members (Review) |
| TeamsAdmin / CampAdmin / FeedbackAdmin / NoInfoAdmin | Reach the `/Admin` dashboard (member of `AnyAdminRole`) but have no sidebar items in the current tree — they act via the dashboard tiles and any direct links from member-facing pages |

## Invariants

- The `Admin` top-nav link and the `/Admin` dashboard are gated by `PolicyNames.AnyAdminRole` (12 roles: Admin, Board, HumanAdmin, TeamsAdmin, CampAdmin, TicketAdmin, FeedbackAdmin, FinanceAdmin, StoreAdmin, NoInfoAdmin, VolunteerCoordinator, ConsentCoordinator). Other actions on `AdminController` remain `PolicyNames.AdminOnly`.
- Sidebar items are filtered per-item by `IAuthorizationService.AuthorizeAsync`; an item the current user cannot access does not appear in the rendered HTML.
- Sidebar groups whose entire visible-item list is empty do not render.
- The admin shell adds no new authorization policies; it reuses existing `PolicyNames.*` constants defined in the Auth section.
- The `body.admin-shell` CSS class scopes all admin-shell styles — no styles bleed into member-facing pages.

## Negative Access Rules

- A user with no admin-shaped role **cannot** reach the `/Admin` dashboard — `[Authorize(Policy = PolicyNames.AnyAdminRole)]` on `AdminController.Index` rejects them before the shell renders. Non-Index actions are individually gated, most by `PolicyNames.AdminOnly`.
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
