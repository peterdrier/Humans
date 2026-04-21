# Scanner — Barcode (Phase 1)

## Business Context

TicketTailor issues ticket stubs carrying a barcode. Staff who want to confirm what's actually encoded on a stub currently have to bounce to the TicketTailor UI — there's no in-app way to read those barcodes. This tool gives them one, using nothing but the device camera and the browser's own APIs.

**Explicitly not a check-in tool.** Humans do not enter the event by having their ticket scanned here. Nothing is written server-side. This exists to prove the camera + decode plumbing end-to-end, so future work (API verification against TicketTailor, ticket lookup, future scanner tools) can layer on top without re-doing the front-end.

Phase 1 stands up the section (`Scanner`) as its own top-level nav area because we expect future scanner tools to live there — ticket lookup, asset tags, etc. — and the role is cross-cutting, not Tickets-specific.

## User Stories

### US-S1.1: Decode a ticket barcode on my phone
**As a** TicketAdmin (or Board/Admin)
**I want to** point my phone camera at a TicketTailor ticket stub
**So that** I can see what value the barcode encodes without opening the TicketTailor dashboard

**Acceptance Criteria:**
- Open `/Scanner/Barcode`, tap Scan, grant camera permission — the rear camera starts a live preview.
- A TicketTailor QR (or CODE128, whichever the vendor uses) is decoded within a reasonable time.
- The decoded value, format, and timestamp appear in a list below the preview, most recent on top.
- Values that look like URLs render as clickable links; other values render verbatim.
- Tapping Stop releases the camera stream (hard requirement — no stale indicator light).

### US-S1.2: Works on a laptop too
**As a** TicketAdmin
**I want to** use the same tool from a laptop webcam
**So that** I'm not blocked if my phone isn't handy

**Acceptance Criteria:**
- The same flow works on desktop Chrome/Edge/Safari with a webcam.
- If the browser lacks `BarcodeDetector` (iOS Safari, older Chromium), the tool transparently falls back to `@zxing/browser` via CDN.
- The fallback is logged in the browser console so troubleshooting has a signal.

## Decode Path

1. **Feature detect `window.BarcodeDetector`.** If available, use it directly — configured for QR, CODE128, CODE39, EAN-13/8, UPC-A/E, PDF417, Data Matrix.
2. **Fallback:** dynamic-import `@zxing/browser@0.1.5` from `cdn.jsdelivr.net` (already allow-listed in CSP). Use `BrowserMultiFormatReader.decodeFromStream`.
3. Either path appends `{format, value, timestamp}` to the on-page list. Duplicate hits within 1.5 s are deduped so scanning holds don't spam the list.

## Scope & Non-Goals

### In Scope (Phase 1)

- New `ScannerController` with `/Scanner` index and `/Scanner/Barcode` tool, gated to `TicketAdminBoardOrAdmin`.
- Nav entry in the main nav.
- Camera start/stop with feature-detect + ZXing fallback.
- Decoded list rendered in the page, client-side only.
- Localized copy across all six supported locales.
- Section invariant doc (`docs/sections/Scanner.md`) and this feature spec.

### Out of Scope (explicitly, for phase 1)

- Check-in / attendance marking. Humans do not enter the event by having their ticket scanned here.
- TicketTailor API calls to validate a decoded barcode. That's a follow-on.
- Server-side storage of scan history. The list in the page is client-side only; refresh clears it.
- Batch scanning / roster lookup / matching a barcode to a `TicketAttendee` row.
- Any future check-in flow — if that ever ships, it's a different tool from this one.

## Implementation Notes

- Camera access requires a secure context. Already satisfied in all Humans environments (QA, preview, production).
- iOS Safari will only open the camera on a user gesture — the Scan button exists for this reason. The page copy doesn't assume auto-play.
- Narrow mobile viewports: preview and controls are touch-friendly and do not depend on hover.
- The `BarcodeDetector` formats list is broad, but the primary target is QR + CODE128 because those are what TicketTailor uses. Adding more formats doesn't cost anything at the API level.
- CSP already allows `cdn.jsdelivr.net` in `script-src` and `connect-src`, so the ZXing fallback loads without CSP changes.

## Follow-ups (Separate Issues)

- TicketTailor API verification — given a decoded value, ask TicketTailor whether the ticket is valid, refunded, already checked in, etc.
- Match decoded value to the local `TicketAttendee` row and show the human's details inline.
- Offline mode / scan queue for poor-connectivity environments.
- If a check-in flow is ever needed, it's a different tool from this one.
