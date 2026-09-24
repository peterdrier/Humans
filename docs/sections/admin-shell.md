<!-- freshness:triggers
  src/Humans.Base/ViewComponents/AdminNavComposition.cs
  src/Humans.Web/ViewComponents/AdminSidebarViewComponent.cs
  src/Humans.Web/ViewComponents/AdminTabsViewComponent.cs
  src/Humans.Base/ViewComponents/AdminBreadcrumbViewComponent.cs
  src/Humans.Web/ViewComponents/AdminSummaryViewComponent.cs
  src/Humans.Base/Interfaces/ISectionAdminNav.cs
  src/Sections/*/SectionAdminNav.cs
  src/Sections/*/SectionAdminTiles.cs
  src/Humans.Web/Controllers/AdminController.cs
  src/Humans.Web/Views/Shared/_AdminLayout.cshtml
  src/Humans.Web/Views/_ViewStart.cshtml
-->

# Admin Shell — Section Invariants

Frame-only section. Provides the shared admin sidebar, tab strip, breadcrumb, and dashboard skeleton. Owns no tables.

## Concepts

- The **Admin Shell** is the persistent layout wrapper rendered for the admin dashboard and section admin pages: top-nav, left sidebar, breadcrumb, tab strip, and page container.
- The **Sidebar** is the left navigation panel inside the admin shell: a pinned "Dashboard" row (`AnyAdminRole` holders only, the dashboard's own gate), then one row per **group**, alphabetical. A group is named after its owning section (the section whose `src/Sections/Humans.<Section>/Docs/` covers the feature); a section contributing into another's group names that group's label (Feedback into "Issues"). A row links to the group's first item the user can see, shows that item's icon, and carries the sum of the group's pill counts. Below 768px the rows render as one horizontally scrolling strip.
- The **Tab strip** renders above the breadcrumb on every page of a group: one tab per visible item of the current group, with its own pill. It is hidden when the group has one visible item or fewer.
- The **Breadcrumb** reads *Group / Page / Subpage*. The page is the nav item the route resolves to (`AdminNavComposition.Locate`): an exact controller+action match, else the item named by `ViewData["AdminNavParent"]` (`"Action"` or `"Controller/Action"`), else the first item on the same controller. A page whose label equals its group's renders the label once. A subpage shows its `ViewData["Title"]` last, or, when the view defines a `Crumbs` Razor section, that section's children instead (the last is the current crumb). A route that resolves to no item shows only its title. Admin views carry no in-page Bootstrap breadcrumb of their own.
- The **Dashboard skeleton** is the top-level `/Admin` landing page. It renders `AdminSummaryViewComponent` (the greeting/strapline plus a tile strip merged from every section's `ISectionAdminTiles` contribution, interleaved with Shell's own presence tiles) and a `chrome-slot` for the `admin-dashboard` slot, into which sections contribute cards (`ISectionChrome`).

## Data Model

This section owns no entities.

## Routing

The `/Admin` route is the shared dashboard. One rule picks the layout for every page, in the Shell's root `Views/_ViewStart.cshtml` (`AdminNavComposition.IsAdminPage`): a page whose endpoint is gated by a policy beyond `AppAccess` (its `[Authorize(Policy = …)]` metadata), on the dashboard or on a controller in the admin nav, gets `_AdminLayout`; every other page gets `_Layout`. The decision is per page, not per user: a page members share (plain `[Authorize]`, `AppAccess`, or an imperative check inside the action, as on `/Shifts`, `/Issues`, `/Expenses/Review` and survey authoring) keeps the member layout for everyone, admins included, and a restricted page is in the shell for whoever passed its gate (a team coordinator on `/Shifts/Dashboard`, the gate terminal on `/Scanner`). `_ViewStart` runs before the view sets `ViewData["AdminNavParent"]`, so the controller alone places a page: one whose controller has no nav item stays out of the shell. Sections declare no admin layout of their own. The shell hides a page's own Bootstrap breadcrumb (`nav[aria-label=breadcrumb]`), since its crumb replaces it. Exceptions, each a view that sets `Layout` itself: the full-screen City Planning maps (`/CityPlanning`, `BarrioMap`, `ContainerMap`) stay on `_Layout`, the gate kiosk uses `_GateLayout` with `/Gate/Admin` opting back into `_AdminLayout`, and print views use none. The breadcrumb and tab strip are resolved from the route by the `AdminBreadcrumb` and `AdminTabs` view components; a page adds only `ViewData["Title"]`, plus `ViewData["AdminNavParent"]` or a `Crumbs` section when it is a subpage.

## Actors & Roles

Sidebar groups, alphabetical: Agent, Audit, Backdoor, Barrios, Budget, Campaigns, Cantina, City Planning, Consent, Debug, Development (env-gated to `IsDevelopment()`), Early Entry, Email, Events, Expenses, Finance, Gate, Google, Governance, Holded, Issues, MailerLite, Onboarding, Rideshare, Scanner, Settings, Shifts, Store, Surveys, Tickets, Users, Workgroups. Each section contributes its groups via `ISectionAdminNav.Groups()` (`src/Sections/*/SectionAdminNav.cs`); `AdminNavComposition.Compose` merges same-label groups from different sections (Issues + Feedback into "Issues"), orders items by weight and groups by label. The per-role expected rows and tabs below are pinned by `tests/e2e/tests/admin-shell.spec.ts` (`sidebarMatrix`).

| Actor | Capabilities |
|-------|--------------|
| Admin | Full access — every group and every item |
| Board | Tickets (Tickets, Onsite roster), Scanner, Users (Humans, Roles), Onboarding (Review), Governance (Voting, Applications, Assembly Votes), Workgroups, Audit (Audit log), Surveys (Surveys, Approvals), Google (Resource sync) |
| HumanAdmin | Users (Humans, Roles) |
| TicketAdmin | Tickets (Tickets, Transfer requests, Attendee contacts, Onsite roster), Scanner, Gate (Terminal, Staff PINs) |
| FinanceAdmin | Budget (Overview), Expenses (Review), Finance (Holded connector), Holded, Store (Catalog, Summary, Payments, Order years) |
| StoreAdmin | Store (Catalog, Summary, Payments, Order years) |
| EventsAdmin | Events (Dashboard, Moderation, Categories, Venues, Export) |
| CantinaAdmin | Cantina (Roster) |
| RideshareAdmin | Rideshare (Settings & stats, Day roster) |
| ConsentCoordinator | Onboarding (Review) |
| VolunteerCoordinator | Early Entry, Onboarding (Review), Shifts (Volunteer tracking, Workload, Post-event stats) |
| TeamsAdmin | Google (Resource sync) |
| CampAdmin | Barrios (Overview, Roles, Compliance), City Planning (Barrio map) |
| NoInfoAdmin | Early Entry, Shifts (Volunteer tracking, Workload, Post-event stats) |

## Invariants

- The `Admin` top-nav link and the `/Admin` dashboard are gated by `PolicyNames.AnyAdminRole` (15 roles: Admin, Board, HumanAdmin, TeamsAdmin, CampAdmin, TicketAdmin, EventsAdmin, FeedbackAdmin, FinanceAdmin, StoreAdmin, CantinaAdmin, RideshareAdmin, NoInfoAdmin, VolunteerCoordinator, ConsentCoordinator). Concrete admin tools are gated on their section controllers.
- `FeedbackAdmin` is in `AnyAdminRole` but owns no sidebar item since nobodies-collective/Humans#977 made every Feedback screen `AdminOnly`. A holder of only that role therefore reaches the shell and sees an empty sidebar. Dropping it from `AnyAdminRole` is a privilege reduction left undecided by #977.
- The Surveys group's "Surveys" item is gated by `PolicyNames.AppAccess` (any Active human), not a Board/Admin policy — every admin-shaped role's holder sees it once they reach the shell, not only Board. Only "Approvals" (the authoring approval queue) is `BoardOrAdmin`.
- Sidebar items are filtered per-item by `IAuthorizationService.AuthorizeAsync`; an item the current user cannot access does not appear in the rendered HTML.
- Sidebar groups whose entire visible-item list is empty do not render; the tab strip shows only the current group's visible items, under the same per-item gate.
- The admin shell renders only on restricted pages: a page members share renders in the member layout for everyone. On a restricted page, a user outside `AnyAdminRole` who passed its gate gets the shell without the Dashboard row.
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
- **Store:** `store.orders` tile, gated to `StoreCatalogAdmin` (Store's internal `Service`).
- **Expenses:** `expenses.reports` tile, gated to `FinanceAdminOrAdmin` (`IExpenseReportServiceRead`).
- **Governance:** the "Tier applications" card (`IApplicationServiceRead`); also contributes the Voting tab pill's unvoted-application count.
- **Debug:** the "User set membership" (Venn/UpSet) card.

Shell's own contribution is the three presence tiles (Online now / Active 1h / Active 24h, from `IUserActivityTracker`) in `AdminSummaryViewComponent`. Every piece above lives in the section it names and reads only that section, so none of them crosses a section boundary to render. Most reach their own data through their section's public read-side contract (`I*ServiceRead` / `I*Contracts`), which exists because something outside the section needs it. Store's tile is the exception and not a violation: Store publishes no read contract, so `SectionAdminTiles` resolves Store's `internal Service` — a section calling itself. Either way the shell holds no repository and writes nothing.

## Architecture

**Owning services:** None — frame only.
**Owned tables:** None.
**Status:** (A) Migrated — greenfield (admin-shell-impl, 2026-04-26).

- The admin shell is implemented as a Razor layout (`Views/Shared/_AdminLayout.cshtml`) plus the `AdminSidebar`, `AdminTabs` and `AdminBreadcrumb` view components, all composed from `AdminNavComposition`. The composition and the breadcrumb live in Base beside the other slot renderers; the sidebar and tabs stay in the Shell.
- **Decorator decision — no caching decorator.** Owns no data.
- **Cross-domain navs:** N/A — owns no entities.
- **Architecture test:** N/A — no service layer to pin. Sidebar authorization is covered by the integration tests for each section's admin pages.
