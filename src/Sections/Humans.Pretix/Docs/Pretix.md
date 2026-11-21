<!-- freshness:triggers
  src/Sections/Humans.Pretix/**
  src/Sections/Humans.Tickets.Contracts/ITicketCheckoutBackend.cs
  tests/Humans.Pretix.Tests/**
-->
<!-- freshness:flag-on-change
  The backend order id and order code scheme, the price check, refund idempotency, and the webhook's trust model.
-->

# Pretix — Section Invariants

The pretix option behind Tickets' checkout port: Humans sets the price, pretix takes the payment.

## Concepts

- The **port** is `Humans.Tickets.Contracts.ITicketCheckoutBackend` (key `pretix`). Tickets owns it; this section implements it and publishes nothing.
- A **backend order id** is `{event slug}/{order code}`. pretix addresses an order by both; the event slug arrives per checkout as `CheckoutRequest.BackendEventRef`.
- The **order code** is 16 symbols from `A-Z2-9` without `I` and `O`, taken from a SHA-256 of the Humans order id. Same order, same code.
- The **carrier items** are two "free price" products in each pretix event, found per checkout by internal name: `humans-ticket` (admission, taxed like a ticket) and `humans-donation` (no admission, no tax). They carry Humans' prices and nothing else; waves, classes and capacity stay in Humans. An event missing either throws `TicketCheckoutException` before any order is made.
- The **pay link** is the order's pay-method page with `return_url`, handled by pretix's bundled `returnurl` plugin, so the buyer lands back on `CheckoutRequest.ReturnUrl` after paying.

## Data Model

None. The section owns no tables. pretix's order is the record of the payment; Tickets owns the Humans order.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Tickets | Resolves the port by key and calls it |
| pretix | Posts order notifications to `/Pretix/Webhook/{secret}` |
| Any human | None; the buyer only follows the pay link to pretix |

## Invariants

- Checkout creates the order through `POST .../orders/` with `force` (Humans owns capacity), `send_email: false` (Humans sends the ticket mail), `expires` set to `ExpiresAt`, the Humans order id in `api_meta.humans_order_id`, and one position per line at the line's exact price.
- A repeat create with the same order id finds the existing order by code and returns its pay link; a code held by another `humans_order_id` throws `TicketCheckoutException`.
- The adapter never changes a price. Each position's price and tax rate must equal its line's amount and `VatRate`, and a fresh order's total must equal the lines' sum; on a mismatch a just-created order is cancelled and `TicketCheckoutException` is thrown. A currency the event does not sell throws before anything is created.
- A buyer language the event does not offer is dropped and the order retried, so pretix uses the event default.
- Status maps pretix `n/p/e/c` to Pending/Paid/Expired/Cancelled. `FeeMinor` is the payment-method fee pretix added when the buyer chose a method (pretix's per-method fixed and percentage fee); `RefundedMinor` is every refund in state `done`, however issued.
- Refund calls `payments/{id}/refund/` on a confirmed payment with enough left, with `mark_canceled: false` and comment `humans-refund:{RefundId}`. A refund already carrying that comment is returned, not repeated. A pretix 400 (the method cannot refund automatically) is `ManualRequired` with the order's pretix control page; asking for more than any payment has left is `Failed` without a call.
- Cancel throws on a paid order, is a no-op on a cancelled one, and cancels pending and expired ones (pretix still takes a late payment on an expired order).
- The webhook is a nudge only: a constant-time match of the URL secret against `PRETIX_WEBHOOK_SECRET`, 503 while unset, 404 on a mismatch, 200 and nothing for another organizer. Tickets re-reads the order from pretix, so a forged call costs one read.
- Several Humans events can sell at once, each on its own pretix event; nothing in the settings is per event.
- The backend is live in every environment once configured (`BaseUrl`, `Organizer`, `PRETIX_API_TOKEN`); unconfigured, `IsConfigured` is false and every call throws `TicketCheckoutException`.

## Negative Access Rules

- Code outside this project **cannot** name `PretixCheckoutBackend` or `PretixSettings`.
- The webhook **cannot** change any state itself; it only tells Tickets an order changed.
- A pretix team token **can** refund (pretix has one `event.orders:write` permission for create and refund). Peter allowed API refunds for ticket backends on 2026-11-21; give the token only the events Humans sells.

## Triggers

- A pretix webhook calls `ITicketCheckoutEvents.CheckoutChangedAsync("pretix", "{event}/{code}")`.

## Cross-Section Dependencies

- **Tickets**: implements `ITicketCheckoutBackend`, calls `ITicketCheckoutEvents`, both from the `Humans.Tickets.Contracts` leaf.
- **Base**: configuration registry.

## pretix setup

Per event: the two carrier items, the `returnurl` plugin with Humans' return URL prefix, the Stripe plugin with card, iDEAL and Klarna enabled and their fees, a webhook for the order actions to `https://<humans>/Pretix/Webhook/<secret>`, and ticket download and every buyer mail off (`send_email: false` covers only the create call; a payment confirmed later by Stripe sends pretix's paid mail unless the event's `mail_sales_channel_placed_paid` is empty, which `bootstrap.py` sets). Hosting: `deploy/pretix/` in the repo root.

## Architecture

**Owning services:** `PretixCheckoutBackend`
**Owned tables:** None, adapter over Tickets' port.
**Status:** (A) Migrated, the adapter shape: plain `Microsoft.NET.Sdk`, no tables, an empty `Contracts/`, one webhook controller.
