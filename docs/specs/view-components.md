# View Components — Inventory & Specs

**March 2026 | Informed by designer Figma mockup**

Source: `https://finder-vector-52685742.figma.site/`

---

## 1. Current Inventory

### View Components (fetch their own data)

| Component | Location | Data Source | Used In |
|-----------|----------|-------------|---------|
| `ProfileCardViewComponent` | `ViewComponents/` | 7 injected services | Profile pages |
| `NavBadgesViewComponent` | `ViewComponents/` | DB query (cached 2min) | Layout navbar |
| `UserAvatarViewComponent` | `ViewComponents/` | None (pure params) | Various |
| `TempDataAlertsViewComponent` | `ViewComponents/` | TempData | Layout |

### Partials (pure presentation — keep as partials)

| Partial | Model | Purpose |
|---------|-------|---------|
| `_RoleBadge` | `TeamMemberRole` | Coordinator/Member badge |
| `_VolunteerProfileBadges` | `(VolunteerEventProfile?, bool)` | Skills/quirks/dietary badges |
| `_ShiftsSummaryCard` | `ShiftsSummaryCardViewModel` | Progress bar + fill stats |
| `_LoginPartial` | — | Auth links |
| `_LanguageChooser` | — | Locale switcher |
| `_ValidationScriptsPartial` | — | jQuery validation scripts |
| `_ApplicationHistory` | — | Application timeline |

### Partials That Should Be Promoted to View Components

| Partial | Why | Priority |
|---------|-----|----------|
| `_ShiftCards` | HomeController fetches shift data specifically to pass through. Should fetch its own data. | High — simplifies HomeController |
| `_HumanSearchInput` | Self-contained widget with its own JS, API calls, debouncing. Currently uses stringly-typed `ViewData["FieldName"]`. Duplicates `<script>` block if rendered twice. | High — used in ShiftAdmin + ShiftDashboard |
| `_StaffingChart` | Chart.js widget used in ShiftAdmin and ShiftDashboard. Each controller assembles chart data independently. | Medium — works today but duplicates data assembly |

---

## 2. New View Components (from Designer Mockup)

### 2.1 DepartmentOverviewCardViewComponent

**Figma location:** Homepage — "Volunteering Overview" grid

**Purpose:** Renders a single department card showing fill rate, slot counts, team count, and pending count. The homepage renders a grid of these.

**Data fetched:**
- Department (parent team) name and slug
- Aggregate fill rate: `SUM(confirmed) / SUM(maxVolunteers)` across all active rotas/shifts
- Slot counts: `confirmed / total`
- Child team count
- Pending signup count

**Parameters:**
```csharp
public async Task<IViewComponentResult> InvokeAsync(Guid departmentId)
```

**Renders:** A clickable card linking to the department detail page. Shows: department name, fill percentage, confirmed/total slots, team count, pending badge (if > 0).

**Why View Component:** The homepage shouldn't need to query shift fill rates for every department. This component encapsulates the aggregate query and can be cached independently per department.

**Caching:** `IMemoryCache` keyed by `departmentId`, 2-minute expiry. Invalidated on signup state changes.

---

### 2.2 DepartmentOverviewGridViewComponent

**Figma location:** Homepage — the "Volunteering Overview" section as a whole

**Purpose:** Fetches all departments and renders the grid of `DepartmentOverviewCard` components. Alternative to having the HomeController know about departments.

**Data fetched:**
- All parent teams (departments) that have active rotas
- Aggregate fill data per department (single query, not N+1)

**Parameters:**
```csharp
public async Task<IViewComponentResult> InvokeAsync()
```

**Renders:** Section heading ("Volunteering Overview"), "View all" link, responsive card grid. Each card can either be a nested `DepartmentOverviewCard` component invocation or an inline loop rendering — implementation can decide based on query efficiency (a single aggregate query with inline rendering avoids N+1).

**Why View Component:** Completely decouples the homepage from shift/department concerns. The HomeController stays focused on profile completion, consents, and other homepage cards.

---

### 2.3 MyShiftsTableViewComponent

**Figma location:** Homepage "My Shifts" section, also `/volunteer-management/my-duties`

**Purpose:** Renders the current user's shift signups as a table (desktop) or card list (mobile) with Bail actions.

**Data fetched:**
- Current user's active signups (Confirmed + Pending), resolved to absolute dates
- Department name per signup (via Rota → Team)

