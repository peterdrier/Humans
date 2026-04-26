# Admin Shell + Left Nav — Design Spec

**Date:** 2026-04-26
**Status:** Draft — pending review
**Owner:** Peter Drier
**Related:** Design system bundle (`humans-design-system/project/ui-kits/admin-console.html`, `components/navigation.html`, `tokens.css`)

## 1. Problem

The top navbar in `Views/Shared/_Layout.cshtml` has accumulated nine admin-only links rendered in dark-orange (`nav-restricted`): Review, Voting, Board, Humans, Vol, Tickets, Scanner, Finance, Google. Combined with the existing `Admin` link (system tools index), the bar is crowded and most of its surface area serves a small minority of users — coordinators and admins — while pushing member-facing items (Home, Camps, Teams, Calendar, City, Shifts, Budget) off the visual centerline.

A handoff design from Claude Design (`humans-design-system`) provides a Renaissance-aesthetic admin console with an ink-dark left sidebar and a parchment content area. This spec replaces all nine restricted top-nav items with a single `Admin` link that opens that admin shell, with the sidebar populated dynamically based on the user's actual role assignments.

## 2. Decisions taken in brainstorming

| Decision | Choice |
|---|---|
| Visual scope | **Admin-area only.** Member-side pages stay on current Bootstrap styling. Admin area adopts the design's ink-dark sidebar, parchment palette, Cormorant Garamond + Source Sans 3 typography, and `tokens.css` variables. |
| Routing | **No URL changes.** Existing controller routes (`/Ticket`, `/Finance`, `/Board`, `/OnboardingReview`, `/Profile/AdminList`, `/Vol`, `/Google`, `/Admin/*`) stay where they are. The only new route is `/Admin` itself, which becomes a dashboard. The change is layout, not routing. |
| `/Admin` Index | **Skeleton dashboard with real data where cheap.** Greeting + 4 stat tiles (active humans, shift coverage %, open feedback, system health) + a recent-activity audit feed + a staffing-chart panel that uses real per-department data if accessible, placeholder otherwise. The current 2-column link list is removed — sidebar carries navigation. |
| Sidebar grouping | Operations / Members / Money / Governance / Integrations / People data / Diagnostics / Dev (non-prod). Ordered by daily-traffic-across-the-whole-admin-audience, not by structural prominence. |
| Mobile | **Part of the shell.** ≥768px: fixed 240px sidebar. <768px: Bootstrap offcanvas drawer triggered by a hamburger in a small ink top bar. |
| `AdminController` | Becomes thin over time. This PR keeps existing actions (`Logs`, `Configuration`, `DbStats`, `CacheStats`, `AudienceSegmentation`, `Index`) working unchanged. Re-homing them to section owners is captured as follow-up. |

## 3. Top nav after this PR

Visible items in `_Layout.cshtml` navbar:

| Audience | Items |
|---|---|
| Anonymous / no profile | Home, Camps, Teams, Calendar, Legal |
| Authenticated active members | Home, Camps, Teams, Calendar, City, Shifts, Budget |
| Anyone with any admin-shaped role (Admin, HumanAdmin, TeamsAdmin, CampAdmin, TicketAdmin, FeedbackAdmin, FinanceAdmin, NoInfoAdmin, Board, VolunteerCoordinator, ConsentCoordinator) | …their normal items, plus a single `Admin` link at the end (rendered with the existing `nav-restricted` amber color) |

The composite role check at `_Layout.cshtml` lines 33-41 (build-hash tooltip gate) is reused as the visibility gate for the new `Admin` link — no new policy is added.

Removed: Review, Voting, Board, Humans, Vol, Tickets, Scanner, Finance, Google `<li>` items.

## 4. Admin shell (`_AdminLayout.cshtml`)

### 4.1 Outer chrome

