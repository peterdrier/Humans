# Ticket checkout (2027)

Design record: [`checkout-design.md`](checkout-design.md). Humans decides who may buy which ticket at what price and owns the order and
the named ticket; the ticketing platform only takes the payment and refunds it.

## The port

`ITicketingPlatform` in `Humans.Tickets.Contracts`: `Key`, `IsConfigured`, `CreateOrderAsync` (lines with exact
amounts and VAT, buyer prefilled, expiry, return URL; returns the platform order id and payment URL), `GetStatusAsync`
(state, fee, method, paid at, refunded total), `RefundAsync` (exact amount, `RefundId` idempotent; may answer
`ManualRequired` with a dashboard link), `CancelAsync`, `ErasePersonalDataAsync` (GDPR: one person's name and email off
one order), `Setup` (display name, hosting, its server settings with secrets only as set or missing, webhook path,
setup notes), `CheckAsync` (read-only connection test, optionally for one platform event; never throws, answers
findings) and `ListOrdersAsync` (every order the platform holds created in a period, for reconciliation; read-only,
throws rather than answer a partial list) and `GetPaymentLedgerAsync` (the payment account's balance movements and
payouts in a period, for fee and payout reconciliation; null where Humans cannot read the account). Adapters call `ITicketingPlatformEvents.PlatformOrderChangedAsync` from
their webhook; Tickets then re-reads the status. Each platform is its own section: `Humans.BoxOffice` (`boxoffice`,
Stripe hosted Checkout), `Humans.Pretix`, `Humans.HiEvents`.

Rules every platform follows:

- **Exact price.** The buyer pays exactly the lines; no fee is added. `PlatformOrderStatus.FeeMinor` is only what the
  payment provider kept (processing cost), null when unknown.
- **Payment metadata.** A newly reported payment method is saved independently of changes to state, fees, refunds or chargebacks, with the order's update time. A missing method in a later status does not clear the known one.
- **Late payments.** Tickets owns the hold. When `ExpiresAt` passes unpaid, webhooks and background recovery call `CancelAsync` (also when the
  platform reports Expired or Failed, and the order stays open until that cancel succeeds) so the platform stops taking
  money. A payment that lands anyway is reported as
  `Paid`; the adapter never refunds or keeps it silently. Tickets issues the tickets, says so on the timeline and in the
  audit log ("after the order was Expired") and logs a warning; a released resale offer another order took stays
  with that order, so the late ticket is fresh stock and may put the event one over capacity for the team to settle.
- **Stuck order.** While a buyer's own unpaid order still holds their ticket, the buy page links to it and offers
  "cancel and start again", so a dropped connection never locks them out until `ExpiresAt`. Starting checkout runs to
  the end even if the buyer's request is aborted, so a half-made order always has its platform session.
- **Payment on its way.** A platform reports `Processing` once the buyer finished paying with a method that settles
  later (a SEPA debit, a bank transfer, sometimes the next day). The order then holds its tickets past `ExpiresAt`, is
  never cancelled by Tickets or the buyer (the buy page offers no "start again" for it), and ends Paid or Failed. One
  still on its way 14 days after it was created goes to the audit log as needing attention, once.
- **Chargebacks.** `ChargedBackMinor` is what the buyer's bank has taken back and the event has not won. Each change goes on
  the timeline and to the audit log as needing attention; tickets stay valid until the ticketing team decides. It comes
  off what can still be refunded, and the order's tickets cannot be offered for resale while it stands.
