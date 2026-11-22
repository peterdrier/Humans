# Tickets — Target Shape

## What the section does

The org sells event tickets. For years an outside vendor sold them; this section mirrors that
vendor's orders, issued tickets and gate scans into Humans and works out which human each ticket
belongs to by matching the attendee email against members' verified emails. From 2027 the section
sells tickets itself: the ticketing team sets up a sales event (prices, capacity, child types,
donation purposes, switches for partners, companions and resale), a signed-in member buys for
themselves, their partners and their children, and a payment backend (Box Office, pretix or
Hi.Events) takes the money. Paid orders become issued tickets with a code, mailed to each holder
and shown on their profile. A holder who cannot come can put their ticket in the resale queue or
offer it to named people; when it sells, the holder is refunded in the evening batch after a
holding period. A holder may ask for a companion; the ticketing team approves or declines. The
team can refund, rename, void and restore tickets, look up an order or a person, and cancel a whole
event, which voids every ticket and refunds every paid order. Every night the section reconciles
its own record of money and tickets against each backend and against card payments, and says where
they disagree.

The rest of the app asks it: does this person hold a ticket and what are they holding; mint discount
codes for a campaign wave; is this code at the gate a valid ticket, and record that it was admitted;
run a vendor sync, or is it in error. Holding a ticket becomes the member's yearly participation
record, and a member's erasure scrubs their name and email from the sales records without deleting
them.

## The shapes

| Shape | Members | Notes |
|---|---|---|
| Mirror the vendor | `TicketSyncService`, `TicketSyncJob`, `POST /Tickets/Sync`, `POST /Tickets/FullResync`, `TicketSyncState` | One pipeline: fetch → verified-email lookup → upsert orders → fee enrichment → upsert attendees → check-ins → VAT → code redemption → participation reconcile; incremental by cursor |
| "Does this human hold a ticket" | `ITicketService` (`GetTicketOrdersAsync`, `GetUserTicketHoldingsAsync`) | One projection per question; holdings rows are vendor tickets, and Humans-issued tickets feed only the post-event deletion hold |
| Sales setup | `/Tickets/Admin/Sales`, `/Tickets/Admin/Sales/Edit/{id}`, `SaveSalesEventAsync`, `SetResaleOpenAsync`, `/Tickets/Admin/Backends/{key}` | One sales event per selling window; switches live on the sales event |
| Buy | `/Tickets/Buy`, `/Tickets/Buy/Public`, `/Tickets/Buy/Resale/{token}`, `StartCheckoutAsync`, `/Tickets/Buy/Order/{id}` | One checkout path; partners, children, supporter totals and donations are lines on one order |
| Payment backends | `ITicketCheckoutBackend`, `ITicketCheckoutEvents`, `CheckoutChangedAsync`, `RefreshOrderAsync` | The port the adapter sections implement; webhooks and the reconcile both land in one refresh |
| Keep orders true | `ReconcileOpenOrdersAsync`, `TicketCheckoutReconcileJob` | One sweep: open orders, unsettled refunds, crash repair, resale queue, cancelled events |
| My tickets | `/Tickets/Buy/Mine`, `GetHeldTicketsAsync`, `<vc:issued-tickets>`, `/Tickets/Code/{token}` | Offer, withdraw, invite, cancel invite, ask for a companion |
| Resale | `OfferForResaleAsync`, `InviteBuyersAsync`, `RunResaleRefundBatchAsync`, `TicketResaleRefundJob`, `/Tickets/Admin/Sales/{id}/Resale` | Queue plus directed offers; one evening refund batch |
| Companions | `RequestCompanionAsync`, `DecideCompanionRequestAsync`, `/Tickets/Admin/Sales/{id}/Companions` | Holder asks, team decides; Admin may override |
| Team tools | `RefundAsync`, `RenameTicketAsync`, `VoidTicketAsync`, `RestoreTicketAsync`, `CancelSalesEventAsync`, `/Tickets/Admin/Sales/Lookup`, `/Tickets/Admin/Sales/{id}/Report` | Every mutation writes the order timeline; admin actions on members audit |
| Delivery | `ITicketDelivery`, `TicketDeliveryService`, `TicketCodeImages` | Ticket mail with code image, resale and companion notices, keyed by delivery id |
| Reconciliation | `ReconcileBackendAsync`, `ReconcilePaymentsAsync`, `TicketReconBuilder`, `TicketPaymentReconBuilder`, `TicketReconJob`, `/Tickets/Admin/Sales/Recon` | Ours vs backend and ours vs card payments, per period, with drill-down |
| Gate | `ITicketGateLookup` (`FindIssuedTicketAsync`, `GetIssuedTicketsForEventAsync`), `ITicketWrite` | Looks a code up; the Gate section owns the scan |
| Member holdings | `<vc:my-ticket-stubs>`, `<vc:ticket-holdings>`, `<vc:ticket-stub>`, `<vc:member-ticket-status>`, `<vc:guest-ticket-orders>` | One stub renderer reused by homepage and profile |
| Vendor reporting | `/Tickets`, `/Tickets/Orders`, `/Tickets/Attendees`, `/Tickets/Codes`, `/Tickets/SalesAggregates`, `/Tickets/WhoHasntBought`, `/Tickets/Export/*` | Paged lists, aggregates and CSVs over the mirror |
| Onsite and gate tooling | `/Tickets/Admin/Onsite`, `/Tickets/Admin/Gate` | One roster join, one credential rotation |
| Contact import | `/Tickets/Admin/Contacts` | Plan/apply over unmatched attendees |
| Event participation | `EventParticipationService`, `/Tickets/DeclareNotAttending`, `/Tickets/UndoNotAttending`, `<vc:onsite-chip>`, `/Tickets/Participation/Backfill` | One record per user and year |
| Vendor access | `ITicketDiscountCodes`, `ITicketVendorMirror`, `ITicketVendorService` | Discount mapping stays at the boundary |
| GDPR | `IUserDataContributor` on `TicketQueryService`, `EventParticipationService` and `TicketCheckoutService` | Export plus tombstone erasure; open offers and companion requests are withdrawn |
| Caches | `CachingTicketQueryService`, `CachingTicketVendorService` | Two decorators, each evicting on its own writes |
| Health | `TicketVendorHealthCheck` | Vendor reachability probe |