Same `<head>` as `_Layout.cshtml` (Bootstrap 5.3, Font Awesome, Flag-icons, fonts) **plus** `wwwroot/css/tokens.css` (copied from the design bundle) **plus** `wwwroot/css/admin-shell.css` (new — sidebar, breadcrumb, page-head, dashboard tiles, mobile offcanvas). All admin-shell rules are scoped under `body.admin-shell` so tokens/styles do not leak into member pages. Bootstrap stays loaded so existing admin views (`card`, `list-group`, `btn`, `table`) keep rendering without per-view rework.

### 4.2 Body structure

```html
<body class="admin-shell">
  @* env-banner kept from _Layout for non-prod *@
  <div class="env-banner">…</div>

  <div class="app">
    <aside class="sidebar">
      @await Component.InvokeAsync("AdminSidebar")
    </aside>
    <main class="main">
      <div class="admin-mobile-header d-md-none">
        <button data-bs-toggle="offcanvas" data-bs-target="#adminSidebarOffcanvas">…</button>
        @await Component.InvokeAsync("AdminBreadcrumb")
      </div>
      <div class="crumb d-none d-md-flex">
        @await Component.InvokeAsync("AdminBreadcrumb")
      </div>
      <partial name="_AuthorizationPill" />
      @RenderBody()
    </main>
  </div>

  @* toast container, cookie banner, bootstrap js (same as _Layout) *@
  @await Component.InvokeAsync("FeedbackWidget")
</body>
```

### 4.3 Sidebar footer (the design's `.me` block)

Bottom of the sidebar shows the user's avatar (initials, sepia background), name, and primary role label. Clicking the avatar opens a dropdown matching `_LoginPartial`: Profile, Language, About, Privacy, Sign out. The notification bell from current `_Layout` moves into this footer area.

Primary-role label picks the most-privileged role the user holds, in this order: `Admin > Board > HumanAdmin > FinanceAdmin > TicketAdmin > TeamsAdmin > CampAdmin > FeedbackAdmin > NoInfoAdmin > VolunteerCoordinator > ConsentCoordinator`. Implemented as a static method on a small `AdminUserRoleSummary` helper.

### 4.4 Mobile pattern

| Viewport | Behavior |
|---|---|
| ≥768px | Fixed 240px sidebar always visible. `.app` is a CSS grid with `grid-template-columns: 240px 1fr`. |
| <768px | Sidebar becomes Bootstrap `offcanvas-start`. A 56px ink-dark top bar appears in the main area with hamburger (left), truncated breadcrumb (center), avatar (right). Tap-outside or close button dismisses. |

Bootstrap offcanvas is already loaded (`bootstrap.bundle.min.js`) — no new JS dependency.

Stat row breakpoints: 4 columns at ≥1024px, 2 columns at 768-1023px, 1 column below.

### 4.5 What inherits `_AdminLayout`

A `_ViewStart.cshtml` setting `Layout = "_AdminLayout"` is added to each fully-admin view folder:

- `Views/Admin/`
- `Views/AdminMerge/`
- `Views/AdminDuplicateAccounts/`
- `Views/AdminLegalDocuments/`
- `Views/Board/`
- `Views/Finance/`
- `Views/Google/`
- `Views/OnboardingReview/`
- `Views/Scanner/`
- `Views/Ticket/`
- `Views/Vol/` — existing `_ViewStart.cshtml` is updated, `_VolLayout.cshtml` deleted

`Views/Profile/` is mixed (admin-side and member-side views in the same folder). The six admin-side views set `Layout = "_AdminLayout"` at the top of each file:

- `Profile/AdminList.cshtml`
- `Profile/AdminDetail.cshtml`
- `Profile/Search.cshtml`
- `Profile/SendMessage.cshtml`
- `Profile/Outbox.cshtml`
- `Profile/AddRole.cshtml`

Member-side `Profile/` views (`Edit`, `Index`, `Privacy`, `CommunicationPreferences`, `Emails`, `ShiftInfo`, `VerifyEmailResult`) remain on the default `_Layout`.

## 5. Sidebar (`AdminSidebarViewComponent`)

### 5.1 Model

