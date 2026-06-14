# Rideshare — Section Design

**Date:** 2026-06-14
**Status:** Design only — not scheduled for build. Targeted for **Q4**.
**Author:** Peter (dictated), drafted via brainstorming dialogue.

> **Naming.** Called **Rideshare**, not "carpool": the vehicle might not be a car (van, camper, etc.), and for this European community the BlaBlaCar-style "ridesharing" framing — long-distance, one-off, cost-shared trips between people travelling anyway — is the natural mental model. The commercial Uber/Lyft connotation of "rideshare" in American English is explicitly **not** what this is; the non-goals (§3) fence that off.

> **Q3-refactor note.** The impending Q3 refactor will reshape section/interface names and possibly the service/repository split conventions. This spec captures the **intent and shape** of the Rideshare section, which is stable and survives that refactor. Where it names a concrete collaborator (`IBurnSettingsService`, `INotificationService`, `LocationProfileInfo`, the `(A)`-migrated service/repository pattern, the `§15` caching decorator, etc.), the implementer should re-anchor to whatever the post-Q3 equivalent is. The *concept* — a member-facing rideshare board over a map, anchored to one burn — does not change.

---

## 1. Purpose

A centralized place for the community to organize rides to and from the burn, replacing the spreadsheets people use today. The leap over a spreadsheet is the **map**: every offered ride is drawn as its real road route, so a member can pick a date and *see* which drivers pass near them — a driver going Berlin→Barcelona via Munich is visibly near a rider in Munich without anyone having to cross-reference cells.

The system facilitates people meeting and arranging rides; it does **not** broker, book, or take payment. Real Humans profiles back every posting, which is itself the core safety upgrade over an anonymous sheet: you know a little about who you'd be riding with.

Build this only if it earns its keep — the admin statistics view (§12) exists precisely so we can see whether the community uses it.

## 2. Goals

- Drivers post ride **offers**; riders post ride **requests**; both are tied to a real Humans profile and visible to members.
- A **map board**: pick a date + direction, see every offer (route lines) and request (pickup pins) active that day.
- Real road routing via OpenRouteService, computed once and stored, rendered on the existing MapLibre stack.
- Capture the two binding constraints of burn travel: **seats (people)** and **cargo (stuff)**.
- A **lightweight join/decline lifecycle** so capacity drains as people are accepted, with declines kept private and non-confrontational.
- Cost-sharing expectations, multi-day spans, and an overnight blurb.
- An admin **statistics** view to judge whether the system is working.

## 3. Non-goals (v1)

- **No payment processing.** Cost-sharing is an *expectation* only; no money moves through the app.
- **No in-app chat threads.** Contact is a single notification intro; the conversation happens off-platform.
- **No precise home addresses.** Endpoints are coarse city-level points the user chooses to publish.
- **No multi-event.** One active burn at a time.
- **No automated matching — humans match by eye.** Neither the server nor the client computes "who's near whom." The board simply renders every offer (line) and request (pin) for the date; riders and drivers look at the map, spot who's there, and reach out. (A client-side "highlight near my pickup" aid is noted as a possible *future* enhancement in §15 — explicitly **not** v1.)
- **No per-day route-position tracking.** A multi-day trip shows its whole route on every covered date; we do not compute where along the route a driver is on day 2.
- **No safety scoring, vetting workflow, or surveillance.** Accept/decline is the driver's gut call; the app stays out of it.
- **No automatic cargo-capacity math.** Cargo does not subtract cleanly; the driver eyeballs it via the capacity note as riders are accepted.

## 4. Concepts & vocabulary

- A **Ride offer** (a `RideshareTrip`) is one driver's leg — a route from their city to the burn (inbound) or from the burn to their city (outbound), with seats and cargo space on offer.
- A **Ride request** (a `RideshareRequest`) is one rider's need — a pickup point and a desired date, with party size and the cargo they're hauling.
- An **Interest** (a `RideshareInterest`) is the "I'm interested" / "I can take you" action. It **always anchors to the `Trip` whose seat would be consumed** (so seat-draining and stats stay correct on both the offer-join and the request-pin paths), and *optionally* records the rider's `Request` it answered. It carries a status and fires a notification. It is **not** a booking.
- The **burn end** of any trip is fixed: every trip has one end at the burn destination (from section settings) and one end at the member's city.
- A trip is **inbound** (to the burn) or **outbound** (from the burn). Creating an inbound offer **auto-seeds** the inverse outbound offer; the two then detach and are edited/deleted independently.
- **Seats remaining** is *derived*: `SeatsOffered − Σ(seats of accepted interests)`. A trip is `Full` when this hits zero.
- The **two capacity axes** are independent: **people** (seats ↔ party size) and **stuff** (luggage capacity ↔ luggage load), the latter on a shared coarse scale.