## Structure

- Two vendor ports, each implemented outside the section: `ITicketVendorService` (the mirror, by
  `Humans.TicketTailor`) and `ITicketCheckoutBackend` (selling, by the BoxOffice, Pretix and
  HiEvents sections, one keyed implementation each). The section never names a backend's own
  vocabulary.
- One checkout service, split by lane into partial files (buy and refresh, tickets and resale,
  companions, team tools, admin, gate, recon), over one checkout repository and the new checkout
  tables. Pure builders turn loaded rows into reconciliation views.
- One sync service and one repository for the vendor mirror; one transfer repository over the
  retired `ticket_transfer_requests`.
- One query service behind `ITicketManagement` and `ITicketService`, wrapped once by a caching
  decorator that owns invalidation.
- Controllers per audience: buyer, code page, vendor dashboard and exports, sales admin, support
  admin, backends, contacts, gate credential, onsite. Each translates and formats only.
- Member-facing views localized through `TicketsResource`; admin views unlocalized.

## Invariants

- Buyer-only matches never count as holding a vendor ticket: ownership is the attendee's
  `MatchedUserId` (`Services/TicketAttendeeOwnership.cs:12`) and `HasEventTicketAsync` reads
  attendee rows only (`Data/TicketRepository.cs:408`).
- Sync is cursor-driven: success moves `LastSyncAt` (`Services/TicketSyncService.cs:137`), a
  transient vendor error restores the previous cursor (`Services/TicketSyncService.cs:159`), any
  other failure records `Error` (`Services/TicketSyncService.cs:192`); an unconfigured vendor is a
  no-op (`Services/TicketSyncService.cs:35`).
- Sync never downgrades `Attended` (`Services/TicketSyncService.cs:493`) and removes a sync-sourced
  `Ticketed` row once no valid ticket remains (`Services/TicketSyncService.cs:512`).
- An unconfigured vendor short-circuits the dashboard (`Models/TicketDashboardPageBuilder.cs:17`),
  the member status card (`ViewComponents/MemberTicketStatusViewComponent.cs:44`), the health check
  (`Health/TicketVendorHealthCheck.cs:19`) and the sync job (`Jobs/TicketSyncJob.cs:32`).
- Vendor erasure tombstones name and email in place and a later sync never writes them back
  (`Data/TicketRepository.cs:197`).
- Board reads the reporting and sales pages (`Controllers/TicketController.cs:19`,
  `Controllers/TicketsSalesAdminController.cs:20`) but changes nothing: sync, exports, sales
  setup, refunds and every support mutation need Manage (`Controllers/TicketController.cs:174`,
  `Controllers/TicketsSalesAdminController.cs:55`, `Controllers/TicketsSalesAdminController.cs:94`,
  `Controllers/TicketsSupportAdminController.cs:212`).
- Overrides, restoring a voided ticket and cancelling an event are Admin only
  (`Controllers/TicketsSupportAdminController.cs:101`, `Controllers/TicketsSupportAdminController.cs:228`,
  `Controllers/TicketsSupportAdminController.cs:235`, `Controllers/TicketsSupportAdminController.cs:243`);
  so are full re-sync and the donor list (`Controllers/TicketController.cs:184`,
  `Controllers/TicketController.cs:349`), and every donor-list download audits
  (`Services/TicketQueryService.cs:179`).
