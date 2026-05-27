# Store — Team Orders (Non-Billable Counterparty)

**Status:** Design approved, pending implementation.
**Section:** Store.
**Date:** 2026-05-27.

## Problem

`StoreOrder` currently has one counterparty type: a `CampSeason`. The Store's value to the org isn't just camp billing — it's also the consolidated demand signal used to size supplier purchases. Departments consume from the same suppliers (kitchen, build, first-aid, etc.) but the org covers their cost, so they have no invoice path. Today their demand is invisible to `/Store/Admin/Summary` and to suppliers.

## Goal

Let department coordinators place orders against the existing catalog so the supplier-aggregation totals reflect the full need. Teams never get invoiced, never pay, and stay in `Open` indefinitely.

## Non-goals

- Sub-team orders. Only departments (top-level teams), placed by their coordinator.
- A lifecycle for team orders beyond `Open`. Manual close-out post-event is out of scope.
- Cost allocation memos / chargeback. Not in this change.
- Touching the camp-side ordering flow beyond the lazy `Year` backfill described below.

## Conceptual model

A `StoreOrder` has exactly one counterparty: either a `CampSeasonId` (billable, full lifecycle) or a `TeamId` (non-billable, never leaves `Open`). The DB does not enforce the "exactly one" invariant — `StoreService` does, at write time.

A team order:
- Is scoped to one `Year` (the active event year at create time).
- Reuses `StoreProduct` catalog and the per-product `OrderableUntil` deadline gate.
- Has no counterparty fields (`CounterpartyName`/`VatId`/`Address`/`CountryCode`/`Email`), no payments, no invoice.
- Is authored by the team's coordinator. StoreAdmin / FinanceAdmin / Admin retain the superset view.
- Stays in `Open` forever. Implicit close-out is "no product has an open `OrderableUntil` anymore".

One order per team per year. Multiple orders per team per year are not supported.

## Data model

### `StoreOrder` (modified)

| Change | Detail |
|---|---|
| `CampSeasonId` | `Guid` → `Guid?` (nullable). |
| `TeamId` | New `Guid?`. FK-only, no navigation, no DB FK constraint (per `memory/architecture/no-cross-section-ef-joins.md`). |
| `Year` | New `int`. Always set on write. |

Existing columns unchanged. No new tables. No FKs across sections.

**Invariant (service-enforced, not DB-enforced):** exactly one of `CampSeasonId` / `TeamId` is non-null on any `StoreOrder`.

**Indexes:** add `TeamId` (non-filtered is fine at ~500 users). Keep existing `CampSeasonId` index.

### `Year` backfill

No backfill script. New writes populate `Year` lazily:

- **Team orders** — set at creation time to the active event year (resolved via `IShiftManagementService.GetActiveAsync()`).
- **Camp orders** — existing rows keep `Year = 0` (or whatever EF defaults to) until they're next saved through the service, at which point the service writes `CampSeason.Year` into the column.
- **The summary report** must tolerate `Year = 0` rows until they're touched — they're old orders that haven't been re-saved since the column existed. Filtering is `(Year == requestedYear) OR (CampSeasonId in seasons-for-requestedYear AND Year == 0)`. (This fallback can be removed once all rows have been touched.)

## Service surface

### `IStoreService` additions

```csharp
Task<Guid> CreateTeamOrderAsync(Guid teamId, Guid actorUserId, CancellationToken ct = default);
Task<OrderDto?> GetOrderForTeamAsync(Guid teamId, CancellationToken ct = default);
```

### `IStoreService` modifications (guard rails)

The following methods reject team-owned orders at the top of the method with `InvalidOperationException("Team orders are non-billable")`:

- `UpdateCounterpartyAsync` / `UpdateCounterpartyWithResultAsync`
- `RecordManualPaymentAsync`
- `CreateStripeCheckoutSessionAsync`
- `RecordStripePaymentAsync`
- `HandleStripeCheckoutWebhookEventAsync` (only matters if a malformed event references a team order id — defense-in-depth)
- `IssueInvoiceAsync`

`AddLineAsync` / `RemoveLineAsync` / `AddLineWithResultAsync` / `RemoveLineWithResultAsync` — unchanged. The `OrderableUntil` gate is per-product and is independent of counterparty type.

### `GetIndexDataAsync` (modified)

`/Store` index lists the user's orderable counterparties. Today: camps-you-lead. Becomes: camps-you-lead + teams-you-coordinate. Service merges the two collections and resolves each into a unified item with display name, type (Camp / Team), and either the existing order or "Create order" affordance.

### DTO changes

