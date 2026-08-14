<!-- freshness:triggers
  src/Sections/Humans.Stripe/**
-->
<!-- freshness:flag-on-change
  Re-read the connector-seam invariant (no Stripe.net type on Contracts/) and the key/scope table when this section changes.
-->

# Stripe — Section Invariants

The payments connector. Wraps the Stripe.NET SDK behind `IStripeService` so no other
assembly names a Stripe type. Owns no tables and no UI.

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
  The connector categorizes; it does not decide.
- The **webhook registrar** is a boot-time job that creates the Store webhook endpoint
  for ephemeral environments (PR previews) and sweeps endpoints belonging to closed PRs.
  Gated on `STRIPE_STORE_WEBHOOK_REGISTRAR_KEY`, which is never set in QA or production.
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

- No Stripe.NET SDK type appears on `Contracts/` — verified by
  `StripeConnectorArchitectureTests.IStripeService_ExposesNoStripeSdkTypesOnItsPublicSurface`,
  which walks nested generics and arrays.
- `Humans.Stripe` is the only production project with a `Stripe.net` package reference;
  `Humans.Application` carrying one fails
  `StripeConnectorArchitectureTests.HumansApplicationAssembly_HasNoReferenceToStripeNet`.
- Everything but `Contracts/` and `Section` is `internal sealed` (HUM0034).
- `ParseStoreCheckoutEvent` returns `null` for an invalid signature and for an unset
  signing secret; it never throws and never partially trusts a payload.
- `ListStoreCheckoutSessionsAsync` returns `null` — "Stripe could not be queried" — as
  distinct from an empty list, so a caller cannot mistake an unreadable account for an
  account with no sessions and false-flag recorded payments as orphans.
- `CreateCheckoutSessionAsync` rejects a non-positive amount and an unconfigured Store key
  before any network call, and throws (rather than returning a sentinel) on Stripe failure.
- EUR ↔ minor units round half away from zero, both directions, in one place
  (`ToStripeMinorUnits` / `FromStripeMinorUnits`).
- The section references no other section. That is what makes `Humans.Store → Humans.Stripe`
  and `Humans.Tickets → Humans.Stripe` acyclic and a `.Contracts` leaf unnecessary.

## Negative Access Rules

- A consumer **cannot** receive a Stripe SDK object: it sees only `StripePaymentDetails`,
  `StoreCheckoutWebhookEvent` and `StoreCheckoutSessionData`.
- This section **cannot** write any table — it holds no `DbContext` and no repository.
- This section **cannot** decide what a payment means. Categorizing an event is its job;
  recording, transitioning and reconciling a payment is Store's.
- The webhook registrar **cannot** run outside a `*.n.burn.camp` host, and **cannot** run
  at all without `STRIPE_STORE_WEBHOOK_REGISTRAR_KEY`.
- Refunds, payouts and chargebacks **cannot** be issued from the app — they are
  dashboard-manual by design
  ([`memory/architecture/refunds-manual-via-dashboard.md`](../../../../memory/architecture/refunds-manual-via-dashboard.md)).

## Triggers

- At boot, the smoke probe reads one PaymentIntent (Tickets key) and lists one Checkout
  Session (Store key), logging a warning per unconfigured or under-scoped key. It never
  blocks or fails startup.
- At boot in an ephemeral environment, the registrar sweeps webhook endpoints whose host
  is `{N}.n.burn.camp` for a PR `{N}` no longer open, deletes any endpoint already
  pointing at this host's URL, creates a fresh one, and stamps its signing secret into
  `StripeSettings` in memory.
- On a `permission_error` from any call, the connector logs which key is missing which
  scope and either returns `null` (reads) or rethrows (checkout creation).

## Cross-Section Dependencies

- **None outbound.** The section names no other section's types.
- Inbound: **Store** injects `IStripeService` for checkout creation, webhook parsing and
  reconciliation (`Service`, `StoreStripeWebhookController`). **Tickets** injects it for
  fee enrichment on `TicketOrder` (`TicketSyncService`).

## Architecture

**Owning services:** `StripeService`, `StripeStartupSmokeService`, `StoreWebhookRegistrationService` (all `internal sealed`, `Humans.Stripe.Services`)
**Owned tables:** None — connector section.
**Status:** (A) Migrated — moved out of `Humans.Application` / `Humans.Infrastructure` / `Humans.Web` into its own project by nobodies-collective/Humans#866 (G5 lane 4b-2a), 2026-08-14.

### Cross-section read interface

| Read interface | Methods | Notes |
|---|---:|---|
| `IStripeService` | 4 + 3 flags | The whole outward surface. Read/write is not split: the connector's "write" is a call to Stripe, not to a table, so `peters-hard-rules.md`'s `I<Section>ServiceRead` pattern has nothing to separate. |

- The section is plain `Microsoft.NET.Sdk` plus a `FrameworkReference` (for `IHostedService`
  and `IOptions`); no Razor, no `Humans.UI`, no `Humans.Infrastructure`.
- **Architecture test** — `tests/Humans.Stripe.Tests/Architecture/StripeConnectorArchitectureTests.cs`.
- **Namespace hazard:** the section is named after the vendor, so inside
  `Humans.Stripe.*` the bare name `Stripe` binds to the section, not to the SDK. `using
  Stripe;` at compilation-unit level resolves against the global namespace and is safe;
  an inline `Stripe.Xxx` qualification is not. Use simple type names, or `global::Stripe.Xxx`.