```csharp
public sealed record AdminNavGroup(string LabelKey, IReadOnlyList<AdminNavItem> Items);

public sealed record AdminNavItem(
    string LabelKey,                                                  // localizer key
    string? Controller,
    string? Action,
    object? RouteValues,
    string? RawHref,                                                  // for /hangfire, /health/ready
    string IconCssClass,                                              // FontAwesome class
    string? Policy,                                                   // PolicyNames.* (preferred)
    Func<ClaimsPrincipal, bool>? RoleCheck = null,                    // fallback for non-policy gates
    Func<IServiceProvider, ValueTask<int?>>? PillCount = null,
    Func<IWebHostEnvironment, bool>? EnvironmentGate = null);         // for non-prod-only items
```

### 5.2 Configured tree

Order is by daily-traffic-across-the-whole-admin-audience.

| Group | Item | Target | Policy / gate |
|---|---|---|---|
| **Operations** | Volunteers | `Vol/Index` | `VolunteerSectionAccess` |
| | Tickets | `Ticket/Index` | `TicketAdminBoardOrAdmin` |
| | Scanner | `Scanner/Index` | `TicketAdminBoardOrAdmin` |
| **Members** | Humans | `Profile/AdminList` | `HumanAdminBoardOrAdmin` |
| | Review | `OnboardingReview/Index` | `ReviewQueueAccess` (pill: review queue depth) |
| **Money** | Finance | `Finance/Index` | `FinanceAdminOrAdmin` |
| **Governance** | Voting | `OnboardingReview/BoardVoting` | `BoardOrAdmin` (pill: voting queue depth) |
| | Board | `Board/Index` | `BoardOrAdmin` |
| **Integrations** | Google | `Google/Index` | `AdminOnly` |
| | Email Preview | `Email/EmailPreview` | `AdminOnly` |
| | Email Outbox | `Email/EmailOutbox` | `AdminOnly` |
| | Campaigns | `Campaign/Index` | `AdminOnly` |
| | Workspace Accounts | `Google/Accounts` | `AdminOnly` |
| **People data** | Merge Requests | `AdminMerge/Index` | `AdminOnly` |
| | Duplicate Detection | `AdminDuplicateAccounts/Index` | `AdminOnly` |
| | Audience Segmentation | `Admin/AudienceSegmentation` | `AdminOnly` |
| | Legal Documents | `AdminLegalDocuments/Index` | `AdminOnly` |
| **Diagnostics** | Logs | `Admin/Logs` | `AdminOnly` |
| | DB Stats | `Admin/DbStats` | `AdminOnly` |
| | Cache Stats | `Admin/CacheStats` | `AdminOnly` |
| | Configuration | `Admin/Configuration` | `AdminOnly` |
| | Hangfire | raw `/hangfire` | `AdminOnly` |
| | Health | raw `/health/ready` | `AdminOnly` |
| **Dev (non-prod)** | Seed Budget | `DevSeed/SeedBudget` | `AdminOnly` + `IWebHostEnvironment.IsDevelopment` |
| | Seed Camp Roles | `DevSeed/SeedCampRoles` | `AdminOnly` + `IWebHostEnvironment.IsDevelopment` |

The configured tree is a single static `IReadOnlyList<AdminNavGroup>` defined alongside the ViewComponent, not pulled from configuration. Adding/removing entries is a code change.

### 5.3 `Humans` policy change

The current top-nav uses `HumanAdminOnly` (HumanAdmin and *not* Admin/Board) precisely because Admins already saw their own admin links. In the sidebar, Admins should see Humans too — every section is in the sidebar — so the gate becomes the broader `HumanAdminBoardOrAdmin` policy. The `HumanAdminOnly` policy stays defined; deletion is captured as a follow-up after a global usage check.

### 5.4 Rendering rules