`OrderDto`:
- Add `Guid? TeamId`.
- `CampSeasonId` becomes `Guid?` (already exposed as Guid; change to nullable).
- Add `string CounterpartyDisplayName` — resolved by the service (camp name OR team name).
- Add `StoreOrderCounterpartyType` enum (`Camp` / `Team`) for view-side branching.

`StoreIndexData` — gains the team list (or extends the existing camp list with a typed item; structure decided during impl).

## Authorization

`StoreOrderAuthorizationHandler` extends its current camp-lead check:

```
if (order.CampSeasonId is not null)   → existing camp-lead check
if (order.TeamId is not null)          → ITeamServiceRead.IsCoordinatorAsync(userId, teamId)
```

Operation gating for team orders:
- `View`, `Create`, `AddLine`, `RemoveLine` — allowed for the coordinator.
- `EditCounterparty`, `Pay` — denied for team orders. UI does not surface them; handler denies as defense-in-depth.

StoreAdmin / FinanceAdmin / Admin — unchanged. They retain full access (`memory/code/admin-role-superset.md`).

`StoreCatalogAdmin` policy on `/Store/Admin/*` — unchanged.

## Cross-section dependencies

Adds `ITeamServiceRead` to Store's dependency set. Methods required (audit during impl, add only what's missing):

- `GetTeamByIdAsync(Guid teamId)` — name + parent-team check (must be a department, not a sub-team).
- `GetCoordinatedTeamsAsync(Guid userId)` — populates the `/Store` index list.
- `IsCoordinatorAsync(Guid userId, Guid teamId)` — used by `StoreOrderAuthorizationHandler`.

If any of these don't exist on `ITeamServiceRead` today, they're added there (not on Store's side) per the section-read-write-split rule. Implementation reuses existing methods where possible.

`ICampService` and `IShiftManagementService` — unchanged.

## Routing / UI

| Route | Change |
|---|---|
| `/Store` | Lists camps-you-lead and teams-you-coordinate together. Each entry deep-links to its single order (or a "Create order" affordance if none exists). |
| `/Store/Order/{id}` | Branches on counterparty type. Camp orders unchanged. Team orders render: header (team name, year), lines table, add/remove-line form, "Non-billable — supplier aggregation only" footer. No counterparty form, no Pay button, no payments list, no Issue Invoice. |
| `/Store/Admin/Summary` | Merges team rows into the by-counterparty table and the cross-tab. A "Type" column distinguishes Camp / Team rows. Suppliers totals are unambiguous because the column makes the counterparty type explicit. |
| `/Store/Admin/Catalog` | Unchanged. |
| `/Store/Admin/Orders` | Unchanged (still the Phase-5 stub). |

## Audit

Reuse existing event types: `StoreOrderCreated`, `StoreLineAdded`, `StoreLineRemoved`. The audit row's counterparty descriptor is "Camp: {name}" or "Team: {name}" — picked by the service. No new event vocabulary.

## Lifecycle

Team orders stay in `Open` indefinitely. There is no `Submitted` / `Closed` / `Fulfilled` state. The implicit "this order is done" signal is supplier-side: once every product on the catalog has passed its `OrderableUntil`, the order is effectively read-only by the deadline gate alone.

## Out of scope / explicit non-features

- DB check constraint on the "exactly one counterparty" invariant. Service-layer only.
- Migration backfill of `Year` for existing camp orders. Lazy fill on next save.
- Sub-team orders. Coordinators only, departments only.
- Cost allocation memos / chargeback to the parent org. Future work, not this change.
- A separate `store_team_orders` table. Polymorphic `StoreOrder` is the cheaper variant of the same thing.

## Testing

- `StoreService.CreateTeamOrderAsync` — sets `TeamId`, `Year`, `State = Open`; rejects sub-teams; rejects when actor isn't a coordinator (defense-in-depth — auth handler is the primary gate).
- Guard-rail tests on `RecordManualPaymentAsync` / `IssueInvoiceAsync` / `CreateStripeCheckoutSessionAsync` / `UpdateCounterpartyAsync` — throw on team-owned orders.
- `Year` backfill on next camp order save (mutating the row writes `CampSeason.Year` into the column).
- `StoreOrderAuthorizationHandler` — coordinator can `AddLine` on their team's order; non-coordinator denied; `EditCounterparty` and `Pay` denied for team orders regardless of role (except FinanceAdmin/Admin where applicable — though those ops are no-ops for team orders anyway).
- Summary cross-tab — camp + team rows aggregate per product; per-counterparty type distinction is preserved in the by-counterparty list.

## Architecture impact

No layer rule changes. Repository surface grows by one or two methods (team-scoped order lookup). All cross-section access goes through `ITeamServiceRead` per the section-read-write-split rule. No new tables, no new connectors.
