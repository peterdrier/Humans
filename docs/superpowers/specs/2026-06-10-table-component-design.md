# Table Component — One Definition of a Table

**Date:** 2026-06-10
**Status:** Accepted 2026-06-10 (currency + enforcement decisions resolved by Peter)
**Branch:** `feat/table-component`

## Problem

The app has ~117 views containing ~175 hand-authored `<table>` elements. Because there is no single definition of what a table is, they drift on every axis at once:

- **Markup/styling** — dominant pattern is `table table-sm table-hover mb-0`, but views use bare `table`, `table-striped`, inline `style=` attributes; three different class strings appear within single views (`Store/Order.cshtml`, `ProfileAdmin/EmailProblems.cshtml`).
- **Sorting** — two unrelated mechanisms: a server-side query-param pattern (`sortBy`/`sortDesc` header links, e.g. `TicketController.Attendees`) and a client-side `data-sortable-table` enhancer in `site.js` used by only 7 tables. The server-side header links repeat ~8 `asp-route-*` attributes per column, copy-pasted (see `Views/Ticket/Attendees.cshtml:61-66`).
- **Filtering** — server-side GET forms re-implemented per controller (`filterX` params, some `.js-auto-submit`). No client-side filtering exists anywhere.
- **Formatting** — dates go through shared `DateTimeDisplayExtensions` (`.ToDate()`/`.ToDateTime()`), but currency is shown three different ways (bare `N2` in Tickets, `€` suffix in Store, `"C"` in Expenses), and enum→badge / bool→icon / null→em-dash logic is re-invented inline in each view (e.g. the status `switch` in `Attendees.cshtml:100-108`).
- **Empty states, accessibility, row-click** — ad-hoc per view.

Bolting a sorter onto more tables would add a feature without fixing the cause. The fix is one declarative, column-model-driven table component that becomes the single authority for list-table rendering and behavior.

## Goals

1. One canonical definition of a list table: typed column model + one shared renderer.
2. Sorting, filtering, pagination, and formatting become **per-column configuration**, not per-view implementations.
3. Client-side and server-side execution are an implementation detail behind the same façade — same call-site shape, swappable engine.
4. Shared formatters kill the inline currency/badge/icon duplication.
5. Consistent Bootstrap markup, accessibility (`scope="col"`, `aria-sort`, keyboard sort), empty states, and `data-href` row-click.
6. A burn-down mechanism so the migration actually finishes.

## Non-goals (explicitly NOT owned by the component)

Tables that are layouts rather than entity lists stay bespoke Razor:

- The calendar grid (`Calendar/Index.cshtml` — `table-layout: fixed` day grid).
- Rota tables (`_EventRotaTable.cshtml`, `_BuildStrikeRotaTable.cshtml` — structural rows, row partials).
- Nested/aggregated Finance tables (`Finance/CashFlow.cshtml`).
- Key-value detail panes rendered as `<dl>` or two-column tables.

Also out of scope:

- **No JS grid library** (DataTables/Tabulator/AG Grid). They rewrite the DOM, which fights server-rendered Razor cells (`<vc:human>`, `asp-route` links), `tr[data-href]`, CSP nonces, and server-side authorization.
- **No abstraction of server-side data shaping.** Controllers keep parsing params and calling services exactly as today; only the *rendering* of sort headers/filter bars/pager is unified.

## Design

### Component shape

New files under `src/Humans.Web/Models/Tables/` + one shared partial:

```
TableModel<TRow>          — columns, rows, mode, empty message, row href/class
TableColumn<TRow>         — header, value selector, format, sort, filter, align, css
TableModelBuilder<TRow>   — fluent builder
ITableModel / ITableColumn — non-generic render contract consumed by the partial
Views/Shared/_Table.cshtml — the single renderer
```

Call-site sketch (in a view):

```cshtml
@{
    var table = TableModel.For(Model.Attendees)
        .Column("Name", a => a.AttendeeName, c => c.Sort("name"))
        .Column("Email", a => a.AttendeeEmail, c => c.Sort("email").Small())
        .Column("Ticket Type", a => a.TicketTypeName, c => c.Sort("type").SelectFilter())
        .Column("Price", a => a.Price, c => c.Currency().Sort("price"))
        .Column("Status", a => a.Status, c => c.EnumBadge().Sort("status").SelectFilter())
        .Column("Matched Human", template: @<text>
            @if (item.MatchedUserId.HasValue) { <vc:human user-id="@item.MatchedUserId.Value" link="Admin" /> }
            else { <span class="text-muted">&mdash;</span> }
        </text>)
        .RowHref(a => Url.Action("Detail", new { id = a.Id }))
        .ServerMode(Model)            // or .ClientMode() — the default
        .Empty("No attendees found")
        .Build();
}
<partial name="_Table" model="table" />
```

### Where the model is built: in the view