- ViewComponent calls `IAuthorizationService.AuthorizeAsync(User, null, item.Policy)` per item, hides items the user cannot see, hides whole groups whose items all hide.
- Active-state matching: compare `RouteData.Values["controller"]` to `item.Controller` (action match optional). Matching item gets the gold left-border + `.active` class. For deep paths (`/Ticket/Sales/Edit/123`), controller-only match keeps the parent item highlighted.
- Pill counts in this PR: only **Review** and **Voting** populate counts, reusing the `NavBadges` ViewComponent's existing data sources (`queue=review`, `queue=voting`). Other items render no pill. Future panels (waitlist, open consents, etc.) plug into `PillCount` in follow-up PRs.
- Items with `RawHref` (Hangfire, Health) render as plain `<a href="…">` rather than tag-helper `asp-controller`/`asp-action`.
- All labels use `IStringLocalizer<AdminSidebarViewComponent>` keys (e.g. `AdminNav_Volunteers`, `AdminNav_Tickets`). Initial English entries are added to `Resources/`.

### 5.5 Breadcrumb (`AdminBreadcrumbViewComponent`)

Looks up the current item by matching `RouteData.Values["controller"]` against the configured tree, then renders:

```
Admin / <Group> / <Item>
```

Where `<Group>` is non-clickable (it has no destination) and `<Item>` is the current page. If no item matches (e.g., a sub-route under a controller without an explicit sidebar entry), renders just `Admin / <PageTitle>` using `ViewData["Title"]`.

## 6. Dashboard skeleton (`Admin/Index`)

### 6.1 Page head

Greeting line: "*Welcome back,* {firstname}." (firstname italic, gold) — pulled from `User.Identity.Name` resolved through `IUserService` to a profile firstname. Subtitle one-liner summarizes the four stat tiles ("412 active humans · 78% shift coverage · 6 open feedback · all systems normal."). No event countdown in v1.

### 6.2 Stat tiles (4 cards)

| Tile | Source method (added if absent) | Notes |
|---|---|---|
| Active humans | `IProfileService.CountActiveAsync(CancellationToken)` | Cached if profile cache exists |
| Shift coverage | `IShiftService.GetOverallCoverageAsync(CancellationToken)` | Returns `(filled, total, ratio)`; tile renders "—" if no active event |
| Open feedback | `IFeedbackService.CountUnresolvedAsync(CancellationToken)` | |
| System health | `IAdminDashboardService.GetSystemHealthAsync(CancellationToken)` | New thin service that wraps log-store error count + Hangfire failed-job count over last 24h |

If any of those methods don't exist on the service today, this PR adds them as small additions to the section's existing service interface. They are read-only counts; no service-design rework.

### 6.3 Staffing chart

`IShiftService.GetCoverageByDepartmentAsync(CancellationToken)` returns a list of `(departmentName, ratio)` pairs. Rendered as the design's `.dept-row` track bars. If the service can't produce per-department data today, the panel renders a placeholder ("Live staffing tile coming — see Volunteers section") and a follow-up issue is captured. The placeholder takes the same physical space so layout doesn't reflow when the real implementation lands.

### 6.4 Recent activity

`IAuditLogService.GetRecentAsync(limit: 8, CancellationToken)` returns audit entries already filtered by what the current user is authorized to see (existing service behavior). The view renders the design's `.activity-item` rows, picking a bubble icon per `EventType` via a small switch in the partial. Each row is clickable and links to the audit-log detail page.

### 6.5 Notably absent

No "Pending approvals" inline table. No "Open votes" stat tile. No quick-action button (`+ New shift` from the design). Sidebar pills carry approvals/votes for the small audience that cares; the dashboard is operational, not governance-shaped.

## 7. Files

### Created

