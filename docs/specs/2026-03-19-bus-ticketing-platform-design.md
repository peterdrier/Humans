# Bus Ticketing Platform — Design Spec

**Date**: 2026-03-19
**Status**: Draft

## Business Context

Nobodies Collective runs a multi-day event at a venue called "Elsewhere" near Barcelona. Attendees need shuttle bus transport between Barcelona city and the venue. Event tickets are sold through TicketTailor; bus tickets are sold separately.

Today there is no system for selling or managing bus transport. This project creates a **standalone web application** (`bustickets.nobodies.team`) that allows event ticket holders to purchase bus seats, validates their event ticket ownership via the TicketTailor API, takes payment through the existing Stripe account, and issues QR-coded bus tickets that can be scanned at boarding.

The platform must also provide an admin dashboard for managing bookings, capacity, refunds, and departure manifests, plus a mobile-friendly QR scanner for drivers/stewards.

## Scope

### Scale

- **11 departures** across 2 routes (6 outbound Barcelona → Elsewhere, 5 return Elsewhere → Barcelona)
- **56 seats** per departure (616 total seats across all departures)
- **~300 bookings** expected, each containing 1–4 booking legs (individual passenger × departure combinations)
- Single event, single use — the platform serves one event then goes quiet

### In Scope

- Standalone .NET 10 application following Humans app architecture and conventions
- Public booking flow: validate TicketTailor order → select departures per passenger → pay via Stripe Checkout → receive QR-coded tickets
- Per-passenger, per-direction departure selection (including "No bus needed" option)
- TicketTailor API integration for order/ticket validation and passenger name retrieval
- Stripe Checkout integration for payments + webhook handling for confirmation
- QR code generation (one per passenger per departure leg)
- Browser-based QR scanner for boarding validation (no app install required)
- Admin dashboard with Google OAuth (restricted to authorised email)
- Configurable pricing, phase-based departure release with configurable utilisation threshold
- Vague availability indicators (Available / Filling up / Last few tickets / Sold out)
- Confirmation emails via MailKit + Google Workspace SMTP relay
- Docker deployment via Coolify

### Out of Scope

- User accounts for passengers (booking accessed via booking reference + email)
- Multi-event support
- Automated refunds (admin triggers refunds manually)
- Webhook receiver from TicketTailor (we call their API, not the reverse)
- Integration back into the Humans application (standalone app)
- Seat assignments or seat selection
- Accessibility transport or special requirements tracking (handled separately by ops team)

## Data Model

### New Entities

#### Departure

One record per scheduled bus departure.

| Field | Type | Notes |
|-------|------|-------|
| `Id` | `Guid` | PK |
| `Route` | `RouteDirection` | Enum: `OutboundToVenue` / `ReturnToCity` |
| `DepartureTime` | `Instant` | NodaTime — when the bus departs |
| `Label` | `string` | Human-readable label, e.g. "Barcelona → Elsewhere" |
| `Capacity` | `int` | Default 56 |
| `Phase` | `int` | 1 = open immediately, 2 = held back until threshold met |
| `IsManuallyOpen` | `bool` | Admin override to force a Phase 2 departure open |
| `SortOrder` | `int` | Controls display ordering within each route direction |
| `CreatedAt` | `Instant` | |

**Navigation**: `ICollection<BookingLeg> BookingLegs`

**Computed (not persisted)**: `ConfirmedLegCount` (count of legs where parent booking is Confirmed), `SeatsRemaining` (Capacity − ConfirmedLegCount), `IsSoldOut`, `IsAvailable` (considers phase, manual override, and sold-out status)

#### Booking

One record per purchase transaction. A booking may contain legs for multiple passengers across different departures.

| Field | Type | Notes |
|-------|------|-------|
| `Id` | `Guid` | PK |
| `BookingRef` | `string` | Unique, human-readable, e.g. "BUS-A1B2C3" |
| `Email` | `string` | Buyer's email address |
| `BuyerName` | `string` | Primary buyer name (from TT order) |
| `TtOrderId` | `string` | TicketTailor order ID |
| `TtOrderRef` | `string` | TicketTailor order reference (customer-facing) |
| `StripeCheckoutSessionId` | `string?` | Stripe Checkout session ID |
| `StripePaymentIntentId` | `string?` | Stripe Payment Intent ID (from webhook) |
| `Status` | `BookingStatus` | Enum: `Pending` / `Confirmed` / `Refunded` / `Cancelled` |
| `TotalAmountCents` | `int` | Total charged in cents (e.g. 9600 = €96.00) |
| `Currency` | `string` | Default "eur" |
| `CreatedAt` | `Instant` | |
| `ConfirmedAt` | `Instant?` | Set when Stripe webhook confirms payment |

