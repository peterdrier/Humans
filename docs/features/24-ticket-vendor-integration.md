# 24. Ticket Vendor Integration

## Business Context

Nobodies Collective sells event tickets through external vendors (currently TicketTailor). Discount codes are distributed to humans via the campaign system. This feature creates a dedicated Tickets section giving the ticketing team (TicketAdmin role) a dashboard with sales data, revenue metrics, attendee tracking, and operational tools.

## Data Model

### New Entities

- **TicketOrder** — one record per purchase from vendor. Fields: VendorOrderId, BuyerName, BuyerEmail, MatchedUserId (auto-matched by email), TotalAmount, Currency, DiscountCode, DiscountAmount, PaymentStatus, VendorEventId, PurchasedAt, SyncedAt, StripePaymentIntentId, PaymentMethod, PaymentMethodDetail, StripeFee, ApplicationFee
- **TicketAttendee** — one per issued ticket (multiple per order). Fields: VendorTicketId, TicketOrderId, AttendeeName, AttendeeEmail, MatchedUserId, TicketTypeName, Price, Status (Valid/Void/CheckedIn), VendorEventId, SyncedAt
- **TicketSyncState** — singleton (Id=1) tracking sync operational state. Fields: VendorEventId, LastSyncAt, SyncStatus (Idle/Running/Error), LastError, StatusChangedAt

### Modified Entities

- **CampaignGrant** — added `RedeemedAt` (Instant?) set when sync discovers the grant's discount code was used in an order

## Architecture

- **ITicketVendorService** — vendor-agnostic interface (Application layer)
- **TicketTailorService** — TicketTailor API client (Infrastructure layer). Basic Auth, cursor-based pagination. Captures `txn_id` (Stripe PaymentIntent ID) and discount amounts from line items.
- **IStripeService / StripeService** — Stripe API client (read-only). Looks up PaymentIntent → Charge → BalanceTransaction to get payment method type and fee breakdown (Stripe processing fee vs TT application fee).
- **ITicketSyncService / TicketSyncService** — sync orchestration: fetch orders/attendees, upsert, email-match to users, match discount codes to campaign grants, enrich orders with Stripe fee data
- **TicketSyncJob** — Hangfire recurring job (default every 15 min)

## Authorization

| Action | TicketAdmin | Admin | Board |
|--------|:-----------:|:-----:|:-----:|
| View dashboard/orders/attendees/codes | Yes | Yes | Yes |
| Trigger sync, CSV exports, generate codes | Yes | Yes | No |

## Routes

| Route | Purpose |
|-------|---------|
| `/Tickets` | Summary dashboard with cards, Chart.js daily sales chart, problems |
| `/Tickets/Orders` | Paginated order list with search/sort/filter |
| `/Tickets/Attendees` | Paginated attendee list with search/sort/filter |
| `/Tickets/Codes` | Discount code redemption tracking tied to campaigns |
| `/Tickets/GateList` | Stub for June implementation |
| `/Tickets/WhoHasntBought` | Active humans without ticket purchases |

## Configuration

- `TicketVendor:EventId` and `TicketVendor:SyncIntervalMinutes` in appsettings.json
- `TICKET_VENDOR_API_KEY` environment variable (sensitive, not in appsettings)
- `STRIPE_API_KEY` environment variable (read-only restricted key for fee tracking)

## Related Features

- [22. Campaigns](22-campaigns.md) — discount code distribution and redemption tracking
- [23. Membership Status](23-membership-status.md) — "Who Hasn't Bought?" uses active volunteer status
