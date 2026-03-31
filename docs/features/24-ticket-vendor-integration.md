# 24. Ticket Vendor Integration

## Business Context

Nobodies Collective sells event tickets through external vendors (currently TicketTailor). Discount codes are distributed to humans via the campaign system. This feature creates a dedicated Tickets section giving the ticketing team (TicketAdmin role) a dashboard with sales data, revenue metrics, attendee tracking, and operational tools.

## Data Model

### New Entities

- **TicketOrder** — one record per purchase from vendor. Fields: VendorOrderId, BuyerName, BuyerEmail, MatchedUserId (auto-matched by email), TotalAmount, Currency, DiscountCode, DiscountAmount, DonationAmount, VatAmount, PaymentStatus, VendorEventId, PurchasedAt, SyncedAt, StripePaymentIntentId, PaymentMethod, PaymentMethodDetail, StripeFee, ApplicationFee
- **TicketAttendee** — one per issued ticket (multiple per order). Fields: VendorTicketId, TicketOrderId, AttendeeName, AttendeeEmail, MatchedUserId, TicketTypeName, Price, Status (Valid/Void/CheckedIn), VendorEventId, SyncedAt
- **TicketSyncState** — singleton (Id=1) tracking sync operational state. Fields: VendorEventId, LastSyncAt, SyncStatus (Idle/Running/Error), LastError, StatusChangedAt

### Modified Entities

- **CampaignGrant** — added `RedeemedAt` (Instant?) set when sync discovers the grant's discount code was used in an order

## Architecture

- **ITicketVendorService** — vendor-agnostic interface (Application layer)
- **TicketTailorService** — TicketTailor API client (Infrastructure layer). Basic Auth, cursor-based pagination. Captures `txn_id` (Stripe PaymentIntent ID) and discount amounts from line items.
- **IStripeService / StripeService** — Stripe API client (read-only). Looks up PaymentIntent → Charge → BalanceTransaction to get payment method type and fee breakdown (Stripe processing fee vs TT application fee).
- **ITicketSyncService / TicketSyncService** — sync orchestration: fetch orders/attendees, upsert, email-match to users, match discount codes to campaign grants, enrich orders with Stripe fee data, compute VAT using VIP split logic
- **TicketSyncJob** — Hangfire recurring job (default every 15 min)

## VAT and Donation Tracking

### VIP Ticket Split
Tickets priced above 315 EUR (the VIP threshold, `TicketConstants.VipThresholdEuros`) are split:
- First 315 EUR = taxable ticket revenue at 10% Spanish event VAT
- Remainder = VAT-free donation (VIP premium)

### Data Fields
- **DonationAmount** on TicketOrder — standalone donations parsed from TicketTailor `donation` line items (VAT-exempt)
- **VatAmount** on TicketOrder — correctly computed VAT using the VIP split logic (ignores TT's own incorrect tax line item)

### Reporting
- `/Tickets/SalesAggregates` — weekly and quarterly views with real Donations, VIP Donations, VAT, and Net columns
- `/Tickets/Orders` — donation and VAT columns per order
- `/Tickets/Attendees` — VIP badge and taxable/donation split per attendee

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
| `/Tickets/SalesAggregates` | Weekly (Mon–Sun) and quarterly (Spanish tax Q1–Q4) aggregate reports with real VAT/donation data |

## Dashboard Widgets

### Ticketing Dashboard (admin)

- **Avg. Net Price** — net revenue divided by tickets sold (Stripe/TT fees deducted). Handles zero tickets gracefully.
- **Volunteer Ticket Coverage** — percentage and count of active Volunteers team members matched as ticket attendees. Progress bar with color thresholds (green >= 75%, yellow >= 50%, red < 50%). Links to "Who Hasn't Bought?" detail view.

### Homepage Dashboard (per-user)

- **Your Ticket** card in sidebar — shows ticket status for the current user:
  - **Has ticket(s)**: confirmation message with count when multiple
  - **No ticket**: CTA button linking to `tickets.nobodies.team`
  - **Not configured**: warning message
- Matching checks attendee records via `MatchedUserId`, then falls back to all verified user emails against `TicketAttendee.AttendeeEmail` (case-insensitive). A buyer who purchased tickets for others does NOT count as having a ticket.

## Configuration

- `TicketVendor:EventId` and `TicketVendor:SyncIntervalMinutes` in appsettings.json
- `TICKET_VENDOR_API_KEY` environment variable (sensitive, not in appsettings)
- `STRIPE_API_KEY` environment variable (read-only restricted key for fee tracking)

## Related Features

- [22. Campaigns](22-campaigns.md) — discount code distribution and redemption tracking
- [23. Membership Status](23-membership-status.md) — "Who Hasn't Bought?" uses active volunteer status