The hard rule "controllers are responsible for formatting, sorting, filtering" governs the controller↔service boundary: data shaping (executing sort/filter/page against the dataset) stays in the controller, exactly as today. Column *declaration* is pure presentation and lives in a code block at the top of the view, because custom cells need Razor — inline templates (`@<text>…</text>`, Razor's `Func<TRow, IHelperResult>`) are the only way to keep ViewComponents (`<vc:human>`), tag helpers, and `Url.Action` usable inside cells.

Alternatives rejected:

- **Build in controller, attach templates in view by key** — splits one declaration across two files; the column list and its templates drift apart.
- **Tag helper as the renderer** — tag helpers cannot carry typed cell templates; viable later as sugar for the trivial all-text table, not the foundation.
- **ViewComponent as the renderer** — adds async invocation and DI ceremony for what is pure model→markup rendering; a partial is the simplest correct mechanism and matches `_Pager.cshtml`.

### Formats

`CellFormat` covers what the codebase actually does today:

| Format | Replaces | Behavior |
|---|---|---|
| `Text` (default) | — | `@value`, null → muted `—` |
| `Date` / `DateTime` | inline `.ToDate()`/`.ToDateTime()` calls | reuses `DateTimeDisplayExtensions` (NodaTime, user-timezone aware); emits ISO `data-sort-value` |
| `Currency` | `N2` / `€` / `"C"` divergence | one shared formatter, bare `N2` (`#,##0.00`) app-wide — no currency symbol (Peter's call, 2026-06-10); raw numeric `data-sort-value` |
| `Number` | ad-hoc `N0`/`N2` | numeric alignment + `data-sort-value` |
| `EnumBadge` | per-view `switch` → badge class | central per-enum-value → Bootstrap badge class registry; unmapped values get `bg-secondary` |
| `BoolIcon` | ad-hoc check/×/em-dash | standard check / muted em-dash |
| `Template` | — | inline Razor template, full escape hatch |

The `EnumBadge` registry is one static dictionary in `Models/Tables/` mapping enum values to badge classes (e.g. `TicketAttendeeStatus.Valid → bg-success`). Views stop owning color decisions.

### Sorting

- **Client mode:** the renderer emits `data-sortable-table` / `data-sort` / `data-sort-type` attributes consumed by the **existing** `site.js` engine (`site.js:56-146`) — already handles numeric/text detection, `aria-sort`, keyboard activation, `data-sort-value` overrides. No engine rewrite; formatters emit `data-sort-value` so dates/currency sort correctly.
- **Server mode:** `c.Sort("name")` renders the header as a link that toggles `sortBy`/`sortDesc` while preserving every other current query param — using the same `Context.Request.Query` rebuild technique `_Pager.cshtml` already uses. This deletes the 8-route-param copy-paste per column.

### Filtering

Two filter types, matching everything the existing filter bars do:

- **`TextFilter()`** — text contains, case-insensitive.
- **`SelectFilter(options?)`** — dropdown. Client mode auto-populates options from distinct rendered cell values; server mode requires caller-supplied options (e.g. `Model.AvailableTicketTypes`).

Plus an opt-in **global search box** above the table.

- **Client mode:** new, small extension to `site.js`: a filter row under `<thead>` with `data-filter-col` inputs, and `data-table-search` for the global box; hides non-matching rows, debounced, updates an optional result count. Declarative data attributes only — CSP-safe, no inline JS.
- **Server mode:** the renderer emits the GET filter form (`name="filterX"`, `name="search"`, `.js-auto-submit`) from the filterable columns, matching the existing controller param convention so **controller signatures don't change** (`TicketController.Attendees` keeps `search, sortBy, sortDesc, page, pageSize, filterTicketType, …`).

### Pagination

Server mode reuses `_Pager.cshtml`/`PagerViewModel` unchanged — the table model carries an optional `PagerViewModel` and the renderer emits it after the table. Client mode renders all rows; at ~500 users that is the correct default (per the project's in-memory-over-optimization rule). No client-side pagination in v1.

### Mode selection

`ClientMode()` is the default and right for nearly every table. `ServerMode(PagedListViewModel)` is for the handful of views that already do server paging (Tickets, audit logs). Same columns, same renderer; mode changes which sort/filter wiring is emitted.

### Accessibility & chrome standard (applies to every rendered table)

`<div class="table-responsive">` wrapper · canonical `table table-sm table-hover mb-0` (+opt-in extras like `align-middle`) · `<th scope="col">` · `aria-sort` via the engine · standard empty-state row (`colspan`, muted, configurable message) · `tr[data-href]` row-click via the existing global handler.

## Enforcement — deferred

No burn-down guard in v1 (Peter's call, 2026-06-10). The component must prove itself on a few real views first. If/when migration goes "full blast", revisit an enforcement mechanism then.

## Migration plan

- **Phase 1 (this branch):** component + formatters + `site.js` filter extension + `_Table.cshtml` + WidgetGallery entry + flagship conversions proving both modes:
  - client mode: `StoreAdmin/Summary.cshtml` (already uses `data-sortable-table`) and one plain admin list (e.g. `ProfileAdmin/EmailProblems.cshtml`, the worst class-string offender);
  - server mode: `Ticket/Attendees.cshtml` (sort links + filter bar + pager, controller untouched).
- **Evaluation gate:** Peter gets a feel for the component on the converted views (PR preview deploy) before any wider rollout is planned.
- **Phase 2+ (only after the gate):** per-section migration waves as GitHub issues, sprint-batch sized (~10–15 views each).

## Testing

- Unit tests (`Humans.Web.Tests`): builder behavior, each formatter (incl. null handling and `data-sort-value` emission), server sort-link query-string preservation, EnumBadge registry fallback.
- Rendering: WidgetGallery demo page with one table per feature (sortable, filterable, server-mode mock) serves as the living visual check; `/test-site` smoke covers the converted flagship views.
- `site.js` filter logic: no JS test infra exists; keep the engine small and declarative, verified through the smoke tests.

## Resolved decisions (spec review, 2026-06-10)

1. **Currency convention** — bare `N2` (`#,##0.00`), no currency symbol. Store/Expenses views adopt this as they migrate.
2. **Burn-down guard** — not yet. Deferred until the component has been evaluated on the Phase 1 conversions.
