# Stripe — Data Access

## Stripe

Project: `src/Sections/Humans.Stripe` — services under `Services/`. **No
DbContext, no repository, no tables:** the section is the Stripe payment
connector; every persisted artefact of a payment belongs to Store.

### StripeService (Scoped)

No repository. Wraps the Stripe SDK using the keys in
`IOptions<StripeSettings>`: `CreateCheckoutSessionAsync`,
`GetPaymentDetailsAsync`, `ListStoreCheckoutSessionsAsync`, and
`ParseStoreCheckoutEvent`, which verifies the webhook signature against
`StoreWebhookSecret`. Holds no DB access and no cache; callers persist results
through their own sections.

### StripeStartupSmokeService (IHostedService)

No repository, no DB access. At boot, one low-risk read per configured key —
a PaymentIntent list on the Tickets key, a Checkout Session list on the Store
key — so a missing key or missing scope surfaces in the log rather than at the
first real payment. Never blocks or fails startup.

### StoreWebhookRegistrationService (IHostedService)

No repository, no DB access. Reads `IOptions<StripeSettings>`,
`IOptions<GitHubSettings>` and `Email:BaseUrl` from configuration. In ephemeral
environments only (gated on `STRIPE_STORE_WEBHOOK_REGISTRAR_KEY`) it registers
this host's Store webhook endpoint with Stripe, deletes endpoints belonging to
closed PRs, and writes the returned signing secret back into `StripeSettings`
in memory — the section's one mutable setting. Never blocks or fails startup.

---
