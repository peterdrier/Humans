<!-- freshness:triggers
  src/Sections/Humans.Rideshare/**
-->
<!-- freshness:flag-on-change
  Trip/request/interest state machines, the offer-create auto-seed rule, and the notification
  triggers — review when Rideshare controllers, service, or entities change.
-->

# Rideshare Board

Design of record: [`docs/superpowers/specs/2026-06-14-rideshare-section-design.md`](../../../../../docs/superpowers/specs/2026-06-14-rideshare-section-design.md).
Current-state invariants: [`Rideshare.md`](../Rideshare.md).

## Business Context

Humans organize rides to and from the burn today over a shared spreadsheet — no way to see
routes, no way to tell who's near whom, no safety context beyond a name in a cell. Rideshare
replaces it with a members-only map board: every offered ride is drawn as its real road route,
so a rider can pick a date and see which drivers pass near them. The system helps people find
each other; it does not broker, book, or take payment, and it does not match automatically —
humans look at the map and reach out.

## User Stories

### US-1: Driver Posts a Ride Offer

**As an** active human with a vehicle
**I want to** post a ride offer with my route, seats, and luggage space
**So that** riders can see and reach out to me

**Acceptance Criteria:**
- Offer form captures direction, place + coarse coordinates (pre-filled from profile, overridable), optional waypoints, dates/duration, vehicle type, seats offered, luggage capacity, cost-sharing expectation, and free-text notes
- On create, the route is geocoded and computed once through the year's destination and stored; a paired inverse-direction offer is auto-seeded (same driver/vehicle/seats/luggage/cost, waypoints reversed) and the two are linked for display
- On an edit that changes the member point, waypoints, or direction, the route is recomputed
- Only the driver can edit or cancel their own offer; a cancelled offer cannot be edited
- A null routing result never blocks the save — the offer still saves, with no drawn route

### US-2: Rider Posts a Ride Request

**As an** active human without a ride
**I want to** post a pickup point and desired date
**So that** drivers passing near me can offer a seat

**Acceptance Criteria:**
- Request form captures direction, pickup place + coarse coordinates (pre-filled, overridable), desired date, party size, luggage load, fuel-contribution willingness, and notes
- Only the rider can edit or cancel their own request; a cancelled request cannot be edited

### US-3: Members Browse the Board

**As an** active human
**I want to** pick a date and direction and see every offer and request for it
**So that** I can spot a ride that works for me

**Acceptance Criteria:**
- `/Rideshare` renders a map: offer routes as lines, requests as pickup pins, the destination as a fixed point
- Only joinable offers appear as lines (`Active`, not full, matching direction, covering the date); requests appear when `Active` and matching direction/date
- Clicking a line surfaces the driver's card and an "I'm interested" action; clicking a pin surfaces the rider's card and an "I can take you" action
- No automated proximity ranking — humans match by eye

### US-4: Expressing and Resolving Interest

**As an** active human
**I want to** express interest in an offer (or answer a request) and get a clear yes/no
**So that** I can arrange a ride without back-and-forth in the app

**Acceptance Criteria:**
- Expressing interest always anchors to a trip; answering a request's pin sets the request as the interest's origin pointer, seats defaulting to the request's party size
- Interest cannot be expressed on your own trip, on a non-`Active` trip, without enough remaining seats, or as a duplicate `Pending` interest on the same trip+request pair
- The posting owner sees a `Pending` interest and can accept or decline; the trip's driver can also withdraw on behalf of a no-longer-relevant match, as can the interest's author
- Accept requires the trip still has enough remaining seats; it notifies the author and drains capacity
- Decline requires no reason, stores none, and notifies the author with neutral language only
- Create, accept and decline each fire a notification (best-effort; a failure never blocks the interest action); withdraw is silent, per the design spec's notification list

### US-5: Admin Configures the Season

**As an** Admin
**I want to** set the year's destination and travel windows
**So that** routing has a burn endpoint to compute against and the board scopes to the right dates

**Acceptance Criteria:**
- `/Rideshare/Admin` lets an Admin set `DestinationLabel`/coordinates and the inbound/outbound travel windows for the active year
- Saving writes an audit log entry
- The same page shows season statistics: offers posted, requests posted, seats offered and seats filled (both on active trips only), riders still looking

### US-6: Admin Views the Operational Day Roster

**As an** Admin
**I want to** see every ride happening on a given day, including full and cancelled ones, with its accepted riders
**So that** I have safety/incident visibility and a retrospective view of what actually ran

**Acceptance Criteria:**
- `/Rideshare/Admin/Day?date=` lists every trip covering that date regardless of status or fill state
- Each trip shows its accepted rider roster (driver + accepted interests) — never shown on the public board

## Data Model Summary

Four tables — `rideshare_trips`, `rideshare_requests`, `rideshare_interests`,
`rideshare_settings` — full field lists and enums in [`Rideshare.md`](../Rideshare.md#data-model).
`SeatsRemaining` and a request's `Matched` state are always derived from `rideshare_interests`,
never stored.

## Workflows / State Machines

**Trip:** `Active → Cancelled`. `Full` is a derived read-model property (`SeatsRemaining ≤ 0`),
not a stored status — a full trip stays `Active` and simply stops being joinable.

**Request:** `Active → Cancelled`. `Matched` is likewise derived, not stored.

**Interest:**

```text
Pending ──accept (posting owner)──> Accepted
Pending ──decline (posting owner)──> Declined
Pending ──withdraw (author or posting owner)──> Withdrawn
Accepted ──withdraw (author or posting owner)──> Withdrawn
```

`Declined` and `Withdrawn` are terminal; an `Accepted` interest that withdraws frees the seat it
had consumed (`SeatsRemaining` recomputes on next read — nothing to reconcile, since it was
never stored).

## Related Docs

- [`Rideshare.md`](../Rideshare.md) — section invariants, data model, routing, GDPR wiring
- [`docs/superpowers/specs/2026-06-14-rideshare-section-design.md`](../../../../../docs/superpowers/specs/2026-06-14-rideshare-section-design.md) — design of record
