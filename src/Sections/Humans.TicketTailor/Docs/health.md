# TicketTailor — target shape

## What the section does

The application's only conversation with the Ticket Tailor ticketing service. It answers
Tickets' questions — which orders, issued tickets and gate check-ins changed since a moment,
and how an event's capacity stands — and carries out Tickets' instructions: mint discount
codes, void a ticket (optionally into a hold), issue a ticket, record a gate check-in.

Outside Production it substitutes a deterministic in-memory event so every other environment
runs the same sync, transfer and gate code against a realistic dataset without a vendor
account. It stores nothing, caches nothing, and talks to nobody but Tickets and Ticket Tailor.

## The shapes

| Shape | Port methods | Vendor side | What the section adds |
|---|---|---|---|
| Changed-since list | `GetOrdersAsync`, `GetIssuedTicketsAsync`, `GetCheckInsAsync` | `GET /orders`, `/issued_tickets`, `/check_ins` — cursor-paged | Follows `links.next` by `starting_after=<last id>`; maps cents to euros; orders: discount code, discount and donation amounts from line items; tickets: attendee email from the "Email" custom question; check-ins: net quantity per ticket, earliest positive scan |
| Snapshot read | `GetEventSummaryAsync` | `GET /events/{id}` | Capacity from `ticket_groups.max_quantity` (fallback `ticket_types.quantity_total`) |
| Discount codes | `GenerateDiscountCodesAsync` | `POST /voucher_codes` | `NOBO-` prefixed codes; percentage vs monetary (cents) |
| Ticket writes | `VoidIssuedTicketAsync`, `IssueTicketAsync`, `CreateCheckInAsync` | form-encoded `POST /issued_tickets/{id}/void`, `/issued_tickets`, `/check_ins` | Void and issue classify failures into `TicketVendorWriteException.Kind`; check-in throws the raw `HttpRequestException` |

## Structure

- `Section.cs` — the environment switch: Production binds the HTTP client, anything else the
  stub plus placeholder settings. Both under Tickets' keyed inner-service key; the unkeyed port
  and its caching decorator are Tickets' own registration.
- `Services/TicketTailorService.cs` — the client: the port methods, then the private mapping
  helpers, then the wire records it deserializes. One naming mechanism for the wire shape (the
  snake_case policy), and one mapping per wire record — an issued ticket maps to the port's
  DTO the same way whether it came from a list page or an issue response.
- `Services/StubTicketVendorService.cs` — the fixture: a lazily built, process-wide sample
  event and per-instance ticket state for void/issue.
