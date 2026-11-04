# Stripe — Data Access

## Stripe

Project: `src/Sections/Humans.Stripe` — services under `Services/`. **No
DbContext, no repository, no tables, no DB access in any service:** the section
is the Stripe payment connector; callers persist results through their own
sections (Store, Tickets).

### StripeService (Scoped)

Wraps the Stripe SDK using the keys in `IOptions<StripeSettings>`:
`CreateCheckoutSessionAsync`, `GetPaymentDetailsAsync`,
`ListStoreCheckoutSessionsAsync`, and `ParseStoreCheckoutEvent`, which verifies
the webhook signature against `StoreWebhookSecret`. Holds no cache.

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