- **Recovery cancellation.** A status read remains cancellable. Before closing a platform order, recovery checks cancellation; once that remote attempt starts, its local outcome finishes independently of job shutdown.
- **Refund cancellation.** Cancellation is checked before each attempt. A started platform refund and its local completion finish independently of request abort or job shutdown. The initial read-only status fetch stays cancellable; settlement replays finish their saved outcome and timeline before cancellation stops the next refund.
- **Order page reads.** Buyer/holder and admin GETs read status with the request token and apply local payment, fee, refund-total and chargeback changes, including ticket issuance for a new payment. They never cancel at the platform or replay refunds. An expired/failed status keeps an open order reserved until recovery confirms cancellation; visiting an overdue order cannot release capacity while the platform may still take payment.
- **Refund outcome.** `RefundedMinor` moves only once money went back. Webhooks and background recovery settle Tickets'
  `Pending` and `ManualRequired` refunds, oldest first. A pending refund the platform knows by id is repeated under that
  id and takes the state the platform answers (`RefundFailed` may follow only the newest refund); one whose call got no
  answer waits for the reconcile job's resend. So a refund made in the platform's dashboard never settles one of Tickets'
  own. Only `ManualRequired` refunds settle from the part of `RefundedMinor` that Tickets' succeeded refunds do not explain. Failed refunds stop counting against what can still be refunded. The reconcile job re-reads paid orders with
  unsettled refunds and resends any refund whose platform call threw.
- **Erasure.** A deletion waits while the member holds a valid or offered ticket, until 30 days after that event's strike
  (Users re-checks when the deletion falls due), so erasure normally runs after the event. It then calls `ErasePersonalDataAsync` for each of their orders before Tickets tombstones its
  own copy; a platform failure throws, so the deletion retries with the emails still known. Box Office is a no-op: Stripe
  keeps payment records as processor. An order on a platform no longer registered is logged and audited and skipped, so
  it never blocks a deletion: a platform's data is exported or erased before its adapter is removed. Tickets then voids
  the member's live tickets and their children's, withdraws their offers, tombstones their names and emails (children's
  names included) clears any resale invitation sent to one of their addresses, and tombstones lines and tickets without an
  account that carry one of those addresses (the account's primary address and those on the member's own orders; such
  a ticket is tombstoned but left valid, and one issued to another of their addresses is missed). Someone bought for is
  always an existing account, so their line is erased with that account.

## Reconciliation

A standing rule (Peter, 2026-11-21): anything financial is checked against the other side. For one platform and a
period, Humans' orders on it are matched with the platform's own list (`ListOrdersAsync`) by the Humans order id the
platform stores (Stripe session metadata, pretix `api_meta`, Hi.Events `external_reference`). Each side's totals count
paid orders only: orders, tickets, charged, refunded, chargebacks and net. Humans also counts the tickets it issued for
those orders. The period is by the day the order was created; a pair belongs to its Humans order's day and the platform
is read an hour wider on both sides, so an order made at midnight is not split. A free order (nothing to charge) never
reaches a platform, so it stays out of both sides' totals and only its tickets are checked.

Per order it names what differs: missing at the platform, extra at the platform (no Humans order, or one on another
platform), a duplicate platform order for one Humans order, a platform order Humans never recorded (a create whose answer
was lost, or one listed in place of the recorded one, which then reads missing), paid on one side only, total, refunded,
chargeback or ticket count, and a paid order whose tickets were not all issued. A difference where no money moved on
either side (an open order, an expired leftover) is listed but does not unbalance the platform. A platform that cannot
be read gives an error, never a partial comparison.

