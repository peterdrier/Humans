<!-- freshness:triggers
  src/Sections/Humans.TicketTailor/**
  src/Sections/Humans.Tickets/Contracts/ITicketVendorService.cs
  src/Sections/Humans.Tickets/Contracts/TicketVendorSettings.cs
  src/Humans.Web/Extensions/Infrastructure/TicketVendorInfrastructureExtensions.cs
  tests/Humans.Web.Tests/Architecture/TicketVendorPortArchitectureTests.cs
-->
<!-- freshness:flag-on-change
  The environment switch in Section.cs, the wire mapping and error contract in TicketTailorService.cs, and the stub dataset's shape.
-->

# TicketTailor — Section Invariants

The adapter behind Tickets' `ITicketVendorService` port: the application's only conversation with the Ticket Tailor v1 API, and a deterministic stand-in for it everywhere but Production.

## Concepts

- The **port** is `Humans.Tickets.Contracts.ITicketVendorService`, its DTOs and `TicketVendorSettings`. Tickets owns it; this section implements it and publishes nothing.
- **`TicketTailorService`** is the live client: one method per port method over an injected `HttpClient`, bound in Production only.
- **`StubTicketVendorService`** is the fixture: a lazily built, process-wide sample event with per-instance ticket state for void and issue, bound in every other environment.
- A **wire record** (`TtOrder`, `TtIssuedTicket`, `TtCheckIn`, …) is the vendor's JSON shape, nested inside the client and named by its `snake_case` policy.

## Data Model

None — the section owns no tables. Tickets owns every local row mirrored from the vendor.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Tickets (`TicketVendorGateway`, `TicketSyncService`, `TicketTransferService`, `TicketDashboardPageBuilder`, `TicketVendorHealthCheck`) | The only injectors of the port (`tests/Humans.Web.Tests/Architecture/TicketVendorPortArchitectureTests.cs`) |
| Any human | None — the section has no controllers, views or routes |

## Invariants

- The environment name decides the binding, never the presence of a key: only an exactly-`Production` host environment binds `TicketTailorService`; every other value, or none, binds `StubTicketVendorService` (`Section.cs`; `tests/Humans.TicketTailor.Tests/SectionRegistrationTests.cs`).
- Outside Production `TicketVendorSettings.EventId` and `ApiKey` are filled with placeholders when empty, so `IsConfigured` is true and Tickets' sync runs against the fixture.
- Both implementations are `internal sealed`; only `Section.Register` binds them, and only Tickets injects the port.
- The Basic auth header is set only when `ApiKey` is non-empty.
- List reads page until `links.next` is null. Orders and issued tickets filter on `updated_at.gte`; check-ins filter on `created_at.gte` (upload time, not scan time), so a late-uploaded offline scan is never skipped.
- A check-in is reported only when a ticket's net quantity across records is positive; its time is the earliest positive record's `check_in_at`, falling back to `created_at`.
- Attendee email is the answer to the custom question whose text is exactly `Email`, else the ticket's top-level email.
- Money crosses the boundary in euros: vendor cents divided by 100 on the way in, monetary discount values multiplied by 100 on the way out.
- Event capacity is `ticket_groups.max_quantity` summed, falling back to `ticket_types.quantity_total`; the summary is held 15 minutes under `CacheKeys.TicketEventSummary`, and a failed read is never cached.
- List and event reads throw `HttpRequestException`; `GetDiscountCodeUsageAsync` alone reports a non-2xx as "not redeemed" instead. Void and issue throw `TicketVendorWriteException` with a `TicketVendorFailureKind`: 400/422 Validation, 401/403 AuthFailed, 404 NotFound, 429 RateLimited, 5xx and transport failure Transient.
- Issue requires either `HoldId` or both `EventId` and `TicketTypeId`; anything else is an `ArgumentException` before any call.
- Check-in posts form-encoded `issued_ticket_id`, `quantity=1` and `check_in_at`; the vendor call is not idempotent, so callers never retry it. The key needs Event-manager scope.
- The stub dataset is deterministic: the first order is `peter@nobodies.team`; 600 valid tickets across 450 paid orders plus four non-paid orders with one void ticket each; check-ins fall on 2026-07-08; incremental syncs (`since` set) return no tickets and no check-ins (`tests/Humans.TicketTailor.Tests/Services/StubTicketVendorServiceTests.cs`).

## Negative Access Rules

- Sections other than Tickets **cannot** inject `ITicketVendorService`; they ask Tickets through `Humans.Tickets.Contracts` (Gate's check-in mirror goes through `ITicketVendorMirror`).
- Code outside this project **cannot** name `TicketTailorService` or `StubTicketVendorService`.
- A non-Production environment **cannot** reach the live vendor, whatever `TICKET_VENDOR_API_KEY` holds.
- The client **cannot** retry a check-in.

## Triggers

None — the section is a pure request/response surface. Side effects around sync, transfer and gate check-in belong to Tickets and Gate.

## Cross-Section Dependencies

- **Tickets**: implements `Humans.Tickets.Contracts.ITicketVendorService`; reads `TicketVendorSettings` through `IOptions<>`, which Shell binds (`src/Humans.Web/Extensions/Infrastructure/TicketVendorInfrastructureExtensions.cs`) so that deleting this project cannot take the port's configuration with it. The `.csproj` references `Humans.Tickets` (the owner, not a leaf) directly — sanctioned and acyclic, nobodies-collective/Humans#866 — and `Humans.Tickets.Contracts`.
- **Base**: `CacheKeys.TicketEventSummary` (Tickets' invalidator clears it) and `TimeOperation`.

## Architecture

**Owning services:** `TicketTailorService`, `StubTicketVendorService`
**Owned tables:** None — adapter over Tickets' port.
**Status:** (A) Migrated — the G5 adapter shape (`docs/sections/G5-SECTION-TEMPLATE.md`): plain `Microsoft.NET.Sdk`, no tables, an empty `Contracts/`.

### Cross-section read interface

| Read interface | Methods | Notes |
|---|---:|---|
| — | — | Not cross-section-consumed; the port is Tickets' |

- **Decorator decision** — no caching decorator. `GetEventSummaryAsync` holds its result in `IMemoryCache` for 15 minutes under `CacheKeys.TicketEventSummary`; nothing else is cached.
- **Cross-section calls** — none beyond the port.
- **Architecture tests** — `tests/Humans.Web.Tests/Architecture/TicketVendorPortArchitectureTests.cs` pins the injection sites and the adapter count; `tests/Humans.TicketTailor.Tests/Architecture/TicketVendorArchitectureTests.cs` pins that the port's signatures expose no vendor or HTTP types.
- The 2027 vendor swap deletes this project and adds `Humans.<NewVendor>`; nothing in Tickets or any consumer changes.
