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
| pretix | Posts order notifications to `/Pretix/Webhook?secret={secret}` |
| Any human | None; the buyer only follows the pay link to pretix |

## Invariants

- Checkout creates the order through `POST .../orders/` with `force` (Humans owns capacity), `send_email: false` (Humans sends the ticket mail), `expires` set to `ExpiresAt`, the Humans order id in `api_meta.humans_order_id`, and one position per line at the line's exact price.
- A repeat create with the same order id finds the existing order by code and returns its pay link; a code held by another `humans_order_id` throws `TicketCheckoutException`.
- The adapter never changes a price. Each position's price and tax rate must equal its line's amount and `VatRate`, and a fresh order's total must equal the lines' sum; on a mismatch a just-created order is cancelled and `TicketCheckoutException` is thrown. A currency the event does not sell throws before anything is created.
- A buyer language the event does not offer is dropped and the order retried, so pretix uses the event default.
- Status maps pretix `n/p/e/c` to Pending/Paid/Expired/Cancelled, except that money decides: a pending or expired order its confirmed payments cover, or a cancelled order holding unrefunded money, is Paid. A pending order with a payment in pretix's `pending` state (a SEPA debit settling) is Processing; a bank transfer not yet sent stays `created` and the order stays Pending. pretix keeps a payment confirmed after expiry or cancel (a slow iDEAL, Klarna or 3DS) but cannot mark the order paid, since the carrier items have no quota; the port says such a payment is reported, and Tickets issues and flags it. `RefundedMinor` is every refund in state `done`, however issued. `DisputedMinor` stays 0: pretix records no chargebacks.
- `FeeMinor` is always null: the port means what the processor kept, which pretix does not know. The buyer pays exactly the lines, so `bootstrap.py` sets every payment method's fee to zero; a payment surcharge found on an order is logged as an error, not thrown, so a real payment still reaches Humans.
- `RefundFailed` is true while the newest refund on the order is `failed` or `canceled` in pretix; a later refund clears it.
- Erase clears the order's email and phone when the email is the buyer's, and attendee name and email on its positions (the buyer's order, or positions carrying that email); amounts stay. Humans sends no names, so this mostly clears the email. pretix logs the old email in the order's history (`pretix.event.order.contact.changed`) and its API cannot delete log entries, so the erasure completes only when pretix's data shredder runs for the event after it ends (debt PRETIX-1).
- Refund first lowers the pretix order total by the amount, as a negative `other` fee with internal type `humans-refund:{RefundId}`, then calls `payments/{id}/refund/` on a confirmed payment with enough left, with `mark_canceled: false` and the same marker as comment. Without the fee pretix reopens the paid order and its pay page asks the buyer for the refunded amount again. A repeat with the same `RefundId` answers that refund's current state and backend id and never refunds again; when it has since failed or been canceled, the marked fee comes off. A fee already carrying the marker is reused.
- pretix's 400 "refund not available for this payment method" is `ManualRequired` with the order's pretix control page; the fee stays, so pretix shows exactly that amount as owed back. Any other 400 (provider error, bad amount or payment state) is `Failed`, and the fee comes off again, also one left by an earlier unanswered attempt, unless a refund made by hand has already paid that amount out. No answer or a pretix 5xx throws, so Tickets keeps the refund pending and retries it under the same id, which reuses the fee and any refund already made; any other refusal (401, 403, 404) is `Failed` with the fee taken off on the same terms. Asking for more than any payment has left is `Failed` without a call.
- pretix holds no names: an order carries only the buyer's email, the Humans order id and the amounts.
- Cancel throws on an order that reads as Paid or Processing, is a no-op on a cancelled one, and cancels pending and expired ones (pretix still takes a late payment on an expired order). Tickets calls it when its hold lapses.
- The webhook is a nudge only: a constant-time match of the URL secret against `PRETIX_WEBHOOK_SECRET`, 503 while unset, 404 on a mismatch, 200 and nothing for another organizer. Tickets re-reads the order from pretix, so a forged call costs one read.
- Several Humans events can sell at once, each on its own pretix event; nothing in the settings is per event.
- The backend is live in every environment once configured (`BaseUrl`, `Organizer`, `PRETIX_API_TOKEN`); unconfigured, `IsConfigured` is false and every call throws `TicketCheckoutException`.
- `Setup` shows `BaseUrl` and `Organizer` and only set or missing for the token and secret; a `BaseUrl` on `pretix.eu` reads as pretix Hosted, which the adapter serves unchanged. `CheckAsync` only reads: the organizer, then for an event slug its currency, live and test mode, and exactly one active item per carrier name; a test-mode event and an id that is not a slug fail.

## Negative Access Rules

- Code outside this project **cannot** name `PretixCheckoutBackend` or `PretixSettings`.
- The webhook **cannot** change any state itself; it only tells Tickets an order changed.
- A pretix team token **can** refund (pretix has one `event.orders:write` permission for create and refund). Peter allowed API refunds for ticket backends on 2026-11-21; `bootstrap.py` gives the token order read and write on the events Humans sells and nothing else.

## Triggers

- A pretix webhook calls `ITicketCheckoutEvents.CheckoutChangedAsync("pretix", "{event}/{code}")`.

## Cross-Section Dependencies

- **Tickets**: implements `ITicketCheckoutBackend`, calls `ITicketCheckoutEvents`, both from the `Humans.Tickets.Contracts` leaf.
- **Base**: configuration registry.

## pretix setup

Per event: the two carrier items, the `returnurl` plugin with Humans' return URL prefix, the Stripe plugin with card, iDEAL and Klarna enabled and no method fees, a webhook for the order actions to `https://<humans>/Pretix/Webhook?secret=<secret>` (in the query, which request logging leaves out), and ticket download and every buyer mail off (`send_email: false` covers only the create call; a payment confirmed later by Stripe sends pretix's paid mail unless the event's `mail_sales_channel_placed_paid` is empty, which `bootstrap.py` sets). Hosting: `deploy/pretix/` in the repo root.

## Architecture

**Owning services:** `PretixCheckoutBackend`
**Owned tables:** None, adapter over Tickets' port.
**Status:** (A) Migrated, the adapter shape: plain `Microsoft.NET.Sdk`, no tables, an empty `Contracts/`, one webhook controller.