**Parameters:**
```csharp
public async Task<IViewComponentResult> InvokeAsync(int? limit = null)
```

`limit` controls how many to show — homepage uses a limit (e.g., 4), the dedicated My Shifts page shows all.

**Renders:** Table with columns: Duty, Team, Date & Time, Status badge, Bail button. Mobile layout: stacked cards with the same info.

**Why View Component:** Currently the homepage shift cards (`_ShiftCards`) are a partial that the HomeController populates. The designer's mockup shows the same shift table on both the homepage and the dedicated My Shifts tab, so this needs to be a self-contained component usable in both contexts.

**Replaces:** `_ShiftCards` partial (the "My Shifts" half — urgent shifts becomes a separate component).

---

### 2.4 UrgentShiftsTableViewComponent

**Figma location:** Urgent Shifts tab (`/volunteer-management/noinfo`)

**Purpose:** Renders urgency-ranked unfilled shifts with capacity info and "Find volunteer" actions.

**Data fetched:**
- Unfilled shifts sorted by urgency score (existing `ShiftUrgencyService`)
- Remaining slot counts, priority levels

**Parameters:**
```csharp
public async Task<IViewComponentResult> InvokeAsync(int? limit = null, bool showVoluntellAction = false)
```

`limit` — homepage "Shifts Need Help" card uses a small limit. Full page shows all.
`showVoluntellAction` — controls whether "Find volunteer" / Voluntell button appears (role-dependent, but the parent page determines this).

**Renders:** Table with columns: Duty, Team, Date & Time, Capacity (slots remaining), Priority badge, Action button. Sorted by urgency score descending.

**Why View Component:** Reused between the homepage "Shifts Need Help" card (limited, no voluntell) and the full Urgent Shifts page (unlimited, with voluntell). Currently duplicated across `_ShiftCards` partial and `ShiftDashboardController`.

**Replaces:** `_ShiftCards` partial (the "Shifts Need Help" half) + partially overlaps with ShiftDashboard index view.

---

### 2.5 ShiftCardsViewComponent (refactored)

**Figma location:** Homepage — combines MyShiftsTable + UrgentShiftsTable in the homepage context

**Purpose:** Replaces the existing `_ShiftCards` partial. Fetches its own data and renders the two-column homepage card layout (My Shifts + Shifts Need Help).

**Data fetched:**
- Delegates to `MyShiftsTableViewComponent` and `UrgentShiftsTableViewComponent` internally, OR fetches both datasets in a single query for efficiency.

**Parameters:**
```csharp
public async Task<IViewComponentResult> InvokeAsync()
```

**Implementation note:** This can be the first migration — convert the existing `_ShiftCards` partial to a View Component. The internal rendering can initially stay as-is, then later delegate to the more granular components (2.3, 2.4) when those are built.

---

### 2.6 VolunteerSearchViewComponent

**Figma location:** Used in Urgent Shifts (Find volunteer), ShiftAdmin (Voluntell), anywhere a human search with shift context is needed

**Purpose:** Self-contained search widget with typeahead, showing volunteer availability, skills, and current bookings. Replaces `_HumanSearchInput` partial.

**Data fetched:**
- User search results via API (existing `/api/humans/search`)
- Volunteer event profile badges (skills, quirks, dietary, languages)
- Current shift count and overlap warnings

**Parameters:**
```csharp
public IViewComponentResult Invoke(
    string fieldName = "userId",
    string? searchApiUrl = null,
    bool showShiftContext = false)
```

`showShiftContext` — when true, shows skills/quirks badges and overlap warnings alongside search results (for shift-related contexts). When false, behaves as a plain human search (for non-shift use cases).

**Renders:** Text input with dropdown results. Each result shows: name, burner name, and optionally volunteer profile badges + shift count.

**Why View Component:** The current `_HumanSearchInput` partial embeds an inline `<script>` block that would duplicate if rendered twice on one page. As a View Component, the JS can be extracted to a shared script file and the component handles its own initialization.

**Replaces:** `_HumanSearchInput` partial.

---

### 2.7 StaffingChartViewComponent

**Figma location:** ShiftAdmin department page, ShiftDashboard global page

**Purpose:** Renders the Chart.js build/strike staffing visualization. Currently the `_StaffingChart` partial requires each controller to assemble chart data.

**Data fetched:**
- Per-day confirmed volunteers vs total slots for a date range
- Period classification (Build/Event/Strike)

**Parameters:**
```csharp
public async Task<IViewComponentResult> InvokeAsync(Guid? departmentId = null)
```

