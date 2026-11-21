---
name: Financial reconciliation — every money flow is checked against the other side
description: "Anything financial needs a recurring reconciliation: per-period counts and totals, Humans' records against the vendor's (Stripe, pretix, Hi.Events, …), with a per-item drill-down when they differ. Not optional."
---

Peter, 2026-11-21: checks and balances are a standing rule, not a feature. If Humans sells 100 tickets for 25,000 € through a backend, Humans' own records must show the same 100 for the same money, and someone must be able to see that nothing is missing, extra or doubled.

**Why:** a payment flow spans two systems that can drift (a lost webhook, a dashboard refund, an order created twice). Only comparing them finds it.

**How to apply:**
- Any new path that takes or returns money (a new checkout backend, a store, a payout source) ships with its reconciliation: the vendor's own list for a period, compared with Humans' records by counts and totals, plus a per-item drill-down that names missing, extra, duplicate and mismatched items.
- Totals per period are enough; per-person hourly matching is not required.
- It runs on a schedule and surfaces an imbalance (logs an error at least), and an admin page lets the team research it on demand.
- A source that cannot be read reports an error, never a partial comparison.
- Ticket checkout backends: `ITicketCheckoutBackend.ListOrdersAsync`, `TicketReconBuilder`, `/Tickets/Admin/Sales/Recon`, `TicketReconJob` (`src/Sections/Humans.Tickets/Docs/features/checkout.md` "Reconciliation").
