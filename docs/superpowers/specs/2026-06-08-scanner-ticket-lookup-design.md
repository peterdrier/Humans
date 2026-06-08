# Scanner ticket lookup by barcode

**Status:** Draft for review
**Date:** 2026-06-08
**Branch:** `feat/scanner-ticket-lookup`
**Sections touched:** Scanner (server-backed for the first time), Tickets (read-model enrichment + sync), shared TicketStub component.

## Context

Ticket Tailor issues each ticket two identities: the object id `ti_…` (what we store today as `TicketAttendee.VendorTicketId`) and a short scannable **`barcode`** (e.g. `xyz34Qy5`) printed on the ticket and encoded in its QR. We do not currently pull the barcode. Confirmed with Peter: the QR carries the **barcode**, not `ti_…`, so a scanned value matches the barcode field.

The TT API has **no** "look up a ticket by barcode" endpoint — retrieve is by `ti_…` id; list filters by event/time/cursor only. We already sync every issued ticket into `ticket_attendees` and cache the per-order projection (`TicketOrderInfo`, warmed on startup) behind `CachingTicketQueryService`. So the lookup runs entirely against our own cached data — no live TT call at the gate.

## Goals

1. **Gate scanner card** — a new `/Scanner/Tickets` tool: scan a ticket, get an info card. Works for `Valid`, `CheckedIn` (scanned), and `Void` tickets. For void tickets, when the void resulted from a tracked transfer, show who it was transferred to and when.
2. **Admin barcode search** — ticket admins can search `/Tickets/Attendees` by barcode.
3. **Stub cleanup** — remove the confusing `ti_…` serial from the member-facing ticket stub.

## Non-goals

- **Not a check-in tool.** The card is read-only; scanning does not mark attendance or mutate any ticket/`EventParticipation` state. (The void-transfer linkage is display-only.)
- No live Ticket Tailor call from the gate; lookup is against synced data.
- No data-migration backfill of barcodes (see Rollout).
- No new cross-section interface method, no new "purpose-built" DTO (enrich + search the existing cached read model instead).

## Design decision — search the cached read model (Option B)

Rejected: adding `GetTicketByBarcodeAsync` to `ITicketServiceRead`. That interface is `[SurfaceBudget(2)]` and a 3rd method trips HUM0015/0016; more importantly a keyed point-lookup method is the wrong shape here. Per `read-model-enrichment` / `reuse-first-change-discipline`, we **enrich the canonical `TicketAttendeeInfo` projection and filter it in memory** at the call site. At ~600 tickets the projection is already fully cached and warmed; a cache search is cheaper and adds zero interface surface.

## Phase 1 — Pull the barcode in (Domain / Infrastructure)

- **`TicketAttendee.Barcode`** — new `string?` property. EF config: column + **non-unique** index (a reissued ticket can legitimately share nothing; index supports the admin search and is cheap). Schema-only EF migration, generated (never hand-edited).
- **`VendorTicketDto`** — add `Barcode` (string?).
- **`TicketTailorService`** — add `[JsonPropertyName("barcode")] string? Barcode` to `TtIssuedTicket`; map `ticket.Barcode` into `VendorTicketDto` in `GetIssuedTicketsAsync`.
- **`StubTicketVendorService`** — emit a deterministic fake barcode per issued ticket so dev/QA/preview exercise the path.
- **`TicketSyncService`** upsert — set `Barcode` from the DTO (preserved across re-syncs like other vendor fields).

## Phase 2 — Enrich the read model (Application)

`TicketAttendeeInfo` (carried inside `TicketOrderInfo`) gains:

- `string? Barcode` — from the attendee row.
- `string? TransferredToName`, `Instant? TransferredAt` — null **except** for a `Void` attendee that is the `OriginalTicketAttendeeId` of an **Approved** `TicketTransferRequest`; then the request's `ReceiverLegalName` / `DecidedAt`.

The Tickets repository builds `TicketOrderInfo`; both `ticket_attendees` and `ticket_transfer_requests` are Tickets-owned, so the projection builder loads Approved transfers once and maps recipient/decided-at onto void attendees in memory. `TicketOrderInfo` is already cached/warmed and invalidated by sync — no new cache wiring.

> **Tradeoff (flag for review):** this adds a transfer lookup to the order-projection build that the homepage/dashboard also use. At this scale the cost is negligible (one in-memory map over Approved transfers). If you'd rather keep the projection lean, we cut `TransferredToName/At` and the void card just shows "Void" — the existing `/Tickets/Admin/Transfers` detail still explains the why.

