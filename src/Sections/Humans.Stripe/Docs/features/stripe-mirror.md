# Stripe mirror

Step one of getting Stripe into Humans' finance work: a read-only copy of every Stripe account's money data, and
admin pages over it. Peter reviews the mirror before anything is booked into Holded (step two).

## Why

Recon ran off the Stripe Dashboard, one account at a time: finding a buyer's order, matching payouts to the bank,
and spotting money Stripe held back. The 2026 Tickets risk reserve was invisible in the last cycle because the
Dashboard balance hides it.

## Accounts and keys

| Account | Id | Key |
|---|---|---|
| Tickets | `acct_1SzeqEGhYASbLXO0` | `STRIPE_ORG_READ_KEY` + `Stripe-Context` |
| Buses | `acct_1TP76LGSRy8X4KQM` | `STRIPE_ORG_READ_KEY` + `Stripe-Context` |
| Store | `acct_1TSyTLGhmt4dtIKF` | `STRIPE_ORG_READ_KEY` + `Stripe-Context` |
| ICE | `acct_1TpBPCR5uZvBb98F` | `STRIPE_ICE_READ_KEY` (its outside platform refuses the org key) |

The list is `Stripe:MirrorAccounts` in appsettings: the org key cannot list the organization's accounts. An account
whose key is unset is shown as not configured and never swept. The existing `STRIPE_TICKETS_KEY` and
`STRIPE_STORE_KEY` are untouched.

## What is mirrored

- **Balance transactions** (the ledger): type, reporting category, amount, fee, net, created, available_on,
  description, source id, and — from the expanded source charge — PaymentIntent id, buyer name and email, and the
  charge's metadata (Ticket Tailor and Store order ids). Card and address details are not kept.
- **Payouts**, and for each automatic payout the transactions it paid out. Stripe links none to a manual payout.
- **Balance** per currency: available and pending.

## Pages (`/Stripe/Admin`, `FinancePolicies.Read`; Sync now needs `FinancePolicies.Manage`)

- **Accounts**: per account, Stripe's balance beside the sum of every mirrored net (red when they differ), gross
  charges, fees, refunds, paid out, risk reserve held today with its peak, and the payout minimum kept back at the last payout.
- **Transactions**: search by name, email, order id, description or any Stripe id; filter by account, type, payout.
  Newest 1,000 shown; the count says when more matched.
- **Transaction**: every field, the charge metadata and the raw transaction.
- **Payouts**: every payout, its linked row count and net (red when a paid automatic payout's rows do not net to it).
- **Reserves**: risk reserve held per day, for every account that had one.

No bulk export: rows with names and emails are shown on screen only.

## Not in this step

Holded booking, any Stripe write, binding buyers to members.