**Navigation**: `ICollection<BookingLeg> Legs`

#### BookingLeg

One record per passenger per departure. This is the atomic unit — one person on one bus.

| Field | Type | Notes |
|-------|------|-------|
| `Id` | `Guid` | PK |
| `BookingId` | `Guid` | FK → `Booking` |
| `DepartureId` | `Guid` | FK → `Departure` |
| `PassengerName` | `string` | Full name from TicketTailor issued ticket |
| `TtTicketId` | `string` | TicketTailor issued ticket ID — **unique across all BookingLegs** |
| `QrToken` | `string` | Unique token encoded in QR code |
| `ScannedAt` | `Instant?` | When QR was scanned at boarding |
| `ScannedBy` | `string?` | Identifier of who scanned (e.g. admin email) |
| `CreatedAt` | `Instant` | |

**Navigation**: `Booking Booking`, `Departure Departure`

**Unique constraints**: `TtTicketId` (one bus booking per event ticket), `QrToken`

#### SiteSettings

Singleton configuration record (Id always 1). All operational parameters are admin-editable at runtime.

| Field | Type | Default | Notes |
|-------|------|---------|-------|
| `Id` | `int` | 1 | PK, always 1 |
| `SingleLegPriceCents` | `int` | 4800 | €48.00 per leg |
| `Currency` | `string` | "eur" | |
| `Phase2ThresholdPercent` | `int` | 85 | Phase 2 departures unlock when Phase 1 utilisation reaches this % |
| `AvailabilityHighPercent` | `int` | 70 | Above this → "Available" |
| `AvailabilityLowPercent` | `int` | 15 | Below this → "Last few tickets" |
| `SalesOpen` | `bool` | false | Master toggle — when false, public booking flow shows "coming soon" |
| `UpdatedAt` | `Instant` | | |

### Enums

All stored as strings via `HasConversion<string>()`.

| Enum | Values |
|------|--------|
| `RouteDirection` | `OutboundToVenue`, `ReturnToCity` |
| `BookingStatus` | `Pending`, `Confirmed`, `Refunded`, `Cancelled` |

### Entity Relationship Summary

```
Departure (11 rows)
  └──< BookingLeg (many)
         └──> Booking (many-to-one)

SiteSettings (1 row, singleton)
```

## Architecture

### Solution Structure

Four-project clean architecture, matching Humans conventions:

```
src/
  BusTickets.Domain/
    Entities/
      Departure.cs
      Booking.cs
      BookingLeg.cs
      SiteSettings.cs
    Enums/
      RouteDirection.cs
      BookingStatus.cs

  BusTickets.Application/
    Interfaces/
      ITicketValidationService.cs
      IBookingService.cs
      IPaymentService.cs
      IQrCodeService.cs
      IEmailService.cs
    DTOs/
      TicketValidationDtos.cs
      BookingDtos.cs
      PaymentDtos.cs

  BusTickets.Infrastructure/
    Data/
      BusTicketsDbContext.cs
      Configurations/
        DepartureConfiguration.cs
        BookingConfiguration.cs
        BookingLegConfiguration.cs
        SiteSettingsConfiguration.cs
      Migrations/
      SeedData.cs
    Services/
      TicketTailorValidationService.cs
      BookingService.cs
      StripePaymentService.cs
      QrCodeService.cs
      SmtpEmailService.cs
    Configuration/
      TicketTailorSettings.cs
      StripeSettings.cs
      EmailSettings.cs

  BusTickets.Web/
    Controllers/
      HomeController.cs
      BookingController.cs
      WebhookController.cs
      ScanController.cs
      AdminController.cs
    Views/
      Home/
        Index.cshtml
      Booking/
        Validate.cshtml
        Select.cshtml
        Success.cshtml
        Cancelled.cshtml
        ViewTicket.cshtml
      Scan/
        Index.cshtml
      Admin/
        Index.cshtml
        Departures.cshtml
        Bookings.cshtml
        BookingDetail.cshtml
        Manifest.cshtml
        Settings.cshtml
        ScanLog.cshtml
      Shared/
        _Layout.cshtml
        _AdminNav.cshtml
    ViewModels/
    wwwroot/
      css/
      js/

tests/
  BusTickets.Domain.Tests/
  BusTickets.Application.Tests/
  BusTickets.Integration.Tests/
```