## Phase 3 — Admin barcode search (Application)

Fold `Barcode` into the existing `search` term handling in `GetAttendeesPageAsync` (and the attendees query in the repo) — case-insensitive contains, alongside the fields it already matches. Update the search-box placeholder/help on `/Tickets/Attendees`. **No interface change, no new method.**

## Phase 4 — The Scanner tool (Web)

- **`ScannerController`** (stays `[Authorize(Policy = TicketAdminBoardOrAdmin)]`):
  - `GET /Scanner/Tickets` — camera page.
  - `GET /Scanner/Tickets/Card?barcode=…` — injects `ITicketServiceRead`, calls `GetTicketOrdersAsync()`, flattens `Attendees`, finds the first with `Barcode` matching the scanned value (ordinal). Maps to the card model; returns a rendered partial. No match → "No ticket found for this code."
- **Card rendering** reuses `<vc:ticket-stub>` for the visual (map `TicketAttendeeInfo` → `TicketStubInfo`; `HasPendingTransfer = false`, `EarlyEntryDate = null` — the gate isn't where those are computed). Beneath the stub, an admin detail block: ticket type, check-in status, and for void-with-transfer: "Transferred to {TransferredToName} on {TransferredAt}".
- **Views:** `Views/Scanner/Tickets.cshtml` (camera + result container) and `Views/Scanner/_TicketCard.cshtml` (the partial). Reuse the decode logic in `wwwroot/js/scanner/barcode.js` — extract the shared decode into a reusable function consumed by a small `scanner/tickets.js` that, on decode, fetches `/Scanner/Tickets/Card` and injects the partial (scan-the-next-one flow).
- Add a `/Scanner/Tickets` link to the Scanner index.

## Phase 5 — Stub cleanup (#2)

- Remove the serial block (`ticket-stub-side` `VendorTicketId` render, `TicketStub/Default.cshtml` ~lines 62–68). The 🎟️ side panel can stay or go — implementer's call; the `ti_…` text is what must disappear, everywhere the stub renders (homepage, `/Profile/Me`, transfer wizard).
- Drop `VendorTicketId` from `TicketStubInfo` + `TicketStubInfo.From(...)` **iff** no remaining consumer reads it after the serial render is gone (the side serial appears to be its only display). If any surface still needs it, leave the field and just stop rendering it.

## Phase 6 — Docs & tests

- **`docs/sections/Scanner.md`** — biggest doc change: status moves from "client-only, no server state" to **server-backed read tool**. Add the two routes; add a cross-section read dependency on Tickets (`ITicketServiceRead.GetTicketOrdersAsync`); revise the "nothing sent to the server / no services / no tables" invariants and the negative-access rule (`/Scanner/Barcode` stays client-only; `/Scanner/Tickets` is the sanctioned server-backed tool). Still **not** a check-in tool — keep that invariant.
- **Delete** `EndpointAuthorizationTests.ScannerController_Remains_ClientOnly_GetSurface` (Peter: overbearing).
- **`docs/sections/Tickets.md`** — note `TicketAttendee.Barcode`, the `TicketAttendeeInfo` enrichment (barcode + void-transfer detail), and the barcode admin search.
- **Tests:**
  - Sync maps `issued_ticket.barcode` → `TicketAttendee.Barcode` (stub-driven).
  - Read-model: void attendee with an Approved transfer carries `TransferredToName/At`; others null.
  - Scanner card action: found `Valid` / `CheckedIn` / `Void`+transfer / not-found.
  - `GetAttendeesPageAsync` matches on barcode.
  - Stub view no longer renders any `ti_…` text.

## Edge cases

- **Barcode null** on rows not yet re-synced → those won't be found by scan until re-synced (see Rollout). Admin search simply won't match them.
- **Unknown/garbage scan / non-TT QR / other event** → "No ticket found."
- **Reissued (transfer) ticket** — the receiver's new ticket has its own barcode → scans to the new `Valid` attendee. The sender's old physical ticket scans to the old `Void` attendee and shows the transfer detail.
- **Multiple matches** for one barcode (shouldn't happen) → take the first; barcodes are unique per issued ticket in TT.

## Rollout / migration

- One generated schema-only EF migration (add column + index).
- Barcode is **lazy-filled by sync**, not backfilled by a data migration (per `never-author-data-migrations`). To populate existing rows in one shot, an Admin runs **Full Re-sync** (clears the cursor, re-pulls all tickets) — an operational step, called out in the PR description.

## Open questions

None blocking. The one reviewer flag is the Phase-2 tradeoff (transfer detail on the shared projection vs. cut it).
