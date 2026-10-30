<!-- freshness:triggers
  src/Sections/Humans.Rideshare/**
  tests/Humans.Rideshare.Tests/RideshareArchitectureTests.cs
  src/Humans.Base/Constants/RoleNames.cs
  src/Humans.Base/Authorization/PolicyNames.cs
  src/Sections/Humans.Notifications.Contracts/RideshareNotificationSources.cs
  src/Sections/Humans.AuditLog/Contracts/AuditAction.cs
  src/Sections/Humans.AuditLog/Contracts/IAuditLogService.cs
  src/Sections/Humans.Settings/Contracts/ISettingsService.cs
  src/Sections/Humans.Notifications.Contracts/INotificationEmitter.cs
  src/Sections/Humans.Users.Contracts/IUserService.cs
  src/Sections/Humans.Users.Contracts/UserInfo.cs
  src/Sections/Humans.Users.Contracts/IUserMerge.cs
  src/Sections/Humans.Gdpr/Contracts/IUserDataContributor.cs
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
- **Settings** (`RideshareSettings`) is a per-event singleton: the burn destination point and the
  inbound/outbound travel windows.
- **Event scope.** Every settings row, trip and request belongs to an event (`EventSettingsId`); the
  board, Mine and admin pages show the active event's. A row without an event id (from before events
  were tracked) belongs to the earliest non-deleted event of its `Year` by gate opening, which is the
  event the `rideshare.event-ids` data migration (on `/Debug/DataMigrations`) stamps on it. With no
  active event the pages show "not set up" and posting is refused.
- **SeatsRemaining** is derived, never stored: `SeatsOffered − Σ(Seats of Accepted interests on
  the trip)`. A trip `IsFull` when this is `≤ 0`.
- **Matched** (on a request) is derived: an `Accepted` interest exists either authored by the
  request's owner or referencing the request by id, on an active trip going the request's
  direction and travelling on its date.
- **Full** and **Matched** are read-model properties, computed by `RideshareSnapshot`/`TripView`/
  `RequestView` — no stored flags to drift.

## Data Model

### RideshareTrip

**Table:** `rideshare_trips`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | index; bare FK (driver) — no navigation |
| Year | int | index; the event's year at creation |
| EventSettingsId | Guid? | index; the event; null until `rideshare.event-ids` stamps a legacy row |
| Direction | RideshareDirection | string(50) |
| MemberPlaceLabel | string | max 256, required |
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

**Indexes / constraints:** non-unique on `UserId`, `Year` and `EventSettingsId`; no database FK to users.

**Derived (not stored):** `SeatsRemaining`, `IsFull`, `LastTravelDate`, `IsJoinable`.

### RideshareRequest

**Table:** `rideshare_requests`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | index; bare FK (rider) — no navigation |
| Year | int | index |
| EventSettingsId | Guid? | index; as on trips |
| Direction | RideshareDirection | string(50) |
| PickupPlaceLabel | string | max 256, required |
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

**Indexes / constraints:** non-unique on `UserId`, `Year` and `EventSettingsId`; no database FK to users.

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

**Indexes / constraints:** non-unique on `FromUserId`, `TripId`, `RequestId`; FK `TripId` → `rideshare_trips` (cascade), FK `RequestId` → `rideshare_requests` (set null).

**Cross-section FKs:** `UserId`/`FromUserId` → User (Users section) — bare Guid, no navigation.

### RideshareSettings

**Table:** `rideshare_settings`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Year | int | indexed; the event's year |
| EventSettingsId | Guid? | indexed; one row per event is a repository rule, not a constraint |
| DestinationLabel | string | max 200, required |
| DestinationLatitude | double | required |
| DestinationLongitude | double | required |
| InboundWindowStart | LocalDate | required |
| InboundWindowEnd | LocalDate | required |
| OutboundWindowStart | LocalDate | required |
| OutboundWindowEnd | LocalDate | required |
| UpdatedAt | Instant | required |

**Indexes / constraints:** non-unique on `Year` and `EventSettingsId`; one row per event is the repository rule, not a constraint. Saving an event's settings stamps its year's legacy row rather than adding a second one.

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
| `/Rideshare/Admin` | GET/POST | `RideshareAdminOrAdmin` | Set the active event's destination + travel windows; season statistics |
| `/Rideshare/Admin/Day?date=` | GET | `RideshareAdminOrAdmin` | Operational day roster: every trip happening that day, any status, with its accepted riders |
| `/api/rideshare/board?date=&direction=` | GET | `AppAccess` | GeoJSON FeatureCollection of joinable trips, active requests, and the destination |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any active human | Browse the board; post/edit/cancel own offers and requests; express interest in a trip (optionally answering a request's pin); accept/decline/withdraw interests on own postings; view own offers/requests/interests on `Mine` |
| RideshareAdmin | Set the active event's destination and travel windows; view season statistics; view the operational day roster (every trip happening on a date, including full and cancelled, with its accepted rider roster). Board-grantable; confers nothing outside Rideshare |
| Admin | Everything RideshareAdmin can do (Admin is always a superset) |

## Invariants

- The cached snapshot leaves display ordering to the board, Mine, map and day-roster builders. Creation time breaks display ties; day-roster riders remain chronological.

- **The list works independently of the map.** Interest forms remain usable from the server-rendered cards when the map library, WebGL, map load or board data request fails.
- **Map popup dates follow the UI culture.** GeoJSON retains ISO departure/desired dates and provides display labels from the same formatter as the list; popups escape and render those labels.
- **Avatar initials are whole graphemes.** A map-popup initial keeps the name's first Unicode grapheme (combining accents, joined emoji); names and initials are HTML-escaped.
- **Expected refusals log without stacks.** Not-found, forbidden and rule refusals in the member and admin controllers log at Warning with the action (or event) and the reason or rule key, never the exception.
- **No anonymous postings.** Every trip, request, and interest is bound to a real Humans profile (`UserId`/`FromUserId`).
- **Members-only board.** Every route requires `AppAccess` or `RideshareAdminOrAdmin`; there is no anonymous or public access.
- **Interest always anchors to a trip.** `RideshareInterest.TripId` is required on both the rider→offer and driver→request-pin paths; `RequestId` is an optional origin pointer only, never the anchor.
- **Seats remaining is derived, never stored.** `SeatsRemaining = SeatsOffered − Σ(Seats of Accepted interests on the trip)`; a trip is full when this is `≤ 0`.
- **A request's Matched state is derived, never stored.** True when an `Accepted` interest exists with `FromUserId == request.UserId` or `RequestId == request.Id`, on an `Active` trip with the request's direction that travels on its desired date; a ride accepted for the other leg or another day does not match it, and cancelling the trip un-matches the request.
- **Routing preparation stays section-internal.** `RideshareRouting` resolves member points, reuses or geocodes waypoints, and builds travel-order routes through `IRouteProvider`. `RideshareService` retains authorization, validation, lifecycle transitions and persistence.
- **Route geometry is computed once at save and frozen.** Recomputed only on create, on an update that changes the member point, waypoints, or direction, or on an update of a trip saved without a route (a retry after a provider outage) — never at view time, and never invalidated by a later settings edit. The straight-line fallback the board draws when the stored route is absent or invalid is not a route: it is rendered at view time through the current destination, so it follows a later destination edit.
- **Form pins match valid coordinates.** Map clicks wrap longitude into −180…180 before filling fields and placing the marker. Invalid or incomplete initial/manual coordinates remain unpinned; clearing or invalidating either field removes the pin without discarding the entered values. Valid edits restore it. Changing the place label clears the previous coordinates even when the map library or WebGL fails, so saving geocodes the new place.
- **Routing geometry is validated.** Provider geocodes must be Points and directions must be LineStrings with at least two positions. Coordinates must be numeric and finite, with latitude within −90…90 and longitude within −180…180. Route altitude and geometry metadata are retained. Invalid responses warn and return null. Invalid stored routes warn without exception stacks and use the travel-order fallback without rewriting data; when only the origin is available, the fallback is a Point.
- **A null route never blocks a save.** When the routing provider is unavailable, `RouteGeoJson` is stored as null and a warning is logged; the save still succeeds.
- **Declines are private.** No reason is required or stored; the declined party sees neutral language only, never a score or a broadcast reason.
- **Driver discretion is absolute.** Accept/decline is the posting owner's call; the app never prompts for or records a justification.
- **Coarse locations only.** Only city-level points are geocoded and sent to the routing provider; profile location is a pre-fill the user can always override. Offer and request labels accept the full 256-character profile city in forms and storage, without shortening it.
- **Rosters are admin-and-driver only.** A trip's accepted riders are visible to that trip's driver (via `Mine`) and to RideshareAdmin/Admin (Day roster); never on the public board. The rider on a roster row is the request's owner when the driver answered a pin, else the interest's `FromUserId`.
- **Rule failures are localized.** Mutation services return a refusal carrying a `RideshareResource` key and format arguments; member and admin controllers localize it and preserve the form or toast flow. Dependency faults propagate without becoming refusal feedback. Not-found and unauthorized exceptions retain their separate 404/403 paths. Offer/request form annotations use the shared required, maximum-length and numeric-range translations in all six cultures.
- **Ownership gates edits.** Only `trip.UserId` may update or cancel that trip; only `request.UserId` may update or cancel that request. A cancelled trip or request cannot be edited.
- **Admin settings require posted destination coordinates.** Missing latitude or longitude returns the form with field errors before any settings save or audit; an explicitly posted zero is valid. Existing coordinate bounds still apply.
- **Offer and request forms require posted choices and counts.** Direction, vehicle type, luggage capacity/load, cost sharing, detour willingness, fuel contribution, duration, seats and party size cannot silently fall back to initial values when omitted. Invalid forms show field errors before any save; explicitly posted zero-valued choices and both true and false checkbox values remain valid.
- **Interest posts reject malformed ids, not a cleared seats box.** An unparseable trip or request id is a 400 before any write. A blank `seats` binds to null and takes the service default (party size for a driver's offer, the seats-≥-1 refusal otherwise).
- **ExpressInterest requires capacity and consent.** The trip must be `Active` with `SeatsRemaining ≥ seats` (seats ≥ 1); a human cannot express interest in their own trip; when `requestId` is given, the request must be `Active`, the caller must be the trip's owner (driver answering a pin), and the trip must go the request's direction and travel on its `DesiredDate`; a duplicate `Pending` interest by the same user on the same trip+request pair is rejected.
- **Accept requires ownership, pending state, and capacity.** The actor must be the posting owner (`requestId != null ? request.UserId : trip.UserId`); the interest must be `Pending`; the trip must be `Active` with `SeatsRemaining ≥ interest.Seats`. On a pin answer the pin must still be `Active` and the trip must still go that direction on the pin's date, the same check as ExpressInterest, since either side may have edited in between.
- **Interest and posting mutations are serialized.** Interest creation, acceptance, decline, and withdrawal share a boundary through persistence with offer/request edits and cancellations. Concurrent requests cannot create duplicate pending interests, exceed capacity while reducing seats, overwrite a completed response, or restore a cancelled posting through a stale edit. Notifications run after the boundary.
- **Interest messages are limited to 1000 characters after trimming.** Longer messages receive a localized refusal before persistence or notification; messages at the limit remain valid.
- **Withdraw is available to the author or the posting owner**, from `Pending` or `Accepted`.
- **Offer travel spans must fit within the supported date range.** Creation validates both the posted departure and the seeded inverse departure; edits validate the new departure and duration before persistence. Invalid spans return the localized invalid-date refusal.
- **Creating an inbound (or outbound) offer auto-seeds the inverse leg.** `LinkedTripId` is a soft, display-only link — the two legs are not otherwise coupled and are edited/cancelled independently.

## Negative Access Rules

- Unauthenticated visitors **cannot** view the board or reach any `/Rideshare` or `/api/rideshare` route.
- A human **cannot** edit or cancel another human's trip or request.
- A human **cannot** express interest in their own trip.
- A human **cannot** accept or decline an interest on a posting they do not own.
- A human **cannot** withdraw an interest they neither authored nor own the posting for.
- Non-admins **cannot** set the destination/travel windows, view season statistics, or view the operational day roster.
- The public board **cannot** show a `Full` or `Cancelled` trip as joinable — only the admin day view does. The board list, map and current driver’s offer selector share the snapshot’s joinable-trip filter (active, seats available, direction and travel date).
- The declined party **cannot** see a reason for the decline — none is stored.

## Triggers

- When an offer is created: the member point (or pickup) is geocoded, a route is computed once
  through the event's destination and stored as `RouteGeoJson`, and a second, inverse-direction
  offer is auto-seeded (same driver/vehicle/seats/luggage/cost fields, waypoints reversed,
  `LinkedTripId` set on both rows).
- When an offer is edited and the member point, waypoints, or direction changed, or no route was
  stored: the route is recomputed and re-stored.
- When an interest is created: `INotificationEmitter` notifies the posting owner (`Actionable`),
  best-effort — name/recipient lookup, message preparation and delivery failures are caught and logged,
  never surfaced after the interest is saved. The same boundary applies to acceptance and decline.
  Reads and persistence honor caller cancellation; after a successful save, notification lookups and
  delivery run without the browser token so navigation cannot interrupt the committed action.
- When an interest is accepted: the interest's author is notified (`Informational`) — a rider
  that they are in, a driver whose answer to a pin was accepted that their offer was; seats
  remaining recomputes on next read.
- When an interest is declined: the interest's author gets a neutral `Informational`
  notification with no reason.
- When settings are saved: `IAuditLogService.LogAsync(AuditAction.RideshareSettingsUpdated, ...)`
  records the change.
- After every write, `CachingRideshareService` clears its snapshot cache so the next read
  rebuilds it. A read begun before that clear cannot repopulate the cache with its old snapshot.

## Cross-Section Dependencies

- **Users**: `IUserService` — display name/picture for driver and rider cards, board
  properties, and notification "from" names. `IUserMerge` — on account merge,
  `ReassignAsync` re-points the archived user's trips, requests and interests at the survivor,
  then drops interests the fold made self-interest (rider on their own trip, driver answering
  their own request) and duplicate pending interests on one trip/request (earliest wins).
  Idempotent; the decorator clears the snapshot cache afterwards.
- **Settings**: `ISettingsService.GetActiveEventSettingsAsync()` — the active event everything anchors to;
  `GetAllEventSettingsAsync()` — names events and maps a legacy row's year to its event.
  The decorator implements `IEventSettingsChangeListener` and clears every snapshot when an event changes (a rename,
  or a gate-opening change that moves a year's legacy rows).
- **Notifications**: `INotificationEmitter.SendAsync` — interest created/accepted/declined Its sources are declared in `RideshareNotificationSources` (`src/Sections/Humans.Rideshare/RideshareNotificationSources.cs`), one `NotificationSourceDefinition` each: `RideshareInterestReceived` (actionable, to the trip/request owner: someone expresses interest in a ride); `RideshareInterestAccepted` (informational, to the interested user: owner accepts the interest); `RideshareInterestDeclined` (informational, to the interested user: owner declines the interest).
  notifications, localized to each recipient's supported preferred language (English for missing, blank, malformed or unsupported values), including trip dates, action labels and the fallback actor name when no burner name is available, `actionUrl: "/Rideshare/Mine"`.
- **AuditLog**: `IAuditLogService.LogAsync` — settings updates.
- **Gdpr**: `IUserDataContributor` — exports `RideshareTrips`, `RideshareRequests` and
  `RideshareInterests` for the resolved survivor, including requests under archived account IDs;
  erasure deletes the user's interests, then their
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
| `IRideshareService` | — | Not published — no other section consumes Rideshare; no `Contracts` leaf |

### For (A) Migrated sections

- `RideshareService` lives in `src/Sections/Humans.Rideshare/Services/` and never imports `Microsoft.EntityFrameworkCore`.
- `IRideshareRepository` (impl `RideshareRepository`, `IDbContextFactory<RideshareDbContext>`) is the only code path that touches this section's tables via `DbContext`.
- **Decorator decision** — caching decorator (`CachingRideshareService`, Singleton). One
  `TrackedCache<Guid, RideshareSnapshot>` inherited by the decorator, keyed `"Rideshare.Snapshot"` (per event, `warmOnStartup: false`; the no-event snapshot is not cached);
  its guarded loader prevents reads begun before a clear from repopulating stale snapshots.
  Every write delegates to the inner service, then the cache is cleared in full even if the
  inner call throws or is cancelled (a write may have committed before a later failure).
  This includes GDPR erasure and account merge; the original failure still propagates. An event-graph
  cache at this scale, cleared on any write, is acceptable rather than tracking per-event
  invalidation keys.
- **Display stitching** — cross-section display data resolves through `IUserService.GetUserInfosAsync`.
- **Cross-section calls** — `IUserService`, `ISettingsService`, `INotificationEmitter`, `IAuditLogService`, `IClock` (NodaTime).
- **Architecture test** — `tests/Humans.Rideshare.Tests/RideshareArchitectureTests.cs` pins the service/repository split, the canonical `Rideshare` / `api/rideshare` route names, and the caching-decorator shape.

### Routing provider

`IRouteProvider` (geocode + directions) is a section-internal abstraction over OpenRouteService
(`OpenRouteServiceClient`, `HttpClient`-based). Provider failures — including malformed geometry,
an unconfigured API key or a non-success response — log a warning and return null, keeping
unavailable routing from blocking a save (see Invariants). Caller cancellation propagates.