`/Tickets/Admin/Sales/Recon` shows it for any date range (default the last 30 days, Europe/Madrid days), differences
only or every order, each row linking to the Humans order and the platform's admin. It is open to Read, Board included:
checking the money is oversight, and it changes nothing. `TicketReconJob` runs daily at 04:30 over the whole sale (from
the first event's ticketing settings to the start of today), so a late refund on an old order is caught too. It logs an error per
check out of balance or unreadable, an information line per balanced one, and goes on to the next platform when one
fails. When any check is out of balance or could not run, it queues one email to the ticketing team's mailbox
(`tickets@`) listing each finding, with a link to the page for that range; the mail repeats every night until the
reconciliation balances. A platform stays in use while it holds orders, whatever any ticketing settings point at now.

Outside production, QA, previews and dev share the platforms' test accounts, so each sees the others' orders and
payments. There, a platform order or a payment movement that names a Humans order this database never held (on any
platform) is another instance's: it is shown apart, collapsed, and is never a difference, never in the totals of the
order check, never unbalances and is never mailed. A payment movement still counts in the account's totals, since it
moved through the account. In production nothing is set apart, and an entry naming no Humans order at all is a
difference everywhere.

### Fees and payouts

Stripe account ledger reads require explicit EUR on every amount they return or sum. Unknown or other currencies reject the ledger; fee lookups leave the fee unknown, so foreign minor units never enter EUR reconciliation totals.

Where Humans can read the platform's payment account (the shared Tickets Stripe account through the Stripe section's
`Stripe:MirrorAccounts` entry `Tickets`), the page and the daily run
also reconcile it by the day the money moved (`GetPaymentLedgerAsync`), so a refund months after the sale counts in the
month it was made. Every balance movement is tied to its Humans order: Box Office's payments carry it in metadata; pretix's and Hi.Events'
carry the platform's own order, whose Humans order the adapter reads back from the platform. A payment must
name a paid (or settling) order on that platform, for the order's total, with the fee Humans recorded on the order (a
failed delayed debit whose payment came back, in the period or any time after it, may be Failed); a refund or chargeback must name an order; a payout must
send exactly the net of the movements Stripe says it contains. A failed payout is shown but does not unbalance: Stripe
returns the money and pays it out again, and it would otherwise hold the nightly alert forever. Totals show charged, refunded, chargebacks, fees, net into the balance and paid out to the
bank, and the fees on order payments Humans against Stripe. Fees not on a payment (Stripe's own billing, a chargeback
fee) name no order: they are listed and counted, not a difference. A manual payout's contents cannot be listed, nor an
automatic one's until Stripe has finished reconciling it, so only its status is checked. pretix and Hi.Events record the fee on a paid order from
Stripe as Box Office does, so their fees reconcile the same way; until a platform's key is set its ledger is null and the
platforms page Test says so.

Per platform: Box Office lists every Checkout Session on the ticket Stripe account (it has no events) and reads the ticket
count from the session's `humans_ticket_count` metadata, since free lines never reach Stripe; a session without it
counts as Humans' number. pretix lists each event's orders and counts positions on the `humans-ticket` item; a paid
order is charged what pretix received, and refunds made outside pretix (`external`) count as refunded, or as a
chargeback when they carry a Stripe dispute, exactly as the order's status reads them. Hi.Events lists
each event's orders and counts attendees; it reports no chargebacks, and its order list leaves out unpaid orders, so an
open order reads missing (no money moved) and a lost create shows only once paid. Every event a platform's orders were
sold under is read (`EventRefOf`), beside the events their ticketing settings sell through now.

## Flow

1. A TicketsAdmin saves the event's ticketing settings at `/Tickets/Admin/Sales`: platform, mode, capacity, waves, VIP
   totals (the VIP ticket buttons), grant price rules (`ClassPrice`, `Discount`, `WavePin`, `EarlyAccess`), child ticket types, donation purposes, the ticket rules folder
   (a path in the legal GitHub repo, see "Ticket rules" below) and the ticketing switches (below). The form edits waves,
   price rules and VIP totals as rows, the VAT as a percentage and sales close in the organisation's time zone.
   Prices, VAT and refund/void amounts use invariant HTML-number syntax: dot decimals or exponent notation, never grouping separators.
   Missing mandatory settings or malformed typed fields return 400 before saving; explicit zero/false values remain valid. Optional text, blank spare rows and disabled wave-pin amounts retain their form semantics.
   **Early sales** (and test purchases): mode `InvitedOnly` sells only to members holding an active grant in a program
   with an `EarlyAccess` rule (e.g. asociados and colaboradores in a wave zero, or a few testers), with the rule's
   amount off while the mode lasts (none when left empty; someone in several early access programs gets the largest
   amount, not the sum). The dashboard ticket card links the invited to the buy page. Everyone else sees nothing on sale; tickets for others and
   resale wait for general sale. Switching to `Open` ends the early amount.
