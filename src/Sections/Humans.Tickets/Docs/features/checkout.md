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

1. A TicketsAdmin sets up the event at `/Tickets/Admin/Sales`: backend, mode, waves, supporter totals, grant price rules.
2. A member opens `/Tickets/Buy` (linked from the dashboard ticket card while any event sells through Humans), which
   lists every event that is not Closed, each priced in its own time zone and checked for holdings on its own. They see
   their price, optionally adds a partner (name and email; Humans finds or creates the partner's account) and a
   donation, and continues to the backend's payment page.
3. The backend returns them to `/Tickets/Buy/Order/{id}`, which refreshes the status. The webhook and the 5-minute
   reconcile job catch the rest. On `Paid` each ticket line gets a barcode.
4. A TicketsAdmin refunds from the order page: only paid orders, never above the ticket lines net of earlier refunds.

The ticketing team finds orders at `/Tickets/Admin/Sales/Lookup` and works each event's refund list and sales report
from the sales index.

Every step lands in the order's append-only timeline (`ticket_checkout_order_events`) with who did it, shown on the
admin order page beside a link to the order in the backend's own admin (`CheckoutSession.AdminUrl`).

## Not yet

- Resale queue and the `ResaleOnly` mode's resale sales (the mode only stops fresh sales today).
- Public (signed-out) purchase path.
- Low income wave pin (the price rule takes a pricing date, so a pin plugs in there).
- Checkout tickets in holdings, the gate list and the scanner: they live in their own table until a backend is chosen.
- Dependents (child, companion) and payment-method-dependent prices.
- Check-in report: the gate's per-event check-in is not built and Gate has no read Tickets may call.
