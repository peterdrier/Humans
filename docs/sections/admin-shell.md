<!-- freshness:triggers
  src/Humans.Web/ViewComponents/AdminNavComposition.cs
  src/Humans.Web/ViewComponents/AdminSidebarViewComponent.cs
  src/Humans.Web/ViewComponents/AdminSummaryViewComponent.cs
  src/Humans.Base/Interfaces/ISectionAdminNav.cs
  src/Sections/*/SectionAdminNav.cs
  src/Sections/*/SectionAdminTiles.cs
  src/Humans.Web/Controllers/AdminController.cs
  src/Humans.Web/Views/Shared/_AdminLayout.cshtml
-->

# Admin Shell — Section Invariants

Frame-only section. Provides the shared admin sidebar, breadcrumb, and dashboard skeleton. Owns no tables.

## Concepts

- The **Admin Shell** is the persistent layout wrapper rendered for the admin dashboard and section admin pages: top-nav, left sidebar, breadcrumb, and page container.
- The **Sidebar** is the left navigation panel inside the admin shell. It is divided into named groups; each group contains one or more items. Items and groups are filtered at render time by the current user's roles. Groups are filed by owning section (the section whose `src/Sections/Humans.<Section>/Docs/` covers the feature), not by label similarity. Groups marked `System: true` (AdminOnly plumbing: Google, Agent, Legal, Diagnostics, Settings, Dev, Design, Temp) render below a divider and start collapsed on desktop unless they contain the active page; user toggles persist in `localStorage`. Below 768px the sidebar renders as a two-tier horizontal strip: group chips on top, the selected group's items beneath.
- The **Breadcrumb** is the per-page path strip rendered inside the admin shell header. Each page sets its own breadcrumb via the shared `AdminShell` layout.
- The **Dashboard skeleton** is the top-level `/Admin` landing page. It renders `AdminSummaryViewComponent` (the greeting/strapline plus a tile strip merged from every section's `ISectionAdminTiles` contribution, interleaved with Shell's own presence tiles) and a `chrome-slot` for the `admin-dashboard` slot, into which sections contribute cards (`ISectionChrome`).

## Data Model

This section owns no entities.

## Routing

The `/Admin` route is the shared dashboard. The `AdminLayout.cshtml` layout is selected by `_ViewStart.cshtml` in each admin view folder that uses the shell, including section-owned routes such as `/Debug/*`, `/Profile/*/Admin/*`, and `/Campaigns/Admin/*`. Per-page breadcrumb and page title are set via `ViewData["Title"]` and the `AdminBreadcrumb` view component.

## Actors & Roles

Sidebar groups — operational zone: Tickets, Members, Shifts, Barrios, Cantina, Money, Event Guide, Governance, Audit, Feedback, Messaging; system zone (collapsed by default): Google, Agent, Legal, Diagnostics, Settings, Dev (env-gated to `!IsProduction()`), Design, Temp. Each section contributes its groups via `ISectionAdminNav.Groups()` (`src/Sections/*/SectionAdminNav.cs`); `AdminNavComposition.Compose` merges same-key groups from different sections (e.g. "Tickets", "Money") and sorts by weight. The per-role expected items below are pinned by `tests/e2e/tests/admin-shell.spec.ts` (`sidebarMatrix`).

| Actor | Capabilities |
|-------|--------------|
| Admin | Full access — every group and every item |
| Board | Tickets (Tickets, Onsite roster, Scanner), Members (Humans, Roles, Review), Governance (Voting, Applications), Audit (Audit log), Messaging (Surveys), Google (Resource sync) |
| HumanAdmin | Members (Humans, Roles) |
| TicketAdmin | Tickets (Tickets, Transfer requests, Attendee contacts, Onsite roster, Scanner, Gate terminal, Gate settings) |
| FinanceAdmin | Money (Expense review, Finance, Store catalog, Store summary, Store payments) |
| StoreAdmin | Money (Store catalog, Store summary, Store payments) |
| EventsAdmin | Event Guide (Dashboard, Moderation, Settings, Categories, Venues, Export) |
| CantinaAdmin | Cantina (Roster) |
| ConsentCoordinator | Members (Review) |
| VolunteerCoordinator | Tickets (Early entry), Members (Review), Shifts (Volunteer tracking, Workload, Post-event stats) |
| TeamsAdmin | Google (Resource sync) |
| CampAdmin | Barrios (Overview, Roles, Compliance, Barrio map) |
| NoInfoAdmin | Tickets (Early entry), Shifts (Volunteer tracking, Workload, Post-event stats) |

