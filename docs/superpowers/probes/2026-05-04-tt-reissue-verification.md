# TT reissue mechanics — verification (#382 follow-up)

## Question

Is the load-bearing claim of Option B true — that after `POST /v1/issued_tickets/{id}/void` you can call `POST /v1/issued_tickets` (with either `event_id+ticket_type_id` or a `hold_id` from `void_to_hold=true`) using arbitrary `full_name`+`email`, and the resulting ticket is a normal, gate-scannable Ticket Tailor ticket?

## Answer

**Confirmed-with-caveats.** The TT public API documents `full_name` and `email` as plain attendee fields on `POST /v1/issued_tickets` for both inventory-based and hold-based creation; nothing in the published docs, help centre articles, or changelog carves out API-issued tickets as second-class at check-in (the doorlist/check-in app reads from the same `issued_ticket` records, scanned by their barcode/QR). The caveats already documented in the prior probe (no `order_id`, one credit per issue, seated-type rejection, sold-out fail, `send_email` preconditions) are the real edges — but none of them break the core "the new ticket is valid and scannable" assertion.

## Evidence

- The published create-issued-ticket reference at [developers.tickettailor.com/docs/api/create-issued-ticket](https://developers.tickettailor.com/docs/api/create-issued-ticket/) describes **one** endpoint with two creation modes: *"Send the `event_id` and `ticket_type_id` to create the issued ticket from an event occurrence's ticket type allocation. Alternatively, send the `hold_id` to create the issued ticket from a pre-existing hold."* — i.e. attendee fields apply identically regardless of source.
- The OpenAPI schema (extracted verbatim in the prior probe at `2026-05-04-tickettailor-write-api.md` lines 125-134) has `full_name` (REQUIRED, attendee full name) and `email` (attendee email) as plain top-level fields on the request body, with no conditional on `hold_id` being present. There is no field on the hold endpoint that carries attendee identity that would be "inherited" — holds are inventory reservations.
- The void endpoint at [developers.tickettailor.com/docs/api/void-issued-ticket-by-id](https://developers.tickettailor.com/docs/api/void-issued-ticket-by-id/) confirms `void_to_hold` *"will create a hold from the issued ticket and not put the allocation back on sale"* — i.e. the hold is an inventory artifact, not an attendee record.
- TT help centre's API connection article and "Manage orders and issued tickets" describe a single check-in/doorlist flow against `issued_ticket` records — no separate handling for `source: "api"`. From [tickettailor.com features search](https://www.tickettailor.com/features): *"All tickets are confirmed by a QR code that can be scanned at the event with a free Check-in app"*, with no API/dashboard distinction.
- The `ISSUED_TICKET.UPDATED` webhook payload (referenced in the prior probe) carries the same shape regardless of source, indicating the underlying record type is uniform.
- Pricing of the new ticket: the [intro page](https://developers.tickettailor.com/docs/intro/) and create-issued-ticket reference do not expose any price-set field — `listed_price` derives from current ticket-type pricing, as the prior probe noted.

## Caveats / preconditions found

- **`send_email: true` is gated by three TT-side conditions** (valid email, separate-event-confirmation emails enabled in box-office settings, event-series approved by TT staff). These gate **the email**, not ticket creation — the ticket itself still issues and is scannable; a silent email failure just means the recipient never receives the PDF/wallet link. We need a fallback path that re-fetches the issued ticket and emails the recipient ourselves.
- **One TT credit per issued ticket, even free ones, even on void→hold→reissue.** Documented; budgeted for at our scale.
- **Seated ticket types reject the call.** Not relevant for current Nobodies events.
- **Sold-out + no hold → race.** Documented; mitigated by `void_to_hold=true`.
- **API-issued tickets have `order_id: null`.** Sync-side fix already called out in the prior probe; not a check-in problem.
- **Irreversible void.** If the subsequent `POST /v1/issued_tickets` fails after the void succeeded, we are in a partial-state pickle until a retry succeeds; the prior probe already specifies the recovery surface.

## What we still don't know without a live probe

- **Whether the `POST /v1/issued_tickets` response actually shows our supplied `full_name`/`email` verbatim** (vs. some TT-side normalization or rejection of e.g. unicode, long names) — needs a real call against a stage box-office.
- **Whether `void_to_hold=true` then `POST /v1/issued_tickets` with that `hold_id` succeeds at a sold-out event** — the docs imply yes but inventory edge-cases at sold-out are not spelled out.
- **Whether the check-in app treats `source: "api"` tickets identically in offline mode** — the help centre describes uniform behavior online; offline-sync edge cases are not documented.
- **Whether `send_email`'s three preconditions silently no-op (HTTP 200, no email) or surface an error.** Affects how we detect "ticket issued but not delivered" and trigger our fallback.
- **Idempotency under retry.** TT does not document idempotency keys; double-issue on a retried `POST /v1/issued_tickets` would burn a second credit and create a duplicate ticket.