### Service Layer

#### ITicketValidationService

Validates event ticket ownership against TicketTailor API. This is the bus platform's equivalent of `ITicketVendorService` in Humans, but focused purely on validation rather than sync.

```csharp
public interface ITicketValidationService
{
    /// <summary>
    /// Validates a TicketTailor order by reference and email.
    /// Returns the order details and all valid issued tickets.
    /// </summary>
    Task<TicketValidationResult> ValidateOrderAsync(
        string orderReference,
        string email,
        CancellationToken ct = default);
}
```

**DTOs:**

```csharp
public record TicketValidationResult(
    bool IsValid,
    string? ErrorMessage,
    string? TtOrderId,
    string? BuyerName,
    IReadOnlyList<ValidatedTicket> Tickets);

public record ValidatedTicket(
    string TtTicketId,
    string FirstName,
    string LastName,
    string FullName,       // "{FirstName} {LastName}"
    string TicketType,
    string Status);        // Only "valid" tickets returned
```

**Implementation** (`TicketTailorValidationService`):
- Uses `HttpClient` via `IHttpClientFactory` with Basic Auth (`apiKey:` base64-encoded)
- JSON deserialisation with `JsonNamingPolicy.SnakeCaseLower` (matching Humans `TicketTailorService`)
- TT API models as `internal sealed record` types within the service class
- Registered as `services.AddScoped<ITicketValidationService, TicketTailorValidationService>()`

#### IBookingService

Orchestrates the booking lifecycle.

```csharp
public interface IBookingService
{
    /// <summary>
    /// Creates a pending booking and returns a Stripe Checkout session URL.
    /// </summary>
    Task<CreateBookingResult> CreateBookingAsync(
        CreateBookingRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Confirms a booking after successful Stripe payment.
    /// Generates QR tokens for each leg.
    /// </summary>
    Task ConfirmBookingAsync(
        string stripeCheckoutSessionId,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves a booking by reference for the confirmation/ticket view.
    /// </summary>
    Task<BookingDetailDto?> GetBookingByRefAsync(
        string bookingRef,
        CancellationToken ct = default);

    /// <summary>
    /// Checks if a TT ticket ID is already used in a confirmed booking.
    /// Returns the existing booking ref if so.
    /// </summary>
    Task<string?> GetExistingBookingRefForTtTicketAsync(
        string ttTicketId,
        CancellationToken ct = default);
}
```

**DTOs:**

```csharp
public record CreateBookingRequest(
    string Email,
    string BuyerName,
    string TtOrderId,
    string TtOrderRef,
    IReadOnlyList<LegRequest> Legs);

public record LegRequest(
    Guid DepartureId,
    string PassengerName,
    string TtTicketId);

public record CreateBookingResult(
    bool Success,
    string? ErrorMessage,
    string? BookingRef,
    string? StripeCheckoutUrl);

public record BookingDetailDto(
    Guid Id,
    string BookingRef,
    string Email,
    string BuyerName,
    BookingStatus Status,
    int TotalAmountCents,
    string Currency,
    Instant CreatedAt,
    Instant? ConfirmedAt,
    IReadOnlyList<BookingLegDto> Legs);

public record BookingLegDto(
    Guid Id,
    string PassengerName,
    string TtTicketId,
    RouteDirection Route,
    Instant DepartureTime,
    string DepartureLabel,
    string QrToken,
    Instant? ScannedAt);
```

#### IPaymentService

Wraps Stripe API calls.

```csharp
public interface IPaymentService
{
    Task<PaymentSessionResult> CreateCheckoutSessionAsync(
        Booking booking,
        string successUrl,
        string cancelUrl,
        CancellationToken ct = default);

    Task<RefundResult> RefundPaymentAsync(
        string paymentIntentId,
        int? amountCents,  // null = full refund
        CancellationToken ct = default);
}
```

**Implementation** (`StripePaymentService`):
- Uses `Stripe.net` NuGet package
- Creates Checkout Session with itemised line items (one per BookingLeg: "Bus: {PassengerName} — {Route}, {DateTime}")
- Passes `booking.Id` as `client_reference_id` and `metadata["booking_ref"]` for webhook correlation
- Registered as `services.AddScoped<IPaymentService, StripePaymentService>()`

