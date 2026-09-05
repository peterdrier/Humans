# TicketTailor — target shape

## What the section does

The application's only conversation with the Ticket Tailor ticketing service. It answers
Tickets' questions — which orders, issued tickets and gate check-ins changed since a moment,
and how an event's capacity stands — and carries out Tickets' instructions: mint discount
codes, void a ticket (optionally into a hold), issue a ticket, record a gate check-in.

Outside Production it substitutes a deterministic in-memory event so every other environment
runs the same sync, transfer and gate code against a realistic dataset without a vendor
account. It stores nothing, caches one thing (an event's summary, briefly), and talks to nobody
but Tickets and Ticket Tailor.

## The shapes

| Shape | Port methods | Vendor side | What the section adds |
|---|---|---|---|
| Changed-since list | `GetOrdersAsync`, `GetIssuedTicketsAsync`, `GetCheckInsAsync` | `GET /orders`, `/issued_tickets`, `/check_ins` — cursor-paged | Follows `links.next` by `starting_after=<last id>`; maps cents to euros; orders: discount code, discount and donation amounts from line items; tickets: attendee email from the "Email" custom question; check-ins: net quantity per ticket, earliest positive scan |
| Snapshot read | `GetEventSummaryAsync` | `GET /events/{id}` | Capacity from `ticket_groups.max_quantity` (fallback `ticket_types.quantity_total`); held 15 min under `CacheKeys.TicketEventSummary`; failures never cached |
| Discount codes | `GenerateDiscountCodesAsync`, `GetDiscountCodeUsageAsync` | `POST` / `GET /voucher_codes` | `NOBO-` prefixed codes; percentage vs monetary (cents) |
| Ticket writes | `VoidIssuedTicketAsync`, `IssueTicketAsync`, `CreateCheckInAsync` | form-encoded `POST /issued_tickets/{id}/void`, `/issued_tickets`, `/check_ins` | Void and issue classify failures into `TicketVendorWriteException.Kind`; check-in throws the raw `HttpRequestException` |

Error contract by shape: list and snapshot reads throw `HttpRequestException`
(`EnsureSuccessStatusCode`); void and issue throw `TicketVendorWriteException` with a
`TicketVendorFailureKind`; check-in throws `HttpRequestException`; the usage read reports a
non-2xx as "not redeemed".

## Structure

- `Section.cs` — the environment switch: Production binds the HTTP client, anything else the
  stub plus placeholder settings.
- `Services/TicketTailorService.cs` — the client, one method per port method, with the wire
  records it deserializes nested beside it. One naming mechanism for the wire shape (the
  snake_case policy), not two.
- `Services/StubTicketVendorService.cs` — the fixture: a lazily built, process-wide sample
  event and per-instance ticket state for void/issue.
- `Docs/` — the invariant doc (`TicketTailor.md`), the data-access map, this target.
- `Contracts/` — deliberately empty (the port is Tickets'); a README says so.
- `tests/Humans.TicketTailor.Tests` — one client test class per shape family over a single
  request-capturing handler and one service factory; a registration test for the
  environment switch; the port-signature test.

## Invariants

- The environment name decides the binding, never the presence of a key: only an exactly
  `Production` host environment binds `TicketTailorService`; every other value, or none,
  binds `StubTicketVendorService`. A developer holding a real `TICKET_VENDOR_API_KEY` still
  gets the stub.
- Outside Production `TicketVendorSettings.EventId` and `ApiKey` are filled with placeholders
  when empty, so `IsConfigured` is true and Tickets' sync runs against the fixture.
- The Basic auth header is set only when `ApiKey` is non-empty.
- Every list read pages until `links.next` is null. Orders and issued tickets filter on
  `updated_at.gte`; check-ins filter on `created_at.gte` (upload time, not scan time) so a
  late-uploaded offline scan is never skipped.
- A check-in is reported only when a ticket's net quantity across records is positive; its
  time is the earliest positive record's `check_in_at`, falling back to `created_at`.
- Attendee email is the answer to the custom question whose text is exactly `Email`,
  else the ticket's top-level email.
- Money crosses the boundary in euros: vendor cents divided by 100 on the way in, monetary
  discount values multiplied by 100 on the way out.
- Void and issue map HTTP status to `TicketVendorFailureKind`: 400/422 Validation, 401/403
  AuthFailed, 404 NotFound, 429 RateLimited, 5xx and transport failure Transient.
- Issue requires either `HoldId` or both `EventId` and `TicketTypeId`; anything else is an
  `ArgumentException` before any call.
- Check-in posts form-encoded `issued_ticket_id`, `quantity=1`, `check_in_at`; the vendor
  call is not idempotent, so callers never retry it.
- Both implementations are `internal sealed`; only `Section.Register` binds them, and
  only Tickets injects the port (`tests/Humans.Web.Tests/Architecture/TicketVendorPortArchitectureTests.cs`).
- The stub dataset is deterministic: the first order is `peter@nobodies.team`; 600 paid
  tickets across 450 paid orders plus four non-paid orders with one void ticket each;
  check-ins fall on 2026-07-08; incremental syncs (`since` set) return no
  tickets and no check-ins.
- No tables, no repository, no cross-section call in either direction beyond the port.

## Seams

- The 2027 vendor swap: this project is deleted and `Humans.<NewVendor>` added; nothing in
  Tickets or any consumer changes. Every item here is shaped by keeping that true.
- `CreateCheckInAsync` is live code behind Gate's `Gate:VendorMirrorEnabled` flag (default
  off); the mirror has not run in production.

## Deliberately not done

- No retry or backoff in the client: Tickets and Gate own the retry decision, and check-in
  is not idempotent.
- No local copy of vendor data: Tickets owns every mirrored row.
- No `Humans.TicketTailor.Contracts` leaf: the port and its DTOs are Tickets', so the
  adapter publishes nothing.
- No key-presence switch between stub and live client (see the first invariant).
- No `IOptions<TicketVendorSettings>` binding here: Shell binds the port's settings, so
  deleting the adapter cannot take them with it.
- No vendor SDK: the client is `HttpClient` plus `System.Text.Json`.
- No pagination or caching of the discount-code endpoints: they are called a handful of
  times a year.

## Load-bearing weirdness

- The adapter takes a direct `ProjectReference` on `Humans.Tickets` (owner), not on a leaf.
  Sanctioned and acyclic: Tickets names nothing here.
- `Contracts/` holds only a README. The folder is the section shape the G5 template names;
  the README is what keeps it in git.
- `CacheKeys.TicketEventSummary` lives in Base because both this adapter (populates) and
  Tickets' invalidator (clears) name it.
- The stub is Scoped, and void/issue mutate a per-instance copy of the fixture: nothing
  persists across requests. Dev transfers work because Tickets rewrites its own rows and
  the stub returns nothing on incremental sync.
- Reads throw `HttpRequestException` while void/issue wrap into
  `TicketVendorWriteException`. Tickets' health check and Gate's mirror job catch on the
  read contract; unifying it is a port change, not an adapter change.
- `VendorOrderDto.Tickets` is `[]` from the live client and populated by the stub; Tickets'
  sync reads attendees from `GetIssuedTicketsAsync` and never reads the field.
- `GetDiscountCodeUsageAsync` has no caller anywhere; both implementations exist because
  the port declares it.
- The nested wire records are `internal`, not `private`, because `System.Text.Json`
  cannot bind private nested types.
- `InternalsVisibleTo("DynamicProxyGenAssembly2")` is the universal per-section
  convention, not a sign these tests substitute internals.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-05 | First doctoring — invariant doc written, wire records collapsed to one naming mechanism, dead test scaffolding cut, untested invariants pinned | peterdrier/Humans#1595 |
