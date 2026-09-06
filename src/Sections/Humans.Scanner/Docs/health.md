# Scanner — Target Shape

## 1. What the section does

Two camera tools for the people who run ticketing, used from a phone or a laptop.

The first reads a barcode and shows what it says. Nothing leaves the device; the list of
decoded values lives in the page and is gone on reload. It exists so staff can see what a
vendor's ticket stub actually encodes without opening the vendor's dashboard.

The second answers "whose ticket is this?" for one barcode, scanned or typed: the holder's
name and email, the ticket type, its status, and — for a voided ticket — who it went to and
when. When the ticket belongs to a known member, the card also shows what the door needs to
know about that person: from which date they may enter early and why, whether they have
already checked in this event, which required consents they still have not signed, and
what they have committed to provide — their shifts, and the events they are hosting —
in time order. Only this event's tickets are looked up; an older barcode reads as not
found.

Neither tool marks anyone as arrived. Admission is the Gate section's job.

## 2. The shapes

| Question shape | Asked by | Answered by |
|---|---|---|
| "Which tools are here?" | the admin sidebar's Tickets group | `GET /Scanner` |
| "What does this barcode encode?" | ticket staff, camera in hand | `GET /Scanner/Barcode` + `barcode.js`, browser only |
| "Whose ticket is this, and what does the door need to know about them?" | ticket staff, camera or keyboard | `GET /Scanner/Tickets` (the page) + `GET /Scanner/Tickets/Card?barcode=` (the answer, one partial) |

Two question shapes, one landing page. The second shape is one question with one answer,
even though it takes seven cross-section reads to assemble: the card is the unit, never the
reads.

Vocabulary: none of its own. The card speaks Tickets' `TicketStubInfo`/`TicketAttendeeStatus`,
EarlyEntry's `UserEarlyEntry`, Calendar's `CalendarFeedItem`, and Shifts' `BurnSettingsInfo`.

## 3. Structure

The shapes imply what is built: one controller that translates a barcode into a card, one
render model for that card, one partial that draws it, two pages that share the camera
module, and the camera module itself with a thin ticket-lookup wrapper over it. No service
layer — the section has no business rule of its own to enforce once, only reads to
compose. No repository, no tables, no cache: every read goes through the owning section's
interface, which already caches where it should.

The one thing the target would not build is the second half of the provide list: turning
"events this person is hosting" into calendar-feed items (per-occurrence expansion, the
`event-{id}-{date}` uid, the end-time arithmetic) is Events' knowledge, and Events already
does it for its iCal contributor. Scanner should ask Events for those items, not rebuild
them. That needs a read method on Events' contract, so it is a seam, not a strike.

## 4. Invariants

- Every route requires the `ScannerAccess` policy (TicketAdmin, Board, Admin, or the
  gate-terminal account by well-known id). Pinned by `EndpointAuthorizationTests` in
  `Humans.Web.Tests`.
- `/Scanner/Barcode` never sends a decoded value to the server.
- The card lookup writes nothing: no check-in, no participation, no ticket state, no audit
  row. `ScannerController` has no non-GET action.
- Only orders flagged as the current event are searched; a blank or unknown barcode yields
  the not-found card (blank: without echoing a code).
- Door context is assembled only when the ticket has a matched member; otherwise every
  per-person field is null. An empty pending-consents list means "all signed" and is
  distinct from null.
- Check-in is read for the active event year only; a prior year's check-in never shows.
- The provide list carries only Shifts-sourced feed items plus events the member submitted
  outside a camp, expanded per occurrence when an active event cycle gives a time zone.
- The camera stream is stopped on Stop, on page hide and before unload.

## 5. Seams

- **Events-owned provide items.** A read on `IEventServiceRead` returning the feed items for
  events a member is hosting would let `GetProvideItemsAsync` shrink to two calls and a
  merge, and delete this section's copy of Events' occurrence-to-feed-item rule.
- **Event-cycle cutover.** `IBurnSettingsService.GetActiveAsync` is the Shifts-owned twin of
  Settings' staged `IEventSettingsInfo` (nobodies-collective/Humans#1104). The card's year
  and time zone move with that cutover; nothing here should be shaped around Shifts.
- **Vendor verification.** The barcode spec's follow-up — ask the vendor whether a ticket is
  valid or already used — is unbuilt. It would be a new tool with its own route, never a
  server round-trip added to `/Scanner/Barcode`.

## 6. Deliberately not done

- **No service class.** A `ScannerService` would hold one method that the controller action
  already is. Composition of other sections' reads is translation, not a rule.
- **No caching decorator.** Every read is served by the owning section's cache; a Scanner
  cache would cache a cache and duplicate its invalidation.
- **Not merged into Gate.** Gate decides entry and writes an admission record; Scanner looks
  and never writes. The negative invariant is worth a separate section.
- **No Contracts leaf.** Nothing outside the section names a Scanner type.
- **No check-in from either tool**, ever. The spec, the section doc and the page copy all say
  so; the constraint is the section's reason to exist apart from Gate.

## Load-bearing weirdness

- **`Humans.Scanner.csproj` references the `Humans.Tickets` section project, not only its
  Contracts leaf.** The `<vc:ticket-stub>` tag helper is generated from the component type,
  which lives in the section project; a leaf-only reference renders the element as inert
  literal markup with a green build.
- **`ScannerResource` is public and in namespace `Humans.Scanner`.** The manifest name is
  derived from the adjacent `.cs` file's namespace; the boot localization diagnostic finds
  markers through exported types.
- **`Section.Register` is empty and the type still matters.** Implementing `ISection` is
  what makes the assembly a section for discovery, routing and the resource scan.
- **`ScannerAccess` admits the gate-terminal account, but that account never reaches
  `/Scanner/*`.** A route-restriction middleware in Shell bounces it to `/Gate`. The policy
  is shared with Tickets' onsite roster and Gate's read routes, so it stays as is.
- **`_ViewImports.cshtml` must `@addTagHelper *, Humans.Tickets`.** A section's views do not
  inherit Shell's imports; without the line the stub renders as literal markup, silently.
- **The ticket card's stub carries `HasPendingTransfer: false` unconditionally.** The card is
  a door view, not a transfer view; the stamp would be noise here.
- **Favourited events are filtered out of the provide list on purpose.** The iCal feed's
  Events half is what the member wants to attend, which is the wrong signal at the door.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-06 | First doctoring: five stale doc claims, one false page string, two unpinned invariants | peterdrier/Humans#pending |
