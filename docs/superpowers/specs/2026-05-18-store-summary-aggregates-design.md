# Store Summary — admin aggregate views

**Status:** Draft, brainstorm complete, awaiting plan.
**Date:** 2026-05-18
**Owner:** Peter Drier

## Purpose

Fill in the existing `/Store/Summary` Phase 5 stub with admin-facing aggregate views over the year's store orders. Three cuts on the same data: by camp, by item, and a camp × product cross-tab. Lets FinanceAdmin / StoreAdmin see "what's been ordered, by whom, who has paid" at a glance.

This is not a new section — it completes work scoped in the 2026-04-30 store-section design (historical, since removed — current invariants live in `docs/sections/Store.md`).

## Scope

In:
- `/Store/Summary` page, three stacked sections (by-camp / by-item / cross-tab) on a single year.
- Year selector at the top, defaulting to the active event year.
- Paid-status filter on the by-camp section (All / Paid / Partial / Unpaid).

Out:
- CSV / Excel export. (Eyeball-only for v1.)
- Filtering by order state (Open vs InvoiceIssued). Both are shown together.
- Camp-lead-facing aggregate views. Camp leads continue to see only their own order on `/Store`.
- Cross-year roll-ups.

## Audience and auth

FinanceAdmin / StoreAdmin / Admin only. Same gating as `/Store/Admin/Orders`.

## Page layout

Single Razor page at `/Store/Summary`, three sections in order:

### 1. By-camp table
One row per camp with an order in the selected year:

| Column | Source |
|---|---|
| Camp name | `ICampService` (batched lookup from `CampSeasonId`) |
| Label | `StoreOrder.Label` (rarely populated in practice — one order per camp per year) |
| State | `StoreOrder.State` |
| Total due (€) | `BalanceCalculator.Compute(order).LinesSubtotalEur + VatTotalEur + DepositTotalEur` |
| Paid (€) | `PaymentsTotalEur` |
| Balance (€) | `BalanceEur` |

- Sortable columns.
- Camp-name cell links to `/Store/Order/{id}`.
- Paid-status dropdown filter (All / Paid (balance ≤ 0) / Partial (0 < paid < total) / Unpaid (paid = 0)). Client-side, re-renders this section only.

### 2. By-item table
One row per product that has at least one line in the selected year:

| Column | Source |
|---|---|
| Product name | `StoreProduct.Name` |
| Total qty | sum of `StoreOrderLine.Qty` across the year |
| Total revenue (€) | sum of `Qty * UnitPriceSnapshot + line VAT + line deposit` |

- Includes deactivated products if they have lines.
- No per-camp breakdown here — the cross-tab covers that.

### 3. Cross-tab (camps × products)
Matrix:
- Rows: camps (alphabetical).
- Columns: products that have any lines in the year (alphabetical).
- Cells: `Qty` for that camp × product combination. Blank when 0.
- Row totals: total units per camp.
- Column totals: total units per product (matches by-item table).

At project scale (handful of camps × ~15 SKUs) this fits in one HTML table; no virtualization or pagination needed.

## Service surface

Replace the existing unimplemented stub `IStoreService.GetAllOrderSummariesAsync(int year, ct)` with a single composite method (no production callers exist):

```csharp
Task<StoreSummaryDto> GetStoreSummaryAsync(int year, CancellationToken ct = default);
```

`StoreSummaryDto` (new, in `Humans.Application.Services.Store.Dtos`):

```csharp
public sealed record StoreSummaryDto(
    int Year,
    IReadOnlyList<OrderSummaryDto> ByCamp,
    IReadOnlyList<ProductAggregateDto> ByItem,
    StoreCrossTabDto CrossTab);

public sealed record ProductAggregateDto(
    Guid ProductId,
    string ProductName,
    int TotalQty,
    decimal TotalRevenueEur);

public sealed record StoreCrossTabDto(
    IReadOnlyList<StoreCrossTabColumn> Products,
    IReadOnlyList<StoreCrossTabRow> Camps);

public sealed record StoreCrossTabColumn(Guid ProductId, string ProductName, int TotalQty);
public sealed record StoreCrossTabRow(
    Guid CampSeasonId,
    string CampName,
    int TotalQty,
    IReadOnlyDictionary<Guid, int> QtyByProduct); // key: ProductId
```

`OrderSummaryDto` already exists and is reused unchanged.

## Data flow

One round trip:

1. Repository loads all `StoreOrder`s for the year with `Lines` and `Payments` (`AsNoTracking`, eager-loaded navs are aggregate-local — allowed per design-rules).
2. Repository also loads `StoreProduct`s for the year for product names (active + inactive).
3. Application layer batches camp-season → camp-name resolution via `ICampService` (one call, list of GUIDs in, list of names out — existing pattern, no per-row N+1).
4. All three projections are built in-memory from the loaded set using `BalanceCalculator.Compute` for the by-camp totals and straight LINQ for the rest.

Single-server deployment + ~500-user scale + handful of orders per year → no caching layer, no pagination. In-memory aggregation per request is the cheapest, simplest path (per CLAUDE.md "prefer in-memory over query optimization" guidance).

## Authorization

`/Store/Summary` route is gated by the same policy that gates `/Store/Admin/Orders` (FinanceAdmin / StoreAdmin / Admin). No resource-based handler needed — this is org-wide aggregate data, not per-camp-season.

## Testing

Unit tests (Application layer):
- Empty year → empty `ByCamp`, `ByItem`, `CrossTab.Camps`, `CrossTab.Products`.
- Single order, single line → all three projections agree on the qty and total.
- Multiple camps, overlapping products → cross-tab cells, row totals, and by-item column totals are all consistent.
- Paid / partial / unpaid orders all surface in by-camp with correct `BalanceEur`.
- Deactivated product with a line → appears in by-item and cross-tab.

No new integration tests required — the data shape is well-covered by existing `BalanceCalculator` tests; this layer is pure projection.

## Open items / non-decisions

- Cross-tab orientation (camps as rows vs. products as rows) is a UX choice; default is camps-as-rows. Trivially swappable in the view if it reads better the other way.
- Sort defaults: by-camp = camp name asc; by-item = qty desc; cross-tab = both axes alphabetical.
