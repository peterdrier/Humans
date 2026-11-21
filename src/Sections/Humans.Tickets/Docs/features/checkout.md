# Ticket checkout (2027)

Design: the "Ticketing 2027 design" doc. Humans decides who may buy which ticket at what price and owns the order and
the named ticket; whatever sits behind Humans only takes the payment and refunds it.

## The port

`ITicketCheckoutBackend` in `Humans.Tickets.Contracts`: `Key`, `IsConfigured`, `CreateCheckoutAsync` (lines with exact
amounts and VAT, buyer prefilled, expiry, return URL; returns the backend order id and payment URL), `GetStatusAsync`
(state, fee, method, paid at, refunded total), `RefundAsync` (exact amount, `RefundId` idempotent; may answer
`ManualRequired` with a dashboard link), `CancelAsync`. Adapters call `ITicketCheckoutEvents.CheckoutChangedAsync` from
their webhook; Tickets then re-reads the status. Each backend is its own section: `Humans.BoxOffice` (`boxoffice`,
Stripe hosted Checkout), `Humans.Pretix`, `Humans.HiEvents`.

## Flow

1. A TicketsAdmin sets up the event at `/Tickets/Admin/Sales`: backend, mode, capacity, waves, supporter totals (VIP
   buttons), grant price rules (`ClassPrice`, `Credit`, `WavePin`), child price and age, the team whose required
   documents are the ticket rules, when resale opens (tickets sold) and how many days a resale invitation lasts.
2. A member opens `/Tickets/Buy` (linked from the dashboard ticket card while any event sells through Humans), which
   lists every event that is not Closed, each priced in its own time zone and checked for holdings on its own. They see
   their price, may add children (child price, issued on their own ticket), a partner (name and email; Humans finds or
   creates the partner's account; public price), a VIP upgrade (shown as the ticket plus a donation) and a donation.
   They tick the ticket rules (signed through Consent, IP and user agent recorded) and may opt in to the mailing list
   (asked only while they have no Marketing choice on record), then continue to the backend's payment page.
3. The backend returns them to `/Tickets/Buy/Order/{id}`, which refreshes the status and splits ticket and donation
   subtotals. The webhook and the 5-minute reconcile job catch the rest. On `Paid` Tickets issues one
   `ticket_checkout_tickets` row per ticket line (barcode, holder, price paid, wave or class, children pointing at their
   adult) and tells every `ITicketDelivery` (the ticket email with QR lives there). A delivery failure never undoes the
   sale.
4. A TicketsAdmin refunds from the order page: only paid orders, never above the ticket lines net of earlier refunds.

## Returning a ticket and the resale queue

- `/Tickets/Buy/Mine` lists the member's live tickets. A holder offers an adult ticket back while the event is not
  Closed; it joins `ticket_resale_offers` (oldest first) and stays valid at the gate until it sells. They see their
  queue position and can withdraw, except while a buyer's checkout holds it.
- Resale opens once the event has sold `ResaleOpensAtSold` tickets (always, when unset). A buyer's ticket lines take
  the queue head before fresh stock; in `ResaleOnly` mode only queue tickets sell. The buyer pays their own price
  (class price included), the partner the public price.
- When the buyer's order is paid, the offer is sold, the old ticket becomes `Resold`, the old adult's children are
  voided, the buyer gets a new ticket, and the holder is refunded what they paid (adult plus children) through the
  original order's backend. A failed refund is logged and visible on the order's refunds; the timeline of both orders
  records it.
- A checkout that fails, expires or is cancelled releases its offers to their original place.
- **Directed resale:** a holder whose offer is open emails one person a single-use link
  (`/Tickets/Buy/Resale/{token}`, only the SHA-256 of the token is stored). Only a signed-in buyer with that email can
  use it, for themselves, and only while resale is open. The offer skips the public queue until the link lapses after
  `DirectedResaleDays` (then it is back at its place) or the holder cancels it. Invitation, use, lapse and cancel are on
  the holder's order timeline.

## The ticketing team

`/Tickets/Admin/Sales/Lookup` finds orders across events. Per event, the sales index links the refund list, the resale
queue (directed offers marked with the invited email and expiry) and the sales report. Each issued ticket has an admin
page, reached from its order, with the three exceptions: rename (full price adults only), void (no refund; refund from
the order), and a free companion ticket.

Every step lands in the order's append-only timeline (`ticket_checkout_order_events`) with who did it, shown on the
admin order page beside a link to the order in the backend's own admin (`CheckoutSession.AdminUrl`).

## Assumptions to confirm

- A resale buyer pays their own price, so a low income buyer of a returned full-price ticket costs the association the
  difference.
- Only adult tickets can be offered; children go with the adult and are refunded with it.
- Partners do not sign the ticket rules at purchase; the buyer accepts for the order.

## Not yet

- Public (signed-out) purchase path.
- Ticket email with QR and the post-purchase
  shifts email (delivery thread, through `ITicketDelivery`).
- Checkout tickets in holdings, the gate list and the scanner.
- Payment-method-dependent prices.
- Check-in report: the gate's per-event check-in is not built and Gate has no read Tickets may call.
- A renamed ticket is not emailed to its new holder (`ITicketDelivery` is per order, sent once); they see it in their profile.
