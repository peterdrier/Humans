# Stripe — Data Access

## Stripe

Project: `src/Sections/Humans.Stripe` — services under `Services/`. **DbContext:**
`StripeDbContext`. `Repository` (`Data/Repository.cs`, implements
`IStripeMirrorRepository`) injects `IDbContextFactory<StripeDbContext>` directly and owns
`StripeBalanceTransactions`, `StripePayouts`, `StripeBalances`, `StripeSyncStates` — the
read-only mirror. The connector calls persist nothing; callers persist their results
through their own sections (Store, Tickets).

### StripeService (Scoped)

Wraps the Stripe SDK using the keys in `IOptions<StripeSettings>`:
`CreateCheckoutSessionAsync`, `GetPaymentDetailsAsync`,
`ListStoreCheckoutSessionsAsync`, and `ParseStoreCheckoutEvent`, which verifies
the webhook signature against `StoreWebhookSecret`. Holds no cache.

### StripeMirrorService (Scoped, implements `IStripeMirrorAdminService`)

Repository: `IStripeMirrorRepository`.

| Table | R/W |
|-------|-----|
| StripeBalanceTransactions | R/W (an account's rows replaced by its sweep; read whole, without `Raw`, for the pages — a few thousand rows) |
| StripePayouts | R/W (replaced per account) |
| StripeBalances | R/W (replaced per account) |
| StripeSyncStates | R/W (one row per account) |

No cross-section service calls. Its only outbound is `IStripeMirrorClient`
(`StripeMirrorClient`): read-only list/retrieve calls through Stripe.net.

### StripeStartupSmokeService (IHostedService)

At boot, one low-risk read per configured key — a PaymentIntent list on the
Tickets key, a Checkout Session list on the Store key. Never blocks or fails
startup.

### StoreWebhookRegistrationService (IHostedService)

Reads `IOptions<StripeSettings>`, `IOptions<GitHubSettings>` and `Email:BaseUrl`.
Gated on `STRIPE_STORE_WEBHOOK_REGISTRAR_KEY` (PR previews only); writes the
signing secret Stripe mints into `StripeSettings` in memory — the section's one
mutable setting. Behaviour: `Stripe.md` Triggers.

---