| Path | Purpose |
|---|---|
| `src/Humans.Web/wwwroot/css/tokens.css` | Renaissance design tokens, copied from `humans-design-system/project/tokens.css` |
| `src/Humans.Web/wwwroot/css/admin-shell.css` | Admin-shell styles, scoped under `body.admin-shell` |
| `src/Humans.Web/Views/Shared/_AdminLayout.cshtml` | Master layout for admin area |
| `src/Humans.Web/Views/Shared/Components/AdminSidebar/Default.cshtml` | Sidebar render |
| `src/Humans.Web/ViewComponents/AdminSidebarViewComponent.cs` | Builds nav tree, runs `IAuthorizationService` per item, picks active item |
| `src/Humans.Web/ViewComponents/AdminNav.cs` | `AdminNavGroup` / `AdminNavItem` records + the configured static tree |
| `src/Humans.Web/Views/Shared/Components/AdminBreadcrumb/Default.cshtml` | Breadcrumb crumb |
| `src/Humans.Web/ViewComponents/AdminBreadcrumbViewComponent.cs` | Breadcrumb lookup |
| `src/Humans.Web/Models/AdminDashboardViewModel.cs` | Dashboard data |
| `src/Humans.Web/Views/Admin/_DashboardStats.cshtml` | Stat-tile partial |
| `src/Humans.Web/Views/Admin/_DashboardActivity.cshtml` | Activity-feed partial |
| `src/Humans.Web/Views/Admin/_DashboardStaffing.cshtml` | Staffing chart partial |
| `src/Humans.Web/Views/{Admin,AdminMerge,AdminDuplicateAccounts,AdminLegalDocuments,Board,Finance,Google,OnboardingReview,Scanner,Ticket}/_ViewStart.cshtml` | One per fully-admin view folder, sets `Layout = "_AdminLayout"` |
| `src/Humans.Application/Interfaces/IAdminDashboardService.cs` | New thin service for system-health composite |
| `src/Humans.Infrastructure/Services/AdminDashboardService.cs` | Implementation |
| `docs/sections/admin-shell.md` | Section invariant doc per repo convention |

### Modified

| Path | Change |
|---|---|
| `src/Humans.Web/Views/Shared/_Layout.cshtml` | Remove 9 dark-orange `<li>` items; replace remaining `Admin` link with composite-role-gated single `Admin` link |
| `src/Humans.Web/Controllers/AdminController.cs` | `Index()` populates `AdminDashboardViewModel` and returns it |
| `src/Humans.Web/Views/Admin/Index.cshtml` | Replaced wholesale with dashboard skeleton |
| `src/Humans.Web/Views/Profile/{AdminList,AdminDetail,Search,SendMessage,Outbox,AddRole}.cshtml` | Add `@{ Layout = "_AdminLayout"; }` at top |
| `src/Humans.Web/Views/Vol/_ViewStart.cshtml` | Switch `_VolLayout` → `_AdminLayout` |
| `src/Humans.Web/wwwroot/css/site.css` | Drop the unused `.navbar .nav-link.nav-restricted` rule |
| `src/Humans.Application/Interfaces/I{Profile,Shift,Feedback,AuditLog}Service.cs` | Add count/recent methods used by dashboard if absent |
| Localization `.resx` files | Add `AdminNav_*` and `AdminGroup_*` keys |
| `docs/architecture/data-model.md` | Index note: Admin section is a frame, not a data owner |

### Deleted

| Path | Why |
|---|---|
| `src/Humans.Web/Views/Vol/_VolLayout.cshtml` | Vol joins the admin shell |

## 8. Build sequence

Single PR, but committed in this order so the tree builds at each step:

1. **Tokens + scaffolding.** `tokens.css`, `admin-shell.css`, empty `_AdminLayout.cshtml` referencing them. No callers yet — site builds and renders unchanged.
2. **AdminSidebar + AdminBreadcrumb ViewComponents** with the configured tree and per-item authorization. Verified via temporary harness route.
3. **Wire `_AdminLayout` end-to-end** with sidebar, breadcrumb, mobile offcanvas, footer block. Add `_ViewStart.cshtml` to `Views/Admin/` only — `Admin/Index.cshtml` renders inside the new shell. Eyeball desktop + phone in QA preview.
4. **Dashboard content.** `AdminDashboardViewModel`, three partials, real-data wiring. Add count/recent methods on services that don't already expose them.
5. **Migrate the rest of admin folders** to `_AdminLayout` — `_ViewStart.cshtml` per folder + per-view `Layout=` for the six mixed `Profile/` views. Delete `_VolLayout.cshtml`.
6. **Top nav surgery.** Remove the 9 dark-orange `<li>` items; keep one composite-gated `Admin` link.
7. **Cleanup.** Prune unused CSS rules, write `docs/sections/admin-shell.md`.