`departmentId` — when null, shows global staffing. When set, scopes to one department.

**Renders:** Chart.js stacked bar chart with confirmed (color-coded by fill %) vs remaining slots, period labels in tooltip.

**Why View Component:** Currently both `ShiftAdminController` and `ShiftDashboardController` independently assemble chart data arrays with the same logic. This component encapsulates that query.

**Replaces:** `_StaffingChart` partial.

---

### 2.8 EarlyEntryTableViewComponent

**Figma location:** Department detail page — "Early Entry Privileges" section at bottom

**Purpose:** Renders the auto-computed early entry list for a department or globally.

**Data fetched:**
- Volunteers with confirmed build-period signups
- Computed EE arrival date: `GateOpeningDate + MIN(DayOffset) - 1`
- Ticket ID from matched `TicketAttendee`
- EE cap usage vs capacity

**Parameters:**
```csharp
public async Task<IViewComponentResult> InvokeAsync(Guid? departmentId = null)
```

**Renders:** Cap indicator (X/Y, percentage), table with columns: Volunteer name, Email, Ticket ID, Earliest Shift, Computed Arrival date.

**Why View Component:** This is a self-contained data view that requires joining shifts, signups, users, ticket attendees, and event settings. No controller should need to assemble this query inline — the component owns it.

**New — not replacing anything.** This is Slice 4 functionality (Exports & Stats) surfaced as an inline component rather than just a CSV download.

---

### 2.9 VolunteerCapIndicatorViewComponent

**Figma location:** Management tab — "Global Volunteer Cap" indicator

**Purpose:** Shows the current confirmed volunteer count vs the global cap with visual indicators.

**Data fetched:**
- Count of unique users with any Confirmed signup
- `EventSettings.GlobalVolunteerCap`

**Parameters:**
```csharp
public async Task<IViewComponentResult> InvokeAsync()
```

**Renders:** Count/cap display (e.g., "83 / 100"), percentage bar, status label (OK / Approaching Limit / At Limit), remaining spots count.

**Why View Component:** Small, self-contained query. Useful on the Management page and potentially on the ShiftDashboard. Cacheable (2-minute expiry).

---

## 3. Implementation Priority

### Phase 1 — Promote existing partials (no new features, just better architecture)

1. **`ShiftCardsViewComponent`** (2.5) — Convert `_ShiftCards` partial. Removes shift data fetching from HomeController. Quick win.
2. **`VolunteerSearchViewComponent`** (2.6) — Convert `_HumanSearchInput` partial. Fixes the duplicate-script problem. Extract JS to `wwwroot/js/volunteer-search.js`.
3. **`StaffingChartViewComponent`** (2.7) — Convert `_StaffingChart` partial. Eliminates duplicated chart data assembly in ShiftAdmin and ShiftDashboard controllers.

### Phase 2 — New components for designer's homepage vision

4. **`DepartmentOverviewGridViewComponent`** (2.2) — Adds the department fill-rate grid to the homepage. New feature from designer's mockup.
5. **`MyShiftsTableViewComponent`** (2.3) — Extracts a reusable shift table for both homepage and dedicated My Shifts page.
6. **`UrgentShiftsTableViewComponent`** (2.4) — Extracts urgency table for homepage card and full Urgent Shifts page.

### Phase 3 — Slice 4 components

7. **`EarlyEntryTableViewComponent`** (2.8) — Inline EE list for department detail pages.
8. **`VolunteerCapIndicatorViewComponent`** (2.9) — Cap indicator for Management page.

---

## 4. Design Notes

### Caching Strategy

At ~500 users, all View Components that query aggregate data should use `IMemoryCache` with 1–2 minute expiry. The aggregate queries (fill rates, cap counts, urgency scores) touch the same tables that signup state changes write to, so cache invalidation on signup state changes is the right approach.

### Responsive Rendering

The designer's mockup shows two layouts for several components — a table layout (desktop) and a stacked card layout (mobile). View Components should render both and use CSS (`d-none d-md-block` / `d-md-none`) to toggle. This is already the pattern used in `_ShiftCards`.

### Naming Convention

- View Component class: `{Name}ViewComponent.cs` in `ViewComponents/`
- View: `Views/Shared/Components/{Name}/Default.cshtml`
- ViewModel: `{Name}ViewModel.cs` in `Models/`
- Invocation: `@await Component.InvokeAsync("{Name}", new { param = value })`
