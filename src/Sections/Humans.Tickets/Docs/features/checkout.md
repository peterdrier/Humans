# Ticket checkout (2027)

Design: the "Ticketing 2027 design" doc. Humans decides who may buy which ticket at what price and owns the order and
the named ticket; whatever sits behind Humans only takes the payment and refunds it.

## The port

`ITicketCheckoutBackend` in `Humans.Tickets.Contracts`: `Key`, `IsConfigured`, `CreateCheckoutAsync` (lines with exact
amounts and VAT, buyer prefilled, expiry, return URL; returns the backend order id and payment URL), `GetStatusAsync`
(state, fee, method, paid at, refunded total), `RefundAsync` (exact amount, `RefundId` idempotent; may answer
`ManualRequired` with a dashboard link), `CancelAsync`, `ErasePersonalDataAsync` (GDPR: one person's name and email off
one order), `Setup` (display name, hosting, its server settings with secrets only as set or missing, webhook path,
setup notes) and `CheckAsync` (read-only connection test, optionally for one backend event; never throws, answers
findings). Adapters call `ITicketCheckoutEvents.CheckoutChangedAsync` from
their webhook; Tickets then re-reads the status. Each backend is its own section: `Humans.BoxOffice` (`boxoffice`,
Stripe hosted Checkout), `Humans.Pretix`, `Humans.HiEvents`.

Rules every backend follows:

- **Exact price.** The buyer pays exactly the lines; no payment surcharge. `CheckoutStatus.FeeMinor` is only what the
  payment provider kept (processing cost), null when unknown.
- **Late payments.** Tickets owns the hold. When `ExpiresAt` passes unpaid, Tickets calls `CancelAsync` (also when the
  backend reports Expired or Failed, and the order stays open until that cancel succeeds) so the backend stops taking
  money. A payment that lands anyway is reported as
  `Paid`; the adapter never refunds or keeps it silently. Tickets issues the tickets, says so on the timeline and in the
  audit log ("after the order was Expired") and logs a warning; a released resale offer another checkout took stays
  with that checkout, so the late ticket is fresh stock and may put the event one over capacity for the team to settle.
- **Stuck checkout.** While a buyer's own unpaid checkout still holds their ticket, the buy page links to it and offers
  "cancel and start again", so a dropped connection never locks them out until `ExpiresAt`. Starting a checkout runs to
  the end even if the buyer's request is aborted, so a half-made order always has its backend session.
- **Payment on its way.** A backend reports `Processing` once the buyer finished paying with a method that settles
  later (a SEPA debit, a bank transfer, sometimes the next day). The order then holds its tickets past `ExpiresAt`, is
  never cancelled by Tickets or the buyer (the buy page offers no "start again" for it), and ends Paid or Failed. One
  still on its way 14 days after it was created goes to the audit log as needing attention, once.
- **Chargebacks.** `DisputedMinor` is what the buyer's bank has taken back and the event has not won. Each change goes on
  the timeline and to the audit log as needing attention; tickets stay valid until the ticketing team decides. It comes
  off what can still be refunded, and the order's tickets cannot be offered for resale while it stands.
- **Refund outcome.** `RefundedMinor` moves only once money went back. On every status read Tickets settles its
  `Pending` and `ManualRequired` refunds, oldest first. A pending refund the backend knows by id is repeated under that
  id and takes the state the backend answers (`RefundFailed` may follow only the newest refund); one whose call got no
  answer waits for the reconcile job's resend. So a refund made in the backend's dashboard never settles one of Tickets'
  own. Only `ManualRequired` refunds settle from the part of `RefundedMinor` that Tickets' succeeded refunds do not explain. Failed refunds stop counting against what can still be refunded. The reconcile job re-reads paid orders with
  unsettled refunds and resends any refund whose backend call threw.
- **Erasure.** A member's deletion calls `ErasePersonalDataAsync` for each of their orders before Tickets tombstones its
  own copy; a backend failure throws, so the deletion retries with the emails still known. Box Office is a no-op: Stripe
  keeps payment records as processor. An order on a backend no longer registered is logged and audited and skipped, so
  it never blocks a deletion: a backend's data is exported or erased before its adapter is removed. Tickets then voids
  the member's live tickets and their children's, withdraws their offers, tombstones their names and emails (children's
  names included) clears any resale invitation sent to one of their addresses, and tombstones partner lines and tickets without an
  account that carry one of those addresses (the account's primary address and those on the member's own orders; such
  a ticket is tombstoned but left valid, and one issued to another of their addresses is missed). A partner on an order that ended unpaid is forgotten 30 days after its
  hold lapsed, once a late payment can no longer arrive.