## 5. Actors & roles

| Actor | Capabilities |
|-------|--------------|
| Any active member | Browse the board; post/edit/cancel their own offers and requests; express interest in an offer or request; accept/decline/withdraw on their own postings' interests |
| Rideshare admin (role TBD at build — e.g. `Admin`, or a dedicated coordinator) | Set the per-year destination point + travel windows; view the statistics dashboard |

The board is **members-only** (authenticated active members). No anonymous or public access.

## 6. Data model

Four owned tables. All point/coordinate fields are stored as owned scalar columns (`*Latitude`, `*Longitude`, `*Label`), consistent with `LocationProfileInfo`'s shape. Route geometry is stored as **GeoJSON in a `text` column**, matching CityPlanning's deliberate choice (the app never queries inside the JSON; it round-trips whole geometries to MapLibre). Cross-section references are **FK-only scalars, no navigation properties**.

### 6.1 `rideshare_trips` — a driver's leg

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | FK → User (driver) — FK only |
| Direction | RideshareDirection | Inbound (to burn) / Outbound (from burn) |
| MemberPlaceLabel | string | The non-burn end, e.g. "Berlin" |
| MemberLatitude | double | Pre-fillable from the driver's profile location; user-overridable |
| MemberLongitude | double | |
| WaypointsJson | jsonb | Ordered vias `[{label, lat, lng}]` — the driver's declared route (Berlin → **Munich** → Barcelona). Empty = direct. |
| RouteGeoJson | text | Computed polyline (member ↔ burn through vias), stored once at save. Frozen — survives later settings edits. |
| DepartureDate | LocalDate | First day of travel |
| ExpectedDurationDays | int | 1 = same-day; 2+ = multi-day |
| OvernightPlan | string? | Free text, surfaced only when `ExpectedDurationDays > 1` |
| VehicleType | VehicleType | Car / Van / Other — vehicle-agnostic; it might not be a car |
| SeatsOffered | int | People axis |
| LuggageCapacity | LuggageSize | Stuff axis — coarse scale |
| CapacityNote | string? | Specifics the scale can't capture ("half a pickup bed free", "trunk already packed") |
| Restrictions | string? | Rules for riders ("no smoking", "no pets") |
| WillingToDetour | bool | Will pick up en route |
| CostSharing | CostSharing | Free / ShareFuel / Other |
| CostNote | string? | "~€20 to Barcelona, split tolls" |
| LinkedTripId | Guid? | Soft link to the paired return leg — **display only**, non-authoritative |
| Status | TripStatus | Active / Cancelled (`Full` is derived, not stored) |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

**Derived:** `SeatsRemaining = SeatsOffered − Σ(Seats of interests on this trip with Status = Accepted)`. `IsFull = SeatsRemaining ≤ 0`.

### 6.2 `rideshare_requests` — a rider's need

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | FK → User (rider) — FK only |
| Direction | RideshareDirection | Inbound / Outbound |
| PickupPlaceLabel | string | "Lyon, France" |
| PickupLatitude | double | Pre-fillable from profile location; overridable |
| PickupLongitude | double | |
| DesiredDate | LocalDate | Wanted travel date (flexibility expressed in Notes) |
| PartySize | int | People axis |
| LuggageLoad | LuggageSize | Stuff axis — same coarse scale as offers |
| CanContributeToFuel | bool | Rider signals willingness to chip in |
| Notes | string? | Free text ("flexible ±1 day", "one large bag") |
| Status | RequestStatus | Active / Cancelled (`Matched` derived — see below) |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

**Derived:** a request is `Matched` when its owner has secured a ride — i.e. there exists an `Accepted` interest either authored by the rider on an offer, or referencing this request (`RequestId`). Used by the stats view's "still looking" count; kept derived to avoid a stored flag drifting.