## Invariants

- The `Admin` top-nav link and the `/Admin` dashboard are gated by `PolicyNames.AnyAdminRole` (14 roles: Admin, Board, HumanAdmin, TeamsAdmin, CampAdmin, TicketAdmin, EventsAdmin, FeedbackAdmin, FinanceAdmin, StoreAdmin, CantinaAdmin, NoInfoAdmin, VolunteerCoordinator, ConsentCoordinator). Concrete admin tools are gated on their section controllers.
- `FeedbackAdmin` is in `AnyAdminRole` but owns no sidebar item since nobodies-collective/Humans#977 made every Feedback screen `AdminOnly`. A holder of only that role therefore reaches the shell and sees an empty sidebar. Dropping it from `AnyAdminRole` is a privilege reduction left undecided by #977.
- Sidebar items are filtered per-item by `IAuthorizationService.AuthorizeAsync`; an item the current user cannot access does not appear in the rendered HTML.
- Sidebar groups whose entire visible-item list is empty do not render.
- The admin shell adds no new authorization policies; it reuses existing `PolicyNames.*` constants defined in the Auth section.
- The `body.admin-shell` CSS class scopes all admin-shell styles — no styles bleed into member-facing pages.

## Negative Access Rules

- A user with no admin-shaped role **cannot** reach the `/Admin` dashboard: `[Authorize(Policy = PolicyNames.AnyAdminRole)]` on `AdminController.Index` rejects them before the shell renders. Section admin actions are individually gated, most by `PolicyNames.AdminOnly`.
- An admin-role user **cannot** see sidebar items they are not authorized for — items are individually gated, not globally shown.

## Triggers

None — this section is a pure rendering surface with no DB writes and no side effects.

## Cross-Section Dependencies

None directly — since nobodies-collective/Humans#1091, the shell names no section. Nav groups (`ISectionAdminNav`), dashboard tiles (`ISectionAdminTiles`) and dashboard cards (`ISectionChrome`, `admin-dashboard` slot) are section-contributed and merged by Shell's composition/rendering code, which reaches no section service directly. The tiles and cards are owned by (and call the service interfaces of) their contributing sections:

- **Users:** `users.total` / `users.profiles` / `users.tickets` tiles (`IUserServiceRead`); the "Preferred language" card.
- **Shifts:** `shifts.coverage` tile and the "Staffing by department" card (`IShiftManagementServiceRead`).
- **Feedback:** `feedback.open` tile, AdminOnly (`IFeedbackServiceRead`).
- **Teams:** `teams.total` tile (`ITeamServiceRead`).
- **Audit Log:** `auditlog.total` tile and the "Recent activity" card (`IAuditViewerService`).
- **Email:** `email.outbox` tile (`IEmailOutboxServiceRead`).
- **Store:** `store.orders` tile, gated to `StoreCatalogAdmin` (`IStoreServiceRead`).
- **Expenses:** `expenses.reports` tile, gated to `FinanceAdminOrAdmin` (`IExpenseReportServiceRead`).
- **Governance:** the "Tier applications" card (`IApplicationServiceRead`); also contributes the Voting sidebar pill's unvoted-application count.
- **Debug:** the "User set membership" (Venn/UpSet) card.

Shell's own contribution is the three presence tiles (Online now / Active 1h / Active 24h, from `IUserActivityTracker`) in `AdminSummaryViewComponent`. All section-owned pieces read through public read-side contracts (`I*ServiceRead` / `I*Contracts`) — the shell holds no repository and writes nothing.

## Architecture

**Owning services:** None — frame only.
**Owned tables:** None.
**Status:** (A) Migrated — greenfield (admin-shell-impl, 2026-04-26).

- The admin shell is implemented as a Razor layout (`Views/Shared/_AdminLayout.cshtml`) plus the `AdminShell` partial/view component for the sidebar.
- **Decorator decision — no caching decorator.** Owns no data.
- **Cross-domain navs:** N/A — owns no entities.
- **Architecture test:** N/A — no service layer to pin. Sidebar authorization is covered by the integration tests for each section's admin pages.
