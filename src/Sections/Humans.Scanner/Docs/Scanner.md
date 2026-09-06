<!-- freshness:triggers
  src/Sections/Humans.Scanner/**
  src/Humans.Base/Authorization/PolicyNames.cs
  src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs
  src/Humans.Web/Program.cs
  src/Sections/Humans.Issues/Domain/IssueSectionRouting.cs
  tests/Humans.Web.Tests/Authorization/EndpointAuthorizationTests.cs
-->
<!-- freshness:flag-on-change
  Client-only barcode decode, read-only ticket lookup via ITicketServiceRead, door context (EE/check-in/consents/provide), and the never-a-check-in-tool negative rules — review when ScannerController, the scanner views, or the scanner JS change.
-->

# Scanner — Section Invariants

## Concepts

- **Scanner** is a section for in-browser tools that read the device camera and look up ticket information.
- **`/Scanner/Barcode`**: client-only barcode decode tool. Decodes QR codes and CODE128 barcodes via the browser's `BarcodeDetector` API, falling back to `@zxing/browser` via CDN. No server round-trip — decoded values are displayed in-page only.
- **`/Scanner/Tickets`**: server-backed ticket lookup. Accepts a barcode via camera scan **or manual text entry**, calls `ITicketServiceRead.GetTicketOrdersAsync`, matches an attendee of a current-event order by barcode, and renders a ticket card via `<vc:ticket-stub>`. When the ticket is matched to a Human (`MatchedUserId` set), the card also shows door context: Early Entry date and sources, this event's check-in timestamp, pending consent documents (empty = all signed), and a time-sorted "provides" list (shift commitments + events this person is offering). Read-only — never marks check-in or mutates any state.
- **Not a check-in tool.** The ticket card displays attendee information only; nothing writes to `EventParticipation`, ticket state, or any other server-side record.
- **No owned tables.** Scanner owns no database tables, DTOs, or repositories.

## Routing

| Route | Controller action | Notes |
|-------|------------------|-------|
| `GET /Scanner` | `ScannerController.Index` | Section landing page |
| `GET /Scanner/Barcode` | `ScannerController.Barcode` | Client-only barcode decode tool |
| `GET /Scanner/Tickets` | `ScannerController.Tickets` | Server-backed ticket lookup by barcode |
| `GET /Scanner/Tickets/Card` | `ScannerController.Card` | Rendered ticket card partial |

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| TicketAdmin, Board, Admin | Access the scanner index and use the barcode + ticket lookup tools |
| Gate terminal account (`SystemUserIds.GateTerminal`) | Admitted by `ScannerAccess` by well-known id, but never reaches these routes: the kiosk route-restriction middleware in `src/Humans.Web/Program.cs` redirects the signed-in gate account to `/Gate` for any path outside `/Gate/*` and its own login/logout. Signs in at `/Account/GateLogin` with the credential set on `/Tickets/Admin/Gate`; holds no roles. See `Docs/features/gate-terminal-login.md` and `src/Sections/Humans.Gate/Docs/Gate.md` |
| Everyone else | No access — all routes require `ScannerAccess` |

## Invariants

- All scanner routes require the `ScannerAccess` policy (`TicketAdmin`, `Board`, or `Admin` role — or the gate-terminal account by well-known id). Enforced by `[Authorize(Policy = PolicyNames.ScannerAccess)]` on `ScannerController`; pinned by `EndpointAuthorizationTests` in `tests/Humans.Web.Tests`.
- `/Scanner/Barcode` is client-only: no data from a decoded barcode is sent to the server; all decode logic runs in the browser.
- `/Scanner/Tickets/Card` searches only orders flagged `IsCurrentEvent`; a blank or unknown barcode renders the not-found card (blank: without echoing a code).
- `/Scanner/Tickets` performs cross-section reads via `ITicketServiceRead`, `IEarlyEntryService`, `IConsentServiceRead`, `IUserServiceRead`, `IICalFeedService`, `IEventServiceRead`, and `IBurnSettingsService` to render the ticket card plus door context for matched Humans. It is strictly read-only and must never write server-side state.
- Door context is read only when the ticket has a matched Human; otherwise every per-person field is null. The check-in timestamp is read for the active event year only.
- **The ticket card must never mark check-in, write `EventParticipation`, or mutate ticket state.** Scanner is not an attendance gateway.
- No database tables are owned by this section.
- The camera stream is released (`MediaStreamTrack.stop()` on every track) on Stop, on `pagehide` and on `beforeunload`.

## Negative Access Rules

- Neither scanner tool **can** be used as a check-in gateway. Do **not** wire either to attendance records, `EventParticipation`, ticket check-in state, or anything that would mark a human as having entered an event.
- `/Scanner/Barcode` must never be extended with a server round-trip. Any future server-side capability must be a new tool with its own route and feature spec.
- `/Scanner/Tickets` is read-only. Do not add POST/PUT/DELETE/PATCH actions to `ScannerController`.

## Triggers

- `/Scanner/Barcode`: no server-side side effects. Camera start/stop and the decoded-value list are managed in `wwwroot/js/scanner/barcode.js`; they produce no audit writes, notifications, or cross-section calls.
- `/Scanner/Tickets`: reads ticket data via `ITicketServiceRead` and door context from EarlyEntry, Consent, Users, Events, Shifts (burn settings) and Calendar (the iCal feed) on each card request. No writes, no audit, no cache mutations.

## Cross-Section Dependencies

Project references (`Humans.Scanner.csproj`): `Humans.Base`, `Humans.Events.Contracts`, `Humans.Consent.Contracts`, `Humans.Shifts.Contracts`, `Humans.Users.Contracts`, `Humans.Tickets.Contracts`, `Humans.Tickets` (section project — the `<vc:ticket-stub>` tag helper is generated from the component type, which lives there), `Humans.EarlyEntry`, and `Humans.Calendar` (section project — its `Contracts/` is a folder, not a leaf).

- **Tickets**: `ITicketServiceRead.GetTicketOrdersAsync` (read-only) and `<vc:ticket-stub>`. The barcode tool has no runtime Tickets coupling — it is gated behind `ScannerAccess` because its use case is reading TicketTailor ticket stubs.
- **EarlyEntry**: `IEarlyEntryService.GetForUserAsync` — earliest entry date and grant-source list for the matched Human.
- **Consent**: `IConsentServiceRead.GetPendingDocumentNamesAsync` — names of unsigned required consent documents for the matched Human.
- **Users**: `IUserServiceRead.GetUserInfoAsync` — event participations (check-in timestamp for the active event year).
- **Events**: `IEventServiceRead.GetApprovedEventsAsync` — events the matched Human is offering (`SubmitterUserId` match, non-camp, expanded per occurrence for recurring events).
- **Shifts / Calendar**: `IBurnSettingsService.GetActiveAsync` for the active event year and time zone; `IICalFeedService.GetFeedItemsAsync` for the Human's shift commitments (`Shifts`-sourced items only).
- **Issues**: feedback filed from `/Scanner/*` routes to `IssueSectionRouting.Scanner`, whose queue TicketAdmin and Board handlers see. Scanner does not call `IIssuesService`.

## Architecture

**Owning services:** none — no business logic. `ScannerController` injects the read interfaces above directly.
**Owned tables:** none.

- No `Data/`, no migrations, no `Humans.Infrastructure` reference: nothing to persist.
- `Section.Register` is empty and `Contracts/` holds only a README: Scanner registers nothing and nothing outside it names a Scanner type.
- **Decorator decision:** no caching decorator. Each read interface is cached by its owning section.
- **Admin nav:** `SectionAdminNav` contributes the "Scanner" entry to the shared "Tickets" admin group.
- The `HUM0008` controller analyzer and `HUM0009` analyzer cover direct DbContext injection.