## Flow

1. A TicketsAdmin sets up the event at `/Tickets/Admin/Sales`: backend, mode, capacity, waves, supporter totals (VIP
   buttons), grant price rules (`ClassPrice`, `Credit`, `WavePin`), child price and age, the ticket rules page
   (an https address; a changed version gets a new address), when resale opens (tickets sold), how many days a resale invitation lasts
   and the ticketing switches (below).
2. A member opens `/Tickets/Buy` (linked from the dashboard ticket card while any event sells through Humans), which
   lists every event that is not Closed, each priced in its own time zone and checked for holdings on its own. They see
   their price, may add children (child price, issued on their own ticket), a partner (name and email; public price;
   the account is found or made once the order is paid), a VIP upgrade (shown as the ticket plus a donation) and a donation.
   They tick the ticket rules on every checkout (the page's address goes on the order's timeline; no Consent signing) and may opt in to the mailing list
   (asked only while they have no Marketing choice on record), then continue to the backend's payment page.
3. The backend returns them to `/Tickets/Buy/Order/{id}`, which refreshes the status and splits ticket and donation
   subtotals. The webhook and the 5-minute reconcile job catch the rest. The move to `Paid` is one conditional update,
   so of several callers seeing the same payment only one fulfils it. Tickets then resolves each partner's account (a
   verified address attaches to that account; one unverified row is replaced by a new account as the attendee import
   does; an address on several accounts leaves the ticket unattached and says so on the timeline; a partner who already
   holds a ticket is flagged for the team, on the timeline and as a `TicketCheckoutNeedsAttention` audit entry), issues one `ticket_checkout_tickets` row per ticket line (unique per order
   and line: barcode, holder, price paid, wave or class, children pointing at their adult) and tells every
   `ITicketDelivery` (the ticket email with QR, the receipt and the volunteer invitation: [ticket-delivery.md](ticket-delivery.md)). A delivery failure never undoes the sale; delivery steps go
   on the timeline through `RecordDeliveryAsync`. Reconcile hands an order to delivery again when it was paid 30 minutes to 7 days ago,
   has tickets and has no emailed or skipped entry; delivery dedupes the repeat. The reconcile job finishes any paid order a crash left without
   tickets or without its resale refund.
4. A TicketsAdmin refunds from the order page: only paid orders, never above the ticket lines net of earlier refunds
   (pending and manual ones count until they settle). A refund call that throws stays `Pending`, since the money may
   have gone, with no backend id; that is how reconcile knows to resend it, so an adapter must always name a refund it
   accepted. A refund never voids a ticket; voiding is the admin's separate step.

Each order carries a short `Reference` (the first 8 hex digits of its id) shown to the buyer and the team.

## Ticketing switches

Each sales setup carries switches so an organisation that runs Humans differently can turn a scheme off; the
defaults are the Nobodies scheme, and a setup saved before the switches existed reads as the defaults. The checkout
service enforces each one, so a switched-off scheme is refused there, not only hidden on the page. Saving them is
part of the audited sales setup save.