### 6.3 `rideshare_interests` — the "I'm interested" log

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| FromUserId | Guid | FK → User — FK only |
| TripId | Guid | FK → rideshare_trips — **required**. The trip whose seat this interest consumes; the capacity/stats anchor for **both** paths (rider→offer and driver→request-pin). |
| RequestId | Guid? | FK → rideshare_requests — **optional origin pointer**. Set when a driver answers a rider's pickup pin ("I can take you"); records which request it answered. Null on the rider→offer path. |
| Seats | int | How many people this interest is for (defaults to the request's PartySize, else 1) |
| Message | string? | Optional intro note |
| Status | InterestStatus | Pending → Accepted / Declined / Withdrawn |
| CreatedAt | Instant | |
| RespondedAt | Instant? | When the owner accepted/declined |

**Constraint:** `TripId` is **always** set — every interest consumes (or would consume) a seat on a specific trip, so capacity and fill-rate stay correct regardless of which side initiated. `RequestId` is the optional origin pointer (no XOR). On the request-pin path, the driver picks or creates the trip the seat comes from when they click "I can take you." An interest fires a notification on create (§10); it is a log + signal, never a reservation.

### 6.4 `rideshare_settings` — per-year singleton

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Year | int | Burn year (unique) — anchored to `PublicYear` |
| DestinationLabel | string | "Burn site — main gate" |
| DestinationLatitude | double | Where vehicles actually drive to (meeting point / gate), not a map-polygon centroid |
| DestinationLongitude | double | |
| InboundWindowStart | LocalDate | ~4-week arrival spread |
| InboundWindowEnd | LocalDate | |
| OutboundWindowStart | LocalDate | ~2-week departure spread |
| OutboundWindowEnd | LocalDate | |
| UpdatedAt | Instant | |

> **Judgment call:** the travel windows are admin-set here. They could instead default off the burn's gate date via `IBurnSettingsService` and a build/teardown offset. Either is fine — implementer's choice. The destination *coordinates*, however, must live here regardless: no existing entity stores the site as a routable point.

### 6.5 Enums (section-owned)

| Enum | Values |
|------|--------|
| RideshareDirection | Inbound, Outbound |
| VehicleType | Car, Van, Other *(vehicle-agnostic; extend with Camper/Motorhome/Motorcycle if the community wants — left out of v1 to avoid speculative values)* |
| LuggageSize | Minimal *(a bag or two)*, Moderate *(a few bags)*, Lots *(a trunkful / big gear)*, Huge *(van / half-a-pickup load)* |
| CostSharing | Free, ShareFuel, Other |
| TripStatus | Active, Cancelled |
| RequestStatus | Active, Cancelled |
| InterestStatus | Pending, Accepted, Declined, Withdrawn |

## 7. Map & routing architecture

The differentiator is the map, and the key realisation is that **computing a route and displaying routes are separate**:

- **Compute once, at save.** When an offer is created or its endpoints/waypoints change, the service geocodes the member point (or uses the profile location), reads the destination from `rideshare_settings`, and calls the routing provider for the polyline through (member → vias → burn). The returned geometry is stored in `RouteGeoJson` and never recomputed at view time.
- **Display many.** The board view loads all relevant trips for a date and hands their stored polylines to MapLibre. Rendering N lines at once is exactly what CityPlanning already does with camp polygons — no per-view routing calls, no provider cost or latency on read.

**Provider: OpenRouteService (OSM-based).** It does both routing and geocoding, its geometry is freely storable (the store-once-redraw-for-weeks model depends on this), and it renders natively on the existing MapLibre client. Google Maps was considered — access exists — but its terms restrict caching/storing route results and expect them rendered on a Google map, which fights both the storage model and the existing MapLibre stack. If the implementer prefers Google for cost reasons, the route-storage clause must be verified first; ORS is recommended for stack consistency.

**Abstraction.** A section-level `IRouteProvider` (geocode + directions) lives in the Application layer; its ORS implementation is an external API client in Infrastructure, alongside the existing Google clients. Swapping providers is then a single implementation change.

**Privacy.** Only coarse city points are sent to the provider — never a precise home address. Profile location is a pre-fill the user can override.

## 8. Views, routes & API

| Route | Purpose | Access |
|-------|---------|--------|
| `GET /Rideshare` | The board: date picker + inbound/outbound toggle → all offers (lines) + requests (pins) for that day. Click a line → driver card + "I'm interested". Click a pin → rider card + "I can take you" (driver picks/creates the trip the seat comes from). Humans match by eye — no automated proximity ranking. | Members |
| `GET/POST /Rideshare/Offer` | Create/edit a ride offer. On create, auto-seeds the inverse return leg. | Members (own) |
| `GET/POST /Rideshare/Request` | Create/edit a ride request. | Members (own) |
| `GET /Rideshare/Mine` | My offers + requests + interest received/sent. | Members (own) |
| `GET/POST /Rideshare/Admin` | Set destination + travel windows; admin views (day roster + statistics). | Rideshare admin |
| `GET /Rideshare/Admin/Day?date=` | Operational day view: every ride *happening* that day — including full and cancelled — with its accepted rider roster. Safety/incident visibility + retrospective. | Rideshare admin |
| `POST /Rideshare/Interest` | Express interest in a trip or request (creates Pending interest, fires notification). | Members |
| `POST /Rideshare/Interest/{id}/Accept` · `/Decline` · `/Withdraw` | Lifecycle transitions. | Posting owner (accept/decline) / interest author (withdraw) |
| `GET /api/rideshare/board?year=&date=&direction=` | FeatureCollection: route lines (offers) + pickup pins (requests) for the day. Mirrors `CityPlanningApiController`. | Members |

## 9. Lifecycle

**Offer:** `Active` → `Cancelled`. Derives `Full` when seats remaining reaches zero. Seasonal: after the outbound window closes the trip is historical (kept for stats; not shown on the live board). The public board offers only **joinable** rides (Active, seats remaining > 0); once a ride is full or cancelled it drops off the openly-offered board but stays visible to admins in the day view (§12.1).

**Request:** `Active` → `Cancelled`. Derives `Matched`.

**Interest:** `Pending` → `Accepted` / `Declined` / `Withdrawn`.
- Rider expresses interest in an offer (or driver in a request) → `Pending` interest + notification to the posting owner.
- Owner **accepts** → interest `Accepted`, rider notified ("you're in"), seats-remaining recomputes, trip may derive `Full`.
- Owner **declines** → interest `Declined`, rider gets neutral notification (§11), no reason captured or shown.
- Either party **withdraws** → interest `Withdrawn`, seats-remaining recomputes if it had been accepted.

## 10. Triggers / side effects

- **Offer created** → geocode + route → store `RouteGeoJson`; seed the inverse return offer (swap ends, return date from the outbound window).
- **Offer edited** (member point / waypoints / destination changed) → recompute and re-store `RouteGeoJson`.
- **Interest created** → `INotificationService` fires to the posting owner, carrying the author's name, the pickup/route context, and the author's contact for off-platform follow-up.
- **Interest accepted** → notify the author; recompute seats remaining.
- **Interest declined** → neutral notification to the author (no reason).
- Admin actions (settings edits) → AuditLog entry (consistent with other admin surfaces).

## 11. Safety & privacy posture (invariants)

These are stated as hard invariants so an implementer cannot "helpfully" weaken them:

- **No anonymous postings.** Every offer, request, and interest is bound to a real Humans profile.
- **Members-only board.** No public or unauthenticated access.
- **Declines are private.** No reason is required, none is stored or shown, and a decline is never broadcast. The declined rider sees neutral language ("the driver wasn't able to offer a spot"). No "rejected", no scores, no public who-said-no.
- **Driver discretion is absolute.** Accept/decline is a gut call on comfort/safety; the app neither prompts for justification nor records one.
- **Coarse locations only.** City-level points, never precise home addresses. Profile location is a pre-fill, always overridable.
- **No vetting, scoring, or surveillance.** The safety model is simply that profiles let members know a little about each other — the app surfaces real people and stays out of the way.
- **Rosters are admin-and-driver only.** Who is riding with whom (a trip's accepted riders) is visible to that trip's driver and to Rideshare admins (incident/safety visibility, §12.1), never on the public board.
- **GDPR.** The section owns user-scoped data, with **export and erasure as two separate integrations** (they are not the same hook):
  - **Export:** implement `IUserDataContributor` (read-only `ContributeForUserAsync`), fanned out by `GdprExportService`.
  - **Right-to-erasure:** `IUserDataContributor` does **not** delete. Add an explicit per-section cleanup call (delete/anonymize the user's trips, requests, interests) into `AccountDeletionService.AnonymizeExpiredAccountAsync` (with `PurgeAsync` parity), the way Teams/Shifts rows are cleaned today. A bare-FK `UserId` has no DB cascade, so the rows must be removed explicitly or they outlive the account.

## 12. Admin views

### 12.1 Operational day view — rides *happening* on a day

`GET /Rideshare/Admin/Day?date=` lists every ride happening that day — **including full and cancelled trips** (which the public board no longer offers to new riders) — each with its accepted **roster** (driver + accepted riders). Two purposes: **safety/incident visibility** (who was travelling together on a given day if there's a problem) and **retrospective/celebration** (we ran all these rides — yay). No new data: it reads the same trips and interests, expands accepted interests into rosters, and applies no seats-remaining filter.

### 12.2 Season statistics — "is this working?"

A **derived** dashboard on `/Rideshare/Admin` — no analytics table; aggregate over the three tables for the active year:

- Offers posted; requests posted.
- Seats offered (Σ `SeatsOffered`); seats filled (Σ accepted-interest seats); people seated.
- Requests still unmatched ("still looking").
- Fill rate.

Headline reads like *"78 offers · 100 seats filled · 12 riders still looking."* If those numbers stay near zero across a season, that is the cheap signal that the community prefers the spreadsheet and we should not invest further.

## 13. Architecture placement

- **New section, `(A)` from day one** (new sections must be — see SECTION-TEMPLATE).
- **Owning service:** `RideshareService` (`Humans.Application.Services.Rideshare`), never imports EF. **Repository:** `RideshareRepository` (`Humans.Infrastructure/Repositories/Rideshare/`) — the only code path touching the four `rideshare_*` tables.
- **Caching decorator (§15), following the Events pattern.** The board is a hot, read-heavy seasonal surface; cache the active-year snapshot (trips + requests + stored route geometry) and invalidate inline on every write. The day/direction filter runs in memory over the snapshot.
- **Cross-section read interface** `IRideshareServiceRead` only if another section consumes rideshare data (none anticipated at v1; do not pre-build it).
- **GDPR** — implement `IUserDataContributor` for export (read-only); wire right-to-erasure **separately** into `AccountDeletionService` (explicit per-section delete/anonymize of trips/requests/interests), not via the contributor — which is export-only.
- **Architecture test:** `RideshareArchitectureTests` pins the service/repository split, route names, the no-cross-section-nav rule, and the caching-decorator shape. The universal `HUM0025` analyzer enforces single-repository table ownership.
- **On build:** add the four tables to `design-rules.md §8`, add `docs/sections/Rideshare.md`, add nav links (no orphan pages), and add the CLAUDE.md Extended Docs entry if warranted.

## 14. Cross-section dependencies

- **Users / Profiles** — `IUserService` / `IProfileService`: display name, profile picture, default origin from `LocationProfileInfo`, and contact details for the interest notification.
- **Notifications** — `INotificationService`: the interest intro and accept/decline notifications.
- **BurnSettings (Shifts)** — `IBurnSettingsService`: `PublicYear` (anchors the per-year settings singleton) and, optionally, the gate date to default the travel windows.
- **OpenRouteService** — external routing/geocoding API client in Infrastructure (not a section), behind `IRouteProvider`.

## 15. Deferred / open judgment calls

Minor decisions explicitly punted to the implementer:

- **`PartySize` semantics** — assumed a simple count; could allow "flexible" via Notes only.
- **Vehicle enum** — starts `Car / Van / Other`; extend with `Camper` etc. if the community wants it.
- **Travel windows** — admin-set in `rideshare_settings` vs. derived from the burn gate date.
- **Luggage scale labels** — the four-point scale wording can be refined; keep it coarse.
- **Rideshare admin role** — reuse `Admin`, or introduce a dedicated coordinator role.
- **Client-side "highlight near my pickup/route" aid** — a possible future convenience (point-to-line distance in the browser); **not v1**. v1 keeps matching fully manual — the map shows everyone, the actual humans decide and reach out.

---

*Brainstormed 2026-06-14. This document is the design of record for the Rideshare section. It is intentionally complete enough to build from after the Q3 refactor with minimal further input; the concept is stable even as the surrounding section/interface names shift.*
