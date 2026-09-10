<!-- freshness:triggers
  src/Sections/Humans.Rideshare/**
  tests/Humans.Rideshare.Tests/**
-->

# Rideshare — Target Shape

Derived fresh each section-doctor run, before any scan. What the section *should* be, not a
summary of what it is.

## 1. What the section does

Lets members find each other for the drive to and from the burn. A driver posts the leg they
are driving — where from, when, how many seats and how much boot space — and the app draws it
on a map as a route to the burn. A rider posts where they need picking up and when, and the app
drops a pin. Everyone looks at the same map for a date and a direction, spots who is near whom,
and says "I'm interested" or "I can take you". The owner of the posting accepts or declines;
an accepted interest consumes seats, and a trip with no seats left drops off the board.

Nothing is booked, paid for, or matched by the app. It notifies the two people, shows each of
them their own postings and answers on one page, and stops there. An admin sets the year's
destination and the two travel windows, sees season totals, and can pull up any day's rides
with who was travelling in each car.

## 2. The shapes

The section's external surface, grouped by the question each endpoint answers rather than
listed. The grouping is what makes collapse and duplication visible.

| # | Shape | Where it appears | Notes |
|---|---|---|---|
| S1 | **Read the board for a date and direction** — joinable trips, active requests, the destination | `/Rideshare` (HTML), `/api/rideshare/board` (GeoJSON) | One question, two renderings of one snapshot; the HTML page is the accessible list under the map the API feeds |
| S2 | **Own-posting lifecycle** — show form, save (create or edit), cancel | `/Rideshare/Offer`, `/Rideshare/Request` and their `/{id}/Cancel` | One shape over two posting kinds; create seeds the inverse leg for offers only |
| S3 | **Interest lifecycle** — express, then accept / decline / withdraw | `/Rideshare/Interest`, `/Rideshare/Interest/{id}/{Accept,Decline,Withdraw}` | Express has two entry paths (rider → offer, driver → pin); the three transitions share one owner-or-party gate |
| S4 | **My corner** — own postings with what each received, plus what I sent | `/Rideshare/Mine` | The landing page every write redirects to |
| S5 | **Season admin** — set destination + windows, read season totals, read a day's rosters | `/Rideshare/Admin`, `/Rideshare/Admin/Day` | RideshareAdmin or Admin only; the only place accepted riders are listed together |
| R1 | **Resolve a place and draw a road** — geocode a label, route through the destination | the routing provider | Outbound read-only calls; best-effort, never blocks a save |
| F1 | **User-data fan-outs** — export slices, erasure, merge fold | the service, via its decorator | Owed because all three posting tables are user-keyed |

What follows from the table drives everything below:

- **S1 through S5 all read one thing: the year's snapshot.** Every page and the API build
  their view model from the same in-memory graph of trips, requests, interests and settings;
  no page runs its own query. A read path that queries the repository directly is a second
  source of truth.
- **S2 is one pipeline carried twice.** Offer and Request differ in their form model and
  their save record; the controller flow (GET prefilled form, POST validate-save-redirect,
  POST cancel) and the error contract are identical. The two copies must stay step for
  step the same: a check present in one and missing from the other is the defect.
- **S3's transitions are one gate plus one status write each.** Accept carries the extra
  capacity and pin-still-valid checks; decline and withdraw carry none. Anything that grows
  beyond that in a transition is business logic looking for a home.

## 3. Structure

The layout those shapes imply, written fresh:

```
Controllers/        RideshareController      — S1 (HTML), S2, S3, S4; two error-mapping helpers, no rules
                    RideshareApiController   — S1 (GeoJSON)
                    RideshareAdminController — S5
Services/           IRideshareService + RideshareService — every rule in the section
                    CachingRideshareService  — Singleton decorator over the snapshot; carries F1
                    RideshareReadModels      — the snapshot and its per-row views; derived facts live here
                    RideshareRuleException   — a resource key plus args, the only way a rule reaches a user
                    AuditEntityTypes         — the one audit discriminator
                    Routing/                 — IRouteProvider + the OpenRouteService client + its options — R1
Data/               one repository over four tables, one context, one factory, four configurations, one migration
Domain/             four entities, six enums
Models/             one view model per page plus the GeoJSON builder; Build() methods project the snapshot, nothing more
Views/              Rideshare/ (board, mine, two forms, one interest-row partial), RideshareAdmin/ (settings, day)
wwwroot/js/rideshare/  board.js (map + popups → modals), pick-point.js (coarse point picker)
RideshareResource*  one resx set, six cultures, every key Rideshare_-prefixed
Docs/               Rideshare.md (invariants), authorization.md, data-access.md, features/, the dated design record
```

The structural rules the layout has to keep:

- **The service is the only place a rule lives.** Controllers translate exceptions into 404 /
  403 / localized error and redirect; view models project; JavaScript renders and opens
  modals whose forms POST normally. A seats check, an ownership check or a date check
  anywhere but `RideshareService` is a rule in the wrong layer.
- **Derived facts are computed, never stored.** Seats remaining, full, matched, joinable and
  last travel date are properties of the read models. A column for any of them is drift
  waiting to happen.
- **Every write goes through the decorator and clears the snapshot.** The inner service is
  keyed and never resolved by anything but the decorator.
- **The routing client returns null or a value, never throws to the service.** Every
  provider failure is logged and swallowed inside `Routing/`.

## 4. Invariants

Stated so a violation is recognisable, each with where the code enforces it:

- Every route requires a signed-in member. `AppAccess` on the board, forms and API
  (`Controllers/RideshareController.cs:20`, `Controllers/RideshareApiController.cs:16`);
  `RideshareAdminOrAdmin` on admin (`Controllers/RideshareAdminController.cs:15`,
  `SectionPolicies.cs:16`).
- Only the owner edits or cancels a posting, and a cancelled posting is never edited
  (`Services/RideshareService.cs:111`, `:113`, `:150`, `:187`, `:189`, `:204`).
- An offer's seats can never be edited below the seats already accepted
  (`Services/RideshareService.cs:118`).
- An interest always anchors to a trip; the request is an optional origin pointer
  (`Data/Configurations/RideshareInterestConfiguration.cs:29` required cascade FK, `:34`
  optional set-null FK; `Services/RideshareService.cs:219`).
- Seats remaining and full are derived from accepted interests, never stored
  (`Services/RideshareService.cs:602`–`605`, `Services/RideshareReadModels.cs:40`).
  Matched on a request is derived from an accepted interest on an active trip
  (`Services/RideshareService.cs:631`).
- Expressing interest requires an active trip with enough seats, at least one seat, not
  one's own trip, and no duplicate pending interest on the same trip and request
  (`Services/RideshareService.cs:221`–`247`). Answering a pin additionally requires being
  the trip's driver, an active request, and a trip that goes that direction on the request's
  date (`:230`, `:522`–`527`).
- Accept requires the posting owner, a pending interest, an active trip with enough seats,
  and, for a pin answer, the pin still answerable (`Services/RideshareService.cs:275`–`285`,
  owner gate `:589`–`599`). Decline requires owner and pending (`:299`–`303`). Withdraw is
  open to author or posting owner from pending or accepted (`:325`–`329`).
- Route geometry is computed at create, and on an edit only when the point, waypoints or
  direction changed; it is never recomputed at view time
  (`Services/RideshareService.cs:99`–`100`, `:124`–`138`). A null route never blocks a save
  (`:494`–`502`; `Services/Routing/OpenRouteServiceClient.cs:40`, `:68`).
- Declines carry no reason: the transition takes none and the author's notification is
  neutral (`Services/RideshareService.cs:299`–`320`).
- Notifications are best-effort; a failed send is logged and never rolls back the write
  (`Services/RideshareService.cs:650`–`665`).
- Saving settings writes one audit entry (`Services/RideshareService.cs:361`) and rejects
  a blank destination or an inverted window (`:340`–`342`).
- Every write through the decorator clears the whole snapshot cache
  (`Services/CachingRideshareService.cs:61`–`162`).
- Accepted riders are listed only to the trip's driver on Mine and to admins on the day
  roster (`Models/MineViewModel.cs:23`, `Models/AdminViewModels.cs:81`–`88` behind
  `Controllers/RideshareAdminController.cs:15`); the board API carries no interest data
  (`Models/BoardFeatureCollection.cs:93`–`129`).
- The public board offers only joinable trips: active and not full
  (`Services/RideshareReadModels.cs:47`, `:102`).
- The routing API key never reaches a log line (`Section.cs:42`–`44`).
- Erasure removes the member's interests, their trips with the trips' interests, and other
  drivers' answers to their requests before the requests themselves; the FK's set-null is a
  safety net, not the path (`Data/RideshareRepository.cs:150`–`173`). Merge re-points all
  three user columns and drops self-interest and duplicate pending interests the fold
  created (`:178`–`231`).

## 5. Seams

Specified-but-unbuilt work. Not built here, not ranked — reserved so items touching it are
shaped by it.

- **Notification board.** The interest-received, accepted and declined notifications are
  today three `INotificationEmitter` sends with English text and a link to Mine. The
  notification-board spec in flight (peterdrier/Humans#1635) makes interest-received a
  board entry and the accept/decline pair emails. When it lands, `NotifyAsync` is the one
  place that changes.
- **"Near my pickup" highlight.** The design record reserves a client-side proximity aid
  for a later version; the board stays fully manual until someone asks.

## 6. Deliberately not done

- **No Contracts leaf, no `IRideshareServiceRead`.** Nothing outside the section reads
  rideshare data. The first consumer earns the interface; until then every type is internal.
- **No automated matching, booking or payment.** The section is a map and two buttons; the
  humans do the matching and settle costs off-platform. Stated in the design record and the
  feature spec, and the reason there is no "match" state anywhere.
- **No per-year cache invalidation.** One write clears every year's snapshot. A year rebuilds
  in milliseconds at this scale; tracking which year a row belongs to would cost more code
  than it saves.
- **No stored seats-remaining, full or matched.** Derived on read from the interests, so
  nothing can drift.
- **No unique constraint on settings year.** One row per year is the repository's upsert rule;
  a constraint would add a migration to guard a table with one writer.
- **No FK from postings to users.** Cross-section Guids by rule; the user columns are indexed
  and bare.
- **No email on interest events.** The emitter decides the channel; Rideshare says what
  happened and to whom, not how it is delivered.
- **No generic form pipeline over the two posting kinds.** A helper taking the form model,
  the prefill, the save call and the success key as parameters would fold two twenty-line
  actions into one unreadable one. The copies stay; keeping them identical is the rule.

## Load-bearing weirdness

Essential complexity and settled decisions, so later runs stop re-litigating them:

- **The whole year is one snapshot in RAM.** `GetYearGraphAsync` loads every trip, request,
  interest and the settings row for a year in one go, and every page projects from that.
  This is the small-scale rule applied on purpose, not a query that needs optimising.
- **The decorator is a Singleton over a keyed Scoped inner.** `CachingRideshareService`
  opens a scope per call to resolve `RideshareService` under `InnerServiceKey`; the inner is
  never registered unkeyed. The GDPR contributor and the merge hook bind to the decorator so
  erasure and the merge fold clear the cache; the three fan-out methods sit on
  `IRideshareService` so the decorator can reach the inner through the same interface.
- **Creating an offer writes two trips.** The inverse leg is auto-seeded with waypoints
  reversed and `LinkedTripId` set both ways. The link is display-only; the legs edit and
  cancel independently by design, because drivers routinely change one direction's plan.
- **The straight-line fallback is drawn at view time.** When a stored route is null the
  board draws member point → waypoints → current destination on the fly. It follows a later
  destination edit while a real route does not; that asymmetry is documented, not a bug.
- **OpenRouteService, not Google.** Google's terms restrict storing route results and expect
  a Google map; the section stores every route and draws on MapLibre over OpenStreetMap
  tiles, so an OSM-based provider is the only one that fits the storage model.
- **`RemoveAllLoggers()` on the routing HttpClient.** The geocode request carries the API
  key in its query string; the default client logger would print it at Information.
- **Erasure deletes other drivers' answers to the erased member's requests.** Letting the
  FK set-null those interests would leave a driver's interest pointing at their own trip
  with no request, which reads as the driver riding with themselves.
- **Notification text is English and not localized.** The emitter contract takes literal
  strings; the section follows it. The seam above is where that changes, if it does.
- **`RideshareService.cs` is the section's one large file.** Every rule for five shapes plus
  the export projection lives in it on purpose, so a reader finds all of them in one place.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-09-10 | A sent interest now shows when its trip was cancelled; the untested rules and projections pinned | PR: pending |