- A member buying for themselves holds at most one adult ticket per sales event
  (`Services/Checkout/TicketCheckoutService.cs:295`, `Services/Checkout/TicketCheckoutService.cs:1164`).
- A paid order issues its tickets exactly once (`Services/Checkout/TicketCheckoutService.Tickets.cs:285`).
- A refund needs a paid order and never exceeds what its tickets can still be refunded, chargebacks
  included (`Services/Checkout/TicketCheckoutService.cs:785`,
  `Services/Checkout/TicketCheckoutService.cs:787`); the refund row is saved before the backend is
  called.
- A resale refund is paid only by the evening batch, only after the holding period, only while the
  buyer's order is paid with nothing refunded or charged back, and never for a cancelled event
  (`Services/Checkout/TicketCheckoutService.Tickets.cs:519`,
  `Services/Checkout/TicketCheckoutService.Tickets.cs:522`,
  `Services/Checkout/TicketCheckoutService.Tickets.cs:551`).
- An unsold-resale notice ("no refund, your ticket still admits you") goes only for an event that
  closed, never for a cancelled one (`Services/Checkout/TicketCheckoutService.Tickets.cs:672`).
- A cancelled event voids every live ticket, withdraws its offers and refunds each paid order once,
  waiting while a buyer's checkout holds a returned ticket
  (`Services/Checkout/TicketCheckoutService.TeamTools.cs:118`).
- An adult ticket carries at most one valid companion and one pending request
  (`Services/Checkout/TicketCheckoutService.Companions.cs:140`).
- Erasure withdraws the member's open and reserved offers and pending companion requests
  (`Services/Checkout/TicketCheckoutService.cs:1039`).
- A reconciliation is balanced only when counts and totals match on both sides and no row that
  moved money has an issue (`Services/Checkout/TicketRecon.cs:110`).

## Seams

- **`VendorStepsJson`** is dormant until prod soak; the drop is a scheduled follow-up.
- **Manual bank transfer** as a payment backend is specified but not built.
- **Low income** is a separate section; ticketing holds only a placeholder for it.

## Deliberately not done

- No guest checkout: a signed-out buyer gets a sign-in link first.
- No signing or consent step anywhere in buying, holding or admitting a ticket.
- No backend keys, secrets or instance URLs editable in Humans; they stay in server config.
- No concurrency tokens on checkout rows; repeats are made safe by ids (delivery ids, the
  cancellation refund id) instead.
- No read-through cache on the vendor dashboard stats.
- Paging stays on the orders, attendees and who-hasn't-bought lists, the one place the dataset is
  large enough for it to buy something.
- No volunteer-coverage figure on the dashboard; the who-hasn't-bought list is the operational answer.
- No local ticket issuance through the vendor mirror port.

## Load-bearing weirdness

- **Email matching uses `NormalizingEmailComparer`** so aliases and casing collide; raw matching
  would split one person into two.
- **The vendor ticket count falls back to verified emails** while `MatchedUserId` is unset, through
  the same `VerifiedEmailLookup` the sync matches with, so the two agree. `HasEventTicketAsync` has no
  such fallback, so `HasCurrentEventTicket` waits for the next sync.
- **The caching decorators are Singletons wrapping Scoped or keyed inners**, because `TrackedCache`
  slices must outlive a request.
- **`TicketCheckoutService` is one class in many partial files**: the lanes share the order
  refresh, the timeline and the refund core, and splitting them into services would put those
  behind new interfaces.
- **The reconcile sweep does everything in one pass with a time budget on cancellation**, because
  one job on one server is the whole concurrency story; each step is safe to repeat.
- **Resale refunds wait for the evening batch** so a buyer's chargeback or refund within the holding
  period holds the seller's refund back instead of paying twice.
- **`TicketRepository` and `TicketTransferRepository` are Singletons** over a context factory while the
  services are Scoped.
- **The Scanner gate card builds `TicketStubInfo` itself** to show the scanned attendee's Early Entry,
  not the operator's; every holder-facing surface goes through `TicketStubInfo.From`.
- **The gate-terminal account is a real user with no roles** and a rotating password.
- **Contact import re-queries at apply time**, because a sync can land between preview and apply.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-09-05 | First doctoring: invariant doc rebuilt against the code, narration purged, dead resx keys cut, sync-cursor test pinned | peterdrier/Humans#1589 |
| section-doctor | 2026-10-05 | Dashboard computes and docs describe only what `/Tickets` renders; view model renders the service DTOs | peterdrier/Humans#1905 |
| section-doctor | 2026-11-21 | Target covers Humans' own ticket sales; a cancelled event no longer tells resale sellers there is no refund | pending |
