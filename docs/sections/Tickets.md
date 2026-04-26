<!-- freshness:triggers
  src/Humans.Application/Services/Tickets/**
  src/Humans.Infrastructure/Services/TicketQueryService.cs
  src/Humans.Domain/Entities/TicketOrder.cs
  src/Humans.Domain/Entities/TicketAttendee.cs
  src/Humans.Domain/Entities/TicketSyncState.cs
  src/Humans.Domain/Constants/TicketConstants.cs
  src/Humans.Infrastructure/Data/Configurations/Tickets/**
  src/Humans.Web/Controllers/TicketController.cs
-->
<!-- freshness:flag-on-change
  Vendor sync flow, Stripe-fee enrichment, auto-matching to humans by email, and EventParticipation derivation rules — review when Tickets services/entities/controller change.
-->

# Tickets — Section Invariants

External ticket vendor sync (orders + attendees), Stripe-fee enrichment, auto-matching to humans by email, event-participation derivation.

## Concepts

- **Ticket Orders** and **Ticket Attendees** are records synced from an external ticket vendor (Ticket Tailor in production, an in-process stub in dev). They are not manually created in the system.
- A **Ticket Order** represents a purchase (one per transaction). It carries the gross total, currency, vendor discount/donation line-item amounts, computed VAT (using VIP-split logic, not the vendor's tax line), and is enriched with Stripe fee data (payment method, Stripe fee, application fee) during sync via `IStripeService.GetPaymentDetailsAsync` keyed by the vendor's payment-intent id.
- A **Ticket Attendee** represents an individual ticket holder (one per issued ticket, multiple per order). Tickets above `TicketConstants.VipThresholdEuros` (315 EUR) are treated as VIP — the portion above the threshold is a VAT-free donation, the portion at-or-below is taxable at `TicketConstants.VatRate` (10%) inclusive.
- **Auto-matching** links orders to humans by buyer email and attendees to humans by attendee email. The lookup runs against the full `UserEmails` set (OAuth + verified + unverified) under `NormalizingEmailComparer` so gmail/googlemail aliases collapse; when one normalized email maps to multiple users the OAuth owner wins, otherwise the email is left unmatched.
- **Ticket Sync** is a background Hangfire job (`TicketSyncJob`, every 15 min by default, configurable via `TicketVendor:SyncIntervalMinutes`) that pulls order and attendee data from the vendor through `ITicketVendorService`.
- **Vendor connector** is a thin Infrastructure adapter behind `ITicketVendorService`. Production binds `TicketTailorService` (HTTP client against `https://api.tickettailor.com/v1`); non-production binds `StubTicketVendorService` (deterministic in-memory fixture with ~450 orders / ~600 tickets).

## Data Model

### TicketOrder

**Table:** `ticket_orders`

Ticket purchase order synced from vendor (one per purchase). Vendor-agnostic identity is `VendorOrderId`; payment-intent linkage is `StripePaymentIntentId` (from the vendor's `txn_id`). Stripe enrichment fields (`PaymentMethod`, `PaymentMethodDetail`, `StripeFee`, `ApplicationFee`) are filled in by `EnrichOrdersWithStripeDataAsync` after the upsert and preserved across re-syncs. `DonationAmount` (standalone vendor donation line items, VAT-exempt) and `DiscountAmount` (absolute value of vendor `gift_card` line items) come from the vendor; `VatAmount` is recomputed locally via VIP-split logic — the vendor's tax line is intentionally ignored because Ticket Tailor mis-applies 10% to the full ticket price.

Cross-domain nav `TicketOrder.MatchedUser → MatchedUserId` (Users/Identity). Target: strip nav, keep FK only.
Aggregate-local: `TicketOrder.Attendees`.

### TicketAttendee

**Table:** `ticket_attendees`

Individual ticket holder (issued ticket, multiple per order). Vendor-agnostic identity is `VendorTicketId`. `AttendeeEmail` is nullable — some vendor flows don't capture per-ticket email; in that case `MatchedUserId` stays null. `Status` is normalized from the vendor string into `TicketAttendeeStatus` (`Valid` / `CheckedIn` / `Void`). Only `Valid` and `CheckedIn` count as revenue or as ticket coverage.

Cross-domain nav `TicketAttendee.MatchedUser → MatchedUserId`. Target: strip nav, keep FK only.
Aggregate-local: `TicketAttendee.TicketOrder`.

### TicketSyncState

**Table:** `ticket_sync_states`

Singleton (`Id` always 1) tracking ticket sync operational state. `VendorEventId` records the event currently being synced. `LastSyncAt` doubles as the resume cursor passed to the vendor's `updated_at.gte` filter on the next run. `SyncStatus` is `Idle` / `Running` / `Error`; if a sync is found stuck in `Running` for >30 min, `GetDashboardStatsAsync` auto-resets it to `Error` with a stale-state message. `FullResync` clears `LastSyncAt` so the next run pulls all orders again.

### EventParticipation (derived, not owned)

`event_participations` is owned by the User section (per peterdrier/Humans PR #243). The Tickets section *derives* its `TicketSync`-sourced rows during each sync — see Triggers below. Tickets must never query or mutate the table directly.


## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| TicketAdmin, Board, Admin | View the ticket dashboard, orders, attendees, codes, gate list, sales aggregates, and the "Who Hasn't Bought" report (controller-wide policy `TicketAdminBoardOrAdmin`) |
| TicketAdmin, Admin | Trigger an incremental ticket sync. Export attendee/order CSV. Generate discount codes for campaigns (Campaign section, policy `TicketAdminOrAdmin`) |
| Admin | Trigger a full re-sync (clears the `LastSyncAt` cursor). Open and submit the participation backfill page (`/Tickets/Participation/Backfill`) |

## Invariants

- Ticket orders and attendees are synced from the external vendor — they cannot be manually created or edited from this app.
- Stripe enrichment (`PaymentMethod`, `PaymentMethodDetail`, `StripeFee`, `ApplicationFee`) is preserved across re-syncs and only re-run for orders that have a `StripePaymentIntentId` and are still missing fee data; if `IStripeService.IsConfigured` is false the pass is silently skipped.
- VAT is computed locally per order using VIP-split logic on attendees with `Status` in (`Valid`, `CheckedIn`); orders not in `Paid` status carry `VatAmount = 0`. The vendor's tax line is intentionally ignored.
- Auto-matching uses normalized email comparison (`NormalizingEmailComparer`) against the full UserEmails set; collisions resolve to the OAuth owner or stay unmatched. Buyer match writes `TicketOrder.MatchedUserId`; attendee match writes `TicketAttendee.MatchedUserId` independently.
- A user "has a ticket" iff at least one `Valid` or `CheckedIn` `TicketAttendee` is matched to their `UserId`. Buyer-only matches do not count — purchasing tickets for others does not give the buyer ticket coverage.
- `TicketSyncState` is a singleton row (Id = 1). `LastSyncAt` is the resume cursor passed back to the vendor as `updated_at.gte` on the next run. A sync stuck in `Running` for >30 minutes is auto-reset to `Error` by `GetDashboardStatsAsync` (crash recovery).

## Negative Access Rules

- Board **cannot** trigger any sync (incremental or full), export CSV, or open the participation backfill page.
- TicketAdmin **cannot** trigger a Full Re-sync or open the participation backfill page (both `AdminOnly`).
- Nobody can edit ticket configuration (vendor `EventId`, API key, sync interval) from inside the app — those values come from `appsettings`'s `TicketVendor` section and the `TICKET_VENDOR_API_KEY` environment variable, set at deploy time.
- Regular humans have no access to `/Tickets/*` (dashboard, orders, attendees, codes, gate list, who-hasn't-bought, sales aggregates) — the controller-wide policy is `TicketAdminBoardOrAdmin`.

## Triggers

- When ticket sync runs: vendor orders and issued tickets are upserted into `ticket_orders` / `ticket_attendees` (existing rows keyed by `VendorOrderId` / `VendorTicketId` retain their `Id` and their already-enriched fields), Stripe fees are enriched for newly-paid orders, VAT is recomputed for every order, vendor discount codes are matched to `CampaignGrants` via `ICampaignService.MarkGrantsRedeemedAsync`, and event participation is reconciled. On success, `LastSyncAt` is set to the start-of-sync instant and `_cache.InvalidateTicketCaches()` runs (clears `TicketDashboardStats`, `UserIdsWithTickets`, `ValidAttendeeEmails`, plus the per-event `TicketEventSummary:{eventId}`).
- `EventParticipation` derivation (Ticket Tailor's active vendor event only, scoped to `EventSettings.Year` from `IShiftManagementService.GetActiveAsync`):
  - For each user with at least one matched attendee: any `CheckedIn` ticket → `ParticipationStatus.Attended`; otherwise any `Valid` ticket → `ParticipationStatus.Ticketed`. Both write through `IUserService.SetParticipationFromTicketSyncAsync` with `ParticipationSource.TicketSync`.
  - For each prior `(TicketSync, Ticketed)` row in the year: if the user no longer has any `Valid`/`CheckedIn` matched ticket, the row is removed via `IUserService.RemoveTicketSyncParticipationAsync`. `Attended` rows are never removed by sync — being checked in is permanent.
  - Self-declared `NotAttending` rows are owned by the User section and are never overwritten or removed by ticket sync; ticket purchase does not flip a user's prior `NotAttending` declaration in this section's code path.
- "Who Hasn't Bought" lists active Volunteers-team members (via `ITeamService.GetActiveMemberUserIdsAsync(SystemTeamIds.Volunteers)`) minus those whose current-year `EventParticipation.Status` is `NotAttending`. `HasTicket` is true when the user appears in the union of matched attendee user-ids and matched order user-ids (see `GetAllMatchedUserIdsAsync`).
- The Volunteer Ticket Coverage card on `/Tickets` divides matched-attendee Volunteers (`UserIdsWithTickets`) by total active Volunteers — buyer-only matches are excluded by construction.
- Code redemption: vendor discount codes attached to orders are pushed back to Campaigns via `ICampaignService.MarkGrantsRedeemedAsync` so each `CampaignGrant.RedeemedAt` reflects the order's `PurchasedAt`.

### TicketDashboardStats cache (ghost cache key)

`CacheKeys.TicketDashboardStats` is **invalidator-only**. `TicketQueryService.GetDashboardStatsAsync` is the canonical producer of the `TicketDashboardStats` DTO and is called fresh on every `TicketController.Index` request — there is no `_cache.GetOrCreateAsync(TicketDashboardStats, …)` read-through. The cache key, the `Metadata` row (5 min, Static), and `MemoryCacheExtensions.InvalidateTicketDashboardStats` (called from `TicketSyncService` via `InvalidateTicketCaches()`) exist so a future caching wrapper can be added without changing the invalidation seam. Treat this as a documented placeholder, not a live cache; see `docs/architecture/service-data-access-map.md` for the cross-cutting rule.

## Cross-Section Dependencies

- **Campaigns:** `ICampaignService` — Tickets reads campaign + grant data for the Codes page (`GetCodeTrackingAsync`) and pushes redemptions back during sync (`MarkGrantsRedeemedAsync`). Discount-code *generation* lives in the Campaigns section's `CampaignController` (which calls `ITicketVendorService.GenerateDiscountCodesAsync` directly); the `/Tickets/Codes` page only reports redemption status, it does not create codes.
- **Users/Identity:** `IUserService` — `GetAllUsersAsync` / `GetByIdsAsync` for stitching matched-user names into orders/attendees lists; `SetParticipationFromTicketSyncAsync` / `RemoveTicketSyncParticipationAsync` / `GetAllParticipationsForYearAsync` / `BackfillParticipationsAsync` for derived `EventParticipation` writes (User section owns `event_participations` per peterdrier/Humans PR #243).
- **Profiles:** `IUserEmailService` — `GetAllUserEmailLookupEntriesAsync` builds the sync-time email→userId index; `GetVerifiedEmailsForUserAsync` and `SearchUserIdsByVerifiedEmailAsync` back the per-user ticket probe and the Who-Hasn't-Bought email search; `GetNotificationEmailsByUserIdsAsync` hydrates the report. `IProfileService.GetByUserIdsAsync` supplies `MembershipTier`.
- **Teams:** `ITeamService.GetActiveMemberUserIdsAsync(SystemTeamIds.Volunteers)` for the Volunteers cohort used by both the dashboard's coverage card and the Who-Hasn't-Bought list; `GetActiveNonSystemTeamNamesByUserIdsAsync` for team labels on the report.
- **Shifts:** `IShiftManagementService.GetActiveAsync` — active-event lookup for the year used by `EventParticipation` derivation and by the `Participation/Backfill` page (replaces the prior direct `EventSettings` read, PR #545c).
- **Budget:** `IBudgetService` — `GetActiveYearAsync` + `ComputeBudgetSummary` feed the dashboard's break-even calculation; `TicketingBudgetService` bridges paid-order data into Budget's projection writes via `ITicketingBudgetRepository` (Tickets-owned narrow read surface) without ever touching `budget_*` tables directly.
- **GDPR:** `TicketQueryService` implements `IUserDataContributor`, contributing the `TicketOrders` and `TicketAttendeeMatches` slices to the per-user data export.
- **Stripe (Infrastructure):** `IStripeService.GetPaymentDetailsAsync` looks up payment-intent details to populate `PaymentMethod` / `PaymentMethodDetail` / `StripeFee` / `ApplicationFee` per order. Configuration is via `STRIPE_API_KEY` env var; if unset, enrichment is skipped silently and the dashboard's fee breakdown stays empty.

## Architecture

**Owning services (all in `Humans.Application.Services.Tickets`):**
- `TicketQueryService` — read-side dashboard / orders / attendees / codes / who-hasn't-bought / sales aggregates / per-user ticket probes; also implements `IUserDataContributor` for GDPR export.
- `TicketSyncService` — vendor sync orchestrator (orders + attendees upsert, Stripe enrichment, VAT compute, code redemption push, EventParticipation derivation).
- `TicketingBudgetService` — Tickets→Budget bridge (feeds completed-week paid-sales totals into Budget projections via `IBudgetService`).

**Owned tables:** `ticket_orders`, `ticket_attendees`, `ticket_sync_states`

**Vendor connectors (Infrastructure-only, behind `ITicketVendorService`):**
- `TicketTailorService` — production HTTP client; bound when `IHostEnvironment.IsProduction()`.
- `StubTicketVendorService` — deterministic in-memory fixture; bound in dev/QA/preview. The stub fills in placeholder `EventId`/`ApiKey` so `TicketVendorSettings.IsConfigured` returns true even without env vars.

**Stripe connector (Infrastructure-only):** `StripeService` (`IStripeService`) wraps the Stripe SDK and is consumed by `TicketSyncService` for fee enrichment.

**Status:** (A) Fully §15-compliant. All three section services live in `Humans.Application.Services.Tickets` and route every database read/write through `ITicketRepository` (or, for the Budget bridge, `ITicketingBudgetRepository`). Neither `TicketQueryService.cs`, `TicketSyncService.cs`, nor `TicketingBudgetService.cs` imports `Microsoft.EntityFrameworkCore` or references `HumansDbContext`. Umbrella issue nobodies-collective/Humans#545 closed by sub-tasks #545a (TicketQueryService → Application), #545b (TicketingBudgetService + `ITicketingBudgetRepository`), #545c (TicketSyncService → Application + `IShiftManagementService` / `IUserService` routing). `ITicketVendorService` connector split landed in peterdrier/Humans PR #277.

### Repositories

- **`ITicketRepository`** (Tickets-owned) — owns reads/writes for `ticket_orders`, `ticket_attendees`, `ticket_sync_states`. Aggregate-local navs kept (`TicketOrder.Attendees`, `TicketAttendee.TicketOrder`). Cross-domain `MatchedUser` navs are still present on the entities and are not currently `Include`-d by any repo method — joining to `User` is done in-memory via `IUserService.GetByIdsAsync` after the read. Stripping the navs entirely is the only outstanding §15 cleanup for this section.
- **`ITicketingBudgetRepository`** (Tickets-owned, consumed by `TicketingBudgetService`) — narrow read surface for paid-order projections.

### Touch-and-clean guidance

- New cross-section data needs always go through the owning section's interface — `ICampaignService`, `IUserService`, `IProfileService`, `IUserEmailService`, `ITeamService`, `IShiftManagementService`, `IBudgetService`. Do not add `Include`-chains off `MatchedUser` to `ITicketRepository` even though the navs still exist; project in memory by `MatchedUserId`.
- Cache calls (`TicketCount`, `UserIdsWithTickets`, `ValidAttendeeEmails`, plus the invalidation seam `InvalidateTicketCaches()`) stay inside `TicketQueryService` / `TicketSyncService`. Do not push `IMemoryCache` into controllers or view components. Prefer adding to `MemoryCacheExtensions.InvalidateTicketCaches` over sprinkling `_cache.Remove` calls so the eventual caching wrapper has a single seam.
- The `TicketDashboardStats` cache key is invalidator-only (see *TicketDashboardStats cache* under Triggers). Don't flip `GetDashboardStatsAsync` to `GetOrCreateAsync` without weighing the staleness trade-off — invalidation already runs on every successful sync, but on-demand staleness on the dashboard during sync windows is currently acceptable.
- When extending the Tickets→Budget bridge, add new read methods to `ITicketingBudgetRepository` (Tickets-owned). Projection/line-item writes remain Budget-owned and must route through `IBudgetService`.
- The vendor split is doctrinal: business code talks to `ITicketVendorService` and never to "Ticket Tailor" directly. Any new vendor capability needs an interface method first, then a `TicketTailorService` impl plus a deterministic `StubTicketVendorService` impl so dev/preview environments still exercise the call.