| Switch | Values | Off means |
|---|---|---|
| `Resale` | `Off`, `Queue`, `QueueAndDirected` (default) | `Off`: holders cannot return a ticket, the queue never sells, `ResaleOnly` mode is refused, and saving the switch withdraws every open offer (the ticket is the holder's again, on the order timeline; one in a checkout is withdrawn if that checkout fails). `Queue`: no directed invitations; saving the switch ends live ones (on the order timeline) and those offers sell from their place in the queue. |
| `PartnerTickets` | on (default) / off | No partner ticket on the buy page; a partner line is refused. |
| `Donations` | on (default) / off | No VIP buttons and no free donation; donation lines are refused. |
| `MailingListOptIn` | on (default) / off | Checkout never asks about the mailing list. |

## Signed-out buyers

`/Tickets/Buy/Public` (where a signed-out `/Tickets/Buy` lands) shows each selling event at today's public price and
asks for an email. Auth's magic link signs an existing member in, or starts signup (legal name, consents) for a new
one, and returns them to the buy page, which then prefills them and shows their own price. Tickets are named, so the account always comes before the order.

## Returning a ticket and the resale queue

- `/Tickets/Buy/Mine` lists the member's live tickets with the valid child tickets under their adult. Its GET is
  open before the account is active, so a partner reaches their ticket from the email (name entry still comes first). A holder offers an adult ticket back while the event is not
  Closed; it joins `ticket_resale_offers` (oldest first) and the gate refuses it while it can still sell (the holder withdraws it to get in; once sales close it is valid again). They see their
  queue position and can withdraw, except while a buyer's checkout holds it.
- Resale opens once the event has sold `ResaleOpensAtSold` tickets (always, when unset). A buyer's ticket lines take
  the queue head before fresh stock; in `ResaleOnly` mode only queue tickets sell. A returned ticket sells only at the
  resale price: the current public wave less the buyer's credits (the association keeps any difference). A buyer with
  a class price or a pinned wave is served from fresh stock while the event is `Open`, and pays the resale price for a
  queue ticket in `ResaleOnly` mode or through an invitation.
- When the buyer's order is paid, the offer is sold, the old ticket becomes `Resold`, the old adult's children are
  voided, the buyer gets a new ticket, and the holder is refunded what they paid (adult plus children) through the
  original order's backend, under the offer's id so a repeat finds the same refund and a call that threw is retried.
  A refund that cannot even start is recorded as a `Failed` refund on the holder's order for the team to pay by hand;
  the timeline of both orders records it.
- While fresh stock is sold out, a class-price buyer in `Open` mode takes a queue ticket at the resale price.
- A checkout that fails, expires or is cancelled releases its offers to their original place.
- **Directed resale:** a holder whose offer is open emails one person a single-use link
  (`/Tickets/Buy/Resale/{token}`, only the SHA-256 of the token is stored). Only a signed-in buyer with that email can
  use it, for themselves, and only while resale is open. The offer skips the public queue until the link lapses after
  `DirectedResaleDays` (then it is back at its place) or the holder cancels it. Invitation, use, lapse and cancel are on
  the holder's order timeline. One invitation is live at a time, and a ticket gets at most three over all its offers; the invited email is
  cleared once the invitation ends.

## The ticketing team

`/Tickets/Admin/Sales/Lookup` finds orders across events. Per event, the sales index links the refund list, the resale
queue (directed offers marked with the invited email and expiry) and the sales report. Each issued ticket has an admin
page, reached from its order, with the three exceptions: rename (full price adults only), void (no refund; refund from
the order), and a free companion ticket.

Every step lands in the order's append-only timeline (`ticket_checkout_order_events`) with who did it, shown on the
admin order page beside a link to the order in the backend's own admin (`CheckoutSession.AdminUrl`).

## At the gate

Gate looks codes up through `ITicketGateLookup.FindIssuedTicketAsync` (exact code, any event; Gate keeps only the
active event's). `Valid` scans valid; `Offered` scans valid only once sales are closed, so a ticket can never admit its holder and then sell. A ticket the gate admitted (or whose child ticket it admitted) cannot be offered, and an admitted offer is never sold (`IGateAdmissions`, Gate's read). A child or companion ticket without an account carries the adult's
user as `EntryUserId`, whose Early Entry applies. Nobody signs the ticket rules at the gate: the buyer's acceptance
covers every ticket on the order (Peter, 2026-11-21). The gate's scan row is the only check-in record; no backend is told.

Partners do not sign the ticket rules: the buyer accepts for the whole order (decided by Peter, 2026-11-21).

## Assumptions to confirm

- Only adult tickets can be offered; children go with the adult and are refunded with it.

## Not yet

- Ticket email with QR and the post-purchase
  shifts email (delivery thread, through `ITicketDelivery`).
- Checkout tickets in holdings, the gate list and the Scanner lookup. (Gate admits them: `ITicketGateLookup`.)
- Chargebacks and disputes: a dispute is read only as `RefundedMinor` and nothing voids the ticket yet.
- Payment-method-dependent prices.
- Check-in report: the gate's per-event check-in is not built and Gate has no read Tickets may call.
- Moving a ticket to another person moves its order line, so the order is no longer the previous holder's and erasing them
  never asks the backend to erase its copy of that order (pretix, Hi.Events; Box Office keeps none).
- A renamed ticket is not emailed to its new holder (`ITicketDelivery` is per order, sent once); they see it in their profile.
