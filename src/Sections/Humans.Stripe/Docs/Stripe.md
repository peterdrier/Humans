<!-- freshness:triggers
  src/Sections/Humans.Stripe/**
  tests/Humans.Stripe.Tests/**
-->
<!-- freshness:flag-on-change
  Re-read the connector-seam invariant (no Stripe.net type on Contracts/) and the key/scope table when this section changes.
-->

# Stripe — Section Invariants

The payments connector. Wraps the Stripe.NET SDK behind `IStripeService` so no other
production assembly names a Stripe type. Also holds a read-only **mirror** of every Stripe
account's ledger and the `/Stripe/Admin` pages over it (recon: find an order, match payouts,
see reserve holds). Spec: [`features/stripe-mirror.md`](features/stripe-mirror.md).

- **For:** the one place Humans talks to Stripe. Store gets hosted checkout and a verified
  webhook; Tickets gets each payment's fee breakdown.
- **Good looks like:** a thin, honest translator. Each call answers, returns `null` for a case
  it checked, or throws — never a silent failure, never a decision about what a payment means.
- **Deliberate choices:** two Stripe accounts on least-privilege restricted keys; no retries, no
  cache; the webhook registrar exists only for PR previews.
- **Out of scope:** issuing payouts and chargebacks (dashboard-manual; the mirror only reads
  them), invoicing (Store/Holded), payment bookkeeping (Store) and booking the mirror into
  Holded (a later step, after Peter has reviewed the mirror). Refunds are in scope, through one guarded path only
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
- The **mirror** is a cache of each mirrored account's balance transactions (the ledger), payouts
  and current balance, rebuilt per account by every sweep. The account list is config
  (`Stripe:MirrorAccounts`); the org key cannot list the organization's accounts.
- A **risk reserve** is Stripe holding funds back (`reporting_category` `risk_reserved_funds`).
  Each hold and its release are created together; each takes effect on its `available_on`
  date, so *held on day D* is the negated running sum of reserve rows available by D.
  The **payout minimum balance** (Store keeps 2,500 for deposits owed back) is different: a
  `payout_minimum_balance_hold` and `_release` in the same second around each payout leave the
  amount out of the payout and back in the available balance, so it is shown as the last hold's
  amount, not as a curve.
- The **startup smoke probe** makes one low-risk read per configured key at boot so a
  missing key or missing scope surfaces in the log rather than at the first real payment.

## Data Model (`StripeDbContext`, history `__EFMigrationsHistory_Stripe`)

| Table | Content |
|---|---|
| `stripe_balance_transactions` | one row per balance transaction, keyed by Stripe id: account, type, reporting category, status, amount/fee/net (minor units), created, available_on, description, source id, automatic payout id, PaymentIntent id, buyer name and email, the source charge's metadata (jsonb) and `Raw` (jsonb, the transaction with its source collapsed to the id) |
| `stripe_payouts` | one row per payout: account, amount, status, automatic, method, created, arrival, failure |
| `stripe_balances` | per account and currency: available and pending |
| `stripe_sync_states` | per account: status, last run, rows mirrored, last error |

All four are a cache, rebuilt per account by every sweep. Sentinel table:
`stripe_balance_transactions`. Everything else Stripe-derived is persisted by its owner:
fee/payment-method fields on Tickets' `ticket_orders`, payment rows on Store's `store_payments`.

## Configuration

| Variable | Account | Scopes used | Set where |
|---|---|---|---|
| `STRIPE_TICKETS_KEY` | Tickets | PaymentIntent read, BalanceTransaction read | QA, production |
| `STRIPE_STORE_KEY` | Store | `checkout_session:write` (Write ⊇ Read, so it also lists) | QA, production |
| `STRIPE_STORE_WEBHOOK_SECRET` | Store | — (signing secret) | QA, production; stamped in-memory in PR previews |
| `STRIPE_STORE_WEBHOOK_REGISTRAR_KEY` | Store | `webhook_endpoint:read/write` | PR previews **only** |
| `Stripe:WebhookCleanupOwner` / `…Repository` | — | — | PR previews only (GitHub repo for the open-PR list) |
| `STRIPE_ORG_READ_KEY` | every mirrored account without its own key, via `Stripe-Context: <acct>` | read-only Organization key (`sk_org_*`): balance, balance transaction sources, charges, refunds, disputes, payouts, PaymentIntents and more | QA, production |
| `STRIPE_ICE_READ_KEY` | ICE (run by an outside platform that refuses the org key) | balance, balance transactions + sources, payouts read | QA, production |
| `Stripe:MirrorAccounts` | — | — | appsettings: `Name`, `Id`, optional `KeyVariable` (the env var holding that account's own key) |

The mirror sends the SDK's pinned `Stripe-Version` (`ApiVersion.Current` of the Stripe.net
package) on every call, which an org key requires.

Production keys must be Restricted API Keys (`rk_*`) scoped to the minimum permission the
integration uses — see [`memory/code/stripe-restricted-keys.md`](../../../../memory/code/stripe-restricted-keys.md).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Finance admin / Admin | `/Stripe/Admin/*` and Sync now (`FinancePolicies.Manage`). |
| Board | Read `/Stripe/Admin/*` (`FinancePolicies.Read`). |
| Any other human | Nothing. |
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
- A checkout attempt id becomes the Stripe idempotency key, so repeating an attempt replays its
  session; the session is then re-read so its live status (not the cached `open`) comes back. A
  superseded session is expired before its replacement is created; when it has already completed,
  no replacement is created and the completed session comes back (Status `complete`, no Url).
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

- **Mirror:** a sweep fetches an account's transactions (sources expanded), payouts, each
  automatic payout's transaction list and the balance before writing anything, then replaces
  that account's rows in one save: rows the sweep did not return are deleted, other accounts
  are untouched. A failed account records the error on its sync row and keeps its mirror; the
  others still sweep. The pager fails closed (`has_more` on an empty page, or a 500-page cap,
  throws). One sweep at a time: an in-process gate shared by the job and Sync now.
- The mirror is read-only: list and retrieve calls only, whatever the key's scopes.
- Payout link: Stripe links transactions only to automatic payouts, so `PayoutId` is null on a
  manual-payout account (Tickets); a payout's own row carries its id.
- A refund or dispute row takes its buyer from a row on the same PaymentIntent, so a search by
  name finds the whole story. The expanded charge's card and address details are not stored.
- Account totals are EUR only; non-EUR rows are mirrored and flagged, never summed in.
- Display order is the controller's.

## Negative Access Rules

- This section **cannot** write any other section's table; its own are the mirror cache.
- Board **cannot** sync. No route exports the mirror in bulk (no name/email CSV): a search
  shows matching rows on screen only.
- This section **cannot** decide what a payment means. Categorizing an event is its job;
  recording, transitioning and reconciling a payment is Store's.
- The webhook registrar **cannot** run outside a `*.n.burn.camp` host, and **cannot** run
  at all without `STRIPE_STORE_WEBHOOK_REGISTRAR_KEY` (never set in QA or production).
- Payouts and chargebacks **cannot** be issued from the app. Refunds cannot either until the
  guarded refund path (nobodies-collective/Humans#1254) ships, and then only through it
  ([`memory/architecture/stripe-money-out.md`](../../../../memory/architecture/stripe-money-out.md)).

## Triggers

- `stripe-mirror-sync`, nightly at 03:30 (Hangfire, `StripeMirrorSyncJob`): the full sweep of
  every configured account. No key: logs and skips. A failed account fails the job run.
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

- Outbound, contracts leaves only: Finance.Contracts (`FinancePolicies`) and Users.Contracts
  (`IUserService`, through `HumansControllerBase`). Neither reaches back to Stripe, so
  `Humans.Store → Humans.Stripe` and `Humans.Tickets → Humans.Stripe` stay acyclic.
- Inbound: **Store** injects `IStripeService` for checkout creation, webhook parsing and
  reconciliation (`Service`, `StoreStripeWebhookController`). **Tickets** injects it for
  fee enrichment on `TicketOrder` (`TicketSyncService`).

## Architecture

**Owning services:** `StripeService`, `StripeMirrorService`, `StripeStartupSmokeService`, `StoreWebhookRegistrationService` (all `internal sealed`, `Humans.Stripe.Services`)
**Owned tables:** `stripe_balance_transactions`, `stripe_payouts`, `stripe_balances`, `stripe_sync_states` (the mirror cache).
**Status:** (A) Migrated (nobodies-collective/Humans#866).

### Public contract

| Public interface | Methods | Notes |
|---|---:|---|
| `IStripeService` | 4 + 3 flags | The whole outward surface. |
| `I<Section>Write` | — | The connector's one write is a call to Stripe, not to a table. |

- Services never import `Microsoft.EntityFrameworkCore`; `Data/Repository.cs` (`IStripeMirrorRepository`) is the only reader and writer of the mirror tables, and only `StripeMirrorService` calls it.
- **Decorator decision** — no caching decorator: every call exists to see Stripe's current state.
- The section is a Razor class library (`Microsoft.NET.Sdk.Razor`, MVC) for `/Stripe/Admin`.
- **Architecture test** — `tests/Humans.Stripe.Tests/Architecture/StripeConnectorArchitectureTests.cs`.
- **Namespace hazard:** inside `Humans.Stripe.*` the bare name `Stripe` binds to the section,
  not to the SDK. `using Stripe;` at compilation-unit level is safe; an inline `Stripe.Xxx`
  qualification is not. Use simple type names, or `global::Stripe.Xxx`.

## GDPR

The mirror holds buyer names and emails as Stripe records them (billing details, receipt email,
the ticket vendor's charge metadata), bound to no member, so there is no export contributor or
erasure path to hang them on; a sweep keeps only what Stripe still returns. They are the
association's payment records, which Stripe retains regardless. No bulk export of them exists.