2. A member opens `/Tickets/Buy` (linked from the dashboard ticket card while any event sells through Humans), which
   lists every event that is not Closed (an `InvitedOnly` one only to the invited), each priced in its own time zone and checked for holdings on its own. They see
   their price, may add children (each at the child ticket type they pick, issued on their own ticket), other people already in Humans (see "Buying
   for other people" below), a VIP ticket (shown as the ticket plus a donation) and a donation.
   They read the ticket rules on the page and tick them on every order (the version goes on the order; no Consent signing) and may opt in to the mailing list
   (asked only while they have no Marketing choice on record), then continue to the platform's payment page.
3. The platform returns them to `/Tickets/Buy/Order/{id}`, which refreshes the status and splits ticket and donation
   subtotals. The webhook and the 5-minute reconcile job catch the rest. The move to `Paid` is one conditional update,
   so of several callers seeing the same payment only one fulfils it. Tickets then flags anyone on the order who meanwhile
   came to hold another ticket (a lapsed order that paid late) for the team, on the timeline and as a
   `TicketCheckoutNeedsAttention` audit entry, issues one `ticket_checkout_tickets` row per ticket line (unique per order
   and line: barcode, holder, price paid, wave or class, children pointing at their adult) and tells every
   `ITicketDelivery` (the ticket email with QR, the receipt and the volunteer invitation: [ticket-delivery.md](ticket-delivery.md)). A delivery failure never undoes the sale; delivery steps go
   on the timeline through `RecordDeliveryAsync`. Reconcile hands an order to delivery again when it was paid 30 minutes to 7 days ago,
   has tickets and has no emailed or skipped entry; delivery dedupes the repeat. The reconcile job finishes any paid order a crash left without
   tickets or without its resale refund.
4. A TicketsAdmin refunds from the order page: only paid orders, never above the ticket lines net of earlier refunds
   (pending and manual ones count until they settle). A refund call that throws stays `Pending`, since the money may
   have gone, with no platform id; that is how reconcile knows to resend it, so an adapter must always name a refund it
   accepted. A refund never voids a ticket; voiding is the admin's separate step. Refunds outside resale are kept for
  rare cases such as a death or serious illness just before the event; there is no self-service refund request, and
  resale is the answer for everyone else (Peter, 2026-11-22).

Each order carries a short `Reference` (the first 8 hex digits of its id) shown to the buyer and the team.

## Ticket types per event

What an event sells is its own configuration (A29), so another organisation sets up its scheme without code changes:
waves, VIP totals (the VIP ticket buttons), grant price rules (class prices, discounts, wave pins, early access), and:

- **Child ticket types.** Any number, each a name, an age (under it on the event's first day) and a price; none means
  children are not sold. The buyer picks one per child (with one type, it is picked for them). The child's ticket and
  order line carry the type's name in `WaveName`. Humans stores no birth date, so the type is the buyer's word.
- **Donation purposes.** What a free donation can go to, as the buyer sees it; none means one general donation with
  no choice. A line stores the purpose's name as it read at checkout (null is general, as is the VIP remainder), so
  renaming a purpose later leaves old orders as they were. Reports group by that name.
- **Companion tickets** are a switch (below). A companion ticket is always free.

These names are the organiser's own words and are not translated, like wave names and price rule labels.

## Ticketing switches

Each event's ticketing settings carry switches so an organisation that runs Humans differently can turn a scheme off; the
defaults are the Nobodies scheme, and settings saved before the switches existed read as the defaults. The checkout
service enforces each one, so a switched-off scheme is refused there, not only hidden on the page. Saving them is
part of the audited ticketing settings save.

| Switch | Values | Off means |
|---|---|---|
| `Resale` | `Off`, `Queue`, `QueueAndDirected` (default) | `Off`: holders cannot put a ticket up for resale, the queue never sells, `ResaleOnly` mode is refused, and saving the switch withdraws every open offer (the ticket is the holder's again, on the order timeline; one an unpaid order holds is withdrawn if that order fails). `Queue`: no directed invitations; saving the switch ends live ones (on the order timeline) and those offers sell from their place in the queue. |
| `PartnerTickets` | on (default) / off | No "tickets for other people" on the buy page; other people on an order are refused. |
| `Donations` | on (default) / off | No VIP buttons and no free donation; donation lines are refused. |
| `MailingListOptIn` | on (default) / off | Checkout never asks about the mailing list. |
| `CompanionTickets` | on (default; null reads as on) / off | Holders cannot ask for a companion ticket. Requests already waiting stay in the team's queue to approve or decline. |

## Signed-out buyers

`/Tickets/Buy/Public` (where a signed-out `/Tickets/Buy` lands) shows each selling event at today's public price and
asks for an email. Auth's magic link signs an existing member in, or starts signup (legal name, consents) for a new
one, and returns them to the buy page, which then prefills them and shows their own price. Tickets are named, so the account always comes before the order.

## Buying for other people

A buyer adds up to 10 other people by the email address each has verified in Humans (B9). Knowing that address is the
light check that it is them; nobody gets an account made for them, so someone not yet in Humans must join first. Each
gets an adult ticket in their own legal name, at the public price (resale queue first when it is selling), and their
own ticket email; the buyer pays once. Before anything is saved the checkout refuses, naming the address: one verified on
no live account or on several, the buyer's own, a person without a legal name, and anyone who already holds a ticket,
offers one for resale, or is on another unpaid order. A person shows on the order once however many of their
addresses were typed. Directed resale links buy only the buyer's own ticket.

## Resale and the resale queue

- `/Tickets/Buy/Mine` lists the member's live tickets with the valid child tickets under their adult. Its GET is
  open before the account is active, so someone bought for reaches their ticket from the email (name entry still comes first). A holder puts an adult ticket up for resale while the event is not
  Closed; it joins `ticket_resale_offers` (oldest first) and the gate refuses it while it can still sell (the holder withdraws it to get in; once sales close it is valid again). They see their
  queue position and can withdraw, except while a buyer's unpaid order holds it.
- Resale opens by hand: the ticketing team presses Open resale on the resale queue page (`ResaleOpenedAt`, audited
  `TicketResaleOpenSet`) and can pause it again; `ResaleOnly` mode sells resale whatever the button. A buyer's ticket lines take
  the queue head before fresh stock; in `ResaleOnly` mode only queue tickets sell. A resale offer sells only at the
  resale price: the current public wave less the buyer's discounts (the association keeps any difference). A buyer with
  a class price or a pinned wave is served from fresh stock while the event is `Open`, and pays the resale price for a
  queue ticket in `ResaleOnly` mode or through an invitation.
- Only adult tickets can be offered. Child and companion tickets go with their adult ticket: voided and refunded with it,
  never resold alone (Peter, 2026-11-22).
- When the buyer's order is paid, the offer is sold, the old ticket becomes `Resold`, the old adult's children are
  voided, the buyer gets a new ticket, and the holder is told it sold (`ticket_resale_sold`).
- **Evening refund batch** (`TicketResaleRefundJob`, daily 19:00 UTC, H11): the holder is refunded what they paid
  (adult plus children) through the original order's platform, once the buyer's order has held for 12 hours, under the
  offer's id so a repeat finds the same refund and a call that threw is retried. Both orders are read from their
  platform first, so a refund or chargeback a missed webhook never reported still counts; when a platform cannot be
  read, the refund waits for the next batch. A sale whose buyer order is no longer paid, or has a refund (ours or the
  platform's) or a chargeback, is held back for good and flagged for the team (`TicketCheckoutNeedsAttention`,
  "Resale refund held back"): it is recorded as a `Failed` refund on the holder's order, so no later batch pays it and
  reconciliation lists it among failed refunds until the team settles it. A refund that cannot even start is recorded
  the same way, for the team to pay by hand; the timeline of both orders records it. A cancelled event is
  skipped: its cancellation already refunds the holder's and the buyer's orders in full. Each run
  leaves one `TicketResaleRefundBatch` audit row per event with the count and total started, held back and waiting;
  the batch refunds are reconciled with the platform like every other refund. The resale queue page shows a sold offer
  without its refund as waiting for the batch.
- **Did not sell** (H12): once sales close (mode `Closed` or `SalesCloseAt` passed) on an event that is not cancelled, the reconcile job tells each
  holder whose offer is still open, once (`UnsoldNotifiedAt`, `ResaleUnsold` on the timeline, `ticket_resale_unsold`):
  no refund, and the ticket admits them. It is marked only once the mail is queued, so a failed hand-off is retried
  by the next sweep. The offer stays open in case sales reopen.
- While fresh stock is sold out, a class-price buyer in `Open` mode takes a queue ticket at the resale price.
- An order that fails, expires or is cancelled releases its offers to their original place.
- **Directed resale** (H10): a holder whose offer is open emails one or more people each their own link
  (`/Tickets/Buy/Resale/{token}`, one token per address, only its SHA-256 is stored in the offer's `directed_invites`).
  Only a signed-in buyer with that address can use a link, for themselves, and only while resale is open. The links
  have no expiry and the offer stays in the public queue meanwhile, so whoever buys first gets it; a later invitee's
  link then sells the next ticket in the queue at the resale price, even to a buyer with a class price or pinned wave
  while fresh stock is on sale ("someone else bought it first"); only an empty queue sells fresh stock. A ticket gets at most ten invitations
  over all its offers, each address once. Invitation, use and cancel are on the holder's order timeline; cancel
  clears every invitation, and the switch to `Queue`, a void or erasure clears them too.

## Companion tickets

A holder of a valid adult ticket can ask, on `/Tickets/Buy/Mine`, for a free companion ticket for someone already in
Humans: their email (the same lookup as buying for others: one live verified account with a legal name, not the holder,
not already holding a ticket) and a short note on why. One ask is pending at a time; the holder can withdraw it. The
ticketing team answers from the event's Companions queue: approve rechecks the companion and issues the free ticket to
their account (the usual ticket email; the holder gets the zero receipt), decline needs a note the holder sees on their
ticket. Nobody's account is created and the companion is never mailed an invitation. The ask and the answer land on the
order timeline and the answer is audited.

## The ticketing team

`/Tickets/Admin/Sales/Lookup` finds orders across events. Per event, the sales index links the refund list, the resale
queue (directed offers marked with the invited emails, sold ones waiting for the evening refund batch), the companion requests and the sales report. Each issued ticket has an admin
page, reached from its order, with the three exceptions: rename (full price adults only), void and refund (or void
with no refund, said plainly), and a free companion ticket. The resale queue marks a reserved offer Stuck once its
buyer's order closed or expired, and the team can take an open offer out for its holder.

Overrides (Admin only) repair what the rules miss: restore a voided ticket, move a class priced
ticket, a companion past capacity. Each needs a reason and is audited `TicketOverride`.

Cancelling an event (Admin only, `/Tickets/Admin/Sales/{id}/Cancel`) closes sales and resale for good. The reconcile
job then works the paid orders within a two minute budget per run: withdraw open offers, void live tickets, refund the
ticket lines through the order's platform, timeline `EventCancelled`, email the buyer (donation only orders too; marked
`CancellationEmailedAt`, sent under a fixed delivery id). A repeat is safe: void stays void,
and a refund asked for since the cancellation is not asked again, so one the platform turns down (or that cannot start)
stays failed, flagged `TicketCheckoutNeedsAttention`, for the team to do by hand. Donations are not refunded; the email asks buyers to reply if they want theirs back. When the refund failed, the email
says the team will refund by hand instead of promising it is on its way. An order with
a ticket in resale that a buyer is paying for waits until that settles. The cancel page balances the ticket money: taken =
chargebacks + refunded + in flight + still refundable; the platform side is on the reconciliation page.

Every step lands in the order's append-only timeline (`ticket_checkout_order_events`) with who did it, shown on the
admin order page beside a link to the order in the platform's own admin (`PlatformOrderSession.AdminUrl`).

## Chargebacks (team procedure, draft for Peter)

Chargebacks are rare (one or two a year at 3,000 tickets), so they are handled by hand; nothing in Humans acts on the
tickets by itself (Peter, 2026-11-22).

1. Open the order from the alert and read its timeline: who paid, which tickets, whether any was admitted or resold.
2. Contact the buyer. Most chargebacks are a misunderstanding or a stolen card.
3. If the buyer agrees it was theirs, ask them to withdraw the dispute; nothing changes on the tickets.
4. If the card was stolen or the buyer will not withdraw, void the order's tickets from their admin pages (void with no
   refund) and answer the dispute in Stripe with the order's timeline as evidence.
5. If the dispute is won after a void, an Admin restores the tickets with an override, giving the reason.



Gate looks codes up through `ITicketGateLookup.FindIssuedTicketAsync` (exact code, any event; Gate keeps only the
active event's). `Valid` scans valid; `Offered` scans valid only once sales are closed, so a ticket can never admit its holder and then sell. A ticket the gate admitted (or whose child ticket it admitted) cannot be offered, and an admitted offer is never sold (`IGateAdmissions`, Gate's read). A child or companion ticket without an account carries the adult's
user as `EntryUserId`, whose Early Entry applies. Nobody signs the ticket rules at the gate: the buyer's acceptance
covers every ticket on the order (Peter, 2026-11-21). The gate's scan row is the only admission record; no platform is told.
`GetIssuedTicketsForEventAsync` lists every ticket of one event in any state, for the gate's name search and
arrivals report. The holder sees the date their Early Entry starts on My tickets.

People bought for do not sign the ticket rules: the buyer accepts for the whole order (decided by Peter, 2026-11-21).

## Ticket rules

The rules are markdown in the legal GitHub repo (`GitHubSettings`, the repo the legal pages read), in the folder the
ticketing settings name: `name.md` is Spanish, `name-en.md` English, more languages optional (`name-de.md`). Spanish and
English are required: saving the settings refuses a new folder without both, or with a second markdown file lacking a language
suffix (a README beside the rules), since that one would read as Spanish. Display only (Peter, 2026-11-21): nothing is signed
and nothing goes to Consent.

- The version is the last commit changing the folder; the text is read at that commit, so the text shown and the version
  recorded always match. Reads are cached for 10 minutes; GitHub requests time out after 5 seconds.
- When GitHub cannot be read, the rules are unavailable; an older version is never substituted. Failures and invalid
  folders are cached for one minute before retrying. The settings' save check reads GitHub afresh, so a fixed folder is
  accepted at once.
- The buy page shows the rules in language tabs and sends the folder and version it showed. Checkout refuses either one no
  longer current (`Tickets_Buy_TermsChanged`), so the buyer reads the new text first. While there are no rules to show
  (unreadable or invalid), the event is unavailable to buy (`Tickets_Buy_TermsUnavailable`).
- The order keeps `TermsFolder` and `TermsVersion`; its `TermsAccepted` timeline entry links that exact version on GitHub.

## Not yet

- Humans-issued tickets in holdings, the gate list and the Scanner lookup. (Gate admits them: `ITicketGateLookup`.)
- Telling the team about a chargeback: an email to the ticketing team when one opens, with a link to the order, and a
  daily reminder while it stays open. Today it is only recorded (`ChargedBackMinor`) and flagged on the order and in the
  audit log. Tickets are never voided automatically on a chargeback (Peter, 2026-11-22); see "Chargebacks" above.
- Fee transparency: one price for every payment method, but the buy page shows what each method costs the association
  for this order (a range for cards, iDEAL, Klarna; rates per method in the ticketing settings) and asks buyers to pick the
  cheapest that works for them, on every platform. Once Stripe reports the fee, the order page shows what it really
  cost, with a thank-you for a cheap method (Peter, 2026-11-22).
- Telling the holder who bought their resold ticket: only if the buyer opts in at checkout (off by default), and then by
  burner name, never legal name (Peter, 2026-11-22).
- Participation: a ticket issued here does not yet mark its holder Ticketed for the event year, so 2027 buyers would read
  as not having bought on "Who Hasn't Bought", shift planning and mailing cohorts.
- Moving a paid child ticket to another adult: refund and reissue for now; a reattach action only if a real case needs it.
- Payment-method-dependent prices: not planned. One price for every method until the lawyer says a surcharge is allowed.
- Check-in report: the gate's per-event admission list is not built and Gate has no read Tickets may call.
- Renaming a ticket to another person moves its order line, so the order is no longer the previous holder's and erasing them
  never asks the platform to erase its copy of that order (pretix, Hi.Events; Box Office keeps none).
- Matching a payout to the bank statement.
- Reconciliation follow-ups: signing off a difference (an unresolved one is mailed every night); a platform admin link on extra rows; currency on platform rows (all are summed as EUR cents); comparing
  refunds with Humans' own refund records rather than the platform's copy.
