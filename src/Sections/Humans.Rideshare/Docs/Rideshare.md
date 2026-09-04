<!-- freshness:triggers
  src/Sections/Humans.Rideshare/**
-->
<!-- freshness:flag-on-change
  The interest-always-anchors-to-a-trip rule, seats/matched derivation, the route-frozen-at-save
  posture, the decline-privacy and roster-visibility invariants, and the GDPR export/erasure
  wiring — review when Rideshare services, entities, or controllers change.
-->

# Rideshare — Section Invariants

Members organizing rides to and from the burn: drivers post offers, riders post requests, and
a map board lets people spot each other by eye. No booking, no payment, no automated matching.

## Concepts

- A **Ride offer** (`RideshareTrip`) is one driver's leg — a route between their city and the
  burn destination, inbound (to the burn) or outbound (from it), with seats and luggage space
  on offer. Creating an inbound offer auto-seeds the paired outbound leg; the two then edit and
  cancel independently.
- A **Ride request** (`RideshareRequest`) is one rider's need — a pickup point, a desired date,
  and how many people/how much luggage they're bringing.
- An **Interest** (`RideshareInterest`) is the "I'm interested" / "I can take you" action. It
  always anchors to the `Trip` whose seat it would consume, and optionally records the
  `Request` it answered. It carries a status and fires a notification; it is never a booking.
- **Settings** (`RideshareSettings`) is a per-year singleton: the burn destination point and the
  inbound/outbound travel windows.
- **SeatsRemaining** is derived, never stored: `SeatsOffered − Σ(Seats of Accepted interests on
  the trip)`. A trip `IsFull` when this is `≤ 0`.
- **Matched** (on a request) is derived: an `Accepted` interest exists either authored by the
  request's owner or referencing the request by id.
- **Full** and **Matched** are read-model properties, computed by `RideshareSnapshot`/`TripView`/
  `RequestView` — no stored flags to drift.

## Data Model

### RideshareTrip

**Table:** `rideshare_trips`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | index; bare FK (driver) — no navigation |
| Year | int | index; the active burn year at creation |
| Direction | RideshareDirection | string(50) |
| MemberPlaceLabel | string | max 200, required |
| MemberLatitude | double | required |
| MemberLongitude | double | required |
| WaypointsJson | string? | text; JSON array of `{label, latitude, longitude}`; null/empty = direct |
| RouteGeoJson | string? | text; a GeoJSON geometry (LineString), or null when routing was unavailable |
| DepartureDate | LocalDate | required |
| ExpectedDurationDays | int | required, ≥ 1 |
| OvernightPlan | string? | max 1000 |
| VehicleType | VehicleType | string(50) |
| SeatsOffered | int | required |
| LuggageCapacity | LuggageSize | string(50) |
| CapacityNote | string? | max 500 |
| Restrictions | string? | max 500 |
| CostNote | string? | max 500 |
| WillingToDetour | bool | required |
| CostSharing | CostSharing | string(50) |
| LinkedTripId | Guid? | soft link to the paired leg — display only, no FK |
| Status | TripStatus | string(50) |
| CreatedAt | Instant | required |
| UpdatedAt | Instant | required |

**Derived (not stored):** `SeatsRemaining`, `IsFull`, `LastTravelDate`, `IsJoinable`.

### RideshareRequest

**Table:** `rideshare_requests`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | index; bare FK (rider) — no navigation |
| Year | int | index |
| Direction | RideshareDirection | string(50) |
| PickupPlaceLabel | string | max 200, required |
| PickupLatitude | double | required |
| PickupLongitude | double | required |
| DesiredDate | LocalDate | required |
| PartySize | int | required |
| LuggageLoad | LuggageSize | string(50) |
| CanContributeToFuel | bool | required |
| Notes | string? | max 1000 |
| Status | RequestStatus | string(50) |
| CreatedAt | Instant | required |
| UpdatedAt | Instant | required |

**Derived (not stored):** `IsMatched`.

### RideshareInterest

**Table:** `rideshare_interests`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| FromUserId | Guid | index; bare FK — no navigation |
| TripId | Guid | required; intra-section FK → `rideshare_trips`, `OnDelete(Cascade)`, nav `Trip` |
| RequestId | Guid? | intra-section FK → `rideshare_requests`, `OnDelete(SetNull)`, nav `Request` — optional origin pointer only |
| Seats | int | how many people this interest is for |
| Message | string? | max 1000 |
| Status | InterestStatus | string(50) |
| CreatedAt | Instant | required |
| RespondedAt | Instant? | set on accept/decline |

**Cross-section FKs:** `UserId`/`FromUserId` → User (Users section) — bare Guid, no navigation.

### RideshareSettings

**Table:** `rideshare_settings`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Year | int | unique index |
| DestinationLabel | string | max 200, required |
| DestinationLatitude | double | required |
| DestinationLongitude | double | required |
| InboundWindowStart | LocalDate | required |
| InboundWindowEnd | LocalDate | required |
| OutboundWindowStart | LocalDate | required |
| OutboundWindowEnd | LocalDate | required |
| UpdatedAt | Instant | required |

### RideshareDirection

| Value | Int | Description |
|-------|-----|-------------|
| Inbound | 0 | Travelling to the burn |
| Outbound | 1 | Travelling from the burn |

### VehicleType

| Value | Int | Description |
|-------|-----|-------------|
| Car | 0 | |
| Van | 1 | |
| Other | 2 | |

### LuggageSize

| Value | Int | Description |
|-------|-----|-------------|
| Minimal | 0 | A bag or two |
| Moderate | 1 | A few bags |
| Lots | 2 | A trunkful / big gear |
| Huge | 3 | Van / half-a-pickup load |

### CostSharing

| Value | Int | Description |
|-------|-----|-------------|
| Free | 0 | |
| ShareFuel | 1 | |
| Other | 2 | |

### TripStatus

| Value | Int | Description |
|-------|-----|-------------|
| Active | 0 | Joinable while not full |
| Cancelled | 1 | Withdrawn by the driver |

### RequestStatus

| Value | Int | Description |
|-------|-----|-------------|
| Active | 0 | |
| Cancelled | 1 | Withdrawn by the rider |

### InterestStatus

| Value | Int | Description |
|-------|-----|-------------|
| Pending | 0 | Awaiting the posting owner's decision |
| Accepted | 1 | Seat consumed; author notified |
| Declined | 2 | Seat not consumed; author gets a neutral notification, no reason |
| Withdrawn | 3 | Pulled by the author or the posting owner |

## Routing

| Route | Method | Auth | Purpose |
|-------|--------|------|---------|
| `/Rideshare?date=&direction=` | GET | `AppAccess` | The board: joinable offers (route lines) and active requests (pins) for a date + direction |
| `/Rideshare/Offer?id=` | GET/POST | `AppAccess` | Create/edit own ride offer; create auto-seeds the inverse leg |
| `/Rideshare/Offer/{id}/Cancel` | POST | `AppAccess` | Cancel own offer |
| `/Rideshare/Request?id=` | GET/POST | `AppAccess` | Create/edit own ride request |
| `/Rideshare/Request/{id}/Cancel` | POST | `AppAccess` | Cancel own request |
| `/Rideshare/Mine` | GET | `AppAccess` | Own offers, requests, and interests received/sent |
| `/Rideshare/Interest` | POST | `AppAccess` | Express interest in a trip, optionally answering a request's pin |
| `/Rideshare/Interest/{id}/Accept`, `/Decline`, `/Withdraw` | POST | `AppAccess` | Interest lifecycle transitions |
| `/Rideshare/Admin` | GET/POST | `AdminOnly` | Set the year's destination + travel windows; season statistics |
| `/Rideshare/Admin/Day?date=` | GET | `AdminOnly` | Operational day roster: every trip happening that day, any status, with its accepted riders |
| `/api/rideshare/board?date=&direction=` | GET | `AppAccess` | GeoJSON FeatureCollection of joinable trips, active requests, and the destination |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any active human | Browse the board; post/edit/cancel own offers and requests; express interest in a trip (optionally answering a request's pin); accept/decline/withdraw interests on own postings; view own offers/requests/interests on `Mine` |
| Admin | All active-human capabilities. Additionally: set the year's destination and travel windows; view season statistics; view the operational day roster (every trip happening on a date, including full and cancelled, with its accepted rider roster) |

## Invariants

- **No anonymous postings.** Every trip, request, and interest is bound to a real Humans profile (`UserId`/`FromUserId`).
- **Members-only board.** Every route requires `AppAccess` or `AdminOnly`; there is no anonymous or public access.
- **Interest always anchors to a trip.** `RideshareInterest.TripId` is required on both the rider→offer and driver→request-pin paths; `RequestId` is an optional origin pointer only, never the anchor.
- **Seats remaining is derived, never stored.** `SeatsRemaining = SeatsOffered − Σ(Seats of Accepted interests on the trip)`; a trip is full when this is `≤ 0`.
- **A request's Matched state is derived, never stored.** True when an `Accepted` interest exists with `FromUserId == request.UserId` or `RequestId == request.Id`.
- **Route geometry is computed once at save and frozen.** Recomputed only on create, or on an update that changes the member point, waypoints, or direction — never at view time, and never invalidated by a later settings edit.
- **A null route never blocks a save.** When the routing provider is unavailable, `RouteGeoJson` is stored as null and a warning is logged; the save still succeeds.
- **Declines are private.** No reason is required or stored; the declined party sees neutral language only, never a score or a broadcast reason.
- **Driver discretion is absolute.** Accept/decline is the posting owner's call; the app never prompts for or records a justification.
- **Coarse locations only.** Only city-level points are geocoded and sent to the routing provider; profile location is a pre-fill the user can always override.
- **Rosters are admin-and-driver only.** A trip's accepted riders are visible to that trip's driver (via `Mine`) and to Admins (Day roster); never on the public board. The rider on a roster row is the request's owner when the driver answered a pin, else the interest's `FromUserId`.
- **Rule failures are localized.** Services throw `RideshareRuleException` carrying a `RideshareResource` key (plus format args); controllers localize it, never show an exception message verbatim.
- **Ownership gates edits.** Only `trip.UserId` may update or cancel that trip; only `request.UserId` may update or cancel that request. A cancelled trip or request cannot be edited.
- **ExpressInterest requires capacity and consent.** The trip must be `Active` with `SeatsRemaining ≥ seats` (seats ≥ 1); a human cannot express interest in their own trip; when `requestId` is given, the request must be `Active`, the caller must be the trip's owner (driver answering a pin), and the trip must go the request's direction and travel on its `DesiredDate`; a duplicate `Pending` interest by the same user on the same trip+request pair is rejected.
- **Accept requires ownership, pending state, and capacity.** The actor must be the posting owner (`requestId != null ? request.UserId : trip.UserId`); the interest must be `Pending`; the trip must be `Active` with `SeatsRemaining ≥ interest.Seats`. On a pin answer the pin must still be `Active` and the trip must still go that direction on the pin's date, the same check as ExpressInterest, since either side may have edited in between.
- **Withdraw is available to the author or the posting owner**, from `Pending` or `Accepted`.
- **Creating an inbound (or outbound) offer auto-seeds the inverse leg.** `LinkedTripId` is a soft, display-only link — the two legs are not otherwise coupled and are edited/cancelled independently.

## Negative Access Rules

- Unauthenticated visitors **cannot** view the board or reach any `/Rideshare` or `/api/rideshare` route.
- A human **cannot** edit or cancel another human's trip or request.
- A human **cannot** express interest in their own trip.
- A human **cannot** accept or decline an interest on a posting they do not own.
- A human **cannot** withdraw an interest they neither authored nor own the posting for.
- Non-admins **cannot** set the destination/travel windows, view season statistics, or view the operational day roster.
- The public board **cannot** show a `Full` or `Cancelled` trip as joinable — only the Admin day view does.
- The declined party **cannot** see a reason for the decline — none is stored.

## Triggers

- When an offer is created: the member point (or pickup) is geocoded, a route is computed once
  through the year's destination and stored as `RouteGeoJson`, and a second, inverse-direction
  offer is auto-seeded (same driver/vehicle/seats/luggage/cost fields, waypoints reversed,
  `LinkedTripId` set on both rows).
- When an offer is edited and the member point, waypoints, or direction changed: the route is
  recomputed and re-stored.
- When an interest is created: `INotificationEmitter` notifies the posting owner (`Actionable`),
  best-effort — failures are caught and logged, never surfaced to the caller.
- When an interest is accepted: the interest's author is notified (`Informational`); seats
  remaining recomputes on next read.
- When an interest is declined: the interest's author gets a neutral `Informational`
  notification with no reason.
- When settings are saved: `IAuditLogService.LogAsync(AuditAction.RideshareSettingsUpdated, ...)`
  records the change.
- After every write, `CachingRideshareService` clears its snapshot cache so the next read
  rebuilds it.

## Cross-Section Dependencies

- **Users**: `IUserServiceRead` — display name/picture for driver and rider cards, board
  properties, and notification "from" names.
- **Shifts**: `IBurnSettingsService.GetActiveAsync()` — the active year settings anchor to
  (`GetActiveYearAsync` falls back to the clock's UTC year when no active burn is set).
- **Notifications**: `INotificationEmitter.SendAsync` — interest created/accepted/declined
  notifications, plain English (not localized), `actionUrl: "/Rideshare/Mine"`.
- **AuditLog**: `IAuditLogService.LogAsync` — settings updates.
- **Gdpr**: `IUserDataContributor` — exports three slices (`RideshareTrips`,
  `RideshareRequests`, `RideshareInterests`); erasure deletes the user's interests, then their
  trips (cascades the trips' interests), then other drivers' answers to their requests and the
  requests themselves — idempotent. (The FK's `SetNull` is a database safety net, never the
  intended path: an orphaned answer would read as the driver riding their own trip.)

## Architecture

**Owning services:** `RideshareService` (inner), `CachingRideshareService` (Singleton decorator)
**Owned tables:** `rideshare_trips`, `rideshare_requests`, `rideshare_interests`, `rideshare_settings`
**Status:** (A) Migrated — new section, built in this shape from day one.

### Cross-section read interface

| Read interface | Methods | Notes |
|---|---:|---|
| `IRideshareServiceRead` | — | Not published — no other section consumes Rideshare; no `Contracts` leaf |

### For (A) Migrated sections

- `RideshareService` lives in `src/Sections/Humans.Rideshare/Services/` and never imports `Microsoft.EntityFrameworkCore`.
- `IRideshareRepository` (impl `RideshareRepository`, `IDbContextFactory<RideshareDbContext>`) is the only code path that touches this section's tables via `DbContext`.
- **Decorator decision** — caching decorator (`CachingRideshareService`, Singleton). One
  `TrackedCache<int, RideshareSnapshot>` keyed `"Rideshare.Snapshot"` (per year, `warmOnStartup: false`);
  every write delegates to the inner service, then the cache is cleared in full — a year-graph
  cache at this scale, cleared on any write, is acceptable rather than tracking per-year
  invalidation keys.
- **Display stitching** — cross-section display data resolves through `IUserServiceRead.GetUserInfosAsync`.
- **Cross-section calls** — `IUserServiceRead`, `IBurnSettingsService`, `INotificationEmitter`, `IAuditLogService`, `IClock` (NodaTime).
- **Architecture test** — `tests/Humans.Rideshare.Tests/RideshareArchitectureTests.cs` pins the service/repository split, the canonical `Rideshare` / `api/rideshare` route names, and the caching-decorator shape.

### Routing provider

`IRouteProvider` (geocode + directions) is a section-internal abstraction over OpenRouteService
(`OpenRouteServiceClient`, `HttpClient`-based). It never throws — an unconfigured API key or a
non-success response logs a warning and returns null, so routing is always best-effort and never
blocks a save (see Invariants).
