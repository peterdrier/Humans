# Ticket Vendor Integration — Design Spec

**Date**: 2026-03-15
**GitHub Issue**: [#117 — Add ticket vendor integration](https://github.com/nobodies-collective/Humans/issues/117)
**Status**: Draft

## Business Context

Nobodies Collective sells event tickets through external vendors (currently TicketTailor). Discount codes are distributed to humans via the campaign system. Today, the ticketing team has no visibility into which codes were redeemed, which humans bought tickets, or basic revenue metrics — everything requires manual checking in the vendor dashboard.

This feature creates a dedicated **Tickets section** in the application that gives the ticketing team (TicketAdmin role) a dashboard with sales data, revenue metrics, attendee tracking, and operational tools like gate lists and "who hasn't bought?" reports.

## Scope

### Scale
- **2,000–3,000 tickets** across **1,500+ orders** for a single event
- All table views must support **search, sorting, and server-side pagination**
- Dashboard summary page focuses on stats, charts, and problems — detail is on separate sub-pages

### In Scope
- Vendor-agnostic `ITicketVendorService` interface with TicketTailor as first implementation
- Hangfire job polling TicketTailor API for orders and issued tickets
- New `TicketOrder` and `TicketAttendee` entities with auto-matching to humans by email
- Top-level `/Tickets` section: summary dashboard + separate detail pages for orders, attendees, code tracking
- **Daily ticket sales chart** with 7-day rolling average (for projecting total sales)
- Campaign integration: discount code redemption tracking, API-based code generation
- `TicketAdmin` role for access control
- Single active event model (configured in settings)
- **Comprehensive test coverage** — this feature cannot be properly QA-tested (no real TT data in QA environment), so unit tests with realistic mock data are critical for confidence in production

### Out of Scope
- Webhook receiver (future enhancement)
- Direct Stripe integration (rely on TT API data for payment info)
- Multi-event simultaneous tracking
- Historical event data retention across vendor switches
- Payment processing or refund handling

## Data Model

### New Entities

#### TicketOrder
One record per purchase from the vendor.

| Field | Type | Notes |
|-------|------|-------|
| Id | Guid | PK |
| VendorOrderId | string | Unique, from vendor API |
| BuyerName | string | |
| BuyerEmail | string | |
| MatchedUserId | Guid? | FK → User, auto-matched by email |
| TotalAmount | decimal | |
| Currency | string | e.g. "EUR" |
| DiscountCode | string? | Voucher/discount code used, if any |
| PaymentStatus | TicketPaymentStatus (enum) | Paid, Pending, Refunded |
| VendorEventId | string | Event ID at time of sync (for future multi-event) |
| VendorDashboardUrl | string? | Deep link to vendor order page |
| PurchasedAt | Instant | From vendor data |
| SyncedAt | Instant | When this record was last synced |

Navigation: `ICollection<TicketAttendee> Attendees` (inverse of TicketOrderId FK)

#### TicketAttendee
One record per issued ticket (multiple per order when buyer purchases for others).

| Field | Type | Notes |
|-------|------|-------|
| Id | Guid | PK |
| VendorTicketId | string | Unique, from vendor API |
| TicketOrderId | Guid | FK → TicketOrder |
| AttendeeName | string | Ticket holder name |
| AttendeeEmail | string? | Ticket holder email (may not always be provided) |
| MatchedUserId | Guid? | FK → User, auto-matched by email |
| TicketTypeName | string | e.g. "Full Week", "Weekend Pass" |
| Price | decimal | Individual ticket price |
| Status | TicketAttendeeStatus (enum) | Valid, Void, CheckedIn |
| VendorEventId | string | Event ID at time of sync (for future multi-event) |
| SyncedAt | Instant | When this record was last synced |

#### TicketSyncState
Singleton tracking sync operational state. Note: this is distinct from `SyncServiceSettings` which controls sync *modes* (None/AddOnly/AddAndRemove) for Google/Discord services. Ticket sync tracks *operational state* — when it last ran, whether it succeeded, cursor position.

| Field | Type | Notes |
|-------|------|-------|
| Id | int | PK, always 1 |
| VendorEventId | string | Configured active event |
| LastSyncAt | Instant? | Last successful sync |
| SyncStatus | TicketSyncStatus (enum) | Idle, Running, Error |
| LastError | string? | Error message from last failed sync |
| StatusChangedAt | Instant? | When SyncStatus last changed |

### Existing Entity Changes

**CampaignGrant** — add:
- `RedeemedAt` (Instant?) — set when sync discovers the grant's discount code was used in an order

### New Enums

```csharp
public enum TicketSyncStatus
{
    Idle,
    Running,
    Error
}

public enum TicketPaymentStatus
{
    Paid,
    Pending,
    Refunded
}

public enum TicketAttendeeStatus
{
    Valid,
    Void,
    CheckedIn
}
```

All stored as strings with `HasConversion<string>()`, consistent with other domain enums.

### New Role

Add `TicketAdmin` to `RoleNames` constants. This role grants full read/write access to the `/Tickets` section including sync, exports, and code generation. Board has read-only access; Admin has full access as always.

## Architecture

### Service Layer

**ITicketVendorService** (Application layer — vendor-agnostic interface):
```
GetOrdersAsync(since?, eventId, ct) → IReadOnlyList<VendorOrderDto>
GetIssuedTicketsAsync(since?, eventId, ct) → IReadOnlyList<VendorTicketDto>
GetEventSummaryAsync(eventId, ct) → VendorEventSummaryDto
GenerateDiscountCodesAsync(spec, ct) → IReadOnlyList<string>
    // spec: DiscountCodeSpec { Count, DiscountType (Percentage/Fixed), DiscountValue, ExpiresAt? }
GetDiscountCodeUsageAsync(codes, ct) → IReadOnlyList<DiscountCodeStatusDto>
```

**TicketTailorService** (Infrastructure layer — TT implementation):
- HTTP client using `HttpClientFactory`
- API key from environment variable `TICKET_VENDOR_API_KEY` (not in appsettings — sensitive)
- Non-sensitive config in `appsettings.json`: `TicketVendor:EventId`, `TicketVendor:SyncIntervalMinutes`, `TicketVendor:Provider`
- Handles cursor-based pagination
- Maps TT API responses to vendor-agnostic DTOs
- Basic Auth with API key (per TT API docs)

**ITicketSyncService** (Application layer):
- `SyncOrdersAndAttendeesAsync(ct)` — full sync cycle:
  1. Fetch orders since last sync (or all if first run)
  2. Upsert `TicketOrder` records by `VendorOrderId`
  3. For each order, fetch/upsert `TicketAttendee` records by `VendorTicketId`
  4. Auto-match buyers/attendees to Users by email: query `UserEmails` table matching against **all** email addresses (OAuth, verified, unverified) using case-insensitive ordinal comparison. If multiple users share the same email (shouldn't happen but defensively), match the one with `IsOAuth = true` for that email; if still ambiguous, log a warning and leave unmatched.
  5. Match discount codes to `CampaignGrant` records, set `RedeemedAt` (see Campaign Integration for scoping rules)
  6. Update `TicketSyncState`

**TicketSyncJob** (Hangfire):
- Recurring job, default every 15 minutes
- Calls `ITicketSyncService.SyncOrdersAndAttendeesAsync()`
- "Sync Now" button triggers immediate execution
- Uses `[DisableConcurrentExecution]` attribute to prevent overlapping runs (scheduled sync + manual "Sync Now")
- Logs sync results (orders synced, attendees synced, matches found)
- Job class instantiated by Hangfire's own DI scope (not registered in app DI — follows existing job pattern)

### DI Registration

```csharp
services.AddScoped<ITicketVendorService, TicketTailorService>();
services.AddScoped<ITicketSyncService, TicketSyncService>();
```

Swap vendor by changing the DI registration — no other code changes needed.

## UI Design

### Navigation

Top-level nav item **"Tickets"** visible to `TicketAdmin`, `Admin`, and `Board` roles.

### Page Structure

The `/Tickets` root page is a **summary dashboard** — stats, charts, and problems at a glance. Detail lives on separate sub-pages to avoid overwhelming the ticketing team with 1,500+ rows of data on the landing page.

| Route | Purpose |
|-------|---------|
| `/Tickets` | Summary dashboard — cards, charts, problems |
| `/Tickets/Orders` | Full order list with search/sort/pagination |
| `/Tickets/Attendees` | Full attendee list with search/sort/pagination |
| `/Tickets/Codes` | Code tracking — redemption status tied to campaigns |
| `/Tickets/GateList` | Gate list for event logistics (stub for June) |

### Summary Dashboard (`/Tickets`)

**Summary Cards** (top row, 4 cards):
1. **Tickets Sold** — "1,847 / 3,000" with progress bar
2. **Revenue** — "€277,050" formatted with currency
3. **Avg. Price** — "€150"
4. **Remaining** — "1,153 tickets left"

**Daily Sales Chart** (main visual):
- Bar chart: tickets sold per day over the life of the event sales period
- Line overlay: 7-day rolling average
- Useful for projecting total sales and spotting trends (e.g. "we're averaging 15/day, at this rate we'll sell out by July")
- Built with Chart.js (already available via CDN — check About page for existing dependencies)

**Problems / Attention Items**:
- Unmatched orders count — "47 orders from non-humans" (link to filtered Orders view)
- Failed sync indicator — if last sync errored
- Low inventory warning — if remaining tickets < threshold (e.g. 10%)

**Recent Activity** (compact, last 10 orders):
- Date, Purchaser, Ticket Count, Amount — quick glance, not the full detail table
- "View all orders →" link to `/Tickets/Orders`

**Sync Status Bar** (bottom):
- "Last synced: 5 min ago · Next sync in 10 min" with "Sync Now" button
- Shows error state if last sync failed

### Orders Page (`/Tickets/Orders`)

Full order list with **server-side search, sorting, and pagination** (1,500+ rows).

| Date | Purchaser | Email | Tickets | Amount | Code Used | Matched | |
|------|-----------|-------|---------|--------|-----------|---------|---|
| 15 Mar | Jane Doe | jane@... | 1 | €150 | NOBO25 | ✓ [Jane D.] | → |
| 14 Mar | John Smith | john@... | 2 | €300 | — | ✓ [John S.] | → |

- **Search**: by purchaser name, email, or discount code
- **Sort**: by date (default: newest first), amount, ticket count
- **Filter**: by payment status, matched/unmatched, ticket type
- **Pagination**: 25/50/100 per page
- Purchaser links to TT dashboard (if `VendorDashboardUrl` available)
- Matched human links to `/Admin/Humans/{id}`
- Row click → order detail (expandable or separate page showing individual attendees)

### Attendees Page (`/Tickets/Attendees`)

Full attendee list (2,000–3,000 rows) with **server-side search, sorting, and pagination**.

| Name | Email | Ticket Type | Price | Status | Matched Human | Order |
|------|-------|-------------|-------|--------|---------------|-------|
| Jane Doe | jane@... | Full Week | €150 | Valid | [Jane D.] ✓ | #1234 |
| Mary Smith | — | Weekend | €100 | Valid | — | #1235 |

- **Search**: by attendee name or email
- **Sort**: by name, ticket type, price, status
- **Filter**: by ticket type, status (valid/void/checked-in), matched/unmatched
- **Pagination**: 25/50/100 per page

### "Who Hasn't Bought?" Section

Available as a filtered view within Attendees or as a standalone section on the dashboard.

Lists humans with `MembershipStatus = Active` (i.e. fully onboarded volunteers — profile complete, consents given, consent check cleared) who have no matched `TicketAttendee` record:
- Filterable by team and membership tier (Volunteer/Colaborador/Asociado)
- Shows: Name, Email, Teams, Tier
- Searchable by name/email
- Useful for outreach ("these 50 Colaboradores haven't bought tickets yet")
- Note: attendees without email (`AttendeeEmail` is nullable) will never auto-match; this is an accepted limitation documented here

### Code Tracking Page (`/Tickets/Codes`)

Ties into campaign system:
- Summary: X codes sent, Y redeemed, Z unused (with percentage)
- Per-campaign breakdown with links to Campaign Detail
- Visual indicator of redemption rate (progress bar per campaign)
- Searchable by code string
- Table of individual codes with status (sent/redeemed/unused), grant recipient, redemption date

### Gate List Page (`/Tickets/GateList`) — Stub for June

Placeholder page with basic attendee list for event logistics:
- Name, Ticket Type, Status (valid/void/checked-in)
- Exportable to CSV
- Filterable by ticket type
- **Note**: This is a stub/mockup for now. Full gate list functionality (early entry, VIP, etc.) targeted for June implementation.

## Campaign Integration

### Redemption Tracking

When `TicketSyncJob` processes an order with a `DiscountCode`:
1. Look up `CampaignCode` where `Code == order.DiscountCode` (case-insensitive ordinal), scoped to campaigns that are Active or Completed. Codes are unique per `(CampaignId, Code)`, not globally unique — if the same code string exists in multiple campaigns, match the most recent Active/Completed campaign first. If ambiguous, log a warning and skip redemption linking.
2. If found and has a linked `CampaignGrant`, set `CampaignGrant.RedeemedAt = order.PurchasedAt`
3. Campaign Detail page shows new "Redeemed" column: timestamp or "—"
4. Campaign stats show "X of Y codes redeemed (Z%)"

### Code Generation

New "Generate Codes" button on Campaign Detail page (visible when campaign is Draft):
1. Form fields: count, discount type (percentage/fixed), discount value, optional expiry
2. Calls `ITicketVendorService.GenerateDiscountCodesAsync()` with a `DiscountCodeSpec` DTO containing: `Count` (int), `DiscountType` (enum: Percentage/Fixed), `DiscountValue` (decimal), `ExpiresAt` (Instant?, optional)
3. Generated codes inserted as `CampaignCode` records with `ImportedAt` = now
4. Campaign remains in Draft — admin still clicks "Activate" when ready to send waves (existing workflow unchanged)

Existing CSV import continues to work alongside API generation. Both methods produce `CampaignCode` records in the same way.

## Configuration

### appsettings.json (committed — non-sensitive only)

```json
"TicketVendor": {
  "Provider": "TicketTailor",
  "EventId": "ev_123456",
  "SyncIntervalMinutes": 15
}
```

### Environment Variable (Coolify secrets — sensitive)

```
TICKET_VENDOR_API_KEY=sk_live_...
```

Follows existing pattern for SMTP credentials and Google service account keys.

## Authorization

Board gets **read-only** access to the Tickets section for oversight. Write operations (sync, code generation) and data exports are restricted to TicketAdmin and Admin for safety.

| Route/Action | TicketAdmin | Admin | Board |
|-------------|:-----------:|:-----:|:-----:|
| `/Tickets` dashboard (view) | ✓ | ✓ | ✓ |
| `/Tickets/Orders` (view) | ✓ | ✓ | ✓ |
| `/Tickets/Attendees` (view) | ✓ | ✓ | ✓ |
| `/Tickets/Codes` (view) | ✓ | ✓ | ✓ |
| `/Tickets/GateList` (view) | ✓ | ✓ | ✓ |
| `/Tickets/Sync` (POST — trigger sync) | ✓ | ✓ | — |
| CSV exports | ✓ | ✓ | — |
| Generate codes (Campaign) | ✓ | ✓ | — |

TicketAdmin is a new role added to `RoleNames`, assignable via the existing role management UI.

## Error Handling

- API errors during sync: logged, `TicketSyncState.SyncStatus` set to `Error` with message, dashboard shows stale data with error indicator
- Partial sync: if sync fails mid-way, already-upserted records are kept (idempotent by vendor ID)
- Missing API key: sync job skips with warning log, dashboard shows "Not configured" state
- Email matching: case-insensitive ordinal comparison. No fuzzy matching — exact email match only.
- Rate limiting: TT allows 5000 requests per 30 minutes. With 2,000–3,000 attendees across 1,500+ orders and cursor pagination (100 per page), a full sync uses ~50 requests. Incremental syncs (since last sync) will be much smaller. No concern.

## Testing Strategy

**Critical context**: This feature cannot be properly tested in QA — there's no real TicketTailor data in the QA environment, and creating realistic test data in TT would require paid tickets. This means much of this code will be **virgin in production**. Test coverage and realistic mock data are the primary safety net.

### Unit Tests (TicketSyncService)
- In-memory DbContext with mocked `ITicketVendorService`
- **Realistic mock data**: generate 1,500+ mock orders with 2,000+ attendees to match production scale
- Auto-matching logic: exact email match, no match, multiple emails per user, case-insensitive
- Discount code redemption: code found in single campaign, code in multiple campaigns (ambiguity), code not found
- Upsert idempotency: re-syncing same data doesn't create duplicates
- Incremental sync: only fetches orders since last sync timestamp
- Error handling: API failure mid-sync preserves already-synced data

### Unit Tests (TicketTailorService)
- HTTP response parsing with recorded/mock API responses
- Cursor-based pagination assembly (multiple pages → single result list)
- Error response handling (auth failure, rate limit, server error)
- Field mapping to vendor-agnostic DTOs

### Unit Tests (Dashboard/Chart Data)
- Daily sales aggregation from order data
- 7-day rolling average calculation
- Summary card computations (total, avg price, remaining)
- "Who hasn't bought?" query logic with various membership status combinations

### Controller Tests
- Authorization: TicketAdmin access allowed, non-TicketAdmin denied, Admin fallback works
- Search/sort/pagination parameter handling
- Empty state (no data synced yet)

### Mock Data Seeder
- Development seed data generator that populates realistic ticket data locally
- Useful for UI development and manual verification
- Generates orders spread over weeks with varying ticket types, prices, and match rates

## Documentation Updates Required

When implementing this feature, update:
- `.claude/DATA_MODEL.md` — add TicketOrder, TicketAttendee, TicketSyncState entities; add RedeemedAt to CampaignGrant
- `docs/features/` — create `24-ticket-vendor-integration.md` feature spec
- `docs/features/22-campaigns.md` — add note about RedeemedAt field and code generation
- `Views/Home/About.cshtml` — if new NuGet packages are added

## Future Enhancements (Out of Scope)

- Webhook endpoint for real-time purchase notifications
- Multi-event support with event selector
- Historical event data and year-over-year comparisons
- Direct Stripe dashboard links (if TT API exposes Stripe payment IDs)
- Attendee check-in via the Humans app
- Full gate list functionality with early entry, VIP lists, etc. (June target)
- Sales projection / sell-out date estimation based on rolling average
- Revenue breakdown by ticket type (pie/bar chart)
