# Tickets — Target Shape

## What the section does

The org sells event tickets on an outside vendor's site. This section mirrors the vendor's
orders, issued tickets and gate scans into Humans, and works out which human each ticket
belongs to by matching the attendee email against members' verified emails. From that mirror it
answers its audiences. Members see whether they hold a ticket, who is on it, and can hand a
ticket they hold to another member — the ticket team completes the swap with the vendor and
both people are emailed. The ticket team sees sales, revenue, fees, VAT and donations, who has
not bought yet, which discount codes were redeemed, a live roster of who is on site, and can
provision accounts for buyers who are not yet members. The rest of the app asks it one thing:
does this person hold a ticket, and what are they holding. Holding a ticket also becomes the
member's yearly participation record.

## The shapes

| Shape | Members | Notes |
|---|---|---|
| Mirror the vendor | `TicketSyncService.SyncOrdersAndAttendeesAsync` (+ full re-sync), `tickets-vendor-sync` job, `POST /Tickets/Sync`, `POST /Tickets/FullResync`, `TicketSyncState` | One pipeline: fetch orders + issued tickets + check-ins → verified-email lookup → upsert orders → Stripe fee enrichment → upsert attendees (matched while built) → apply check-ins → VAT split → code redemption → participation reconcile. Incremental by `LastSyncAt` cursor |
| "Does this human hold a ticket" | `ITicketServiceRead` (`GetTicketOrdersAsync`, `GetUserTicketHoldingsAsync`; no `SurfaceBudget` pinned today) fed by `ITicketRepository.HasEventTicketAsync` and the private `ComputeUserTicketCountAsync` | One question over the match paths (`MatchedUserId`, then verified-email fallback); one projection callers derive from |
| Member holds & transfers | `/Tickets/Transfers` (Index, Confirm, Submit, Cancel), `<vc:my-ticket-stubs>`, `<vc:ticket-holdings>`, `<vc:ticket-stub>`, `<vc:member-ticket-status>`, `<vc:guest-ticket-orders>` | One wizard + one stub renderer reused by homepage, profile and wizard |
| Admin transfer processing | `/Tickets/Admin/Transfers` (Index?tab, Detail/{id}, Decide with action ∈ process/retry/marksuccessful/cancel), `ITicketTransferQueue.CountPendingAsync` | One state machine: Pending → Approved / Rejected / Cancelled; vendor void-to-hold + reissue is the automated path, mark-successful the manual one |
| Reporting | `/Tickets` (dashboard), `/Tickets/Orders`, `/Tickets/Attendees`, `/Tickets/Codes`, `/Tickets/SalesAggregates`, `/Tickets/WhoHasntBought`, the CSV exports, `/Tickets/GateList` (placeholder) | Paged lists (search/sort/filter), aggregates, one "who hasn't" cross-join with Users/Teams/Governance |
| Onsite & gate tooling | `/Tickets/Admin/Onsite`, `/Tickets/Admin/Gate` (set/rotate gate-terminal password); barcode → stub for Scanner/Gate is a projection over `GetTicketOrdersAsync` | One roster join, one credential rotation |
| Contact import | `/Tickets/Admin/Contacts` (preview → apply) | Plan/apply over unmatched attendees: attach verified / replace unverified / create user |
| Participation | `IUserParticipationBackfillService` via `/Tickets/Participation/Backfill` (Admin only) | CSV backfill; the reconcile itself lives in the sync pipeline |
| Discount codes for Campaigns | `ITicketDiscountCodes` | Vendor port pass-through so Campaigns never names the vendor |
| GDPR | `IUserDataContributor` on `TicketQueryService` (export + `EraseForUserAsync` tombstone scrub) | Buyer/attendee names + emails tombstoned, rows kept for finance |
| Cache seam | `ITicketCacheInvalidator` (poked by sync, merge fold, transfer transitions) | Owned by the singleton decorator; slices: Orders (warmed), UserHoldings (5 min), per-event vendor summary (IMemoryCache) |
| Health | `TicketVendorHealthCheck` | Vendor reachability probe |

## Structure

The layout these shapes imply:

- One vendor port (`ITicketVendorService` in `Contracts/`), one adapter project
  (`Humans.TicketTailor`), and the Tickets leaf never names the vendor.
- One sync service owning the pipeline end to end; one repository over `ticket_orders`,
  `ticket_attendees` and `ticket_sync_state`; one transfer repository owning
  `ticket_transfer_requests` end to end.
- One query service behind `ITicketService` (admin) and `ITicketServiceRead` (cross-section),
  wrapped once by a singleton caching decorator that also owns invalidation.
- One transfer service holding the state machine; the vendor writeback goes through it and
  nowhere else.
- Controllers per audience: admin dashboard, member transfer wizard, admin transfer queue,
  contacts import, gate credential, onsite roster. Each translates and formats only; the view
  models it needs are the service DTOs or thin projections of them, not parallel copies.
- Member-facing views localized; admin views unlocalized. The transfer wizard uses the section's
  own resx set (`TicketsResource`); the status card and guest-orders card use the shared
  `Dashboard_*`/`Guest_*` keys; the stub and holdings components are still hardcoded English.

## Invariants

- Buyer-only matches never count as holding a ticket: `MatchedUserId` on an attendee row, or an
  attendee email equal to one of the user's verified emails, is the only way a person "has a
  ticket". A paid order in someone's name with no attendee for them does not. The holdings list
  and `TicketCount` already hold to this; `HasCurrentEventTicket` does not yet, because
  `HasEventTicketAsync` still answers true for a buyer-only paid order on the current event.
