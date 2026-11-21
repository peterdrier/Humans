# Tickets — Target Shape

## What the section does

The org sells event tickets on an outside vendor's site. This section mirrors the vendor's
orders, issued tickets and gate scans into Humans, and works out which human each ticket
belongs to by matching the attendee email against members' verified emails. From that mirror it
answers its audiences. Members see whether they hold a ticket, who is on it, and can hand a
ticket they hold to another member — the ticket team completes the swap with the vendor and
both people are emailed. The ticket team sees sales, revenue, fees, VAT and donations, who has
not bought yet, which discount codes were redeemed, a live roster of who is on site, the monthly
income recap and donor list the accountant needs, and can provision accounts for buyers who are
not yet members. The rest of the app asks it: does this person hold a ticket and what are they
holding; mint discount codes for a campaign wave; mirror a gate admit to the vendor; run a sync,
or is the sync in error. Holding a ticket also becomes the member's yearly participation record,
and a member's erasure scrubs their name and email from the sales records without deleting them.

## The shapes

| Shape | Members | Notes |
|---|---|---|
| Mirror the vendor | `TicketSyncService.SyncOrdersAndAttendeesAsync`, `ResetSyncStateForFullResyncAsync`, `TicketSyncJob`, `POST /Tickets/Sync`, `POST /Tickets/FullResync`, `TicketSyncState` | One pipeline: fetch orders + issued tickets + check-ins → verified-email lookup → upsert orders → Stripe fee enrichment → upsert attendees (matched while built) → apply check-ins → VAT → code redemption → participation reconcile. Incremental by `LastSyncAt` cursor |
| "Does this human hold a ticket" | `ITicketService` (`GetTicketOrdersAsync`, `GetUserTicketHoldingsAsync`) | One projection per question; callers derive aggregates from `TicketOrderInfo` |
| Member holdings | `<vc:my-ticket-stubs>`, `<vc:ticket-holdings>`, `<vc:ticket-stub>`, `<vc:member-ticket-status>`, `<vc:guest-ticket-orders>` | One stub renderer reused by homepage and profile |
| Reporting | `/Tickets` (dashboard), `/Tickets/Orders`, `/Tickets/Attendees`, `/Tickets/Codes`, `/Tickets/SalesAggregates`, `/Tickets/WhoHasntBought` | Paged lists (search/sort/filter), aggregates, one "who hasn't" cross-join over Users, Teams and the active year |
| Exports | `/Tickets/Export/Attendees`, `/Tickets/Export/Orders`, `/Tickets/Export/AccountantReport`, `/Tickets/Export/Donations` | Four CSVs; only the donor list is audited (TICKETS-9 covers the other two) |
| Onsite & gate tooling | `/Tickets/Admin/Onsite`, `/Tickets/Admin/Gate` (set/rotate gate-terminal password) | One roster join, one credential rotation |
| Contact import | `/Tickets/Admin/Contacts` (preview → apply) | Plan/apply over unmatched attendees: attach verified / replace unverified / create user |
| Event participation | `TicketQueryService` owns uncached `ITicketService.GetParticipationsAsync` / `GetAllParticipationsAsync` reads of `ticket_event_participations`; `EventParticipationService` owns `ITicketWrite` (Gate admit), `/Tickets/DeclareNotAttending` + `UndoNotAttending`, `<vc:onsite-chip>` on the profile, `IUserParticipationBackfillService` via `/Tickets/Participation/Backfill` | One record per user and year; the copy and fallback retired after production reported done on 2026-11-03; the reconcile itself lives in the sync pipeline |
| Vendor access | `ITicketDiscountCodes` on `TicketVendorGateway`; `ITicketVendorMirror` on `CachingTicketVendorService` | Discount mapping stays at the boundary; check-ins reuse the existing keyed vendor path |
| GDPR | `IUserDataContributor` on `TicketQueryService` and `EventParticipationService` | Export plus tombstone erasure that a later sync cannot undo; participation is retained |
| Caches | `CachingTicketQueryService`, `CachingTicketVendorService` | Two decorators, each evicting on its own write pass-throughs: order + user-holdings slices; per-event vendor summary |
| Health | `TicketVendorHealthCheck` | Vendor reachability probe |

## Structure

The layout these shapes imply:

- One vendor port (`ITicketVendorService` in `Contracts/`), one adapter section
  (`Humans.TicketTailor`), and the Tickets leaf never names the port's vocabulary.
- One sync service owning the pipeline end to end; one repository over `ticket_orders`,
  `ticket_attendees` and `ticket_sync_state`; one transfer repository over the
  retired `ticket_transfer_requests` (approved-row read, merge, erasure; nothing writes requests).
- One query service behind `ITicketManagement` (admin) and `ITicketService` (cross-section),
  wrapped once by a singleton caching decorator that also owns invalidation; a second singleton
  decorator on the vendor port caches only the event summary.