#### IQrCodeService

Generates and validates QR codes.

```csharp
public interface IQrCodeService
{
    /// <summary>
    /// Generates a cryptographically random QR token.
    /// </summary>
    string GenerateToken();

    /// <summary>
    /// Renders a QR code image (PNG bytes) for the given token.
    /// </summary>
    byte[] GenerateQrImage(string token);
}
```

**Implementation** (`QrCodeService`):
- Token generation: `Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))` → URL-safe base64, 32 chars
- QR rendering: `QRCoder` NuGet package, PNG output, suitable size for mobile scanning
- The QR encodes a URL: `https://bustickets.nobodies.team/scan/check/{qrToken}` — this means scanning with any QR reader opens the validation page, but the dedicated scanner page handles it inline

#### IEmailService

Sends transactional emails.

```csharp
public interface IEmailService
{
    Task SendBookingConfirmationAsync(
        BookingDetailDto booking,
        CancellationToken ct = default);
}
```

**Implementation** (`SmtpEmailService`):
- Uses `MailKit` NuGet package, matching Humans conventions
- SMTP via `smtp-relay.gmail.com:587` with StartTLS
- From address: `buses@nobodies.team` (or `bustickets@nobodies.team`)
- Simple HTML email with booking summary, QR codes as inline images (CID attachments), and a link to the online ticket view
- No outbox pattern needed at this scale (~300 emails total)

### DI Registration

```csharp
// BusTickets.Infrastructure/DependencyInjection.cs
services.AddScoped<ITicketValidationService, TicketTailorValidationService>();
services.AddScoped<IBookingService, BookingService>();
services.AddScoped<IPaymentService, StripePaymentService>();
services.AddScoped<IQrCodeService, QrCodeService>();
services.AddScoped<IEmailService, SmtpEmailService>();

services.AddHttpClient<TicketTailorValidationService>(client =>
{
    client.BaseAddress = new Uri("https://api.tickettailor.com");
});
```

## External API Integration

### TicketTailor API

The bus platform calls the TicketTailor API **read-only** to validate event ticket ownership. No data is written back to TT.

#### API Authentication

- **Method**: Basic Auth
- **Credentials**: `{TICKET_VENDOR_API_KEY}:` (key as username, empty password)
- **Header**: `Authorization: Basic {base64("{apiKey}:")}`

This matches the existing pattern in Humans' `TicketTailorService`.

#### API Call 1: Search Orders by Reference

Validates that an order exists, is paid, and belongs to the provided email.

```
GET https://api.tickettailor.com/v1/orders?search={orderReference}
```

**Response** (relevant fields):

```json
{
  "data": [
    {
      "id": "or_12345678",
      "order_reference": "HUM-ABC123",
      "buyer_details": {
        "email": "maria@example.com",
        "first_name": "Maria",
        "last_name": "García"
      },
      "payment": {
        "payment_status": "completed"
      },
      "line_items": [...]
    }
  ]
}
```

**Validation logic:**
1. Search returns results → find order where `order_reference` matches (case-insensitive ordinal)
2. `buyer_details.email` matches provided email (case-insensitive ordinal)
3. `payment.payment_status` is `"completed"`
4. If any check fails → return `IsValid: false` with appropriate error message

#### API Call 2: Get Issued Tickets for Order

Retrieves individual ticket holders (attendees) for the validated order.

```
GET https://api.tickettailor.com/v1/issued_tickets?order_id={ttOrderId}
```

**Response** (relevant fields):

```json
{
  "data": [
    {
      "id": "it_87654321",
      "first_name": "Maria",
      "last_name": "García",
      "email": "maria@example.com",
      "ticket_type": {
        "name": "General Admission"
      },
      "status": "valid"
    },
    {
      "id": "it_87654322",
      "first_name": "José",
      "last_name": "García",
      "email": null,
      "ticket_type": {
        "name": "General Admission"
      },
      "status": "valid"
    }
  ]
}
```