- A gate scan stamps `CheckedInAt` and leaves `Status = Valid`; transferability requires both
  `Status == Valid` and `CheckedInAt == null`, and is enforced in the row flags, the confirm step
  and `CreateRequestAsync`, not only in the view.
- Only the Sender may cancel, only while Pending and not mid-processing; only `TicketAdminOrAdmin`
  may process, retry, mark successful or cancel; a `VoidSucceededIssueFailed` request accepts only
  retry or mark-successful.
- The manual approval path mutates no attendee rows; the automated path writes the swapped rows
  itself; every transition audits (`TicketTransferRequested/Cancelled/Approved/AutoFailed/Rejected`).
- Sync is idempotent and cursor-driven; a non-transient failure leaves `SyncStatus = Error` with
  the message on the singleton row, a transient vendor error (no status or 5xx) restores `Idle`
  and returns empty, and neither moves `LastSyncAt`.
- `ParticipationStatus.Attended` is write-once; sync derives Ticketed/Attended from attendee
  rows and removes Ticketed when no valid ticket remains.
- Erasure tombstones name/email on order and attendee rows and never deletes them; the vendor
  ids stay so finance and sync keep reconciling.
- `Board` reads the dashboard and its reporting tabs (`TicketAdminBoardOrAdmin`) and the onsite
  roster (`ScannerAccess`) but triggers no sync or export; the transfer queue, contacts import
  and gate credential pages are `TicketAdminOrAdmin`; `ParticipationBackfill` and `FullResync`
  are Admin only; the gate-terminal account reaches only `ScannerAccess` and `GateAdmit`.
- `TicketVendorSettings.IsConfigured == false` short-circuits the member status card, the
  dashboard and the health check to the not-configured state and makes the sync job and service
  no-ops; the stub and holdings components still render whatever rows exist.

## Seams

- **Event label on the stub** is a constant (`"Elsewhere 2026 · Admit One"`, `TicketStub`
  view); the design reserves sourcing it from the active event. Not built.
- **`VendorStepsJson`** column is dormant by design until prod soak; the drop is a scheduled
  follow-up, not this section's to do ad hoc.
- **`/Tickets/GateList`** is a placeholder page whose function moved to `/Scanner/Tickets`; its
  removal is a nav decision for Peter.
- **`CacheKeys.TicketDashboardStats`** is a reserved key for a dashboard cache that was never
  added; nothing reads, writes or evicts it.

## Deliberately not done

- No vendor-agnostic transfer abstraction beyond the port: the void-to-hold + reissue sequence
  is TicketTailor's, and the port exposes exactly those calls.
- No read-through cache on the dashboard stats; on-demand staleness during sync is accepted.
- No pagination-free admin lists: orders, attendees and who-hasn't-bought are the one place the
  dataset is large enough that paging buys something.
- No concurrency tokens on the transfer request; the state machine tolerates a double click by
  re-checking status.
- No per-environment toggle for the automated transfer path; it is always offered.
- No separate Attendee aggregate root: `TicketTransferRequest` references the attendee with no
  inverse collection on purpose.
- No local ticket issuance of any kind; Humans never creates a ticket except as a transfer
  reissue from a held seat.

## Load-bearing weirdness

- **Email matching uses `NormalizingEmailComparer`** so gmail/googlemail aliases and casing
  collide (dots and `+tags` do not; that is `GmailAliasEmailComparer`, unused here); matching by
  raw string would silently split one person into two.
- **`ComputeUserTicketCountAsync` falls back to verified emails** when `MatchedUserId` is null,
  because the sync only writes `MatchedUserId` on its own cadence and a member who just verified
  an email expects the homepage to update now. `HasEventTicketAsync` has no such fallback, so
  `HasCurrentEventTicket` waits for the next sync.
- **The caching decorator is a Singleton wrapping a Scoped inner** via `WithInner`, because
  `TrackedCache` slices must outlive a request while the repository must not.
- **Every transfer transition invalidates the holdings cache and audits before any email**, and
  the automated path records the vendor diagnostic on the request before deciding whether to
  email, so a crash mid-flow leaves the admin a readable state rather than a silent Pending.
  Email sends are wrapped so a mail failure never rolls back a recorded decision.
- **The query service reads the transfer repository** to stamp the pending-transfer flag on a
  member's holdings; the transfer table has its own repository so the state machine's writes
  stay in one place, and the holdings read is a projection over it, not a second writer.
- **The gate-terminal account is a real user with no roles** and a rotating password, created
  lazily the first time a ticket admin sets its password from `/Tickets/Admin/Gate`; the
  password lives on the Identity row and rotation bumps the security stamp, so live gate
  sessions lapse on Identity's next stamp validation rather than instantly.
- **`TicketRepository` is a Singleton** over a context factory while the services are Scoped;
  the singleton caching decorator resolves its Scoped inner per call.
- **Contact import re-queries at apply time** rather than trusting the plan, because a sync can
  land between preview and apply.
- **Order-drift table** on the transfer queue exists because the manual path leaves the local
  rows stale until the next sync; it is the team's reconciliation aid, not a bug list.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-09-05 | First doctoring: invariant doc rebuilt against the code, narration purged, two dead resx keys cut, sync-cursor test pinned | peterdrier/Humans#1589 |
