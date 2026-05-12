# Scanner — Section Invariants

## Concepts

- **Scanner** is a section for in-browser tools that read data from the device camera.
- The only current tool is `/Scanner/Barcode` (issue nobodies-collective/Humans#525, 2026-04-26): decodes QR codes and CODE128 barcodes via the browser's `BarcodeDetector` API, falling back to `@zxing/browser` via CDN.
- **Not a check-in tool.** Decoded values are displayed in-page only; nothing is sent to the server.
- **No server-side state.** No owned tables, no DTOs, no services. All decode logic runs in the browser.

## Routing

| Route | Controller action | Notes |
|-------|------------------|-------|
| `GET /Scanner` | `ScannerController.Index` | Section landing page |
| `GET /Scanner/Barcode` | `ScannerController.Barcode` | Barcode decode tool |

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| TicketAdmin, Board, Admin | Access the scanner index and use the barcode tool |
| Everyone else | No access — all routes require `TicketAdminBoardOrAdmin` |

## Invariants

- All scanner routes require the `TicketAdminBoardOrAdmin` policy (`TicketAdmin`, `Board`, or `Admin`). Enforced by `[Authorize(Policy = PolicyNames.TicketAdminBoardOrAdmin)]` on `ScannerController`.
- No scanner endpoint writes server-side state. No database tables are owned by this section.
- Decoded barcode values do not leave the browser.
- The camera stream is released (`MediaStreamTrack.stop()` on every track) when the user taps Stop or the page unloads.

## Negative Access Rules

- The barcode tool **cannot** be used as a check-in gateway. Do **not** wire it up to attendance records, `EventParticipation`, ticket check-in state, or anything that would mark a human as having entered an event.
- No data from a decoded barcode is **sent** to the server. Any future scanner tool that requires a server round-trip must be a new tool with its own route and feature spec — not an extension of `/Scanner/Barcode`.

## Triggers

None — this section is a pure client-side tool with no server-side side effects.

(Client-side only: camera start/stop and the decoded-value list are managed in `wwwroot/js/scanner/barcode.js`; they produce no audit writes, notifications, or cross-section calls.)

## Cross-Section Dependencies

- **Tickets**: the barcode tool is gated behind the ticket-admin policy because its primary use case is reading TicketTailor ticket stubs. No runtime coupling — Scanner does not call any Tickets service or share state with Tickets.
- **Issues**: feedback/issues filed from `/Scanner/*` route to `IssueSectionRouting.Scanner`, visible to TicketAdmin and Board handlers. Scanner does not call `IIssuesService` directly.

## Architecture

**Owning services:** none (phase 1 is presentational — no business logic).
**Owned tables:** none.
**Status:** (A) Migrated — pure presentational section, no repository needed (issue nobodies-collective/Humans#525, 2026-04-26).

- No `Humans.Application.Services.Scanner/` namespace exists — correct for a section with no business logic.
- No `ScannerSectionExtensions.cs` exists — correct while the section has no DI registrations.
- **Decorator decision:** no caching decorator. No server-side data to cache.
- **Cross-domain navs:** none.
- **Cross-section calls:** none.
- **Architecture test:** `EndpointAuthorizationTests.ScannerController_Remains_ClientOnly_GetSurface` pins the no-server-state surface; the `HUM0008` controller analyzer and `HUM0009` analyzer cover direct DbContext injection.
