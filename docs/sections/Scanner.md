<!-- freshness:triggers
  src/Humans.Web/Controllers/ScannerController.cs
  src/Humans.Web/Views/Scanner/**
  src/Humans.Web/wwwroot/js/scanner/**
  src/Humans.Web/Models/ScannerTicketCardViewModel.cs
-->
<!-- freshness:flag-on-change
  Client-only barcode decode, read-only ticket lookup via ITicketServiceRead, door context (EE/check-in/consents/provide), and the never-a-check-in-tool negative rules — review when ScannerController, the scanner views, or the scanner JS change.
-->

# Scanner — Section Invariants

## Concepts

- **Scanner** is a section for in-browser tools that read data from the device camera and look up ticket information.
- **`/Scanner/Barcode`** (issue nobodies-collective/Humans#525, 2026-04-26): client-only barcode decode tool. Decodes QR codes and CODE128 barcodes via the browser's `BarcodeDetector` API, falling back to `@zxing/browser` via CDN. No server round-trip — decoded values are displayed in-page only.
- **`/Scanner/Tickets`**: server-backed ticket lookup tool. Accepts a scanned barcode, calls `ITicketServiceRead.GetTicketOrdersAsync` (cross-section read into Tickets), filters attendees by barcode, and renders a ticket card via `<vc:ticket-stub>`. When the ticket is matched to a Human (`MatchedUserId` set), the card additionally shows door-context: Early Entry eligibility and source list, event check-in timestamp, pending consent documents (missing = all signed), and a time-sorted "provides" list (shift commitments + events this person is offering). Read-only — never marks check-in or mutates any state.
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
| Gate terminal account (`SystemUserIds.GateTerminal`) | Same scanner access, matched by well-known id — the shared laptop at gate signs in at `/Account/GateLogin` with the credential set on `/Tickets/Admin/Gate`. Holds no roles, so it sees nothing admin-gated elsewhere. See `docs/features/scanner/gate-terminal-login.md` |
| Everyone else | No access — all routes require `ScannerAccess` |

## Invariants

- All scanner routes require the `ScannerAccess` policy (`TicketAdmin`, `Board`, or `Admin` role — or the gate-terminal account by well-known id). Enforced by `[Authorize(Policy = PolicyNames.ScannerAccess)]` on `ScannerController`.
- `/Scanner/Barcode` is client-only: no data from a decoded barcode is sent to the server; all decode logic runs in the browser.
- `/Scanner/Tickets` performs cross-section reads via `ITicketServiceRead`, `IEarlyEntryService`, `IConsentServiceRead`, `IUserServiceRead`, `IICalFeedService`, `IEventServiceRead`, and `IBurnSettingsService` to render the ticket card plus door-context for matched Humans. It is strictly read-only and must never write server-side state.
- **The ticket card must never mark check-in, write `EventParticipation`, or mutate ticket state.** Scanner is not an attendance gateway.
- No database tables are owned by this section.
- The camera stream is released (`MediaStreamTrack.stop()` on every track) when the user taps Stop or the page unloads.

## Negative Access Rules

- Neither scanner tool **can** be used as a check-in gateway. Do **not** wire either to attendance records, `EventParticipation`, ticket check-in state, or anything that would mark a human as having entered an event.
- `/Scanner/Barcode` must never be extended with a server round-trip. Any future server-side capability must be a new tool with its own route and feature spec.
- `/Scanner/Tickets` is read-only. Do not add POST/PUT/DELETE/PATCH actions to `ScannerController`.

## Triggers

- `/Scanner/Barcode`: no server-side side effects. Camera start/stop and the decoded-value list are managed in `wwwroot/js/scanner/barcode.js`; they produce no audit writes, notifications, or cross-section calls.
- `/Scanner/Tickets`: reads ticket data via `ITicketServiceRead` and door-context from EarlyEntry, Consents, Users, Events, BurnSettings, and ICalFeed on each card request. No writes, no audit, no cache mutations.

## Cross-Section Dependencies

- **Tickets**: `/Scanner/Tickets` calls `ITicketServiceRead.GetTicketOrdersAsync` (read-only) and renders `<vc:ticket-stub>`. The barcode tool (`/Scanner/Barcode`) has no runtime Tickets coupling — it is gated behind the ticket-admin policy because its primary use case is reading TicketTailor ticket stubs.
- **EarlyEntry**: `IEarlyEntryService.GetForUserAsync` — earliest entry date and grant-source list for the matched Human.
- **Consent**: `IConsentServiceRead.GetPendingDocumentNamesAsync` — names of unsigned required consent documents for the matched Human.
- **Users**: `IUserServiceRead.GetUserInfoAsync` — event participations (check-in timestamp for the active event year).
- **Events**: `IEventServiceRead.GetApprovedEventsAsync` — events the matched Human is offering (filtered by `SubmitterUserId`, non-camp, expanded per occurrence for recurring events).
- **Shifts / BurnSettings / ICalFeed**: `IBurnSettingsService.GetActiveAsync` for active event year + time zone; `IICalFeedService.GetFeedItemsAsync` for the Human's shift commitments (Shifts source only).
- **Issues**: feedback/issues filed from `/Scanner/*` route to `IssueSectionRouting.Scanner`, visible to TicketAdmin and Board handlers. Scanner does not call `IIssuesService` directly.

## Architecture

**Owning services:** none — no business logic. `ScannerController` injects cross-section read interfaces directly (no Application.Services.Scanner namespace).
**Owned tables:** none.
**Status:** (A) Migrated (issue nobodies-collective/Humans#525, 2026-04-26); `/Scanner/Tickets` added in scanner-ticket-lookup feature; door context (EE, check-in, consents, provide list) added in nobodies-collective/Humans#860.

- No `Humans.Application.Services.Scanner/` namespace — correct, no business logic in this section.
- No `ScannerSectionExtensions.cs` — correct, no DI registrations beyond the injected cross-section read interfaces.
- **Decorator decision:** no caching decorator. Scanner reads through existing section interfaces (each section owns its own caching).
- **Cross-domain navs:** none.
- **Cross-section calls (ticket card):** `ITicketServiceRead`, `IEarlyEntryService`, `IConsentServiceRead`, `IUserServiceRead`, `IBurnSettingsService`, `IICalFeedService`, `IEventServiceRead` — all injected into `ScannerController`.
- The `HUM0008` controller analyzer and `HUM0009` analyzer cover direct DbContext injection.