- Controllers per audience: admin dashboard + exports, contacts import, gate credential, onsite roster. Each translates and formats only; the
  view models carry paging/filter state and render the service DTOs, never parallel copies of
  them.
- Member-facing views localized through `TicketsResource` (stub, holdings, status card) or the shared `Dashboard_*`/`Guest_*` keys; admin views unlocalized.
- The dashboard computes only what it renders.

## Invariants

- Buyer-only matches never count as holding a ticket: ownership is the attendee's
  `MatchedUserId` (`Services/TicketAttendeeOwnership.cs:17`), the ticket count reads attendee
  rows with a verified-email fallback (`Services/TicketQueryService.cs:38`), and
  `HasEventTicketAsync` reads attendee rows only (`Data/TicketRepository.cs:413`).
- A gate scan leaves `Status = Valid` and stamps `CheckedInAt`.
- Sync is cursor-driven: success moves `LastSyncAt` (`Services/TicketSyncService.cs:144`), a
  transient vendor error restores `Idle` without moving it (`Services/TicketSyncService.cs:203`),
  any other failure records `Error` with the message and rethrows
  (`Services/TicketSyncService.cs:186`); an unconfigured vendor makes it a no-op
  (`Services/TicketSyncService.cs:39`).
- Sync never removes or downgrades `Attended` (`Services/TicketSyncService.cs:482`) and removes
  a sync-sourced `Ticketed` row once no valid ticket remains
  (`Services/TicketSyncService.cs:509`).
- Erasure tombstones name and email in place and keeps the rows
  (`Data/TicketRepository.cs:927`); a later sync never writes them back while the row carries
  `PiiErasedAt` or the tombstone shape (`Data/TicketRepository.cs:190`).
- Board reads the dashboard and reporting tabs (`Controllers/TicketController.cs:17`) but triggers
  no sync (`Controllers/TicketController.cs:167`) and downloads no export
  (`Controllers/TicketController.cs:248`); full re-sync, backfill and the donor list are Admin
  only (`Controllers/TicketController.cs:177`, `Controllers/TicketController.cs:316`); every
  donor-list download audits (`Services/TicketQueryService.cs:782`).
- `TicketVendorSettings.IsConfigured == false` short-circuits the dashboard
  (`Models/TicketDashboardPageBuilder.cs:17`), the member status card
  (`ViewComponents/MemberTicketStatusViewComponent.cs:30`), the health check
  (`Health/TicketVendorHealthCheck.cs:27`) and the sync job (`Jobs/TicketSyncJob.cs:26`).

## Seams

- **`VendorStepsJson`** column is dormant by design until prod soak; the drop is a scheduled
  follow-up, not this section's to do ad hoc.

## Deliberately not done

- No read-through cache on the dashboard stats; on-demand staleness during sync is accepted.
- No pagination-free admin lists: orders, attendees and who-hasn't-bought are the one place the
  dataset is large enough that paging buys something.
- No local ticket issuance through the vendor port; the retired transfer reissues were the only case.
- No volunteer-coverage figure on the dashboard: the coverage card was replaced by the user
  set-membership view, and the "Who hasn't bought?" list is the operational answer.

## Load-bearing weirdness

- **Email matching uses `NormalizingEmailComparer`** so gmail/googlemail aliases and casing
  collide (dots and `+tags` do not); matching by raw string would silently split one person
  into two.
- **The ticket count falls back to verified emails** when `MatchedUserId` is null, because the
  sync only writes `MatchedUserId` on its own cadence and a member who just verified an email
  expects the homepage to update now. The fallback reads the same index the sync matches with
  (`VerifiedEmailLookup`: aliases folded, an email verified by two users maps to nobody), so the
  two agree. `HasEventTicketAsync` has no such fallback, so `HasCurrentEventTicket` waits for the
  next sync.
- **The caching decorators are Singletons wrapping Scoped or keyed inners**, because
  `TrackedCache` slices must outlive a request while the repository and adapter calls must not.
- **The gate-terminal account is a real user with no roles** and a rotating password, created
  lazily the first time a ticket admin sets its password from `/Tickets/Admin/Gate`.
- **`TicketRepository` and `TicketTransferRepository` are Singletons** over a context factory
  while the services are Scoped.
- **Contact import re-queries at apply time** rather than trusting the plan, because a sync can
  land between preview and apply.
- **The Scanner gate card builds `TicketStubInfo` itself** to show the scanned attendee's Early
  Entry, not the operator's; every holder-facing surface goes through `TicketStubInfo.From`.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-09-05 | First doctoring: invariant doc rebuilt against the code, narration purged, dead resx keys cut, sync-cursor test pinned | peterdrier/Humans#1589 |
| section-doctor | 2026-10-05 | Dashboard computes and docs describe only what `/Tickets` renders; view model renders the service DTOs | peterdrier/Humans#1905 |
