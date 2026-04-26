# Scanner — Section Invariants

## Concepts

- **Scanner** is a section for in-browser tools that read data from the device camera (or similar inputs in the future).
- **Phase 1** (issue nobodies-collective/Humans#525) ships a single tool: `/Scanner/Barcode`, which decodes QR codes and CODE128 barcodes using the browser's `BarcodeDetector` API, falling back to `@zxing/browser` via CDN.
- **Not a check-in tool.** Scanning a ticket here does not mark anyone as arrived. Decoded values are displayed only and not sent to the server.
- **No server-side state.** The section has no owned tables, no DTOs, no services. Decoding happens entirely in the browser; results live in the page and are discarded on navigation.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| TicketAdmin, Board, Admin | View the scanner section index and use the barcode tool |
| Everyone else | No access — all scanner routes require `TicketAdminBoardOrAdmin` |

## Invariants

- All scanner routes require the `TicketAdminBoardOrAdmin` policy (`TicketAdmin`, `Board`, or `Admin`).
- No scanner endpoint writes server-side state. No database tables are owned by this section.
- The barcode tool is explicitly labelled as a test tool on screen. It is never used to mark tickets as checked-in or to authenticate entry.
- Decoded barcode values do not leave the browser.
- The stream is released (`MediaStreamTrack.stop()` on every track) when the user taps Stop or leaves the page (`pagehide`, `beforeunload`).

## Negative Access Rules

- The barcode tool is **not** a check-in gateway. Do not wire it up to attendance records, `EventParticipation`, ticket check-in state, or anything that would mark a human as having entered the event.
- No data from a decoded barcode is sent to the server in phase 1. If a future scanner tool needs a server round-trip, it must be a new tool with its own route and feature spec, not an extension of `/Scanner/Barcode`.

## Triggers

- When the user clicks **Scan** on `/Scanner/Barcode`, the browser prompts for camera permission and starts the rear-facing preview.
- When a barcode is decoded, the tool appends a `{format, value, timestamp}` entry to the on-page list (most recent first). Duplicate hits within 1.5 s are suppressed.
- When the user clicks **Stop** (or leaves the page), the camera stream is released.

## Cross-Section Dependencies

- **Tickets**: the barcode tool is gated behind the ticket-admin policy because its initial use case is reading TicketTailor ticket stubs. No runtime coupling — Scanner does not call any Tickets service or share any state with Tickets.
- **Admin**: none beyond the shared `TicketAdminBoardOrAdmin` policy.

## Architecture — Current vs Target

**Owning service:** none (no business logic at this scale — phase 1 is presentational).
**Owned tables:** none.
**Status:** (A) Migrated — pure presentational section, no repository needed (issue nobodies-collective/Humans#525, 2026-04-26)

Controller lives at `src/Humans.Web/Controllers/ScannerController.cs`. Views under `src/Humans.Web/Views/Scanner/`. Client logic at `src/Humans.Web/wwwroot/js/scanner/barcode.js`.

If future scanner tools grow to need server-side verification (for example, calling a ticket vendor API to validate a decoded reference), that logic goes into a new `IScannerVerificationService` in `Humans.Application` and is consumed by the controller — the `ScannerController` remains a thin web-layer wrapper, and the client logic stays in `wwwroot/js/scanner/`.
