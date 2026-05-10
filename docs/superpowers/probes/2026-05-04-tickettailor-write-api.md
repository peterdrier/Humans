# TicketTailor Write API — research for issue #382

Research-first probe for [issue #382](https://github.com/nobodies-collective/Humans/issues/382)
("ticket transfer" — let a buyer hand a TT ticket to another Humans user). Goal:
decide whether the transfer can rewrite the original issued ticket (Option A),
must void+reissue (Option B), or has to fall back to a Humans-only record with
no TT writeback (Option C).

> **No live calls were made.** Findings are from the published TT REST docs at
> `https://developers.tickettailor.com/`, plus the embedded OpenAPI schemas
> shipped in the docs site's webpack chunks (which contain the per-endpoint
> request/response schemas the rendered pages display client-side). Schema
> snippets quoted below were extracted from those bundles directly so the field
> names are verbatim from the API spec, not paraphrased.

---

## Current integration (what we already use)

From `src/Humans.Infrastructure/Services/TicketTailorService.cs`:

- **Base URL:** `https://api.tickettailor.com/v1` (hard-coded constant).
- **API version:** `v1`.
- **Auth:** HTTP Basic, with the API key as the username and an empty password,
  Base64-encoded into the `Authorization` header (matches the
  [TT auth docs](https://developers.tickettailor.com/docs/api/ticket-tailor-api/)).
  Key comes from `TICKET_VENDOR_API_KEY` env var, plumbed via
  `TicketVendorSettings.ApiKey`.
- **Endpoints currently called** (all read or discount-code writes — no
  attendee/order mutations today):
  - `GET /v1/orders?event_id=…&updated_at.gte=…&starting_after=…` —
    [List orders](https://developers.tickettailor.com/docs/api/get-all-orders/)
  - `GET /v1/issued_tickets?event_id=…&updated_at.gte=…&starting_after=…` —
    [List issued tickets](https://developers.tickettailor.com/docs/api/get-all-issued-tickets/)
  - `GET /v1/events/{eventId}` —
    [Get event](https://developers.tickettailor.com/docs/api/get-event-by-id/)
  - `POST /v1/voucher_codes` —
    [Create voucher / discount code](https://developers.tickettailor.com/docs/api/create-discount-code/)
  - `GET /v1/voucher_codes?code=…` — voucher lookup
- **Pagination:** cursor-based (`starting_after=<last_id>`, `links.next` to
  decide whether to keep going).
- **Sync model:** delta poll on `updated_at.gte` from the last successful
  sync (`TicketSyncService.SyncOrdersAndAttendeesAsync`). Webhooks are not
  consumed today.
- **Vendor abstraction:** `ITicketVendorService` — already vendor-agnostic, so
  any new "transfer" capability slots in there cleanly.

---

## API capabilities

### Option A: PATCH attendee on issued ticket

**Not supported.** The TT API does not expose any update/PATCH/PUT operation
on individual issued tickets.

The complete sidebar under **Issued tickets** in the API reference is
([sidebar HTML inspected directly](https://developers.tickettailor.com/docs/api/create-issued-ticket/)):

| Operation | Method | Path |
|---|---|---|
| List issued tickets | GET | `/v1/issued_tickets` |
| Create an issued ticket | POST | `/v1/issued_tickets` |
| Get a single issued ticket | GET | `/v1/issued_tickets/:id` |
| Void an issued ticket | POST | `/v1/issued_tickets/:id/void` |
| New issued ticket (webhook) | event | — |
| Updated issued ticket (webhook) | event | — |

There is **no** `PATCH /v1/issued_tickets/:id`, no `POST /v1/issued_tickets/:id`,
no "update" operation. The full sitemap (`developers.tickettailor.com/sitemap.xml`)
confirms this — `update-issued-membership-by-id` exists for memberships but the
tickets parallel does not.

The `update-order` endpoint at `POST /v1/orders/:order_id`
([docs](https://developers.tickettailor.com/docs/api/update-order-by-id/))
exists but only mutates **buyer-level** fields, not the per-ticket attendee.
Verbatim request body schema (extracted from the docs site's OpenAPI bundle):

```
properties:
  address_1, address_2, address_3 (string, nullable)
  email                            (string, nullable)  — Buyer email address
  first_name                       (string, nullable)  — Buyer first name
  last_name                        (string, nullable)  — Buyer last name
  phone                            (string, nullable)
  postal_code                      (string, nullable)
```

`issued_tickets` is not in the request body — it appears only in the response.
Updating an order changes `buyer_details` (the receipt-holder), it does **not**
re-attribute the individual `issued_ticket` records hung off the order.

The TT dashboard *does* allow editing per-ticket attendee names ("change the
name, email or other customer details from Orders > Edit order", per
[help.tickettailor.com edit-an-order](https://help.tickettailor.com/en/articles/1549513-how-can-i-edit-an-order)),
and the `ISSUED_TICKET.UPDATED` webhook does fire for those edits
([docs](https://developers.tickettailor.com/docs/api/updated-issued-ticket-webhook/),
payload includes the full mutated attendee record). So the underlying *capability*
exists in TT — it just isn't exposed through the public API.

> **TT does not document an attendee-update API**; would need a probe with creds
> to confirm there's no undocumented endpoint, but absence from the OpenAPI spec
> + sidebar + sitemap is strong evidence it isn't there.

### Option B: Void + reissue

**Partially supported, with a sharp edge: the new ticket cannot be linked to
the original order.**

**Void:** `POST /v1/issued_tickets/:issued_ticket_id/void`
([docs](https://developers.tickettailor.com/docs/api/void-issued-ticket-by-id/)).
Verbatim from the OpenAPI schema:

- Body (optional): `void_to_hold: "true"|"false"`.
- Response 200: `{ id, hold_id (nullable), object: "issued_ticket", voided: "true" }`.
- **Irreversible.** Marks ticket invalid; does **not** cancel the parent order
  and does **not** issue a refund. The original `issued_ticket.id` stays in TT
  with `status: "voided"`.

**Issue replacement:** `POST /v1/issued_tickets`
([docs](https://developers.tickettailor.com/docs/api/create-issued-ticket/)).
Verbatim from the OpenAPI schema, request body (form-urlencoded, `full_name`
required):

```
event_id        (string)   — required if creating from ticket-type inventory
ticket_type_id  (string)   — required if creating from ticket-type inventory
hold_id         (string)   — required if creating from a hold
full_name       (string)   — REQUIRED, attendee full name
email           (string)   — attendee email
send_email      (boolean)  — try to email the ticket to the attendee
barcode         (string)   — optional override; auto-generated if omitted
reference       (string)   — external ref (e.g. our Humans transfer record id)
```

Critical caveats for transfer:

1. **No `order_id` field.** The docs explicitly say *"The issued ticket is not
   associated to an order."* The reissued ticket appears in
   `GET /v1/issued_tickets` with `order_id: null` and `source: "api"`. Our
   sync code at `TicketSyncService.cs:144` currently **drops** any attendee
   whose `VendorOrderId` doesn't match a known order
   (`"references unknown order … skipping"`). That branch would silently
   discard every API-issued ticket. Fix needed in the sync if we go this route.
2. **One credit per issued ticket — even free ones.** The docs:
   *"Issuing tickets via the API is charged at one credit per issued ticket,
   even if the ticket is free."* For a transfer this is one extra credit per
   transfer (the void itself is free).
3. **Inventory must be available.** The endpoint fails *"if no tickets are
   available to fulfil the request."* If the event is sold out, voiding the
   original puts the seat back in inventory only for a moment — race conditions
   possible at sold-out events. Workaround: void with `void_to_hold=true` and
   reissue from that hold (TT supports hold-targeted issuance).
4. **Seated tickets blocked.** The endpoint fails for ticket types that use a
   seating chart. Not relevant for our current flat-seating events but worth
   noting.
5. **New barcode, new ticket id.** The original ticket holder's email / PDF /
   wallet pass becomes invalid; the new holder needs the new barcode delivered.
   `send_email: true` will email the recipient (subject to TT's three
   preconditions — valid email, separate-event-confirmation emails enabled in
   the box office, and event-series approved by TT staff).
6. **No pricing/refund.** `listed_price` on the new ticket reflects the
   ticket-type's *current* listed price at the time of issue, not what the
   original buyer paid. There is no API surface for setting a price or
   recording a refund on the create-issued-ticket call. Pricing is a
   non-issue for our transfer use case (we don't refund or rebill —
   transfer is gratis between Humans users) but means the new ticket's
   `listed_price` field will drift from the original.

### Option C: Humans-only (no writeback)

Always feasible. We track the "logical" current holder in our DB
(`TicketAttendee.MatchedUserId` already does most of this), let an admin
manually edit the order in the TT dashboard if/when the physical ticket
needs to be re-presented at the gate, and accept the drift between
`ticket_attendees.attendee_email` (whatever TT last synced) and
`ticket_attendees.matched_user_id` (the Humans-side current owner).

**Drift risk:** without admin updating TT, the attendee-name match at the
door fails for transferred tickets. Check-in staff would need to
cross-reference our system to validate transfers. The next sync's `updated_at`
filter wouldn't pick up our local override since TT didn't change.

---

## Recommendation

**Option B — void + reissue, gated by a settings flag, with C as the
graceful-degradation default.**

Option A is off the table: the TT public API simply does not expose an
attendee-update mutation, and we will not screen-scrape the dashboard. Option B
works against documented endpoints and produces a TT-native, scannable ticket
for the recipient — which is the entire point of writing back at all (a
transferred ticket the gate can't validate isn't really transferred). The
caveats are real but bounded:

- The "issued ticket has no order_id" gotcha forces a small but contained
  change in `TicketSyncService` (treat null-order-id tickets as standalone
  rows, or thread our Humans transfer record id through `reference` so we
  can re-link on the next sync). This is the largest unknown — our current
  sync schema assumes every attendee has a parent order.
- One TT credit per transfer is acceptable for the volumes involved (~500
  members, transfers will be rare).
- Sold-out races are mitigated by `void_to_hold=true` then
  `POST /v1/issued_tickets` with `hold_id`.

Option C is the right fallback when TT is misconfigured (no event series approval
→ `send_email` fails silently), when a transfer happens after sold-out and we
chose not to use holds, or when the event has already started and reissue stops
being meaningful. Code-shape-wise, Option B and Option C share the same Humans-side
record (the `TicketTransfer` audit row); Option C just skips the vendor calls.
That makes "downgrade B to C on vendor failure" a one-line fallback, not a
separate feature.

## Integration shape (chosen option)

### `ITicketVendorService` additions

```csharp
public interface ITicketVendorService
{
    // existing methods …

    /// <summary>
    /// Voids an issued ticket. Optionally creates a hold from the void
    /// so the seat can be reissued without racing against open inventory.
    /// </summary>
    /// <returns>Hold id when <paramref name="voidToHold"/> is true; null otherwise.</returns>
    Task<VoidIssuedTicketResult> VoidIssuedTicketAsync(
        string vendorTicketId,
        bool voidToHold,
        CancellationToken ct = default);

    /// <summary>
    /// Issues a new ticket, either against event/ticket-type inventory or
    /// against a hold created by a prior void. Sets attendee identity on
    /// creation. Note: TT does not associate API-issued tickets with an order.
    /// </summary>
    Task<VendorTicketDto> IssueTicketAsync(
        IssueTicketRequest request,
        CancellationToken ct = default);
}

public sealed record VoidIssuedTicketResult(string VendorTicketId, string? HoldId);

public sealed record IssueTicketRequest(
    string? EventId,        // required unless HoldId set
    string? TicketTypeId,   // required unless HoldId set
    string? HoldId,         // required unless EventId+TicketTypeId set
    string FullName,        // TT-required
    string? Email,
    bool SendEmail,
    string? ExternalReference); // we pass the Humans TicketTransfer.Id here
```

### HTTP shape

| Operation | Method | URL | Body | Success |
|---|---|---|---|---|
| Void | `POST` | `/v1/issued_tickets/{id}/void` | form-urlencoded `void_to_hold=true|false` | 200 `{ id, hold_id, voided: "true" }` |
| Issue | `POST` | `/v1/issued_tickets` | form-urlencoded: `event_id`+`ticket_type_id` OR `hold_id`; plus `full_name` (req), `email`, `send_email`, `reference`, optional `barcode` | 201 with full `IssuedTicket` payload (status `valid`, `source: "api"`, `order_id: null`) |

Both endpoints use `application/x-www-form-urlencoded` per the OpenAPI spec —
not JSON. Existing `TicketTailorService` uses `PostAsJsonAsync` for vouchers,
which works there because vouchers also accept form bodies; for parity with the
spec's content-type, prefer `FormUrlEncodedContent` for these two new calls.

### Failure modes

- **400 `VALIDATION_ERROR`** — bad ticket-type id, missing required field
  (`full_name`), seated ticket type, sold out. Surface to the operator;
  do **not** retry. Response body shape: `{ status, error_code, message,
  errors: [{ field, value, messages, expected }] }`.
- **401 / 403** — credential rotation problem; alert, do not retry.
- **404** — ticket already voided or unknown id. For `void` followed by
  `issue` we run them sequentially; if the void already succeeded but the
  issue fails downstream we are in a recoverable-but-messy state — record the
  void result before calling issue, and expose a "complete pending transfer"
  admin action that retries just the issue half.
- **409 / 422 inventory** — sold out at the moment of `IssueTicketAsync`. If
  we used `void_to_hold=true` and pass the resulting `hold_id`, this should be
  effectively impossible (the hold reserves the seat). For the no-hold path,
  this is "fall back to Option C" territory.
- **429** — TT documents `5000 req / 30 min` global, with stricter caps on
  some endpoints (memberships are 30/hr; ticket issuance is not stated as
  capped separately). Honor `X-Rate-Limit-*` and `Retry-After` headers; for a
  user-initiated transfer just surface a "try again in a minute" error rather
  than blocking the request thread.
- **5xx** — current sync code already treats 5xx as transient and retries on
  the next loop. For synchronous transfer we should fail-fast and ask the
  user to retry; recording a partial state (void OK, issue 5xx) again
  requires the "complete pending transfer" recovery path above.

### Sync resilience note

`TicketSyncService.SyncOrdersAndAttendeesAsync` currently:

1. Fetches orders + tickets using `updated_at.gte` deltas.
2. Drops any attendee whose `dto.VendorOrderId` doesn't appear in
   `orderIdByVendorId`. (See `TicketSyncService.cs:143-149`,
   `"references unknown order … skipping"`.)

That's a **silent-data-loss bug** the moment we start using
`POST /v1/issued_tickets`, because API-issued tickets have `order_id: null`.

Two-line fix concept (do **not** implement here — research-only doc):

- The DTO `VendorTicketDto.VendorOrderId` becomes nullable.
- In `SyncOrdersAndAttendeesAsync`, when `VendorOrderId` is null, look up the
  parent via the `reference` field we wrote at issue time (which we'd have
  set to the Humans `TicketTransfer.Id` or the original
  `TicketOrder.VendorOrderId`). Failing that, persist the attendee as an
  orphan attached to a synthetic "transferred" pseudo-order or directly
  hung off the original order via the local transfer record. Either way:
  do **not** drop these rows.

In addition: on a successful Option-B writeback we should pre-populate the
new `TicketAttendee` row in the same transaction as the void/issue rather
than waiting for the next sync poll, so the UI reflects the transfer
immediately. The next sync will then upsert (matching on `vendor_ticket_id`)
without resurrecting the old voided row's attendee identity — voided rows
arrive with `status: "voided"`, which our `ParseAttendeeStatus` already
maps to `TicketAttendeeStatus.Void`.

The `ISSUED_TICKET.UPDATED` webhook payload (per the
[webhook docs](https://developers.tickettailor.com/docs/api/updated-issued-ticket-webhook/)
schema, decoded from the docs OpenAPI bundle) carries the full attendee
fields and the `status` enum. We don't subscribe to webhooks today; not
required for this feature, but worth flagging that if a TT-dashboard admin
hand-edits a ticket *between* our 15-minute sync polls, we'd see it on the
next poll regardless. Webhooks would just close that lag, not fix anything
fundamental.

## Open questions / things needing live probe

- **Does `POST /v1/issued_tickets` count against the same 5000-req/30-min cap
  as everything else, or has its own (undocumented) cap?** TT docs only
  publish the membership-issuance cap (30/hr). Worth confirming during
  implementation; in practice transfers will be low volume (<<10/hr).
- **Does `void_to_hold=true` on a ticket that was originally part of a paid
  order create a hold pinned to the same `ticket_type_id`, and can a
  subsequent `POST /v1/issued_tickets` with `hold_id=<that hold>` succeed
  even when the event is "sold out"?** The docs imply yes
  ("voiding to a hold ... will not put the allocation back on sale ...
  create the issued ticket from a pre-existing hold"), but the precise
  inventory behaviour at sold-out events is not spelled out and would need
  a test against a stage/preview event with creds.
- **Does the reissued ticket inherit the original ticket's `listed_price`,
  or does it pick up the current ticket-type price?** Docs say "list price
  of the ticket type at the time of purchase" but the create-issued-ticket
  request body has no price field, so behaviour is not user-controllable.
  Matters for our reporting (we tag `Price` on attendee rows) — would need a
  probe to confirm.
- **Does TT charge the credit even for void→hold→reissue (i.e. when no new
  inventory was consumed), or only when issuing against fresh ticket-type
  inventory?** Docs say "one credit per issued ticket, even if the ticket is
  free", so probably yes always — but worth confirming.
- **Idempotency:** TT does not document idempotency keys
  ([intro page](https://developers.tickettailor.com/docs/intro/) does not
  mention them). For the void+issue pair we should record the resulting
  `vendor_ticket_id` in our DB before returning to the user, so a retry can
  skip already-completed steps rather than relying on TT-side dedup.