- `Docs/` — the invariant doc (`TicketTailor.md`), the data-access map, this target.
- `Contracts/` — deliberately empty (the port is Tickets'); a README says so.
- `tests/Humans.TicketTailor.Tests` — a read-side and a write-side client test class over a
  single request-capturing handler and one service factory; a stub dataset test; a
  registration test for the environment switch; the port-signature test.

## Invariants

- The environment name decides the binding, never the presence of a key: only an exactly
  `Production` host environment binds `TicketTailorService`; every other value, or none,
  binds `StubTicketVendorService` (`src/Sections/Humans.TicketTailor/Section.cs:39`).
- Outside Production `TicketVendorSettings.EventId` and `ApiKey` are filled with placeholders
  when empty, so `IsConfigured` is true and Tickets' sync runs against the fixture
  (`src/Sections/Humans.TicketTailor/Section.cs:53`).
- The Basic auth header is set only when `ApiKey` is non-empty
  (`src/Sections/Humans.TicketTailor/Services/TicketTailorService.cs:42`).
- Every list read pages until `links.next` is null
  (`src/Sections/Humans.TicketTailor/Services/TicketTailorService.cs:101`). Orders and issued
  tickets filter on `updated_at.gte`; check-ins filter on `created_at.gte` (upload time, not
  scan time) so a late-uploaded offline scan is never skipped
  (`src/Sections/Humans.TicketTailor/Services/TicketTailorService.cs:156`).
- A check-in is reported only when a ticket's net quantity across records is positive; its
  time is the earliest positive record's `check_in_at`, falling back to `created_at`
  (`src/Sections/Humans.TicketTailor/Services/TicketTailorService.cs:369`).
- Attendee email is the answer to the custom question whose text is exactly `Email`,
  else the ticket's top-level email
  (`src/Sections/Humans.TicketTailor/Services/TicketTailorService.cs:450`).
- Money crosses the boundary in euros: vendor cents divided by 100 on the way in, monetary
  discount values multiplied by 100 on the way out
  (`src/Sections/Humans.TicketTailor/Services/TicketTailorService.cs:228`).
- Void and issue map HTTP status to `TicketVendorFailureKind`: 400/422 Validation, 401/403
  AuthFailed, 404 NotFound, 429 RateLimited, anything else and transport failure Transient
  (`src/Sections/Humans.TicketTailor/Services/TicketTailorService.cs:462`). Every other
  method throws `HttpRequestException`.
- Issue requires either `HoldId` or both `EventId` and `TicketTypeId`; anything else is an
  `ArgumentException` before any call
  (`src/Sections/Humans.TicketTailor/Services/TicketTailorService.cs:308`).
- Check-in posts form-encoded `issued_ticket_id`, `quantity=1`, `check_in_at`
  (`src/Sections/Humans.TicketTailor/Services/TicketTailorService.cs:252`); the vendor call is
  not idempotent, so callers never retry it.
- Both implementations are `internal sealed`; only `Section.Register` binds them, and
  only Tickets injects the port (`tests/Humans.Web.Tests/Architecture/TicketVendorPortArchitectureTests.cs`).
- The stub dataset is deterministic: the first order is `peter@nobodies.team`; every paid
  order holds one or two valid tickets and every non-paid order one void ticket; check-ins
  fall on 2026-07-08; incremental syncs (`since` set) return no tickets and no check-ins
  (`src/Sections/Humans.TicketTailor/Services/StubTicketVendorService.cs:59`). Exact totals
  are the tests' to own.

## Seams

- The 2027 vendor swap: this project is deleted and `Humans.<NewVendor>` added; nothing in
  Tickets or any consumer changes. Every item here is shaped by keeping that true.
- `CreateCheckInAsync` is live code behind Gate's `Gate:VendorMirrorEnabled` flag (default
  off); the mirror has not run in production.

## Deliberately not done

- No caching here: the event-summary cache is Tickets' `CachingTicketVendorService`, beside
  the port, so a vendor swap keeps it.
- No retry or backoff in the client: Tickets and Gate own the retry decision, and check-in
  is not idempotent.
- No local copy of vendor data: Tickets owns every mirrored row.
- No `Humans.TicketTailor.Contracts` leaf: the port and its DTOs are Tickets', so the
  adapter publishes nothing.
- No key-presence switch between stub and live client (see the first invariant).
- No `IOptions<TicketVendorSettings>` binding here: Shell binds the port's settings, so
  deleting the adapter cannot take them with it.
- No vendor SDK: the client is `HttpClient` plus `System.Text.Json`.
- No pagination of the discount-code endpoint: it is called a handful of times a year.

## Load-bearing weirdness

- The adapter takes a direct `ProjectReference` on `Humans.Tickets` (owner), not on a leaf.
  Sanctioned and acyclic: Tickets names nothing here.
- `Contracts/` holds only a README. The folder is the section shape the G5 template names;
  the README is what keeps it in git.
- The stub is Scoped, and void/issue mutate a per-instance copy of the fixture: nothing
  persists across requests. Dev transfers work because Tickets rewrites its own rows and
  the stub returns nothing on incremental sync.
- Reads, discount codes and check-in throw `HttpRequestException` while void/issue wrap into
  `TicketVendorWriteException`. Tickets' health check and Gate's mirror job catch on the
  raw contract; unifying it is a port change, not an adapter change.
- `GetOrdersAsync` times each page; the other list reads time their whole loop. The per-page
  timing is the fix for the orders sync's false Error entries (nobodies-collective/Humans#946);
  the other two were not part of that incident.
- The nested wire records are `internal`, not `private`, because `System.Text.Json`
  cannot bind private nested types.
- `InternalsVisibleTo("DynamicProxyGenAssembly2")` is the universal per-section
  convention, not a sign these tests substitute internals.
- `StubTicketVendorService.BuildSampleData` is long and branchy by design: it is the one
  deterministic fixture, and a split earns nothing until the dataset grows a second shape.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-05 | First doctoring — invariant doc written, wire records collapsed to one naming mechanism, dead test scaffolding cut, untested invariants pinned | peterdrier/Humans#1595 |
| 2 | 2026-09-24 | Cache-move drift cleared from the docs; one issued-ticket mapping; dead test setup cut | peterdrier/Humans#1817 |
