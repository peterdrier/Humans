<!-- freshness:triggers
  src/Sections/Humans.Stripe/**
  tests/Humans.Stripe.Tests/**
-->
<!-- freshness:flag-on-change
  Re-read the connector-seam invariant (no Stripe.net type on Contracts/) and the key/scope table when this section changes.
-->

# Stripe — Section Invariants

The payments connector. Wraps the Stripe.NET SDK behind `IStripeService` so no other
production assembly names a Stripe type. Owns no tables and no UI.

- **For:** the one place Humans talks to Stripe. Store gets hosted checkout and a verified
  webhook; Tickets gets each payment's fee breakdown.
- **Good looks like:** a thin, honest translator. Each call answers, returns `null` for a case
  it checked, or throws — never a silent failure, never a decision about what a payment means.
- **Deliberate choices:** two Stripe accounts on least-privilege restricted keys; no retries, no
  cache; the webhook registrar exists only for PR previews.
- **Out of scope:** payouts and chargebacks (dashboard-manual), invoicing (Store/Holded) and
  payment bookkeeping (Store). Refunds are in scope, through one guarded path only
  (nobodies-collective/Humans#1254, not built yet).

## Concepts

- A **Stripe account** is one merchant account with one API key. Two are in use: the
  **Tickets account** (`STRIPE_TICKETS_KEY`, PaymentIntent + BalanceTransaction reads for
  fee enrichment) and the **Store account** (`STRIPE_STORE_KEY`, Checkout Session
  create/list).
- A **Checkout Session** is a Stripe-hosted payment page created for one Store order.
  `humans_store_order_id` is stamped on both the session's and the PaymentIntent's
  metadata, which is how a payment is matched back to an order from the dashboard,
  the receipt and PI search.
- A **checkout webhook event** is one of the four `checkout.session.*` events the Store
  subscribes to, projected to `StoreCheckoutWebhookEvent` after signature verification.
- The **webhook registrar** is a boot-time job that creates the Store webhook endpoint
  for ephemeral environments (PR previews) and sweeps endpoints belonging to closed PRs.
- The **startup smoke probe** makes one low-risk read per configured key at boot so a
  missing key or missing scope surfaces in the log rather than at the first real payment.

## Data Model

None — the section owns no tables. Stripe-derived values are persisted by their owners:
fee/payment-method fields on Tickets' `ticket_orders`, and payment rows on Store's
`store_payments`.

## Configuration

| Variable | Account | Scopes used | Set where |
|---|---|---|---|
| `STRIPE_TICKETS_KEY` | Tickets | PaymentIntent read, BalanceTransaction read | QA, production |
| `STRIPE_STORE_KEY` | Store | `checkout_session:write` (Write ⊇ Read, so it also lists) | QA, production |
| `STRIPE_STORE_WEBHOOK_SECRET` | Store | — (signing secret) | QA, production; stamped in-memory in PR previews |
| `STRIPE_STORE_WEBHOOK_REGISTRAR_KEY` | Store | `webhook_endpoint:read/write` | PR previews **only** |
| `Stripe:WebhookCleanupOwner` / `…Repository` | — | — | PR previews only (GitHub repo for the open-PR list) |

Production keys must be Restricted API Keys (`rk_*`) scoped to the minimum permission the
integration uses — see [`memory/code/stripe-restricted-keys.md`](../../../../memory/code/stripe-restricted-keys.md).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any actor | None directly — the section has no controller, no route and no view. |
| Stripe (the vendor) | Delivers signed webhooks to Store's `/Store/StripeWebhook`, which verifies them through this section. |

## Invariants

- No Stripe.NET SDK type appears on `Contracts/`; consumers see only `StripePaymentDetails`,
  `StoreCheckoutWebhookEvent` and `StoreCheckoutSessionData` — verified by
  `StripeConnectorArchitectureTests.IStripeService_ExposesNoStripeSdkTypesOnItsPublicSurface`,
  which walks nested generics and arrays.
- `Humans.Stripe` is the only production project with a `Stripe.net` package reference.
- Everything but `Contracts/` and `Section` is `internal sealed` (HUM0034).
- Webhook parsing rejects malformed JSON, unsupported payload shapes, missing event identity/type/data, and checkout events carrying a non-session object. These return null with a Warning, including when the signature is valid; diagnostics do not log payload contents. Signature verification still precedes deserialization, and unrelated well-formed event types remain ignorable.
- Checkout Session creation is `[ExternalWrite]`; a state-changing caller detaches request
  cancellation at the call boundary.
- Reads return `null` only for what the section checks: the key is unset or lacks its scope
  (`permission_error`); for `ParseStoreCheckoutEvent`, the signing secret is unset, the signature
  is invalid, its payload is malformed/incomplete, or the session lacks EUR; for `GetPaymentDetailsAsync` alone, the PaymentIntent has
  no charge or its balance transaction is not available yet after asynchronous capture. **Every
  other Stripe failure propagates** — an authentication error, a rate limit, a transport failure,
  cancellation — and `ParseStoreCheckoutEvent` never throws. An empty list means Stripe answered
  with nothing, so reconciliation cannot mistake an unreadable account for one with no sessions
  and false-flag recorded payments as orphans.
- A missing balance transaction never becomes an explicit zero; a present transaction with no
  fee entries reports zero.
- `CreateCheckoutSessionAsync` rejects a non-positive amount and an unconfigured Store key
  before any network call, and throws (rather than returning a sentinel) on Stripe failure.
- EUR → minor units rounds half away from zero; minor units → EUR is exact. Both conversions
  live only in `ToStripeMinorUnits` / `FromStripeMinorUnits`.
- Every amount read requires explicit EUR, case-insensitively. Ticket fee reads require it on
  the balance transaction and every fee entry — the charge's currency cannot establish the
  settlement currency — and throw before returning any amount. Store session reads require it
  before projecting `AmountEur`: webhooks reject the event, reconciliation rejects the whole list
  rather than silently dropping sessions. Adaptive Pricing's presentment currency never replaces
  the integration currency or amount.
- Ticket fee reads keep every Stripe fee bucket (the mapping is on `StripePaymentDetails`). When
  the buckets do not sum to the balance transaction's total fee (an unrecognised fee type), the
  read logs a Warning and still returns the known buckets.

- Store-session diagnostics mask valid human-formatted IBANs in provider-controlled fields, including whitespace or hyphens within country codes and check digits.

## Negative Access Rules

- This section **cannot** write any table — it holds no `DbContext` and no repository.
- This section **cannot** decide what a payment means. Categorizing an event is its job;
  recording, transitioning and reconciling a payment is Store's.
- The webhook registrar **cannot** run outside a `*.n.burn.camp` host, and **cannot** run
  at all without `STRIPE_STORE_WEBHOOK_REGISTRAR_KEY` (never set in QA or production).
- Payouts and chargebacks **cannot** be issued from the app. Refunds cannot either until the
  guarded refund path (nobodies-collective/Humans#1254) ships, and then only through it
  ([`memory/architecture/stripe-money-out.md`](../../../../memory/architecture/stripe-money-out.md)).

## Triggers

- At boot, the smoke probe lists one PaymentIntent (Tickets key) and one Checkout Session
  (Store key), logging a warning per unconfigured or under-scoped key. It never blocks or
  fails startup.
- At boot in an ephemeral environment, the registrar lists the account's webhook endpoints
  once and, from that one listing, deletes any `{N}.n.burn.camp` endpoint whose PR `{N}` is no
  longer open and then any endpoint already pointing at this host's URL — then creates a fresh
  one and stamps its signing secret into `StripeSettings` in memory. The own-URL deletion is
  ordered last, immediately before the create, so an abort mid-cleanup cannot leave this host
  without an endpoint. The cross-PR half is skipped when the open-PR list cannot be fetched;
  the own-URL half needs no PR list. A 15-second deadline, which includes the paged GitHub
  open-PR lookup, aborts the whole sequence; cancellation is never read as an unavailable PR list.
- On a `permission_error` from any call, the connector logs which key is missing which
  scope and either returns `null` (reads) or rethrows (checkout creation).

## Cross-Section Dependencies

- **None outbound.** The section references no other section, which is what keeps
  `Humans.Store → Humans.Stripe` and `Humans.Tickets → Humans.Stripe` acyclic and a
  `.Contracts` leaf unnecessary.
- Inbound: **Store** injects `IStripeService` for checkout creation, webhook parsing and
  reconciliation (`Service`, `StoreStripeWebhookController`). **Tickets** injects it for
  fee enrichment on `TicketOrder` (`TicketSyncService`).

## Architecture

**Owning services:** `StripeService`, `StripeStartupSmokeService`, `StoreWebhookRegistrationService` (all `internal sealed`, `Humans.Stripe.Services`)
**Owned tables:** None — connector section.
**Status:** (A) Migrated (nobodies-collective/Humans#866).

### Public contract

| Public interface | Methods | Notes |
|---|---:|---|
| `IStripeService` | 4 + 3 flags | The whole outward surface. |
| `I<Section>Write` | — | The connector's one write is a call to Stripe, not to a table. |

- Services never import `Microsoft.EntityFrameworkCore`; there is no repository and no display stitching.
- **Decorator decision** — no caching decorator: every call exists to see Stripe's current state.
- The section is plain `Microsoft.NET.Sdk` plus a `FrameworkReference` (for `IHostedService`
  and `IOptions`); no Razor, no `Humans.UI`, no `Humans.Infrastructure`.
- **Architecture test** — `tests/Humans.Stripe.Tests/Architecture/StripeConnectorArchitectureTests.cs`.
- **Namespace hazard:** inside `Humans.Stripe.*` the bare name `Stripe` binds to the section,
  not to the SDK. `using Stripe;` at compilation-unit level is safe; an inline `Stripe.Xxx`
  qualification is not. Use simple type names, or `global::Stripe.Xxx`.