## 9. Risks and verification

- **CSS leakage.** `admin-shell.css` rules must all live under `body.admin-shell`. `tokens.css` variables are `--h-*` prefixed; verify no collision via grep before merge.
- **`HumanAdminOnly` policy becomes nav-unused.** Grep current usage — appears to be only in the nav. Spec lists it as candidate for follow-up deletion, not deleted in this PR.
- **`Vol` section sub-nav.** Verify `_VolLayout.cshtml` doesn't carry section-specific UI beyond the layout chrome. If it has a sub-nav (departments, etc.), port it as a partial that `Vol/Index` and friends render directly into the admin shell's main column.
- **Mobile offcanvas + Bootstrap.** `_Layout` already loads `bootstrap.bundle.min.js`. No JS dependency change.
- **Active-state matching with sub-routes.** Verify `/Ticket/Sales/Edit/123` highlights *Tickets* (controller-only match). Add ViewComponent unit tests covering this.
- **Per-PR preview environment.** New `Admin` link only shows for admin-roled users; in preview environments use the dev-login admin user to verify.
- **No existing tests cover `_Layout.cshtml` nav contents.** New tests: `AdminSidebarViewComponentTests` confirming (a) items filter by policy, (b) groups with no visible items disappear, (c) active state matches `RouteData`, (d) pill counts populate from `NavBadges` data source. Optional integration test: `GET /Admin` returns 200 for admin user, returns 403 for non-admin.

## 10. Out of scope (follow-up issues)

Captured here so they don't pollute this PR:

1. **Re-home `/Admin/*` system actions** (`Logs`, `DbStats`, `CacheStats`, `Configuration`, `AudienceSegmentation`) to whichever section truly owns them, per the existing `/Admin/* is a nav holder` rule. Sidebar entries' `asp-action` targets just change.
2. **Move `CampAdmin` / `TeamAdmin` admin actions into the sidebar's `Operations` group** so coordinators reach them from `/Admin` too.
3. **Real per-department staffing chart** if `IShiftService` doesn't already expose `GetCoverageByDepartmentAsync`.
4. **Active-event countdown** on the dashboard greeting once an active-event service exposes a current event start date.
5. **Member-side top-nav redesign** per the design's `Primary top nav — member view` (Shifts / My profile / Barrios / Voting / Events idiom). Separate, larger PR.
6. **Full visual reskin of admin-page interior content** (cards, tables, forms) to drop Bootstrap defaults in favor of `tokens.css`-driven primitives. Each section iterates its own pages.
7. **Delete `HumanAdminOnly` policy** after confirming no remaining references.
8. **Pending-approvals inline table on `/Admin`** if there's ever a clear product reason to surface it on the dashboard alongside the sidebar pill — currently judged unnecessary.
9. **Quick-action slot** (`@RenderSectionAsync("PageActions")`) on the page-head once specific admin pages have universal primary actions worth exposing globally.

## 11. References

- Design source: `https://api.anthropic.com/v1/design/h/tEGeOaNlIrMTa6ZJy7XG9A` (extracted to `humans-design-system/`)
- `humans-design-system/project/components/navigation.html` — sidebar idiom
- `humans-design-system/project/ui-kits/admin-console.html` — full admin-console reference
- `humans-design-system/project/tokens.css` — palette + typography variables
- Existing repo: `docs/architecture/design-rules.md`, `docs/architecture/coding-rules.md`, `docs/sections/SECTION-TEMPLATE.md`
- Existing nav: `src/Humans.Web/Views/Shared/_Layout.cshtml`
- Existing policies: `src/Humans.Web/Authorization/PolicyNames.cs`, `AuthorizationPolicyExtensions.cs`