**Filtering logic:**
- Only return tickets where `status` is `"valid"` (exclude void, refunded, etc.)
- Cursor-based pagination via `starting_after` parameter (same pattern as Humans' `TicketTailorService`)

#### API Call 3: Duplicate Check

Before creating a booking, check if any of the selected TT ticket IDs already have a confirmed BookingLeg in our database. This is a **local database check**, not a TT API call.

```sql
SELECT b.BookingRef
FROM BookingLegs bl
JOIN Bookings b ON bl.BookingId = b.Id
WHERE bl.TtTicketId IN (@selectedTicketIds)
  AND b.Status = 'Confirmed'
```

If a match is found, redirect the user to their existing booking instead of allowing a duplicate.

### Stripe API

#### Checkout Session Creation

```
POST https://api.stripe.com/v1/checkout/sessions
```

Via `Stripe.net` SDK:

```csharp
var options = new SessionCreateOptions
{
    Mode = "payment",
    CustomerEmail = booking.Email,
    ClientReferenceId = booking.Id.ToString(),
    Metadata = new Dictionary<string, string>
    {
        ["booking_ref"] = booking.BookingRef
    },
    LineItems = booking.Legs.Select(leg => new SessionLineItemOptions
    {
        PriceData = new SessionLineItemPriceDataOptions
        {
            Currency = settings.Currency,
            UnitAmount = settings.SingleLegPriceCents,
            ProductData = new SessionLineItemPriceDataProductDataOptions
            {
                Name = $"Bus: {leg.PassengerName} — {leg.Departure.Label}, {FormatDateTime(leg.Departure.DepartureTime)}"
            }
        },
        Quantity = 1
    }).ToList(),
    SuccessUrl = successUrl + "?session_id={CHECKOUT_SESSION_ID}",
    CancelUrl = cancelUrl
};
```

#### Webhook: `checkout.session.completed`

```
POST /webhooks/stripe
```

- Verify webhook signature using `Stripe.net` `EventUtility.ConstructEvent()`
- Extract `client_reference_id` → look up Booking by Id
- Set `Booking.Status = Confirmed`, `ConfirmedAt = now`, `StripePaymentIntentId` from session
- Generate QR tokens for each BookingLeg via `IQrCodeService`
- Send confirmation email via `IEmailService`

#### Refund (Admin)

```csharp
var options = new RefundCreateOptions
{
    PaymentIntent = booking.StripePaymentIntentId,
    Amount = amountCents  // null for full refund
};
```

### Humans Application API (Future / Not Required for Launch)

No direct API calls to the Humans application are needed for launch. The bus platform is fully standalone. However, two integration points could be added later:

1. **Cross-reference attendee data** — if the Humans app exposes an API endpoint to look up a user by email, the bus platform could display team/role info on the admin manifest. Not needed for launch.
2. **Shared TicketTailor event ID** — both apps reference the same TT event (`ev_7718745` in Humans' appsettings). The bus platform needs this same event ID in its own config.

## Departure Schedule (Seed Data)

### Outbound — Barcelona → Elsewhere

| DepartureTime | Phase | SortOrder | Notes |
|---------------|-------|-----------|-------|
| 2026-07-03 11:00 CEST | 1 | 10 | Early setup/arrival |
| 2026-07-06 10:00 CEST | **2** | 20 | Held back |
| 2026-07-06 11:30 CEST | 1 | 30 | |
| 2026-07-06 13:00 CEST | **2** | 40 | Held back |
| 2026-07-06 16:00 CEST | 1 | 50 | |
| 2026-07-07 11:00 CEST | 1 | 60 | |

### Return — Elsewhere → Barcelona

| DepartureTime | Phase | SortOrder | Notes |
|---------------|-------|-----------|-------|
| 2026-07-12 13:00 CEST | 1 | 10 | |
| 2026-07-12 16:00 CEST | **2** | 20 | Held back |
| 2026-07-13 11:00 CEST | **2** | 30 | Held back |
| 2026-07-13 13:00 CEST | 1 | 40 | |
| 2026-07-14 13:00 CEST | 1 | 50 | |

All departures: **56 capacity**.

### Phase 2 Auto-Release Logic

```
For each RouteDirection:
  phase1Capacity = SUM(Capacity) of Phase 1 departures for this direction
  phase1Booked  = COUNT(confirmed BookingLegs) on Phase 1 departures for this direction
  utilisation   = (phase1Booked / phase1Capacity) × 100

  IF utilisation >= SiteSettings.Phase2ThresholdPercent:
    All Phase 2 departures for this direction become visible and bookable

  Admin can override: setting Departure.IsManuallyOpen = true makes it available regardless of phase logic
```

**Outbound Phase 1 capacity**: 4 departures × 56 = 224 seats → Phase 2 unlocks at 191 seats booked (85%)
**Return Phase 1 capacity**: 3 departures × 56 = 168 seats → Phase 2 unlocks at 143 seats booked (85%)

## UI Design

### Public Pages

#### Landing Page (`/`)

Event-branded page with:
- Event name, dates, venue info
- "Book Your Bus" call-to-action button
- Brief info: routes, pricing (€48 per journey), what to expect
- If `SiteSettings.SalesOpen` is false → show "Tickets coming soon" with no booking CTA

#### Step 1: Validate (`/book`)

```
┌──────────────────────────────────────────────┐
│  Ticket Tailor Order Reference:              │
│  [________________________]                  │
│                                              │
│  Email used for purchase:                    │
│  [________________________]                  │
│                                              │
│  [Find My Tickets →]                         │
│                                              │
│  Your order reference can be found in your   │
│  Ticket Tailor confirmation email.           │
└──────────────────────────────────────────────┘
```

**Error states:**
- Order not found → "We couldn't find an order with that reference. Please check and try again."
- Email mismatch → "The email doesn't match this order. Please use the email you purchased with."
- No valid tickets → "This order has no valid tickets. If you believe this is an error, please contact us."
- Already booked → "You already have a bus booking (BUS-XXXXXX). [View your tickets →]"

#### Step 2: Select Departures (`/book/select`)

Per-passenger, per-direction radio selection. Each passenger card shows their name from TicketTailor.

```
┌──────────────────────────────────────────────┐
│  ┌────────────────────────────────────────┐  │
│  │ Maria García                           │  │
│  │                                        │  │
│  │ Outbound — Barcelona → Elsewhere       │  │
│  │ ○ No bus needed                        │  │
│  │ ○ Thu 3 Jul, 11:00 AM   Available      │  │
│  │ ○ Sun 6 Jul, 11:30 AM   Filling up     │  │
│  │ ○ Sun 6 Jul, 4:00 PM    Available      │  │
│  │ ○ Mon 7 Jul, 11:00 AM   Available      │  │
│  │                                        │  │
│  │ Return — Elsewhere → Barcelona         │  │
│  │ ○ No bus needed                        │  │
│  │ ○ Sat 12 Jul, 1:00 PM   Available      │  │
│  │ ○ Sun 13 Jul, 1:00 PM   Filling up     │  │
│  │ ○ Mon 14 Jul, 1:00 PM   Available      │  │
│  └────────────────────────────────────────┘  │
│                                              │
│  ┌────────────────────────────────────────┐  │
│  │ José García                            │  │
│  │ (same selection layout)                │  │
│  └────────────────────────────────────────┘  │
│                                              │
│  Summary:                                    │
│    Maria: 1 × outbound               €48.00 │
│    José: 1 × outbound + 1 × return   €96.00 │
│                                     ──────── │
│    Total:                            €144.00 │
│                                              │
│  [Pay with Stripe →]                         │
└──────────────────────────────────────────────┘
```

**Availability indicator logic** (percentage = seats remaining / capacity):

| Condition | Label | Style |
|-----------|-------|-------|
| > `AvailabilityHighPercent` (70%) | Available | Green / neutral |
| `AvailabilityLowPercent` (15%) – 70% | Filling up | Amber |
| 1% – `AvailabilityLowPercent` (15%) | Last few tickets | Red |
| 0% | Sold out | Grey, radio button disabled |

Phase 2 departures that haven't been released are **not shown at all** (not shown as sold out).

**Validation:**
- At least one leg must be selected across all passengers
- JavaScript updates the summary in real-time as selections change
- "Pay with Stripe" button disabled until at least one leg selected

#### Confirmation (`/book/success`)

Displays after Stripe redirect. Also accessible permanently at `/ticket/{bookingRef}`.

Shows each passenger with their QR code(s) and departure details. QR codes rendered as inline images. Page is bookmarkable — this is the passenger's ticket.

#### Cancelled (`/book/cancelled`)

Simple page: "Payment was cancelled. [Try again →]" with link back to the select step (session data preserved).

### Admin Pages

All admin pages require Google OAuth authentication, restricted to authorised email address(es).

#### Dashboard (`/admin`)

- **Summary cards**: Total bookings, Total revenue, Seats sold / total capacity, Bookings today
- **Capacity table**: Each departure with seats sold, seats remaining, percentage, phase status, availability indicator
- **Quick actions**: Toggle sales open/closed, open Phase 2 departures manually

#### Departures (`/admin/departures`)

Table of all departures with inline editing:
- Edit capacity, phase, sort order
- Toggle `IsManuallyOpen`
- Shows current booking count per departure
- Cannot delete departures that have bookings

#### Bookings (`/admin/bookings`)

Searchable, sortable, paginated list of all bookings:
- Search by booking ref, email, passenger name, TT order ref
- Filter by status (Confirmed / Pending / Refunded / Cancelled)
- Filter by departure
- Shows: booking ref, buyer name, email, leg count, total amount, status, date

#### Booking Detail (`/admin/bookings/{id}`)

Full booking details with all legs. Admin actions:
- **Refund**: triggers Stripe refund, sets status to Refunded
- **Cancel**: sets status to Cancelled (no Stripe refund — for cases where refund done separately)
- **Move leg**: change a BookingLeg to a different departure (within same route direction)
- **Remove leg**: remove a single leg from a booking (partial cancellation)

#### Manifest (`/admin/manifest/{departureId}`)

Passenger list for a specific departure:
- Passenger name, TT ticket ID, booking ref, email, scan status
- Sortable by name
- **CSV export** button for printing / offline use
- Shows scanned vs not-yet-scanned count

#### Settings (`/admin/settings`)

Edit all `SiteSettings` fields:
- Single leg price
- Currency
- Phase 2 threshold percentage
- Availability indicator thresholds (high/low percentages)
- Sales open toggle

#### Scan Log (`/admin/scan-log`)

Chronological list of all QR scans:
- Timestamp, passenger name, departure, scanned by, booking ref
- Useful for auditing and dispute resolution

### Scanner Page (`/scan`)

Mobile-optimised page for drivers/stewards. Requires admin auth.

- Opens device camera using `html5-qrcode` JavaScript library
- Scans QR code → `POST /scan/validate` with token
- Displays result:

```
✅ VALID
Maria García
Sun 6 Jul, 11:30 — Barcelona → Elsewhere
Booking: BUS-A1B2C3
TT Ticket: TT-ABC123
[Scan Next]

⚠️ ALREADY SCANNED
Maria García — scanned at 10:47 AM
[Override & Allow] [Scan Next]

❌ INVALID
QR code not recognised
[Scan Next]
```

The "Override & Allow" on already-scanned tickets is important for edge cases (re-boarding after a stop, scanner error, etc.) — it logs a second scan but doesn't block.

## Configuration

### appsettings.json

```json
{
  "TicketTailor": {
    "EventId": "ev_7718745",
    "BaseUrl": "https://api.tickettailor.com"
  },
  "Stripe": {
    "PublishableKey": "pk_live_...",
    "WebhookSecret": "whsec_..."
  },
  "Email": {
    "SmtpHost": "smtp-relay.gmail.com",
    "SmtpPort": 587,
    "UseSsl": true,
    "FromAddress": "buses@nobodies.team",
    "FromName": "Elsewhere Bus Tickets"
  },
  "Admin": {
    "AllowedEmails": ["your-email@nobodies.team"]
  },
  "Authentication": {
    "Google": {
      "ClientId": "...",
      "ClientSecret": "..."
    }
  }
}
```

### Environment Variables

| Variable | Purpose |
|----------|---------|
| `TICKET_VENDOR_API_KEY` | TicketTailor API key (matches Humans convention) |
| `STRIPE_SECRET_KEY` | Stripe secret key |
| `STRIPE_WEBHOOK_SECRET` | Stripe webhook signing secret |
| `EMAIL_USERNAME` | SMTP username (if required) |
| `EMAIL_PASSWORD` | SMTP password / app password |
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection string |

Sensitive values in environment variables only, never in appsettings. Non-sensitive config in appsettings. This matches the Humans convention where `TICKET_VENDOR_API_KEY` is an env var but `EventId` is in appsettings.

## Authorization

| Page / Endpoint | Access |
|----------------|--------|
| `/` | Public |
| `/book`, `/book/*` | Public (but gated by `SalesOpen` setting) |
| `/ticket/{ref}` | Public (security through obscurity — booking ref is unguessable) |
| `/webhooks/stripe` | Public (verified by Stripe webhook signature) |
| `/admin/*` | Google OAuth, restricted to `Admin:AllowedEmails` |
| `/scan`, `/scan/*` | Google OAuth, restricted to `Admin:AllowedEmails` |

Admin auth implemented via ASP.NET Core authentication with Google OAuth provider + a custom authorization policy that checks the authenticated email against `Admin:AllowedEmails` list.

## Error Handling

- **TicketTailor API errors**: Caught and returned as user-friendly messages ("We're having trouble verifying your ticket — please try again in a moment"). Logged with full details via Serilog.
- **Stripe Checkout creation failures**: Booking remains in `Pending` state. User shown error with retry option. Pending bookings older than 1 hour are eligible for cleanup.
- **Stripe webhook failures**: Stripe retries automatically. Handler is idempotent — confirming an already-confirmed booking is a no-op.
- **Capacity race conditions**: Seat availability checked at booking creation time. If a departure fills between selection and payment, the Stripe webhook handler checks capacity again. If oversold, booking is confirmed anyway (minor overbooking is acceptable at this scale) and admin is notified via log alert. The admin can then move passengers.
- **Stale pending bookings**: A Hangfire job runs hourly to cancel `Pending` bookings older than 1 hour (Stripe Checkout sessions expire after 24 hours by default, but we clean up earlier to free held capacity).
- **QR token collisions**: Cryptographically random 24-byte tokens make collisions practically impossible. Unique constraint on `QrToken` column provides a hard guarantee.

## Testing Strategy

### Unit Tests

- **TicketTailorValidationService**: Mock `HttpMessageHandler` returning realistic TT API responses. Test: valid order, email mismatch, no valid tickets, void tickets filtered, pagination, API errors.
- **BookingService**: In-memory `DbContext`. Test: booking creation, duplicate TT ticket detection, capacity checking, QR token generation, confirmation flow, refund flow, leg movement.
- **Phase 2 logic**: Test threshold calculations with various booking distributions.
- **Availability indicators**: Test all threshold boundaries.

### Integration Tests

- **Full booking flow**: Validate → Select → Create booking → Simulate webhook → Verify confirmation.
- **Admin operations**: Refund, move leg, toggle settings.
- **Scanner**: Scan valid token, scan already-scanned, scan invalid.

### Manual Testing

- **Stripe test mode**: Full end-to-end with `4242 4242 4242 4242` test card.
- **TT sandbox**: If available, or mock API responses locally.
- **QR scanning**: Test on physical mobile device with `html5-qrcode`.

## Deployment

### Infrastructure

- **Docker** container with multi-stage build (matching Humans' Dockerfile pattern)
- **Coolify** on existing server — deployed as a second application
- **PostgreSQL** — separate database on the same Postgres instance, or a new Coolify-managed Postgres
- **Domain**: `bustickets.nobodies.team` — DNS A/CNAME record pointing to Coolify server, SSL via Coolify's built-in Let's Encrypt

### Dockerfile (outline)

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/BusTickets.Web -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "BusTickets.Web.dll"]
```

### Stripe Webhook Configuration

A new webhook endpoint must be added in the Stripe dashboard:
- **URL**: `https://bustickets.nobodies.team/webhooks/stripe`
- **Events**: `checkout.session.completed`
- The signing secret goes into `STRIPE_WEBHOOK_SECRET` env var

This is a separate webhook endpoint from whatever the main event uses — Stripe supports multiple webhook endpoints per account.

## NuGet Packages

| Package | Purpose |
|---------|---------|
| `Microsoft.AspNetCore.Authentication.Google` | Admin OAuth |
| `Microsoft.EntityFrameworkCore` | ORM |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | PostgreSQL provider |
| `NodaTime` | Date/time handling |
| `NodaTime.Serialization.SystemTextJson` | JSON serialisation for NodaTime types |
| `Npgsql.NodaTime` | PostgreSQL NodaTime type mapping |
| `Stripe.net` | Stripe API client |
| `MailKit` | SMTP email sending |
| `QRCoder` | QR code generation |
| `Serilog.AspNetCore` | Structured logging |
| `Hangfire` | Background job scheduling (stale booking cleanup) |
| `Hangfire.PostgreSql` | Hangfire PostgreSQL storage |

## Future Enhancements (Out of Scope)

- Multi-event / multi-season support
- Passenger self-service changes (move departure, cancel leg)
- Automated Phase 2 email notifications ("More buses now available!")
- Integration with Humans app (attendee cross-reference, team manifests)
- Apple Wallet / Google Wallet pass generation
- SMS confirmations
- Waitlist when sold out
- Dynamic pricing
- Accessibility / special requirements booking
- Check-in via Humans app (unified gate list)
- Revenue reporting and reconciliation dashboard
